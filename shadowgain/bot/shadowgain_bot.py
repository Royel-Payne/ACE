#!/usr/bin/env python3
"""
Shadowgain Discord bot (Task 031).

Bridges the game server to Discord WITHOUT the game server ever talking to Discord.
The server appends JSON lines to two files under /opt/ACE/Logs; this bot tails them
and posts. Everything Discord-facing lives here, so the relay format, the embed
styling and the verification rules can all change without a server redeploy.

  chatrelay.jsonl  -> {"type":"chat",   ...}  -> relay channel
  sgevents.jsonl   -> {"type":"bug",    ...}  -> #bugs channel
                      {"type":"verify", ...}  -> role grant

TWO-WAY as of 033. Discord -> game was held back as a security boundary until /link
existed, because the unanswerable question was which in-game identity a Discord line
speaks as. A verified account/character link answers it, so inbound now ships - but
the SERVER validates every line (ownership, gag, rate limit, length, General only).
Nothing here is a security control.

Inbound arrives two ways: the /say command, or - when SG_READ_CHANNEL is on - by
reading the relay channel directly, which needs the Message Content intent.

DB access is READ-ONLY by construction - the bot is given a MySQL user with SELECT
and nothing else (see setup.sql). It never writes game state.
"""

import asyncio
import datetime
import json
import os
import random
import string
import sys
import time
from dataclasses import dataclass, field
from typing import Optional

import discord
from discord import app_commands
import pymysql

# --------------------------------------------------------------------------------------
# Configuration - all from the environment, nothing secret in this file.
# --------------------------------------------------------------------------------------

def _req(name: str) -> str:
    v = os.environ.get(name, "").strip()
    if not v:
        sys.exit(f"FATAL: {name} is not set. See .env.example.")
    return v

def _int(name: str, default: int = 0) -> int:
    raw = os.environ.get(name, "").strip()
    try:
        return int(raw) if raw else default
    except ValueError:
        sys.exit(f"FATAL: {name}={raw!r} is not an integer.")

TOKEN            = _req("DISCORD_BOT_TOKEN")
GUILD_ID         = _int("DISCORD_GUILD_ID")
RELAY_CHANNEL_ID = _int("DISCORD_RELAY_CHANNEL_ID")
BUGS_CHANNEL_ID  = _int("DISCORD_BUGS_CHANNEL_ID")
VERIFIED_ROLE_ID = _int("DISCORD_VERIFIED_ROLE_ID")

LOG_DIR    = os.environ.get("SG_LOG_DIR", "/opt/ACE/Logs")
STATE_PATH = os.environ.get("SG_STATE_PATH", "/opt/ACE/bot-state.json")
# 033: Discord -> game. Lives in the Logs directory because that volume is ALREADY
# mounted rw into the container, so the inbound path needs no compose change and no
# container recreate.
INBOUND_PATH = os.environ.get("SG_INBOUND_PATH", "/opt/ACE/Logs/inbound.jsonl")

DB_HOST = os.environ.get("SG_DB_HOST", "127.0.0.1")
DB_PORT = _int("SG_DB_PORT", 3306)
DB_USER = os.environ.get("SG_DB_USER", "sgbot")
DB_NAME = os.environ.get("SG_DB_NAME", "ace_shard")
DB_PASS = _req("SG_DB_PASSWORD")

# Ratified in Phase 0: level >= 10 AND active within 72 hours.
# 72h rather than the originally proposed 30 days - "a weekend away is fine, lurkers stay
# blinded". Level 10 because leaving the academy already promotes to 10, so this filters
# create-and-never-move accounts without gatekeeping real players.
MIN_LEVEL      = _int("SG_MIN_LEVEL", 10)
ACTIVITY_HOURS = _int("SG_ACTIVITY_HOURS", 72)
SWEEP_HOURS    = _int("SG_SWEEP_HOURS", 24)

CODE_TTL_SECONDS = _int("SG_CODE_TTL_SECONDS", 900)   # 15 minutes to type a code in game

# Chat is batched before posting: Discord rate-limits per channel, and a busy General
# would otherwise generate one HTTP request per line.
# Read the relay channel directly instead of requiring /say.
#
# This needs the MESSAGE CONTENT privileged intent, which must be switched on in the
# developer portal FIRST - discord.py refuses to connect if the bot requests an intent the
# application has not been granted, and under Restart=always that is a crash loop. Hence a
# flag: portal toggle first, then this.
READ_CHANNEL = os.environ.get("SG_READ_CHANNEL", "0").strip().lower() in ("1", "true", "yes")

