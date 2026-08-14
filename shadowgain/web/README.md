# Shadowgain web character sheet — `my.shadowgain.com`

The CLI half of Task.md **124** (design of record: **123**). A read-only web character sheet
that shows a player their **true** ranks — the numbers the retail client physically cannot
display, because its own tables stop at 190 attribute ranks and 208 trained skill ranks.

Cowork owns the front-end. This directory owns everything behind it.

---

## The one rule

**This service never writes to a game database.** Not the shard, not auth, not world.

Enforced in three places, so no single mistake defeats it:

1. There is no `INSERT`/`UPDATE`/`DELETE` anywhere under `api/`.
2. The MySQL user it connects as has `SELECT` and nothing else (`api/setup.sql`).
3. The systemd unit gives the process a read-only filesystem outside its own runtime dir.

It follows that this is **standalone infrastructure**. No game-server code, nothing to restart,
and if this process dies the world does not notice. Deploy it with players online; it is a
non-event.

---

## Layout

```
web/
  api/                 the FastAPI service
    app.py             endpoints, sessions, live dials, online status
    auth.py            bcrypt verify + signed-cookie sessions + login rate limit
    payload.py         shard rows -> the JSON contract (public and private builders)
    curves.py          rank/XP maths, ported from the server (see below)
    names.py           landblock -> place name, and map coordinates
    cache.py           the 5-minute per-character snapshot cache
    db.py              read-only PyMySQL access
    data/              GENERATED, committed: xp tables, skills, enums, landblocks, quests
    setup.sql          the read-only MySQL user
  public/              static web root: assets/icons/** (generated) + Cowork's front-end
  deploy/              systemd unit + Caddy site block
  tools/               generators and the deploy script
../exporter/           the ACE.DatLoader console app that produces data/ and public/assets/
```

---

## Why `curves.py` is a port

True rank is **not stored in the shard**. `biota_properties_skill` holds experience; rank is
derived from it, every time, by `Player.CalcSkillRankUncapped`. So an API that read
`init_Level + level_From_P_P` and called that the true rank would be reading the client's
clamped shadow — and would disagree with what `@mystats` tells the same player in game.

`curves.py` therefore reproduces, function for function:

| Server | Here |
| --- | --- |
| `Player_Skills.CalcSkillRank` / `CalcSkillRankUncapped` / `GetOvercapCurve` / `CalcSkillXpForRank` | `calc_skill_rank*`, `overcap_curve`, `calc_skill_xp_for_rank` |
| `Player_Attributes.CalcAttributeRank` / `CalcAttributeRankScaled` / `AttributeRankCost` / `AttributeMaxRanks` | `calc_attribute_rank*`, `attribute_rank_cost`, `attribute_max_ranks` |
| `CreatureSkill.TrueExperienceSpent` | `true_experience_spent` |
| `AttributeFormula.GetFormula` | `apply_formula` |

against the **same dat tables the server reads**, exported to `api/data/xptable.json`.

**This coupling is real: a change to those functions on the server is a change here.** It was
accepted because the alternative — a new server endpoint — means game-server code and a restart,
which Part 1 explicitly does not do.

Verified against production: all of Black Breath's live rows, 14/14 skills and 6/6 attributes,
computed here match what the shard stores.

The dials the maths depends on (`skill_uncap_ranks`, `attributes_start_at_ten`,
`attribute_max_value`) are read from `config_properties_*` **at runtime**, never from a compiled
default — `PropertyManager` loads those rows over its defaults, so the value in the C# source is
only a fallback.

---

## Regenerating the generated files

Everything in `api/data/` and `public/assets/` is generated and committed. Nothing is
hand-authored, and nothing is read from a dat at runtime.

### 1. Dat tables and icons

```bash
cd ../exporter
dotnet build

# Tables: xp curves, skill table, vital formulas, enums
dotnet run -- --dat "C:/Games/Turbine/Asheron's Call" --out ../web/api --tables

# Icons: skill icons from the dat, generated attribute/vital tiles, item icons in use
cd ../web/tools && bash icon-ids.sh > /tmp/icon-ids.txt
cd ../../exporter
dotnet run -- --dat "C:/Games/Turbine/Asheron's Call" --out ../web/public --icons --item-ids /tmp/icon-ids.txt
```

**A running AC client holds an exclusive lock on its own dats**, so point `--dat` at a second
install. On this machine `C:/Games/Asheron's Call` is the one Chris plays and
`C:/Games/Turbine/Asheron's Call` is the spare — same file, iteration 2072 (end of retail).
Note the files are named `client_portal.dat`; the `portal.dat` in the older folders is a 2004-era
format that this cannot read.

