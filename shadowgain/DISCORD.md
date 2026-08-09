# Discord integration — operator reference

Companion to `DIALS.md`. Everything you need to run, change or fix the Discord bridge,
without re-deriving how it works. Tasks 031 and 033.

---

## What it does

```
   ACE server                        droplet files                    bot          Discord
   ----------                        -------------                    ---          -------
   public chat ── allowlist ──▶ Logs/chatrelay.jsonl ──▶ tail ──▶ #chat
   @bug              ─────────▶ Logs/sgevents.jsonl   ──▶ tail ──▶ #bugs
   @verify           ─────────▶        "             ──▶ tail ──▶ role grant
   General chat  ◀── validate ── Logs/inbound.jsonl   ◀── write ◀── #chat
```

**The game server never talks to Discord.** It writes JSON lines; the bot reads them. The
one inbound path is a file the bot writes and the server polls. That means no outbound
HTTP, no listener, no Discord token on the server, and **the bot's database user is
read-only**.

---

## Commands

Prefix is the tell: **`/` = Discord**, **`@` = in game**. (The game client accepts `/` too,
which is why `/link` in game prints instructions instead of an error.)

### Discord

| command | who | what |
|---|---|---|
| `/link` | anyone | issues a 6-char code (ephemeral, 15 min) to link a character |
| `/bug <summary> [detail]` | anyone | file a bug to #bugs |
| `/override <member> <account>` | **Manage Roles** | link + grant role, **bypassing the gate** |
| `/unlink <member>` | **Manage Roles** | remove the link and the role |

**Typing in #chat is how you speak into the game.** There is no slash command for it — a
`/say` command existed until 034 and was removed once channel-typing became the intended UX.
Keeping it would have left a second inbound surface that the `Verified Player` role does not
gate.

### In game

| command | who | what |
|---|---|---|
| `@verify <code>` | any player | completes the `/link` started in Discord |
| `@bug <text>` | any player | files a bug; auto-attaches character, level, location |
| `@link` | any player | signpost — explains the two-step flow |
| `/sg-dial <dial> <value>` | Advocate+ | change any dial live, no restart |

---

## Access rules

To be granted **Verified Player**, an account needs a character that is:
- **level ≥ 10** (leaving the academy already promotes to 10, so this filters
  create-and-never-move accounts), **and**
- **logged in within 72 hours**

Judged **per account, not per character** — an active main shouldn't be punished for a
level-2 mule. A **daily sweep** re-checks every link and revokes the role when an account
goes quiet. That sweep, not the one-time `/link`, is what "active players only" means.

### Admin override — the escape hatch

The gate is wrong for anyone who rerolls constantly: they never hold a level-10 character
for 72 hours. Greylock is the motivating case — the most active person on the shard,
locked out by a rule meant to include him.

```
/override member:@Someone account:their_account_name
```

- Takes the **ACCOUNT name, not a character** — the account is what's linked, and a
  reroller's character name is a moving target. The bot picks their highest-level
  character to speak as.
- Marks the link **exempt**, so the daily sweep leaves it alone. Without that the override
  would quietly undo itself within 24 hours.
- **Verifies the account exists first.** A typo would otherwise create a link that grants
  the role but can never speak — surfacing much later as "typing in #chat does nothing".
- Gated on **Manage Roles**, not Administrator: anyone who can hand out the role manually
  can already do this.