# Rolling retention on the relay channel. Chat ages out so the channel is a live window,
# not a searchable archive of everything ever said in game. 0 disables.
#
# Applies to the RELAY CHANNEL ONLY - #bugs keeps its history deliberately, so reporters
# can read past reports and self-dedupe.
RETENTION_HOURS = _int("SG_CHAT_RETENTION_HOURS", 24)

BATCH_SECONDS   = float(os.environ.get("SG_BATCH_SECONDS", "2.0"))
MAX_BATCH_CHARS = 1800     # Discord's hard limit is 2000; leave room for formatting.


# --------------------------------------------------------------------------------------
# Persistent state - tail offsets, pending codes, and Discord-user -> game-account links.
# --------------------------------------------------------------------------------------

@dataclass
class State:
    # stream name -> {"inode": int, "pos": int}
    offsets: dict = field(default_factory=dict)
    # code -> {"discord_id": int, "expires": float}
    pending: dict = field(default_factory=dict)
    # discord_id (as str, because JSON keys are strings) -> account name
    links: dict = field(default_factory=dict)

    @classmethod
    def load(cls, path: str) -> "State":
        try:
            with open(path, "r", encoding="utf-8") as f:
                raw = json.load(f)
            return cls(
                offsets=raw.get("offsets", {}),
                pending=raw.get("pending", {}),
                links=raw.get("links", {}),
            )
        except FileNotFoundError:
            return cls()
        except (json.JSONDecodeError, OSError) as e:
            # A corrupt state file must not stop the bot: losing offsets replays a little,
            # losing links makes people re-verify. Both beat a bot that will not start.
            print(f"WARN: state file unreadable ({e}); starting fresh", flush=True)
            return cls()

    def save(self, path: str) -> None:
        # Atomic write. A half-written state file read after a crash would be worse than
        # no state file at all, and truncate-then-write leaves exactly that window open.
        tmp = path + ".tmp"
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump({"offsets": self.offsets, "pending": self.pending, "links": self.links},
                          f, ensure_ascii=False)
            os.replace(tmp, path)
        except OSError as e:
            print(f"WARN: could not save state: {e}", flush=True)


# --------------------------------------------------------------------------------------
# JSONL tailer - survives log4net rotation and bot restarts.
# --------------------------------------------------------------------------------------

class JsonlTailer:
    """
    Follows a file that log4net rotates underneath us.

    RollingFileAppender with staticLogFileName=true renames the active file to `.1` and
    creates a fresh one, so the path stays the same while the INODE changes. Watching the
    path alone would silently keep reading a file nobody writes to any more; watching the
    inode is what makes rotation survivable.

    `start_at_end` differs per stream on purpose:
      - chat starts at the END, so a bot restart does not replay old conversation
      - events start at the BEGINNING, so a bug report filed while the bot was down is
        still delivered. Bug reports are precious; chat is disposable.
    """

    def __init__(self, path: str, name: str, state: State, start_at_end: bool):
        self.path = path
        self.name = name
        self.state = state
        self.start_at_end = start_at_end
        self.fh = None
        self.inode = None
        self._buf = ""

    def _open(self) -> None:
        try:
            # utf-8-SIG, not utf-8. log4net's FileAppender writes a UTF-8 BOM (EF BB BF)
            # when it creates a file, so the first JSON object after every creation and
            # every rotation arrives as a BOM followed by '{"type":...' and json.loads rejects it.
            # utf-8-sig consumes that BOM transparently; the per-line lstrip below is the
            # belt-and-braces for a BOM appearing anywhere else.
            fh = open(self.path, "r", encoding="utf-8-sig", errors="replace")
        except FileNotFoundError:
            self.fh = None
            return

        st = os.fstat(fh.fileno())
        saved = self.state.offsets.get(self.name) or {}

        if saved.get("inode") == st.st_ino:
            # Same file we were reading before: resume where we stopped, unless it was
            # truncated behind us (pos past EOF), in which case start over.
            pos = saved.get("pos", 0)
            fh.seek(min(pos, st.st_size))
        elif self.inode is None and self.start_at_end:
            # First open of a stream we do not want history from.
            fh.seek(st.st_size)
        else:
            # Rotated, or a stream we want in full: read from the top.
            fh.seek(0)

        self.fh = fh
        self.inode = st.st_ino
        self._remember()

    def _remember(self) -> None:
        if self.fh and self.inode is not None:
            self.state.offsets[self.name] = {"inode": self.inode, "pos": self.fh.tell()}

    def read_lines(self):
        """Yield complete JSON objects; partial trailing lines are held until finished."""
        if self.fh is None:
            self._open()
            if self.fh is None:
                return

        chunk = self.fh.read()
        if chunk:
            self._buf += chunk
            # A line is only safe to parse once its newline has arrived - the writer may
            # have flushed mid-line.
            while "\n" in self._buf:
                line, self._buf = self._buf.split("\n", 1)
                # Strip a stray BOM as well as whitespace: utf-8-sig only removes one at
                # the very start of the file, and a rotation can land another mid-stream.
                line = line.strip().lstrip("\ufeff").strip()
                if not line:
                    continue
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    print(f"WARN: unparseable line in {self.name}: {line[:120]!r}", flush=True)
            self._remember()

        # Rotation check: same path, different inode.
        try:
            st = os.stat(self.path)
        except FileNotFoundError:
            return
        if st.st_ino != self.inode:
            try:
                self.fh.close()
            except OSError:
                pass
            self.fh = None
            self._buf = ""
            self._open()


