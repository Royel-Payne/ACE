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

Inbound is typing in the relay channel, which needs the Message Content intent
(SG_READ_CHANNEL). A /say slash command existed as a no-intent fallback and was removed
in 034 once channel-typing became the desired UX - keeping it would have left a second
inbound surface that the Verified Player role does not gate.

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
# 045: read-only #audit. Not covered by the retention purge - that targets RELAY_CHANNEL_ID
# alone, so the audit trail is exempt by construction rather than by a special case.
AUDIT_CHANNEL_ID = _int("DISCORD_AUDIT_CHANNEL_ID")
VERIFIED_ROLE_ID = _int("DISCORD_VERIFIED_ROLE_ID")
# The gold role. Earned by reaching the level ceiling on the HARD lane and never given any
# other way - see account_is_ascendant() for why the two extra conditions matter.
ASCENDANT_ROLE_ID = _int("DISCORD_ASCENDANT_ROLE_ID")
ASCENDANT_LEVEL   = _int("SG_ASCENDANT_LEVEL", 275)
# Sanity floor, not a gate. The real conditions are level + hard lane; this exists only to
# catch a BOOSTED character. The two characters sitting at 275 and 999 today have 1.5 and 1.9
# hours played - that is the signature. A genuine climb to the ceiling on this server is
# hundreds of hours, so this can never fail a real player.
ASCENDANT_MIN_HOURS = _int("SG_ASCENDANT_MIN_HOURS", 100)

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