`/unlink @member` reverses it. Removing the role by hand also stops them speaking (since
034 the channel's send permission is the only inbound gate), but it leaves the link behind —
so a later re-grant of the role silently restores their ability to speak as that character.
`/unlink` clears both.

> **Chris's `Royel` account has neither Administrator nor Manage Roles.** Run overrides
> from the **Shadowgain owner account**, or grant `developers` the Manage Roles permission.

Look up an account name:
```bash
ssh -i ~/.ssh/shadowgain_ed25519 root@137.184.1.44 \
  'docker exec ace-db mysql -uroot -p"$(grep ^MYSQL_ROOT_PASSWORD= /opt/ACE/docker.env | cut -d= -f2)" \
   -N -B ace_shard </dev/null -e "SELECT accountName FROM ace_auth.account;"'
```

---

## Dials

All live-tunable via `/sg-dial` or the console — **no restart**. Full descriptions in
`DIALS.md`.

| dial | default | now | what |
|---|---|---|---|
| `discord_relay_enabled` | `false` | **on** | game → Discord chat relay |
| `discord_bug_reports_enabled` | `true` | on | the in-game `@bug` command |
| `discord_inbound_enabled` | `false` | **on** | Discord → game. The only write path into the world from outside — ships off deliberately |
| `discord_relay_max_message` | `400` | 400 | truncate long lines (both directions) |
| `discord_bug_cooldown_seconds` | `60` | 60 | per-character `@bug` throttle |
| `discord_inbound_rate_seconds` | `3` | 3 | per-**account** inbound throttle |
| `discord_inbound_prefix` | `"[Discord] "` | default | marker on inbound speaker names. **Wire-safe values only** — CP1252, same rule as the `†` prefix |

Bot-side settings live in `/opt/ACE/bot.env` (restart the bot to apply):

| setting | default | what |
|---|---|---|
| `SG_CHAT_RETENTION_HOURS` | `24` | rolling retention on the relay channel; `0` disables |
| `SG_READ_CHANNEL` | `1` | read #chat directly (needs the Message Content intent). **Since 034 this is the only inbound path** — turning it off disables Discord→game entirely |
| `SG_MIN_LEVEL` / `SG_ACTIVITY_HOURS` | `10` / `72` | the verification gate |
| `SG_SWEEP_HOURS` | `24` | how often the role sweep re-checks links |

**Kill switch:** `/sg-dial discord_inbound_enabled false` stops Discord→game instantly.

---

## Chat retention (rolling 24h)

The relay channel keeps only the **last 24 hours** of chat. An hourly sweep deletes anything
older, so the channel is a live window rather than a searchable record of everything ever
said in game.

- **Relay channel only.** #bugs keeps its history deliberately, so reporters can read past
  reports and self-dedupe.
- **Pinned messages survive** — pinning is an explicit request to keep something.
- Configured by `SG_CHAT_RETENTION_HOURS` in `/opt/ACE/bot.env` (`0` disables). Needs a bot
  restart, not a game-server one.
- Runs in the bot, not Mee6: Mee6's free purge is a manual "delete the last N", not
  scheduled time-based retention, and the bot already has `Manage Messages` here.

> **Discord limit:** bulk delete only works on messages **younger than 14 days**. Older ones
> must be deleted individually, so the sweep does a capped straggler pass for those. With
> hourly sweeps and a 24h window this only ever matters if the bot has been down for a
> fortnight.

**Retention replaced the history denial, 2026-08-07.** `Read Message History` was
originally denied on #chat so nobody could backscroll past events - but that made the
channel blank on every login, and it was doing the privacy job badly. Retention does that
job properly, so history is now **granted on both channels** and the 24h window is what
limits exposure.

Current state: `Verified Player` has view + send + history on **both** #chat and #bugs.
Privacy comes from messages ageing out, not from blindness.

Dry-run what a window would delete, without deleting anything:

```python
# /opt/ACE/botenv/bin/python — count messages older than a cutoff in the relay channel
msgs = [m async for m in channel.history(limit=500)]
sum(1 for m in msgs if m.created_at < cutoff and not m.pinned)
```

---

## What is enforced where

The bot proposes; **the server disposes**. A check the sender's own process performs is not
a check.

| rule | enforced in | why there |
|---|---|---|
| chat allowlist (General/Trade/LFG/Roleplay) | **server, in code** | no dial can widen it; new channel types default to not relayed |
| inbound = General only | **server, in code** | same |
| character belongs to the account | **server** | a bug in the bot's link table would otherwise let one player speak as another |
| **gag** | **server** | the bot literally cannot see it — `IsGagged` is a PropertyBool the read-only grant doesn't include |
| rate limit, length cap | **server** | a limit the sender controls is not a limit |
| level / activity gate | bot | it's a Discord role decision, not a game one |

---

## Discord setup that must not be lost

If the server is ever rebuilt, these are the manual pieces:

1. **Bot invite** — View Channels, Send Messages, Embed Links, Read Message History,
   Manage Messages, Manage Roles, Manage Channels.
2. **The `Shadowgain` role must sit at the TOP of the role list** (Server Settings → Roles),
   above every human role. Two separate reasons, and the second is not obvious:
   - it cannot grant `Verified Player` from at or below that role's position;
   - **Administrator does NOT bypass role hierarchy.** A role can only be edited by someone
     whose highest role is strictly above it — so with the bot below the `Envoy` role, it
     could write channel overwrites for that role but could not edit the role itself. That
     is why the bot cannot edit its own role either, at any position.
3. **Privileged intents** (dev portal → Bot → Privileged Gateway Intents):
   - **Message Content ON** — required for typing in #chat to reach the game.
   - **Server Members ON**, and additionally `SG_MEMBERS_INTENT=1` in `bot.env`. The portal
     toggle alone does nothing: the client requests `Intents.default()`, which excludes
     members, and **discord.py refuses `fetch_members()` client-side** without it. Needed
     only for role-holder audits and a warm member cache, never for correctness.
   - **Presence off** — unused.
4. **Channel permissions** — the bot needs View + Send + Embed on both channels *explicitly*.
   A private category denies `@everyone`, and the bot sees channels through `@everyone`
   unless granted directly.
5. **`Verified Player`**: View + Send + **Read Message History** on both channels.
   History is safe because the relay channel only ever holds 24 hours of chat - see the
   retention section. (Earlier revisions denied history on #chat; retention superseded that.)

---

## Troubleshooting

Symptoms we actually hit, and what each one meant:

| symptom | cause |
|---|---|
| `403 (50001) Missing Access` | bot not granted on a private category — it was seeing channels via `@everyone` |
| `403 (50013) Missing Permissions` | bot can see the channel but lacks **Send** (or **Embed** for bug cards) |
| slash commands "unknown command" | client cached the old list — **Ctrl+R** in Discord |
| `/verify` does nothing, log shows `(1046, 'No database selected')` | the bot's DB connection needs `database=ace_shard` |
| first line after a fresh feed file vanishes | log4net writes a **UTF-8 BOM** on file creation; the bot reads `utf-8-sig` to strip it |
| `@verify` accepted in game but no Discord response | check DMs — the bot logs `could not DM`. **The role appearing is the real signal**; the DM is best-effort |
| bug card missing, chat fine | Embed Links not granted — bug reports fall back to plain text |
| inbound message ignored | rate limit (3s/account), gagged character, wrong account, or `discord_inbound_enabled` off. Server logs every drop with a reason |

**Never append to a file log4net has open** (`chatrelay.jsonl`, `sgevents.jsonl`). log4net
tracks its own write offset; an external `>>` desyncs it and corrupts both the file and any
reader's position. `inbound.jsonl` is safe — the bot writes it, not log4net.

### Useful commands

```bash
tools/bot-deploy.sh --status     # service, feed sizes, config keys, recent log
tools/bot-deploy.sh              # ship + restart (runs a syntax check first)
tools/deploy.sh                  # game server

ssh … 'journalctl -u shadowgain-bot -f'                        # live bot log
ssh … 'docker logs ace-server 2>&1 | grep SHADOWGAIN-INBOUND'  # why a line was dropped
ssh … 'cat /opt/ACE/bot-state.json'                            # links, pending codes, offsets
```

**Where things live** (droplet): bot `/opt/ACE/bot/`, venv `/opt/ACE/botenv/`, config
`/opt/ACE/bot.env` (mode 600), state `/opt/ACE/bot-state.json`, feeds `/opt/ACE/Logs/*.jsonl`.

---

## Inbound gating (after 034)

Removing `/say` collapsed inbound to a single surface: **posting in the relay channel**.
That is gated by the `Verified Player` send permission — verified live, only that role
(and MEE6, a bot, which `on_message` ignores) can post there. Since the daily sweep
maintains that role, the privilege is now **verified AND currently active**, with no
gating code of its own.

Lose the role to the sweep and you lose the ability to speak into the game. An unlinked
user who somehow holds the role still cannot: `on_message` requires a stored link and
reacts 🚫 instead.

**The trade:** Message Content is now load-bearing. `/say` was the no-privileged-intent
fallback; with it gone, revoking that intent (or `SG_READ_CHANNEL=0`) kills Discord→game
outright. Accepted knowingly — the intent is on and channel-typing is the wanted UX.

## Known gaps

- **Squelch only applies to online speakers.** `SquelchDB.Contains` needs a `WorldObject`,
  and an offline Discord speaker has none. Fix, if it ever matters, is a name-based check —
  not pretending the current one covers it.
- **The `†` shows only when the speaker is online.** It's applied by the `Player.Name`
  getter, which only runs for a loaded player. Cosmetic.
- **The bot token was pasted in chat once** and should be rotated (dev portal → Reset
  Token, then update `/opt/ACE/bot.env`).

---

## #audit — the durable command trail (045)

Read-only for **everyone**, including Chris. The bot is the only writer. A channel humans can
post in is one they can pad; one they can delete from is one they can edit. Both defeat it.

**What lands there:** every *authorised* privileged command — any command whose required access
level is above Player — from players and from the server console, with timestamp, account,
character, required level, the command and its arguments. Dial changes add a second line with
`before -> after`.

**Chain:** `CommandManager.GetCommandHandler` (the single chokepoint every command passes
through) -> `ShadowgainAudit` -> `/ace/Logs/sgaudit.jsonl` (log4net, 50 x 5MB backups) -> bot
tailer -> #audit.

**Exempt from the 24h retention purge** — by construction, not by a special case: `purge_loop`
only ever targets `RELAY_CHANNEL_ID`.

**Deliberate exclusions, and why:**

| excluded | reason |
|---|---|
| `AccessLevel.Player` commands | `/tell`, `@bug` and ordinary play would bury the staff actions, and would relay chat into #audit |
| `serverstatus`, `serverperformance`, `allstats` | read-only, and `sitedata.sh` polls `serverstatus` **every 15s** — ~5,700 machine-written lines a day |
| arguments to `accountcreate`, `set-accountpassword`, `passwd` | redacted to `***`. This channel is durable and mirrored, so a password here is a permanent, replicated leak. The account name is kept; the secret is not |

**Tamper resistance.** `audit_commands_enabled` is a Shadowgain dial but is **excluded from
`/sg-dial`** (which is Advocate), and `/sg-dial-history` and `/sg-revert` are **Admin-only**.
The people the trail records cannot silence it, read around it, or undo from it.

**Gap worth knowing:** `modifybool`/`modifylong` (Admin) bypass `/sg-dial`, so they are recorded
as commands with their arguments but produce **no** `before -> after` line — only `/sg-dial`
changes appear in `/sg-dial-history`.

---

## Server layout (as of 2026-08-08)

Ordered for a newcomer, not for how it grew — the server is public and linked from Reddit,
so the first thing a stranger sees is onboarding, and the second is the game.

```
Welcome & Help    welcome-to-shadowgain, rules-and-setup, help
Game              chat, info, bugs, audit
Community         general, screenshots
Voice             Hub - Join to create, Private, AFK
Admin Channels    private, mee6-news
```

`Community` and `Voice` were Discord's default `Text Channels` / `Voice Channels`; renamed
because default names read as an unconfigured server.

**`#welcome-to-shadowgain` is Discord's own system channel.** The join messages are native
`new_member` events posted under each joining user's name — **no bot produces them.** Do not
"fix" a bot to restore them.

---

## The permission model

Three principals, three different answers. All of these were verified by reading a member's
**effective** permissions, not by trusting the overwrite that was just written — see the
verification trap in Troubleshooting.

### Greylock (the `Envoy` role)

A trusted mod who is **not** the operator. He gets real power everywhere that is his, and
none where the record lives.

| area | can |
|---|---|
| `Community`, `Admin Channels` | full control — rename, reorder, delete, edit permissions |
| create new channels | yes (guild-level `Manage Channels`) |
| `#rules-and-setup` | full control — **his** channel, though it sits inside a protected category |
| `Welcome & Help` (`welcome`, `help`) | view + chat only |
| `Game` (`chat`, `info`, `bugs`, `audit`) | chats in `#chat`; cannot alter or delete anything |
| `#mee6-news` | **denied** — MEE6's only channel, and only Chris drives MEE6 |

The `Envoy` role (renamed from `admin` on 2026-08-08 — the old name overstated it, and
`Envoy` was a real player-facing rank in retail AC) has **no Administrator** and never did. It carries `manage_roles`,
`manage_messages`, `kick`, `ban`, `mention_everyone`, `moderate_members` and (since
2026-08-08) `manage_channels`.

**`#rules-and-setup` is the interesting case:** it lives inside the protected `Welcome &
Help` category yet stays fully his, because a **channel overwrite beats a category
overwrite**. That is how onboarding channels stay grouped for newcomers without taking his
channel away from him.

### MEE6 — deliberately defanged

It held **Administrator**, which ignores every channel overwrite. Anyone who could drive it
inherited that reach, so a mod with no access to `#audit` could have deleted audit entries
*through MEE6*. Trimmed from **29 permissions to 11**: no Administrator, no `manage_messages`,
no `manage_roles`, no `manage_channels`.

- **Posts in:** `#general` (level-ups) and `#mee6-news`. Denied everywhere else.
- **Commands:** Integrations → Command Permissions is set to `@everyone` ❌ + `royel` ✅ +
  `shadowgain` ✅, and `All Channels` ❌ + `#mee6-news` ✅. Roles and channels are **ANDed**,
  giving two independent locks.
- **Level-ups still work** — Command Permissions govern *user-invoked* commands only, never
  bot-initiated posts.
- Removing Administrator **broke its own `#mee6-news` access**, because it had only ever
  reached that channel by bypassing the `@everyone` deny. General lesson: de-admining a bot
  removes the thing that was silently papering over missing grants.
- Residual permissions all come from `@everyone`, not from MEE6 — it is now an ordinary
  member that posts in two channels.

### AC Support Bot

`#help` **only**, with the full set it needs there: view, history, send, threads, embed
links, attach files, reactions, external emoji + stickers, slash commands. `send_tts_messages`
denied. **No manage permission anywhere.**

### `@everyone`

`create_expressions`, `send_tts_messages` and `create_events` were removed on 2026-08-08.
Harmless on a two-person server; on a public one every new arrival could add emoji, spam
text-to-speech, or create events.

---

## Two Discord rules that caused most of the confusion

**1. A category deny only cascades to SYNCED children.** Every desynced channel needs its own
overwrite. This bit three separate times in one evening — `#help`, `#info` and
`#rules-and-setup` each kept access that a category-level deny appeared to have removed.
**Always verify per channel, never per category.**

**2. Role hierarchy gates editing a ROLE; it does not gate channel OVERWRITES.** The bot wrote
overwrites for a role above its own, and in the same session could not edit its own role.
Corollary discovered later: **a channel overwrite can GRANT `manage_channels`**, not only deny
it — so per-channel management can be handed out without any guild-level permission. Only
*creating* new channels genuinely requires the guild-level bit.

---

## Verification trap

`permissions_for()` computed from channel objects fetched **before** an edit returns stale
results. A verify pass once reported "STILL ABLE" on three channels that were in fact
correctly locked. **Re-fetch channels after writing, then check the member's effective
permissions** — checking the overwrite you just wrote proves only that you wrote it.

---

## Roles

| role | colour | hoisted | notes |
|---|---|---|---|
| `Shadowgain` | — | no | the bot. Administrator, **must stay top of the list** |
| `Envoy` | `#4fd6c6` arcane | **yes** | Greylock. Renamed from `admin`; see the permission model above |
| `Advocate` | `#3aa094` dim teal | **yes** | moderation helpers. Same colour family as Envoy, one rung down |
| `MEE6` / `AC Support Bot` | `#7f8b9c` muted | no | managed app roles — **their names cannot be changed by anyone**, including the owner |
| `Member` | `#7f8b9c` muted | no | baseline |
| `Verified Player` | `#57d98a` green | no | earned via `/verify`. Green = passed the gate |
| `Bots` | `#8f7bd6` violet | **yes** | grouping only, no permissions. Hoisted so bots sit in their own member-list section |

Colours come from the site palette (`landing/index.html`): `--arcane`, `--green`, `--violet`,
`--muted`. **`--gold` (`#d8ac52`) is deliberately unused here** — on this server gold means the
honour roll and the `†`, i.e. *earned the long road*. Spending it on a staff role would dilute
the only thing it signals.

**Deleted 2026-08-08:** `community manager` (duplicated `Envoy` while *exceeding* it with
`manage_webhooks`, and was assignable by Greylock — so he could have handed someone ban
powers) and `developers` (zero holders). `mention_everyone` was removed from `Member`, since a
promoted stranger being able to ping everyone is a bad default on a public server.

**Greylock's promotion ladder is `Member` and `Verified Player`** — neither carries a single
elevated permission, so he can promote freely without being able to hand out anything that
breaks a lock.

---

## Verified Player is enforced by the bot, not by Discord

`Verified Player` is the key to `#chat`, `#bugs` and `#audit`, and it is meant to be **earned**:
`/link` + `/verify`, level 10, active within `SG_ACTIVITY_HOURS`, revoked by the daily sweep
when someone lapses.

But anyone with `Manage Roles` whose top role outranks it can simply hand it out, and a
hand-granted role has **no entry in the link table** — so the sweep, which only re-checks linked
accounts, would never take it back. Permanent access, outside the gate.

**Discord's role hierarchy cannot express the fix.** Positioning `Verified Player` above `Envoy`
stops Greylock granting it — but he *holds* that role, so it becomes his highest and he can then
assign `Envoy` itself. Strictly worse. This was tried and reverted.

So enforcement lives in the bot:

* **`on_member_update`** — a `Verified Player` grant with no link is removed within seconds, and
  a note is posted to `#audit` explaining why, so a mod trying to help someone understands why
  it did not stick.
* **the daily sweep** — same check, as a backstop for grants made while the bot was down.
* **the server owner and any Administrator are exempt** — they hold the role as themselves, not
  as earned access, and revoking it would be noise that looks like a bug.

The bot's own grants are safe: `handle_verify` and `/override` both write the link to state
**before** calling `add_roles`, so by the time the listener fires there is a link to find.

**Net effect: `/verify` is the only way to hold the role, regardless of who has what permission.**

---

## Moderation

Two mod tiers, both named after real Asheron's Call player-facing ranks, in the same order
ACE's own access ladder uses (`Advocate < Sentinel < Envoy`).

| | `Advocate` | `Envoy` |
|---|---|---|
| delete messages (`manage_messages`) | yes | yes |
| timeout (`moderate_members`) | yes | yes |
| kick | yes | yes |
| **ban** | **no** | yes |
| manage channels / roles | no | yes |

**Advocate deliberately has no ban.** A helper should be able to stop trouble in the moment -
delete, timeout, kick - without making a permanent, appeal-only decision. Timeout is included
because it is the right *first* response to spam: reversible, and it does not escalate.

**Where mods can act:** `#chat`, `#help`, `#bugs`, `#general`, `#screenshots`, `#rules-and-setup`.
**Where they cannot:** `#audit` (invisible to Advocate, read-only for Envoy), `#info`,
`#welcome-to-shadowgain`.

**Correction made when the Advocate role was added:** 047's blanket deny on the `Game` category
had left **Envoy unable to delete messages in `#chat`** - the busiest public channel, and the one
that relays into the game. `#chat`, `#help` and `#bugs` now allow `manage_messages` for both mod
tiers. `#bugs` still denies *sending*, so mods can clear spam without adding chatter to the
report feed.

Greylock can assign `Advocate` himself: it sits below `Envoy`, and carries nothing that can reach
the audit trail or the curated channels.

---

## AutoMod

Three server-wide rules, created 2026-08-08 before the server went public.

| rule | trigger | action |
|---|---|---|
| Slurs and severe profanity | Discord keyword presets (profanity, slurs, sexual content) | block + alert |
| Spam | Discord spam detection | block + alert |
| Mention spam | more than 5 mentions in one message | block + alert |

**Why AutoMod rather than a filter in the bot:** blocked messages never post, so nothing reaches
`#chat` and therefore nothing reaches the *game*. Discord maintains the wordlists, so there is
nothing here to curate or get wrong. The bot already reads every message in that channel, so a
custom filter is possible later - but only if AutoMod proves insufficient, not before.

Alerts go to **`#mod-log`** (in Admin Channels, hidden from `@everyone`, visible and writable by
`Envoy` and `Advocate`). Deliberately **not** `#private` - that is Chris's channel with Greylock,
and Advocates are Greylock's friends rather than Chris's.

**Known risk, unresolved by design.** The bot is **not** exempt from these rules. If AutoMod
applies to bot messages, an in-game slur is blocked before it mirrors into Discord - which is
what you want. But a false positive would then **silently drop a legitimate relay line**, and the
in-game speaker would never know their message did not arrive. Whether Discord applies AutoMod to
bot messages was not verified. **If game chat ever goes missing from `#chat`, check this first** -
exempting the `Shadowgain` role from the profanity rule is the fix.