# --------------------------------------------------------------------------------------
# Database - read-only queries against the shard.
# --------------------------------------------------------------------------------------

def _connect():
    return pymysql.connect(
        host=DB_HOST, port=DB_PORT, user=DB_USER, password=DB_PASS,
        # A DEFAULT DATABASE IS REQUIRED. The queries below reference `character` and
        # `biota_properties_int` unqualified (only ace_auth.account is qualified), so
        # without this every one of them fails with (1046, 'No database selected') - which
        # is exactly what broke the first live /verify attempt.
        database=DB_NAME,
        charset="utf8mb4",          # the marker is a dagger; latin1 would mangle it
        cursorclass=pymysql.cursors.DictCursor,
        connect_timeout=10, read_timeout=15,
        autocommit=True,
    )

def _query(sql: str, args=()) -> list:
    """Synchronous query, always called through asyncio.to_thread."""
    conn = _connect()
    try:
        with conn.cursor() as cur:
            cur.execute(sql, args)
            return list(cur.fetchall())
    finally:
        conn.close()

# PropertyInt 25 = Level. Confirmed against the live shard, and the same field the
# honour-roll exporter uses.
_CHAR_SQL = """
SELECT c.name,
       c.account_Id,
       c.last_Login_Timestamp AS last_login,
       COALESCE((SELECT value FROM biota_properties_int
                 WHERE object_Id = c.id AND type = 25), 1) AS level
FROM `character` c
WHERE c.is_Deleted = 0 AND c.delete_Time = 0
"""

async def find_character(name: str) -> Optional[dict]:
    rows = await asyncio.to_thread(
        _query, _CHAR_SQL + " AND c.name = %s LIMIT 1", (name,))
    return rows[0] if rows else None

async def account_name_for(account_id: int) -> Optional[str]:
    rows = await asyncio.to_thread(
        _query, "SELECT accountName FROM ace_auth.account WHERE accountId = %s LIMIT 1",
        (account_id,))
    return rows[0]["accountName"] if rows else None

async def best_character(account_name: str):
    """
    The strongest character on an account, for admin overrides.

    Highest level first, then most recently played. An override still needs a character to
    speak AS, and picking it automatically means the admin does not have to know or type an
    exact character name - which for someone who rerolls constantly changes by the day.
    """
    rows = await asyncio.to_thread(_query, """
        SELECT c.name,
               COALESCE((SELECT value FROM biota_properties_int
                         WHERE object_Id = c.id AND type = 25), 1) AS level,
               c.last_Login_Timestamp AS last_login
        FROM `character` c
        JOIN ace_auth.account a ON a.accountId = c.account_Id
        WHERE c.is_Deleted = 0 AND c.delete_Time = 0 AND a.accountName = %s
        ORDER BY level DESC, last_login DESC
        LIMIT 1
    """, (account_name,))
    return rows[0] if rows else None


async def account_qualifies(account_name: str) -> tuple:
    """
    (qualifies, reason) for an ACCOUNT, judged on its best character.

    Account-level rather than character-level because that is what a Discord user owns.
    Someone whose main is active should not lose the role because they also have a level-2
    mule that has not logged in for a month.
    """
    rows = await asyncio.to_thread(_query, """
        SELECT c.name,
               c.last_Login_Timestamp AS last_login,
               COALESCE((SELECT value FROM biota_properties_int
                         WHERE object_Id = c.id AND type = 25), 1) AS level
        FROM `character` c
        JOIN ace_auth.account a ON a.accountId = c.account_Id
        WHERE c.is_Deleted = 0 AND c.delete_Time = 0 AND a.accountName = %s
    """, (account_name,))

    if not rows:
        return False, "no characters found on that account"

    cutoff = time.time() - ACTIVITY_HOURS * 3600
    if not any(r["level"] >= MIN_LEVEL for r in rows):
        best = max(r["level"] for r in rows)
        return False, f"highest character is level {best}; {MIN_LEVEL} is required"
    if not any(r["level"] >= MIN_LEVEL and (r["last_login"] or 0) >= cutoff for r in rows):
        return False, f"no qualifying character has logged in within {ACTIVITY_HOURS} hours"
    return True, "ok"


