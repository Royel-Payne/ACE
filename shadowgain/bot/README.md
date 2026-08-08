# Shadowgain Discord bot

A two-way bridge between the game world and Discord: public-chat relay out, verified chat
back in, `/link` account verification with a Verified Player role, and a bug funnel.

Built across tasks 031 (relay + verification + bugs), 033 (Discord → game), 034 (removed
the `/say` command in favour of just typing in the channel) and 037 (`@discord` invite).

## How it fits together

```
   ACE server                        droplet filesystem                this bot
   ----------                        ------------------                --------
   TurbineChatHandler ─ allowlist ─> Logs/chatrelay.jsonl  ─tail─>  relay channel
   @bug command       ────────────>  Logs/sgevents.jsonl   ─tail─>  #bugs
   @verify command    ────────────>          "            ─tail─>  role grant

   ShadowgainInbound  <────tail────  Logs/inbound.jsonl    <─write─  relay channel
   (world thread, 1s)                                               (on_message)
```

**The game server never talks to Discord, and the bot never talks to the game.** They pass
JSON lines through three files on a shared volume. That split means the relay format, embed
styling and Discord-side gating all change without a server redeploy, and the server needs
no outbound HTTP, no POST helper and no Discord token.

**Path note:** the bot sees `/opt/ACE/Logs/`, the server sees `/ace/Logs/` — the same
directory, bind-mounted into the container. Both names are correct; they refer to one file.

## Outbound: game → Discord

- **Allowlist, not denylist.** Only General, Trade, LFG and Roleplay are ever emitted, so
  any channel type added later defaults to *not* relayed. Allegiance, Society, Olthoi, the
  private channels (fellow / patron / vassals / monarch / staff) and local say are never
  touched. This lives in code (`ShadowgainRelay.IsRelayableChannel`), not in a dial — it is
  a privacy boundary, not a preference.
- **The hook sits at the bottom of `TurbineChatHandler`**, next to `LogTurbineChat` — after
  every reject path has returned. Hooking higher (where the channel ID is first resolved)
  would relay lines that were never actually delivered in game.
- **Two feed files, not one.** Chat is high-volume and disposable; bug reports and verify
  codes are low-volume and precious. Kept apart so a chat flood cannot roll a bug report
  out of the backup window before the bot reads it.

## Inbound: Discord → game

A verified user typing in the relay channel speaks into **General**, prefixed so nobody can
mistake it for someone standing next to them.

This is the only write path into the world from outside it, so **nothing the bot claims is
trusted**. The bot writes `{account, character, discord_name, text}`; `ShadowgainInbound`
re-derives everything that matters on the world thread:

| check | why the server does it, not the bot |
|---|---|
| character exists | the bot's state file could be stale |
| character belongs to the claimed account | otherwise the bot could impersonate anyone |
| character is not gagged | `IsGagged` is a `PropertyBool` the bot cannot see |
| rate limit, per **account** | a rate limit the sender controls is not a rate limit |
| length cap | same reason |
| **General only** | in code, not a dial — no dial can widen it to Trade or Allegiance |

The Discord side gates on the **Verified Player role**, checked at message time, so removing
the role stops someone speaking immediately.

Bot messages and webhooks are ignored on the way in — otherwise the relay's own posts would
feed straight back into the world.

## Dials

| dial | type | default | what it does |
|---|---|---|---|
| `discord_relay_enabled` | bool | `false` | master switch for game → Discord chat |
| `discord_bug_reports_enabled` | bool | `true` | enables the in-game `@bug` command |
| `discord_inbound_enabled` | bool | `false` | master switch for Discord → game chat |
| `discord_relay_max_message` | long | `400` | truncate a relayed line past this length |
| `discord_bug_cooldown_seconds` | long | `60` | per-character anti-spam on `@bug` |
| `discord_inbound_rate_seconds` | long | `3` | per-account throttle on inbound lines |
| `discord_inbound_prefix` | string | `"[Discord] "` | marker on inbound speaker names |
| `discord_invite_url` | string | `discord.gg/…` | shown by `@discord` and the login greeting |

Both master switches ship **off**: the relay so the feed does not grow unread before a bot
exists, the inbound because a write path into the world should be turned on deliberately.

`discord_inbound_prefix` is **wire-safe values only**. Names are encoded CP1252 by
`WriteString16L` and the length prefix counts *characters* while writing *bytes*, so a
non-CP1252 glyph desyncs the packet rather than merely rendering wrong.

## Commands

**In Discord**

| command | who | what |
|---|---|---|
| `/link` | anyone | issues a 6-character code, ephemerally |
| `/bug` | verified | file a bug from Discord (opens a modal) |
| `/override <user> <account>` | admin | force a link past the activity gate; survives the sweep |
| `/unlink <user>` | admin | drop a link and strip the role |

`/say` existed until 034. It was removed once reading the channel directly became the
intended UX — two ways to do the same thing, one of them worse.

**In game**

| command | what |
|---|---|
| `@bug <text>` | file a bug; character, level and location attach automatically |
| `@verify <code>` | prove you control this character, completing `/link` |
| `@link` | signpost — explains that `/link` is a *Discord* command |
| `@discord` | prints the invite from `discord_invite_url` |

`@link` exists because AC accepts a `/` prefix, so typing `/link` in game is the obvious
wrong guess, and the bare `Unknown command: link` leaves no way forward.