# SERVER MEMBERS privileged intent. Same portal-first rule as above, and the same crash-loop
# risk if it is requested while switched off - hence the flag rather than a hard-coded True.
#
# The bot does not NEED it: every runtime lookup goes through resolve_member(), which falls
# back to a single fetch_member REST call that works without any intent. What it buys is
# (a) a populated member cache, so the daily sweep resolves from memory instead of one HTTP
# call per linked account, and (b) bulk enumeration - discord.py refuses fetch_members()
# client-side unless this is set, which is what makes "who actually holds this role?" audits
# possible at all.
#
# Default OFF so a fresh deployment cannot crash-loop against a portal toggle nobody set.
MEMBERS_INTENT = os.environ.get("SG_MEMBERS_INTENT", "0").strip().lower() in ("1", "true", "yes")

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
    # account name -> {"age": int, "xp": int, "t": float}
    #
    # The baseline for the counter-based activity check. Age and TotalExperience are
    # COUNTERS, not timestamps, so "did this move?" needs something to compare against -
    # and the previous sweep is exactly that. See account_qualifies().
    activity: dict = field(default_factory=dict)
    # Unix time of the last COMPLETED sweep. Persisted so the schedule survives a restart -
    # see sweep_loop() for why sleeping from process start silently disabled it.
    last_sweep: float = 0.0

    @classmethod
    def load(cls, path: str) -> "State":
        try:
            with open(path, "r", encoding="utf-8") as f:
                raw = json.load(f)
            return cls(
                offsets=raw.get("offsets", {}),
                pending=raw.get("pending", {}),
                links=raw.get("links", {}),
                activity=raw.get("activity", {}),
                last_sweep=raw.get("last_sweep", 0.0),
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
                json.dump({"offsets": self.offsets, "pending": self.pending,
                           "links": self.links, "activity": self.activity,
                           "last_sweep": self.last_sweep},
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


async def account_is_ascendant(account_name: str) -> bool:
    """
    Has this account earned gold?

    Three conditions, and the last two are the point:

      1. a character at or past ASCENDANT_LEVEL (275, retail's ceiling);
      2. on the HARD lane - no ShadowgainForfeitedMarker (PropertyBool 9102). Gold means
         "earned the long road". A fast-lane character reaching the cap has not, which is
         exactly why the honour roll refuses them too;
      3. NOT on a staff account (accessLevel < 4) - the same filter the honour roll uses,
         because hand-boosted test characters sit at 275 and 999 right now and would
         otherwise claim it on the first sweep;
      4. with real playtime behind it (Age >= ASCENDANT_MIN_HOURS). Condition 3 only catches
         boosts on STAFF accounts - a character boosted on a Player account would pass it.
         Playtime is what actually distinguishes earned from granted: the two boosted
         characters today show 1.5 and 1.9 hours at levels 275 and 999.

    Judged per ACCOUNT, like every other gate here.
    """
    rows = await asyncio.to_thread(_query, """
        SELECT 1
        FROM `character` c
        JOIN ace_auth.account a ON a.accountId = c.account_Id
        WHERE c.is_Deleted = 0 AND c.delete_Time = 0
          AND a.accountName = %s
          AND a.accessLevel < 4
          AND COALESCE((SELECT value FROM biota_properties_int
                        WHERE object_Id = c.id AND type = 25), 1) >= %s
          AND COALESCE((SELECT value FROM biota_properties_int
                        WHERE object_Id = c.id AND type = 125), 0) >= %s
          AND NOT EXISTS (SELECT 1 FROM biota_properties_bool
                          WHERE object_Id = c.id AND type = 9102 AND value = 1)
        LIMIT 1
    """, (account_name, ASCENDANT_LEVEL, ASCENDANT_MIN_HOURS * 3600))
    return bool(rows)


async def account_qualifies(account_name: str, state: "State" = None) -> tuple:
    """
    (qualifies, reason) for an ACCOUNT, judged on its best character.

    Account-level rather than character-level because that is what a Discord user owns.
    Someone whose main is active should not lose the role because they also have a level-2
    mule that has not logged in for a month.

    TWO ways to pass the activity half, because last_Login_Timestamp alone is wrong for
    the people most likely to be playing:

      1. logged in within ACTIVITY_HOURS, or
      2. the account's PLAY COUNTERS moved since we last looked.

    (1) alone silently punishes anyone who stays connected. last_Login_Timestamp is written
    ONCE, at login (Player_Networking.cs:33), and never refreshed - so a VTank user parked
    in world for a week has a week-old timestamp while actively playing, and the sweep would
    revoke their role mid-session. That is the opposite of what the gate is for.

    (2) fixes it because ACE auto-saves a connected player every `player_save_interval`
    seconds - a live dial, 60 on this server, not the compiled default of 300 - which
    rewrites these rows while the player is still online:

        Age             PropertyInt   125   - total seconds CONNECTED
        TotalExperience PropertyInt64 1     - total XP EARNED

    Age is the one we gate on. It answers "is this account still being used?", which is the
    question the role actually asks - it grants chat access, not merit.

    OPTIONAL PATH, deliberately left wired up but unused (Chris, 2026-08-08 - "grind harder
    fool"): swap `age_delta` for `xp_delta` below and the gate becomes "still PROGRESSING"
    rather than "still connected". Someone parked online overnight gaining nothing would
    fail it. That turns the Discord role into a second progression gate, which is a policy
    change and not obviously wanted - so it stays a one-word switch rather than the default.
    Both deltas are already queried and stored, so flipping it needs no new plumbing.
    """
    rows = await asyncio.to_thread(_query, """
        SELECT c.name,
               c.last_Login_Timestamp AS last_login,
               COALESCE((SELECT value FROM biota_properties_int
                         WHERE object_Id = c.id AND type = 25), 1) AS level,
               COALESCE((SELECT value FROM biota_properties_int
                         WHERE object_Id = c.id AND type = 125), 0) AS age,
               COALESCE((SELECT value FROM biota_properties_int64
                         WHERE object_Id = c.id AND type = 1), 0) AS xp
        FROM `character` c
        JOIN ace_auth.account a ON a.accountId = c.account_Id
        WHERE c.is_Deleted = 0 AND c.delete_Time = 0 AND a.accountName = %s
    """, (account_name,))

    if not rows:
        return False, "no characters found on that account"

    if not any(r["level"] >= MIN_LEVEL for r in rows):
        best = max(r["level"] for r in rows)
        return False, f"highest character is level {best}; {MIN_LEVEL} is required"

    # Summed across the account, not read off one character: alt-hopping is still playing,
    # and the gate is per account everywhere else too.
    age_now = sum(r["age"] or 0 for r in rows)
    xp_now = sum(r["xp"] or 0 for r in rows)

    cutoff = time.time() - ACTIVITY_HOURS * 3600
    logged_in = any(r["level"] >= MIN_LEVEL and (r["last_login"] or 0) >= cutoff for r in rows)

    age_delta = None
    if state is not None:
        prev = state.activity.get(account_name)
        # Record BEFORE deciding, so the next call has a baseline no matter how this one
        # goes. A revoked account that starts playing again must be able to earn it back.
        state.activity[account_name] = {"age": age_now, "xp": xp_now, "t": time.time()}
        if prev:
            age_delta = age_now - (prev.get("age") or 0)

    if logged_in:
        return True, "ok"
    if age_delta and age_delta > 0:
        return True, "ok"
    if age_delta is None:
        # First look at this account since the counter check shipped. Refusing here would
        # revoke people for our lack of a baseline rather than for their inactivity, so
        # fall through to the old rule and let the NEXT sweep use the snapshot just stored.
        return False, f"no qualifying character has logged in within {ACTIVITY_HOURS} hours"

    return False, (f"no qualifying character has logged in within {ACTIVITY_HOURS} hours, "
                   f"and no play time was recorded since the last check")


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
        if MEMBERS_INTENT:
            intents.members = True
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
        # 045: start_at_end=False like events, not like chat. An audit line written while the
        # bot was down is exactly the line you most want to see when it comes back up.
        audit = JsonlTailer(os.path.join(LOG_DIR, "sgaudit.jsonl"), "audit",
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

                for rec in audit.read_lines():
                    await self.handle_audit(rec)

                self.state.save(STATE_PATH)
            except Exception as e:                       # never let the loop die
                print(f"ERROR in tail_loop: {e}", flush=True)

            await asyncio.sleep(1.0)

    async def handle_audit(self, rec: dict):
        """
        Mirror one audit line into #audit.

        Plain text, not an embed. #audit is a record meant to be read in bulk and searched with
        Discord's own search - embeds are taller, and their contents match search inconsistently.
        Deliberately no allowed_mentions: a command argument containing @everyone must never ping.
        """
        if not AUDIT_CHANNEL_ID:
            return

        channel = self.get_channel(AUDIT_CHANNEL_ID)
        if channel is None:
            return

        kind = rec.get("type")

        if kind == "dial":
            line = (f"`{rec.get('t','?')}` **{rec.get('who','?')}** changed "
                    f"`{rec.get('dial','?')}`: `{rec.get('before','?')}` -> `{rec.get('after','?')}`")
        elif kind == "command":
            who = rec.get("character") or rec.get("account") or "?"
            # Show the account too when they differ - the character is who you SEE in game, the
            # account is who is actually responsible, and one account can hold many characters.
            acct = rec.get("account")
            if acct and rec.get("character") and acct != rec.get("character"):
                who = f"{rec.get('character')} ({acct})"
            args = rec.get("args") or ""
            sudo = " *(sudo)*" if rec.get("sudo") else ""
            line = (f"`{rec.get('t','?')}` **{who}** `[{rec.get('access','?')}]` "
                    f"ran `@{rec.get('command','?')} {args}`".rstrip() + f"{sudo}")
        else:
            return

        try:
            await channel.send(line[:1900], allowed_mentions=discord.AllowedMentions.none())
        except discord.HTTPException as e:
            print(f"WARN: could not post audit line: {e}", flush=True)

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
            ok, reason = await account_qualifies(account, self.state) if account else (False, "unknown account")
        except Exception as e:
            print(f"WARN: verify lookup failed for {character}, leaving code valid: {e}", flush=True)
            return

        self.state.pending.pop(code, None)

        guild = self.get_guild(GUILD_ID)
        member = await self.resolve_member(guild, discord_id)

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
    async def resolve_member(guild, discord_id):
        """
        Find a guild member, cache first and API second.

        get_member() alone is a CACHE read, and without the privileged Server Members
        intent that cache is populated only opportunistically - from message events and
        interactions. Someone who has not spoken recently simply is not in it. Treating
        that miss as "this person left the server" is wrong and, in the sweep, silent:
        the loop would skip them and their role would never be re-checked.

        fetch_member() asks Discord directly and needs no intent. It costs an HTTP call,
        which is why it is the fallback rather than the first move.

        Returns None only when the member is genuinely not in the guild.
        """
        if guild is None:
            return None

        member = guild.get_member(int(discord_id))
        if member is not None:
            return member

        try:
            return await guild.fetch_member(int(discord_id))
        except (discord.HTTPException, AttributeError, ValueError):
            return None

    @staticmethod
    def write_inbound(account: str, character: str, discord_name: str, text: str) -> bool:
        """
        Append one line to the inbound feed.

        Kept as a separate method rather than inlined into on_message: it was shared with
        the /say command until 034 removed it, and it remains the single place where the
        record shape and escaping are decided.

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

    async def on_member_update(self, before: discord.Member, after: discord.Member):
        """
        Revoke a Verified Player grant the bot did not make.

        Verified Player is the key to #chat, #bugs and #audit, and it is supposed to be
        EARNED - /link plus /verify, level 10, active within ACTIVITY_HOURS - and taken back
        by the daily sweep when someone lapses. But anyone with Manage Roles whose top role
        outranks it can simply hand it out, and a hand-granted role has no entry in the link
        table, so the sweep would never revoke it. Permanent access, outside the gate.

        Discord's role hierarchy cannot fix this: positioning Verified Player above the mod
        role stops him granting it, but he HOLDS that role, so it becomes his highest and he
        can then assign the mod role itself - strictly worse. Enforcement has to live here.

        Checked at grant time rather than only on the daily sweep, so the window where
        unearned access exists is seconds instead of a day.

        The bot's own grants are safe: handle_verify and /override both write the link to
        state BEFORE calling add_roles, so by the time this fires there is a link to find.
        """
        if not VERIFIED_ROLE_ID:
            return

        role = after.guild.get_role(VERIFIED_ROLE_ID)
        if role is None:
            return

        # Only react to a fresh grant, not to every nickname or presence change.
        if role in before.roles or role not in after.roles:
            return

        if self.state.links.get(str(after.id)):
            return                                  # earned it, or an admin /override

        try:
            await after.remove_roles(role, reason="Shadowgain: Verified Player is granted by /verify only")
            print(f"revoked unearned Verified Player from {after}", flush=True)

            # Surface it rather than silently undoing someone's action - if a mod is trying
            # to help a player, they need to know why it did not stick.
            ch = self.get_channel(AUDIT_CHANNEL_ID) if AUDIT_CHANNEL_ID else None
            if ch is not None:
                stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
                await ch.send(
                    f"`{stamp}` **bot** removed `Verified Player` from **{after}** — "
                    f"granted outside `/verify`, so it was not earned. Have them run `/link`.",
                    allowed_mentions=discord.AllowedMentions.none())
        except discord.HTTPException as e:
            print(f"WARN: could not revoke unearned Verified Player from {after}: {e}", flush=True)

    async def on_message(self, message: discord.Message):
        """
        Relay-channel messages -> game, when SG_READ_CHANNEL is on.

        Typing in the channel is what people actually do - Chris did exactly that within
        minutes of the channel existing - so requiring a slash command for every line was a
        worse experience than it looked on paper. Since 034 this is the ONLY inbound path,
        which also means the Verified Player role's send permission is the sole gate: lose
        the role to the activity sweep and you lose the ability to speak into the game.

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

        SCHEDULED FROM PERSISTED STATE, not from process start. The original loop slept
        SWEEP_HOURS and only then did its first pass, so every bot restart pushed the next
        sweep a full day out - and during active development the bot is redeployed more
        often than daily, which means the sweep had almost certainly never run at all. A
        deploy cadence quietly cancelling a maintenance task is the kind of bug that only
        shows up as "why does nobody ever lose the role", long after the cause.

        Tracking `last_sweep` in the state file makes restarts irrelevant: the schedule
        belongs to the deployment, not to the process. A first run with no recorded sweep
        fires almost immediately, which also seeds the activity baselines rather than
        leaving them empty for a day.
        """
        await self.wait_until_ready()
        await asyncio.sleep(120)   # let the guild/member caches settle before judging anyone

        while not self.is_closed():
            due = (self.state.last_sweep or 0) + SWEEP_HOURS * 3600
            wait = due - time.time()
            if wait > 0:
                # Capped so a clock change or a long-stale state file cannot park the loop
                # for days, and so shutdown is never more than 15 minutes away.
                await asyncio.sleep(min(wait, 900))
                continue
            try:
                guild = self.get_guild(GUILD_ID)
                g_owner_id = guild.owner_id if guild else None
                role = guild.get_role(VERIFIED_ROLE_ID) if guild else None
                if not role:
                    # Do NOT stamp last_sweep here - nothing was checked. Stamping would
                    # turn a transient lookup failure into a skipped day.
                    await asyncio.sleep(300)
                    continue

                print(f"sweep: checking {len(self.state.links)} link(s)", flush=True)

                for discord_id, link in list(self.state.links.items()):
                    # Links were plain account strings before 033; tolerate both shapes
                    # so an existing state file does not have to be thrown away.
                    account = link["account"] if isinstance(link, dict) else link

                    # An admin override outranks the activity gate, and must survive the
                    # sweep - otherwise the override silently undoes itself within 24 hours
                    # and looks like a bug rather than a policy.
                    if isinstance(link, dict) and link.get("exempt"):
                        continue

                    ok, reason = await account_qualifies(account, self.state)

                    # Cache-then-API. A bare get_member() here made the sweep unreliable:
                    # anyone not in the local cache was skipped without a word, so the
                    # role was never revoked and "active players only" quietly stopped
                    # meaning anything. A None now really does mean "left the server".
                    member = await self.resolve_member(guild, discord_id)
                    if member is None:
                        print(f"sweep: {discord_id} is no longer in the guild, skipping", flush=True)
                        continue
                    has = role in member.roles
                    if ok and not has:
                        await member.add_roles(role, reason="Shadowgain sweep: active again")
                    elif not ok and has:
                        await member.remove_roles(role, reason=f"Shadowgain sweep: {reason}")
                        await self.dm(member, f"Your Shadowgain access has lapsed: {reason}. "
                                              f"Log in and run /link again to restore it.")

                    # Gold. Granted once and NEVER revoked - it is a ratchet like the
                    # dagger itself: an achievement, not a status that can lapse. That is
                    # also why it sits outside the qualifies/exempt logic above.
                    if ASCENDANT_ROLE_ID:
                        gold = guild.get_role(ASCENDANT_ROLE_ID)
                        if gold is not None and gold not in member.roles:
                            try:
                                if await account_is_ascendant(account):
                                    await member.add_roles(gold, reason="Shadowgain: reached the ceiling on the hard road")
                                    print(f"ASCENDANT: {member} ({account})", flush=True)
                                    ch = self.get_channel(RELAY_CHANNEL_ID)
                                    if ch is not None:
                                        await ch.send(
                                            f"**{member.display_name}** reached level {ASCENDANT_LEVEL} "
                                            f"on the hard road. That is the whole climb.",
                                            allowed_mentions=discord.AllowedMentions.none())
                            except Exception as e:
                                print(f"WARN: ascendant check failed for {account}: {e}", flush=True)

                # Backstop for on_member_update: catch any Verified Player grant made while
                # the bot was down, when the listener could not fire.
                linked_ids = set(self.state.links.keys())
                for member in list(role.members):
                    # Never strip the server owner or an admin: they hold the role as
                    # themselves, not as earned access, and Administrator already grants
                    # everything it would have given them. Revoking it would be noise that
                    # looks like a bug.
                    if member.id == g_owner_id or member.guild_permissions.administrator:
                        continue
                    if str(member.id) not in linked_ids and not member.bot:
                        try:
                            await member.remove_roles(role, reason="Shadowgain sweep: Verified Player was never earned")
                            print(f"sweep: removed unearned Verified Player from {member}", flush=True)
                        except discord.HTTPException as e:
                            print(f"WARN: could not remove unearned role from {member}: {e}", flush=True)

                # Drop activity baselines for accounts nobody is linked to any more -
                # /verify records one for every account it looks at, including the ones
                # that failed, so without this the table only ever grows.
                linked = {(l["account"] if isinstance(l, dict) else l)
                          for l in self.state.links.values()}
                for acct in [a for a in self.state.activity if a not in linked]:
                    self.state.activity.pop(acct, None)

                self.state.last_sweep = time.time()
                self.state.save(STATE_PATH)
                print("sweep: done", flush=True)
            except Exception as e:
                # Deliberately does not stamp last_sweep: a failed pass should be retried
                # on the next tick, not counted as this period's sweep.
                print(f"ERROR in sweep_loop: {e}", flush=True)
                await asyncio.sleep(300)


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

    Since 034 removed /say, stripping the role by hand DOES stop someone speaking - the
    channel's send permission is the only inbound gate. But it leaves the link in place, so
    re-granting the role later would silently restore their ability to speak as that
    character. /unlink clears both, which is the difference between revoking access and
    revoking identity.
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