# --------------------------------------------------------------------------------------
# Discord client
# --------------------------------------------------------------------------------------

class ShadowgainBot(discord.Client):
    def __init__(self):
        # Message Content is requested ONLY when SG_READ_CHANNEL is on. Server Members is
        # never requested - a single member is fetched over REST when needed, which does not
        # require the intent. Smallest blast radius that still does the job.
        intents = discord.Intents.default()
        if READ_CHANNEL:
            intents.message_content = True
        super().__init__(intents=intents)
        self.tree = app_commands.CommandTree(self)
        self.state = State.load(STATE_PATH)
        self.chat_queue: list = []

    async def setup_hook(self):
        if GUILD_ID:
            try:
                guild = discord.Object(id=GUILD_ID)
                self.tree.copy_global_to(guild=guild)
                # Guild-scoped sync is near-instant; global commands can take an hour.
                await self.tree.sync(guild=guild)
            except discord.Forbidden:
                # Almost always "the bot has not been invited to the guild yet", or it was
                # invited without the applications.commands scope. NOT fatal: the relay and
                # the bug funnel are file-driven and work regardless. Letting this raise
                # would crash-loop the service under Restart=always and bury the reason in
                # a wall of tracebacks.
                print("WARN: could not sync slash commands - is the bot in the guild, "
                      "and was it invited with the applications.commands scope? "
                      "Relay and bug funnel will still run.", flush=True)
            except discord.HTTPException as e:
                print(f"WARN: slash command sync failed: {e}", flush=True)
        self.loop.create_task(self.tail_loop())
        self.loop.create_task(self.flush_loop())
        self.loop.create_task(self.sweep_loop())
        self.loop.create_task(self.purge_loop())

    # -- feed consumption ---------------------------------------------------------------

    async def tail_loop(self):
        await self.wait_until_ready()
        chat = JsonlTailer(os.path.join(LOG_DIR, "chatrelay.jsonl"), "chat",
                           self.state, start_at_end=True)
        events = JsonlTailer(os.path.join(LOG_DIR, "sgevents.jsonl"), "events",
                             self.state, start_at_end=False)

        while not self.is_closed():
            try:
                for rec in chat.read_lines():
                    if rec.get("type") == "chat":
                        self.chat_queue.append(rec)

                for rec in events.read_lines():
                    kind = rec.get("type")
                    if kind == "bug":
                        await self.handle_bug(rec)
                    elif kind == "verify":
                        await self.handle_verify(rec)

                self.state.save(STATE_PATH)
            except Exception as e:                       # never let the loop die
                print(f"ERROR in tail_loop: {e}", flush=True)

            await asyncio.sleep(1.0)

    async def flush_loop(self):
        """Post batched chat. Batching keeps a busy General from hitting Discord's limits."""
        await self.wait_until_ready()
        while not self.is_closed():
            await asyncio.sleep(BATCH_SECONDS)
            if not self.chat_queue:
                continue

            batch, self.chat_queue = self.chat_queue, []
            channel = self.get_channel(RELAY_CHANNEL_ID)
            if channel is None:
                continue

            lines, size = [], 0
            for rec in batch:
                line = self.format_chat(rec)
                if size + len(line) > MAX_BATCH_CHARS and lines:
                    await self.send_plain(channel, "\n".join(lines))
                    lines, size = [], 0
                lines.append(line)
                size += len(line) + 1
            if lines:
                await self.send_plain(channel, "\n".join(lines))

    @staticmethod
    def format_chat(rec: dict) -> str:
        name = discord.utils.escape_markdown(str(rec.get("name", "?")))
        text = discord.utils.escape_markdown(str(rec.get("message", "")))
        return f"`[{rec.get('channel','?')}]` **{name}:** {text}"

    @staticmethod
    async def send_plain(channel, content: str):
        try:
            # allowed_mentions=none is the load-bearing part: without it, a player typing
            # "@everyone" in game would ping the whole Discord server. escape_markdown
            # handles formatting; only this stops the mention actually resolving.
            await channel.send(content, allowed_mentions=discord.AllowedMentions.none())
        except discord.HTTPException as e:
            print(f"WARN: could not post to {channel}: {e}", flush=True)

    async def handle_bug(self, rec: dict):
        channel = self.get_channel(BUGS_CHANNEL_ID)
        if channel is None:
            return
        embed = discord.Embed(
            title="Bug report",
            description=str(rec.get("text", ""))[:2000],
            colour=0xC9A227,
            timestamp=discord.utils.utcnow(),
        )
        embed.add_field(name="Character", value=f"{rec.get('character','?')} (level {rec.get('level','?')})")
        embed.add_field(name="Location", value=str(rec.get("location", "unknown"))[:1024], inline=False)
        try:
            await channel.send(embed=embed, allowed_mentions=discord.AllowedMentions.none())
        except discord.Forbidden:
            # Almost always "Embed Links not granted in this channel". A bug report is too
            # valuable to drop over formatting, so fall back to plain text rather than lose
            # it. Discovered the hard way: send_messages was granted but embed_links was not,
            # and the failure was invisible from Discord's side.
            text = "\n".join([
                f"**Bug report** - {rec.get('character','?')} (level {rec.get('level','?')})",
                str(rec.get("text", ""))[:1500],
                f"_location: {str(rec.get('location','unknown'))[:200]}_",
            ])
            try:
                await channel.send(text, allowed_mentions=discord.AllowedMentions.none())
                print("WARN: posted bug as plain text - Embed Links is not granted", flush=True)
            except discord.HTTPException as e:
                print(f"WARN: could not post bug at all: {e}", flush=True)
        except discord.HTTPException as e:
            print(f"WARN: could not post bug: {e}", flush=True)

    async def handle_verify(self, rec: dict):
        """A player typed @verify <code> in game. Match it to an outstanding /link."""
        code = str(rec.get("code", "")).upper()
        entry = self.state.pending.get(code)
        if not entry:
            return                                  # stale, mistyped, or not ours
        if entry.get("expires", 0) < time.time():
            self.state.pending.pop(code, None)
            return

        discord_id = int(entry["discord_id"])
        account = rec.get("account")
        character = rec.get("character", "?")

        # Decide FIRST, consume the code AFTER. The original order popped the code up front,
        # so when account_qualifies threw (the "No database selected" bug) the player's code
        # was destroyed by a fault that had nothing to do with them - they had to run /link
        # again to retry something that was never their failure.
        try:
            ok, reason = await account_qualifies(account) if account else (False, "unknown account")
        except Exception as e:
            print(f"WARN: verify lookup failed for {character}, leaving code valid: {e}", flush=True)
            return

        self.state.pending.pop(code, None)

        guild = self.get_guild(GUILD_ID)
        member = guild.get_member(discord_id) if guild else None
        if member is None:
            try:
                member = await guild.fetch_member(discord_id)
            except (discord.HTTPException, AttributeError):
                member = None

        if member is None:
            print(f"WARN: verified {character} but Discord member {discord_id} not found", flush=True)
            return

        if not ok:
            await self.dm(member, f"Verification failed for **{character}**: {reason}.")
            return

        # Store the character too, not just the account. 033 needs a name to speak AS,
        # and this is the one moment we know which character the person proved they own.
        # The name here comes from the server's own EmitVerify, so it already carries the
        # 023 dagger for a hard-lane character.
        self.state.links[str(discord_id)] = {"account": account, "character": character}
        self.state.save(STATE_PATH)

        role = guild.get_role(VERIFIED_ROLE_ID)
        if role and role not in member.roles:
            try:
                await member.add_roles(role, reason=f"Shadowgain verify: {character}")
            except discord.HTTPException as e:
                print(f"WARN: could not grant role: {e}", flush=True)

        await self.dm(member, f"Verified as **{character}**. Welcome to the Shadowgain channels.")

    @staticmethod
    def write_inbound(account: str, character: str, discord_name: str, text: str) -> bool:
        """
        Append one line to the inbound feed. Shared by /say and by channel reading, so both
        paths emit byte-identical records and there is exactly one place that can get the
        escaping wrong.

        The SERVER decides whether any of this is actually spoken - it re-checks that the
        character belongs to the account, that they are not gagged, the rate limit and the
        length cap. Nothing here is a security control.
        """
        # Collapse whitespace: the feed is line-delimited JSON, so a raw newline in the
        # payload would split one message into two records, the second of them malformed.
        text = " ".join(text.split())
        if not text:
            return False

        rec = {
            "type": "say",
            "ts": discord.utils.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
            "account": account,
            "character": character,
            "discord": discord_name,
            "message": text,
        }
        try:
            with open(INBOUND_PATH, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec, ensure_ascii=False) + "\n")
                f.flush()
            return True
        except OSError as e:
            print(f"WARN: could not write inbound feed: {e}", flush=True)
            return False

    async def on_message(self, message: discord.Message):
        """
        Relay-channel messages -> game, when SG_READ_CHANNEL is on.

        Typing in the channel is what people actually do - Chris did exactly that within
        minutes of the channel existing - so requiring /say for every line is a worse
        experience than it looks on paper. /say still works; this is the ergonomic path.

        IGNORING OUR OWN MESSAGES IS LOAD-BEARING. The bot posts game chat INTO this
        channel. Without the bot/webhook check, every relayed line would be read straight
        back, sent to the game, and relayed out again - an infinite loop that would flood
        both sides within seconds.
        """
        if not READ_CHANNEL:
            return
        if message.author.bot or message.webhook_id is not None:
            return
        if message.channel.id != RELAY_CHANNEL_ID:
            return
        if not message.content:
            return                      # attachments/embeds only - nothing to say in game

        link = self.state.links.get(str(message.author.id))
        if not link:
            # Unlinked: react rather than reply, so an unlinked user cannot make the bot
            # spam the channel by typing repeatedly.
            try:
                await message.add_reaction("🚫")
            except discord.HTTPException:
                pass
            return

        account = link["account"] if isinstance(link, dict) else link
        character = link.get("character") if isinstance(link, dict) else None
        if not character:
            return

        self.write_inbound(account, character, str(message.author), message.content)

    @staticmethod
    async def dm(member, text: str):
        try:
            await member.send(text)
        except discord.HTTPException as e:
            # Closed DMs are common and not worth failing over - but they must not be
            # SILENT. A swallowed DM looks exactly like "the bot did nothing", which is a
            # miserable thing to debug from the outside. The role grant is the real signal;
            # this line is how we can tell the difference afterwards.
            print(f"WARN: could not DM {member} (DMs closed?): {e}", flush=True)

    # -- periodic re-check ---------------------------------------------------------------

    async def purge_loop(self):
        """
        Rolling retention on the relay channel: chat older than RETENTION_HOURS is deleted.

        The point is privacy without blindness. Denying Read Message History made the channel
        blank on every load, which is a poor experience; ageing messages out instead means
        people can see recent conversation while nothing accumulates into a searchable record
        of everything ever said in game.

        RELAY CHANNEL ONLY. #bugs keeps its history on purpose so reporters can read past
        reports and self-dedupe.

        DISCORD LIMIT: bulk delete only works on messages younger than 14 days; anything older
        must be removed one at a time. `after=` keeps the bulk pass inside that window, and a
        small capped pass handles genuine stragglers - which can only exist if the bot was down
        for two weeks, since it otherwise sweeps hourly.
        """
        await self.wait_until_ready()

        # Short first delay rather than a full hour, so a deploy shows its first result
        # quickly instead of leaving an hour of silence to interpret.
        await asyncio.sleep(120)

        while not self.is_closed():
            try:
                if RETENTION_HOURS > 0:
                    channel = self.get_channel(RELAY_CHANNEL_ID)
                    if channel is not None:
                        now = discord.utils.utcnow()
                        cutoff = now - datetime.timedelta(hours=RETENTION_HOURS)
                        bulk_floor = now - datetime.timedelta(days=13, hours=12)

                        # Pinned messages survive: if someone deliberately pinned it, ageing
                        # it out would be the opposite of what they asked for.
                        deleted = await channel.purge(
                            limit=None, before=cutoff, after=bulk_floor,
                            check=lambda m: not m.pinned,
                            reason=f"Shadowgain retention: older than {RETENTION_HOURS}h")

                        stragglers = 0
                        async for msg in channel.history(limit=50, before=bulk_floor):
                            if msg.pinned:
                                continue
                            try:
                                await msg.delete()
                                stragglers += 1
                            except discord.HTTPException:
                                break        # rate limited or gone; try again next sweep

                        if deleted or stragglers:
                            print(f"retention: purged {len(deleted)} message(s) older than "
                                  f"{RETENTION_HOURS}h"
                                  + (f" (+{stragglers} over 14 days)" if stragglers else ""),
                                  flush=True)
            except discord.Forbidden:
                print("WARN: retention needs Manage Messages + Read Message History "
                      "in the relay channel", flush=True)
            except Exception as e:
                print(f"ERROR in purge_loop: {e}", flush=True)

            await asyncio.sleep(3600)

    async def sweep_loop(self):
        """
        Re-check every linked account and revoke the role when it goes quiet.

        This, not the one-time /link, is what "active players only" actually means: without
        it the role is granted once and kept forever.
        """
        await self.wait_until_ready()
        while not self.is_closed():
            await asyncio.sleep(SWEEP_HOURS * 3600)
            try:
                guild = self.get_guild(GUILD_ID)
                role = guild.get_role(VERIFIED_ROLE_ID) if guild else None
                if not role:
                    continue

                for discord_id, link in list(self.state.links.items()):
                    # Links were plain account strings before 033; tolerate both shapes
                    # so an existing state file does not have to be thrown away.
                    account = link["account"] if isinstance(link, dict) else link

                    # An admin override outranks the activity gate, and must survive the
                    # sweep - otherwise the override silently undoes itself within 24 hours
                    # and looks like a bug rather than a policy.
                    if isinstance(link, dict) and link.get("exempt"):
                        continue

                    ok, reason = await account_qualifies(account)
                    member = guild.get_member(int(discord_id))
                    if member is None:
                        continue
                    has = role in member.roles
                    if ok and not has:
                        await member.add_roles(role, reason="Shadowgain sweep: active again")
                    elif not ok and has:
                        await member.remove_roles(role, reason=f"Shadowgain sweep: {reason}")
                        await self.dm(member, f"Your Shadowgain access has lapsed: {reason}. "
                                              f"Log in and run /link again to restore it.")
            except Exception as e:
                print(f"ERROR in sweep_loop: {e}", flush=True)