### 2. Landblock and quest name tables

```bash
cd tools && bash build-name-tables.sh
```

Reads `ace_world` (read-only) and writes `api/data/landblocks.json` (1,742 blocks) and
`api/data/quests.json` (4,237 quests).

---

## Icons: what came from the dat and what did not

| Set | Source | Count |
| --- | --- | --- |
| Skills | the dat's own `SkillTable.IconId` — the real in-game icons | 38 of 48 |
| Items | `Texture` records for every `IconId` the shard references | 663 |
| Attributes / vitals | the client's own 25×25 panel icons | 6 + 3 |
| Placeholder | generated | 1 |

The 10 missing skill icons are the retired weapon skills (Axe, Bow, Dagger, …), which
`AddRetiredSkills` inserts with no `IconId` because they no longer exist as skills.

**Attributes and vitals DO have icons in the dat**, at `0x060002C4`–`0x060002C9` (attributes) and
`0x06004C3B`–`0x06004C3D` (the three hearts). The first search concluded otherwise and drew
substitutes; it was wrong twice over, and either mistake alone would have hidden them:

* **The sweep only looked at 32×32**, the size item and skill icons use. These are **25×25**. Run
  `--sizes` for a census of the whole range before assuming a size — there are exactly nine
  textures at 25×25, which is six attributes and three vitals.
* **They are palettised**, and `Texture.GetBitmap` resolves a P8/INDEX16 palette through
  `DatManager.PortalDat`. Opening `PortalDatDatabase` directly leaves that static null, so every
  palettised texture throws and `SaveTexture` counts it as "skipped" — the export looks fine while
  the whole palettised half of the dat goes missing. The exporter now goes through
  `DatManager.Initialize`.

**The texture order is not the enum order.** They run Endurance, Focus, Quickness, Self, Strength,
Coordination across `02C4..02C9`, while `PropertyAttribute` runs Strength, Endurance, Quickness,
Coordination, Focus, Self. Assigning them in id order gives every attribute the wrong picture and
looks completely plausible — the pairings in `AttributeIcons` were each confirmed against an
in-game screenshot of the panel.

**Icon URLs carry `?v=<mtime of icon-map.json>`.** Caddy serves `/assets/*` as `immutable` with a
week's max-age, which is right for item icons (the filename *is* the IconId) and wrong for the
named ones — re-pointing `strength.png` at a different texture would otherwise leave every prior
visitor holding the old picture for a week.

---

## Deploying

```bash
cd tools
bash web-deploy.sh --setup     # first run only: unix user, venv, DB user, secrets
bash web-deploy.sh             # every time after
bash web-deploy.sh --assets-only   # front-end iteration
```

`--setup` generates both secrets **on the droplet** — the read-only MySQL password and the
session-signing key — into `/opt/shadowgain-web/web.env` (mode 640, `root:sgweb`). Neither ever
exists on a developer machine or in the repo. It refuses to overwrite an existing `web.env`,
because regenerating the session key logs every player out.

The Caddy block is spliced into `/etc/caddy/Caddyfile` between markers and **validated before it
replaces the live config** — a malformed block would otherwise take `shadowgain.com` down with
it. The apex site's own block is never touched.

---

## Online status

Per-character "online" comes from the server's `listplayers` console command, which
`shadowgain/tools/sitedata.sh` runs on its existing 30-second attach (no extra round-trip, no
server code, no restart). It writes the names to **`/opt/ACE/online-names.json`, outside the web
root** — a public who-is-online roster is a disclosure about players rather than about the
server, so it is not added to the public `status.json`. The API reads the file and uses it for
one thing: a dot beside a character the viewer can already see.

Two traps this avoids:

* **The `†` marker must come off.** Names arrive as `† Black Breath`; the marker is cosmetic and
  is not part of `character.name`, so leaving it on makes every hard-lane character read offline.
* **The shard's timestamps are not a substitute.** ACE writes *future* values into
  `LogoffTimestamp` to drive the PK timer, so a character who fought recently looks logged in
  forever.

A stale (>150s) or missing file yields "everyone offline", which is the right failure.

---

## Refresh model

No refresh button, by design (123).

| What | Cadence | Why |
| --- | --- | --- |
| Character snapshot | 5 min | matches `player_save_interval` (300s) — a live character's shard rows only change on save, so polling faster re-reads identical bytes |
| Online / server status | 25–30 s | rides the existing status feed, so the live parts still feel live |
| Character picker | 60 s | changes only on create/delete/level |

Every payload carries `asOf`, so the page can say how old the data is rather than implying it is
live.
