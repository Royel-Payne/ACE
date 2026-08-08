# Shadowgain Discord bot (Task 031)

One-way bridge from the game to Discord: public-chat relay, `/link` verification with a
Verified Player role, and a two-entry bug funnel.

## How it fits together

```
   ACE server                          droplet filesystem              this bot
   ----------                          ------------------              --------
   TurbineChatHandler ── allowlist ──> /opt/ACE/Logs/chatrelay.jsonl ──> relay channel
   @bug command       ─────────────-─> /opt/ACE/Logs/sgevents.jsonl  ──> #bugs
   @verify command    ─────────────-─>            "                 ──> role grant
```

**The game server never talks to Discord.** It appends JSON lines to two files; the bot
tails them. That split means the relay format, embed styling and gating rules all change
without a server redeploy, and the server needs no outbound HTTP, no POST helper and no
Discord token.

It is one-way on purpose. Discord → game is a security boundary, not a missing feature:
it would be an unauthenticated write path into the world, with impersonation, spam and
moderation questions that need answering first.

## What the server side does

- **Allowlist, not denylist.** Only General, Trade, LFG and Roleplay are ever emitted, so
  any channel type added later defaults to *not* relayed. Allegiance, Society, Olthoi, the
  private channels (fellow / patron / vassals / monarch / staff) and local say are never
  touched.
- **The hook sits at the bottom of `TurbineChatHandler`**, next to `LogTurbineChat` — after
  every reject path has returned. Hooking higher (where the channel ID is first resolved)
  would relay lines that were never actually delivered in game.
- **Two feed files, not one.** Chat is high-volume and disposable; bug reports and verify
  codes are low-volume and precious. Kept apart so a chat flood cannot roll a bug report
  out of the backup window before the bot reads it.

## Dials

| dial | default | what it does |
|---|---|---|
| `discord_relay_enabled` | `false` | master switch for the chat relay |
| `discord_bug_reports_enabled` | `true` | enables the in-game `@bug` command |
| `discord_relay_max_message` | `400` | truncate a relayed line past this length |
| `discord_bug_cooldown_seconds` | `60` | per-character anti-spam on `@bug` |

`discord_relay_enabled` defaults **off** so the feed does not grow unread before a bot exists.

## Manual steps (Discord side)

These cannot be done over the API from here.

1. **Bot token** → `/opt/ACE/bot.env` as `DISCORD_BOT_TOKEN=`.
   discord.com/developers/applications → app → Bot → Reset Token.
   Leave **Message Content** and **Server Members** intents **off** — the bot needs neither.

2. **Invite the bot:**
   ```
   https://discord.com/api/oauth2/authorize?client_id=<APPLICATION_ID>&permissions=268454912&scope=bot%20applications.commands
   ```
   That is View Channels + Send Messages + Embed Links + Manage Roles, and nothing else.

3. **Create the `Verified Player` role**, then put its ID in `DISCORD_VERIFIED_ROLE_ID`.
   **The bot's own role must sit ABOVE `Verified Player`** in Server Settings → Roles.
   Discord refuses to let a bot grant a role at or above its own position, and the failure
   is silent from the user's side.

4. **Channel permissions** (ratified in Phase 0):
   - **Relay channel** — for `Verified Player`: allow *View Channel*, **deny *Read Message
     History***. They see live chat while active, and cannot backscroll past events.
   - **#bugs** — for `Verified Player`: allow *View Channel* **and** *Read Message History*,
     so people can read earlier reports and self-dedupe.
   - Both channels should be hidden from `@everyone`.

## Deploying

```bash
tools/bot-deploy.sh            # ship code, install deps, restart the service
tools/bot-deploy.sh --status   # health check
journalctl -u shadowgain-bot -f
```

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
That sweep, not the one-time `/link`, is what "active players only" actually means.

## Operational notes

- **Rotation-safe tailing.** log4net renames the active file and creates a new one, so the
  path stays the same while the inode changes. The tailer watches the inode; watching the
  path alone would leave it reading a file nobody writes to any more.
- **Restart behaviour differs per stream by design.** Chat resumes at the *end* (no replaying
  old conversation); events resume from the *beginning* (a bug filed while the bot was down
  still arrives).
- **`@everyone` in game cannot ping Discord.** Every post uses
  `allowed_mentions=AllowedMentions.none()`. Escaping markdown alone would not stop the
  mention resolving.
- **State** (tail offsets, pending codes, links) lives in `/opt/ACE/bot-state.json`, written
  atomically via temp + rename.