client = ShadowgainBot()


@client.tree.command(name="link", description="Link your Shadowgain character to Discord.")
async def link(interaction: discord.Interaction):
    code = "".join(random.choices(string.ascii_uppercase + string.digits, k=6))
    client.state.pending[code] = {
        "discord_id": interaction.user.id,
        "expires": time.time() + CODE_TTL_SECONDS,
    }
    client.state.save(STATE_PATH)

    minutes = CODE_TTL_SECONDS // 60
    # Ephemeral: the code is single-use and short-lived, but there is no reason to put it
    # in a public channel where it sits in the archive forever.
    await interaction.response.send_message(
        f"Your code is **{code}**\n\n"
        f"Log in and type `@verify {code}` in game, on the character you want to link.\n"
        f"The code expires in {minutes} minutes.\n\n"
        f"Access requires a character of level {MIN_LEVEL}+ that has played in the "
        f"last {ACTIVITY_HOURS} hours.",
        ephemeral=True,
    )


@client.tree.command(name="bug", description="Report a Shadowgain bug.")
@app_commands.describe(summary="One line: what went wrong",
                       detail="What you were doing, and what you expected instead")
async def bug(interaction: discord.Interaction, summary: str, detail: str = ""):
    channel = client.get_channel(BUGS_CHANNEL_ID)
    if channel is None:
        await interaction.response.send_message("The bug channel is not configured.", ephemeral=True)
        return

    link = client.state.links.get(str(interaction.user.id))
    account = link["account"] if isinstance(link, dict) else link
    embed = discord.Embed(title="Bug report", description=summary[:2000],
                          colour=0x5865F2, timestamp=discord.utils.utcnow())
    if detail:
        embed.add_field(name="Detail", value=detail[:1024], inline=False)
    embed.add_field(name="Reported by", value=f"{interaction.user.mention}"
                                              f"{f' (account `{account}`)' if account else ''}",
                    inline=False)
    embed.set_footer(text="filed from Discord")

    await channel.send(embed=embed, allowed_mentions=discord.AllowedMentions.none())
    await interaction.response.send_message("Thanks - your report has been filed.", ephemeral=True)