## Manual steps (Discord side)

These cannot be done over the API from here.

1. **Bot token** → `/opt/ACE/bot.env` as `DISCORD_BOT_TOKEN=`.
   discord.com/developers/applications → app → Bot → Reset Token.

2. **Intents** — same page, *Privileged Gateway Intents*. Portal-only; there is no API for
   these.
   - **Message Content: ON.** Required. Without it `on_message` receives empty content and
     inbound chat silently does nothing — the bot connects, logs no error, and drops every
     line. This is the single most likely cause of "inbound stopped working".
   - **Presence: off.** Unused.
   - **Server Members: off.** Not needed. Every member lookup goes through
     `resolve_member()`, which tries the cache and falls back to `fetch_member` — an API
     call that works without the intent. Turning it on would make those lookups cheaper,
     nothing more.

3. **Invite the bot:**
   ```
   https://discord.com/api/oauth2/authorize?client_id=<APPLICATION_ID>&permissions=268528640&scope=bot%20applications.commands
   ```
   That is View Channels, Send Messages, Embed Links, Read Message History, Manage Messages
   and Manage Roles — and nothing else. Read Message History and Manage Messages are for the
   rolling retention purge; without them the purge fails silently and history grows forever.

4. **Create the `Verified Player` role**, then put its ID in `DISCORD_VERIFIED_ROLE_ID`.
   **The bot's own role must sit ABOVE `Verified Player`** in Server Settings → Roles.
   Discord refuses to let a bot grant a role at or above its own position, and the failure
   is silent from the user's side.

5. **Channel permissions:**
   - **Relay channel** — for `Verified Player`: allow *View Channel* and *Send Messages*,
     **deny *Read Message History***. They see live chat while active and cannot backscroll.
   - **#bugs** — for `Verified Player`: allow *View Channel* **and** *Read Message History*,
     so people can read earlier reports and self-dedupe.
   - Both hidden from `@everyone`.
   - The bot needs View + Send on **both the channels and their category** — a category
     grant alone is overridden by a channel-level deny, and the resulting `50001 Missing
     Access` / `50013 Missing Permissions` errors only surface one at a time.

## Deploying

```bash
tools/bot-deploy.sh            # ship code, install deps, restart the service
tools/bot-deploy.sh --status   # health check
journalctl -u shadowgain-bot -f
```

`bot-deploy.sh` runs `py_compile` before restarting — a syntax error should fail the deploy,
not restart-loop the service.

The DB user is created by `setup.sql` with a generated password written straight into
`bot.env`. It holds `SELECT` on three tables, and on `ace_auth.account` it is **column
scoped** to `(accountId, accountName)` — so the bot cannot read `passwordHash` even by
accident.

## Verification flow

1. Player runs `/link` in Discord → bot replies **ephemerally** with a 6-character code
   (15-minute expiry). Ephemeral so the code never lands in a public archive.
2. Player types `@verify <code>` in game on the character they want to link.
3. Bot matches the code, checks the account has a character **level ≥ 10** that has
   **logged in within 72 hours**, grants the role, and DMs a confirmation.

Judged per **account**, not per character — an active main should not be punished for a
level-2 mule that has not logged in for a month.

A **daily sweep** re-checks every linked account and revokes the role when it goes quiet.
That sweep, not the one-time `/link`, is what "active players only" actually means. An
`/override` link is marked exempt and survives it, so an admin decision does not silently
undo itself within 24 hours.

## Operational notes

- **Rotation-safe tailing.** log4net renames the active file and creates a new one, so the
  path stays the same while the inode changes. The tailer watches the inode; watching the
  path alone would leave it reading a file nobody writes to any more.
- **log4net writes a UTF-8 BOM** on file creation. Every reader strips it — the bot opens
  with `utf-8-sig` *and* strips per line, the server strips it too. Without that the first
  line after every creation and every rotation is unparseable, which looks like one randomly
  dropped message rather than a systematic bug.
- **Never append to a feed file by hand.** log4net holds it open and tracks its own write
  offset; a shell `>>` desyncs that offset and corrupts the stream. Test by sending real
  chat in game.
- **Restart behaviour differs per stream by design.** Chat resumes at the *end* (no replaying
  old conversation); events resume from the *beginning* (a bug filed while the bot was down
  still arrives). Inbound anchors at zero when the file does not exist yet, so the very first
  message ever sent is not skipped.
- **Rolling retention** deletes relay-channel messages older than `SG_CHAT_RETENTION_HOURS`
  (24 by default), hourly. Discord's bulk delete only works on messages under 14 days, so
  anything older is deleted one at a time. #bugs keeps its history on purpose.
- **`@everyone` in game cannot ping Discord.** Every post uses
  `allowed_mentions=AllowedMentions.none()`. Escaping markdown alone would not stop the
  mention resolving.
- **State** (tail offsets, pending codes, links) lives in `/opt/ACE/bot-state.json`, written
  atomically via temp + rename.

## Known gaps

Documented rather than fixed — none of them is a defect in normal use.

- **An offline speaker cannot be squelched.** `SquelchDB.Contains` needs a `WorldObject`, and
  a Discord user has none. Squelch works on relayed lines only while that character is online.
- **The `†` progression marker only renders when the speaker is online**, for the same reason.
- **Discord identity is not verified beyond the code.** Anyone who controls the Discord
  account and the game account at the same moment can link them; that is the whole proof.