@client.tree.command(name="say", description="Speak into the game's General chat.")
@app_commands.describe(message="What to say in game")
async def say(interaction: discord.Interaction, message: str):
    """
    Discord -> game (033). Writes a line to the inbound feed; the SERVER decides whether it
    is actually spoken.

    Everything meaningful is validated server-side - that the character belongs to the
    account, that they are not gagged, the rate limit, the length cap, and that General is
    the only destination. None of that is duplicated here, because a check the sender's own
    process performs is not a check. What this does is refuse the obviously-pointless cases
    early so the user gets a useful reply instead of silence.
    """
    link = client.state.links.get(str(interaction.user.id))
    if not link:
        await interaction.response.send_message(
            "You need to link a character first - run `/link`.", ephemeral=True)
        return

    account = link["account"] if isinstance(link, dict) else link
    character = link.get("character") if isinstance(link, dict) else None
    if not character:
        # Linked before 033 started recording the character name.
        await interaction.response.send_message(
            "Your link predates this feature - please run `/link` again to refresh it.",
            ephemeral=True)
        return

    text = message.strip()
    if not text:
        await interaction.response.send_message("Nothing to say.", ephemeral=True)
        return

    if not client.write_inbound(account, character, str(interaction.user), text):
        await interaction.response.send_message(
            "Could not reach the game server right now.", ephemeral=True)
        return

    # Ephemeral: the line itself shows up in the relay channel when the server accepts it and
    # broadcasts it back out, so confirming publicly here would double-post every message.
    await interaction.response.send_message(f"Sent to General: {text}", ephemeral=True)


def _is_admin(interaction: discord.Interaction) -> bool:
    """
    Manage Roles rather than Administrator.

    Anyone who can hand out the Verified Player role by hand can already do what /override
    does; requiring full Administrator would gate a lesser power behind a greater one.
    """
    perms = getattr(interaction.user, "guild_permissions", None)
    return bool(perms and (perms.manage_roles or perms.administrator))


@client.tree.command(name="override",
                     description="Admin: link a Discord user to a game account, bypassing the gate.")
@app_commands.describe(member="The Discord user to link",
                       account="Their game ACCOUNT name (not a character name)")
async def override(interaction: discord.Interaction, member: discord.Member, account: str):
    """
    The escape hatch for people the activity gate judges wrongly.

    Greylock is the motivating case: he rerolls constantly, so he never holds a level 10
    character for 72 hours and the gate locks him out forever - even though he is the most
    active person on the shard and the experiment is his idea. A rule that excludes exactly
    the player it should most include needs an override, not a lower threshold.

    The link is marked EXEMPT so the daily sweep leaves it alone. Without that the override
    would quietly undo itself within 24 hours, which would look like a bug rather than a
    policy.
    """
    if not _is_admin(interaction):
        await interaction.response.send_message(
            "That command is for server admins.", ephemeral=True)
        return

    await interaction.response.defer(ephemeral=True)

    account = account.strip()
    try:
        best = await best_character(account)
    except Exception as e:
        await interaction.followup.send(f"Could not reach the game database: {e}", ephemeral=True)
        return

    # Verify the account EXISTS before writing a link. A typo would otherwise create a link
    # that grants the role but can never speak, and the failure would surface much later as
    # "/say does nothing" - far from its cause.
    if not best:
        await interaction.followup.send(
            f"No account named `{account}` with any character was found. "
            f"Note this wants the ACCOUNT name, not a character name.", ephemeral=True)
        return

    client.state.links[str(member.id)] = {
        "account": account,
        "character": best["name"],
        "exempt": True,
        "by": str(interaction.user),
    }
    client.state.save(STATE_PATH)

    guild = client.get_guild(GUILD_ID)
    role = guild.get_role(VERIFIED_ROLE_ID) if guild else None
    granted = False
    if role and role not in member.roles:
        try:
            await member.add_roles(role, reason=f"Shadowgain override by {interaction.user}")
            granted = True
        except discord.HTTPException as e:
            await interaction.followup.send(f"Linked, but could not grant the role: {e}",
                                            ephemeral=True)
            return

    await interaction.followup.send(
        "\n".join([
            f"{member.mention} linked to account `{account}` "
            f"(speaking as **{best['name']}**, level {best['level']}).",
            f"Exempt from the activity sweep. "
            f"Role {'granted' if granted else 'already held'}.",
        ]),
        ephemeral=True)

    await client.dm(member, f"An admin linked you to `{account}`. You can now chat with the "
                            f"game from the Shadowgain channels.")


@client.tree.command(name="unlink", description="Admin: remove a link and the Verified Player role.")
@app_commands.describe(member="The Discord user to unlink")
async def unlink(interaction: discord.Interaction, member: discord.Member):
    """
    The counterpart to /override.

    Without it an exempt link is permanent and unremovable from Discord - stripping the role
    by hand would not help, because the link stays and the person can still speak into the
    game via /say.
    """
    if not _is_admin(interaction):
        await interaction.response.send_message(
            "That command is for server admins.", ephemeral=True)
        return

    existed = client.state.links.pop(str(member.id), None)
    client.state.save(STATE_PATH)

    guild = client.get_guild(GUILD_ID)
    role = guild.get_role(VERIFIED_ROLE_ID) if guild else None
    if role and role in member.roles:
        try:
            await member.remove_roles(role, reason=f"Shadowgain unlink by {interaction.user}")
        except discord.HTTPException:
            pass

    await interaction.response.send_message(
        f"{member.mention} unlinked." if existed else f"{member.mention} had no link.",
        ephemeral=True)


@client.event
async def on_ready():
    print(f"Shadowgain bot online as {client.user} "
          f"(relay={RELAY_CHANNEL_ID}, bugs={BUGS_CHANNEL_ID})", flush=True)


if __name__ == "__main__":
    client.run(TOKEN, log_handler=None)
