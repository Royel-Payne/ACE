# Shadowgain pacing simulator

Answers "where does a character actually end up?" without anyone grinding for a
year. Runs the real award formula over the real cost tables and the real creature
distribution.

```bash
cd tools/pacing
dotnet run -c Release
```

Fully local and self-contained — no dat file, no server, no network. It keeps
working while the AC client is running.

## Inputs

All via environment variables:

| var | default | meaning |
|---|---|---|
| `SG_POLICY` | `matched` | `farming` (0.6x) · `matched` (1.0x) · `aggressive` (1.5x) — how hard you fight relative to your own weapon skill |
| `SG_SWINGS` | `3` | **landed** hits per kill — not swings attempted. Measure with `tools/hitrate.py`. |
| `SG_HITS` | `4` | hits taken per kill |
| `SG_KPH` | `40` | kills per hour. **Sets every hour figure the tool prints** — 40 assumes continuous killing; a respawn-gated camp measured 14. |
| `SG_SKILL_MULT` | `1.0` | `skill_gain_multiplier` |
| `SG_ATTR_MULT` | `1.0` | `attribute_gain_multiplier` |
| `SG_MAX_LEVEL` | `200` | stop here |

```bash
SG_POLICY=aggressive SG_SKILL_MULT=2.0 dotnet run -c Release
```

## What is real vs. assumed

**Real — measured, not guessed:**
- Skill / attribute / vital / character-level XP tables, extracted from
  `client_portal.dat` into `tables.tsv`.
- Creature difficulty-vs-reward, exported from `ace_world` into `mobs.tsv`
  (5,573 XP-bearing creatures).
- The award formula, mirrored from `Proficiency.OnSuccessUse`.
- The 013 stretched attribute curve, mirrored from `Player_Attributes`.

**Assumed — these are the inputs above:** swings per kill, hits taken, kills per
hour, and which tier you choose to fight.

**Not modelled at all:** Quickness from movement, Strength from burden, Endurance
from exertion, and every specialty skill. All of those only *add*, so the
projections are a floor, not a ceiling.

## Content is chosen by defence tier, never by level

Chris: *"levels are not always a measure of difficulty... level 80 mobs aren't
always equal, and XP per mob increases as you access higher tier areas."*

The creature data agrees emphatically. Within a single level band, melee defence
varies up to **850x** and XP by **five orders of magnitude**. Bucketed by defence
instead, median XP climbs cleanly: 661 → 3,500 → 10,000 → 30,000 → 270,000 →
1,400,000. So the character's capability picks a tier and the tier carries its own
reward.

One wrinkle worth knowing: a creature's authored *melee* defence is itself an
imperfect tier proxy — casters and bosses carry modest melee defence but pay
enormous XP. Raw medians spiked in the low bins badly enough that the first ten
levels took eight kills. The tier curve therefore takes a **running maximum** over
defence bins, expressing the real constraint that a harder tier never pays less.

## Two findings that matter more than the tables

**1. `skill_gain_multiplier` barely changes the outcome.** A 16x sweep moves
weapon skill at level 100 by 11%:

| multiplier | weapon skill @100 | hours |
|---|---|---|
| 0.25x | 269 | 539 |
| 1.0x | 283 | 404 |
| 4.0x | 298 | 333 |

The system self-normalises: more gain → higher skill → you fight harder content →
the difficulty ratio returns to ~1.0. **The dial changes how long it takes, not
how strong you end up.** Tune pace with it; don't expect it to move power.

**2. The model is robust to its own weakest assumption.** Swings per kill is the
softest input, and a 4x sweep (6 → 24) moves weapon skill at level 100 from 270
to 297 — same 10% band. The projections don't hinge on guessing it right.

**3. Playstyle decouples level from skill.** At level 126:

| policy | kills | hours | weapon skill |
|---|---|---|---|
| aggressive | 5,567 | 139 | 275 |
| matched | 34,424 | 861 | 304 |
| farming | 163,992 | 4,100 | 320 |

Aggressive players out-level their skills; grinders' skills outrun their level.
Per *hour* though, matched wins comfortably — so the incentives point at fighting
what you can handle, which is the desired behaviour.

## Status — first validation, 2026-08-06

Checked against `Black Breath`, a fresh character at level 7:

| at level 7 | simulator (12 swings) | actual |
|---|---|---|
| Endurance | ~26 | **25** ✓ |
| Strength | ~33 | 16 |
| weapon skill | ~40 | ~17 |

**Endurance matched; everything driven by your own accuracy was ~2x too high.**
That pattern identified the fault precisely: `Proficiency.OnSuccessUse` fires only
on a **landed** hit, so misses train nothing — while Endurance comes from hits
*taken*, which don't depend on your accuracy at all. `SG_SWINGS` therefore means
**landed hits**, not swings attempted, and the old default of 12 was far too high
for a low-skill character. At `SG_SWINGS=3` the model reproduces the observed
character.

**Corroborated independently.** `tools/hitrate.py` parses ACBridge combat chat
and measures landed hits directly:

| character | landed | missed | hit rate | **landed/kill** |
|---|---|---|---|---|
| Misti | 50 | 14 | 78% | **3.3** |
| Misti II | 27 | 14 | 66% | **3.4** |
| Misti Loves Claude | 3 | 2 | 60% | **3.0** |

Back-fitting from Black Breath's skill ranks said ~3. Measuring combat logs
directly says 3.0-3.4. **Those two methods share no inputs**, so the agreement is
real evidence rather than a coincidence of tuning. `SG_SWINGS` default is now 3.

### Second validation, 2026-08-07 — level 20 → 32

An overnight VTank macro run. Per-kill maths held: predicted ~107 for the primary
attribute at level 30, actual Focus 131 at level 32 (the sim models melee, the
character was a caster — names differ, magnitudes match).

**`SG_KPH` was the wrong input, and it is the one that sets every hour figure.**
Default 40 predicted 2 hours; actual was 6.9. At `SG_KPH=14` the model predicts 7.
The kill *count* is identical either way — only the clock moves.

> **A warning about diagnosing from wall-clock alone.** 6.9 hours also matches the
> `farming` policy exactly, and I first concluded the anti-farm floor was throttling
> macro players. It wasn't — the player was on appropriate content and idling
> between respawns in a low-density camp with no nav route. Two different mechanisms,
> same number, opposite conclusions. **Check the kill count and the content defence,
> not just the hours**, before blaming the difficulty curve.

**Every hour figure this tool prints is conditional on `SG_KPH`.** It is a play-rate
assumption, not a property of the game. Measure it before quoting the output.

**Known remaining weakness:** landed-hit rate is not constant — it should rise as
your skill closes on the target's defence, and the samples above are all
low-to-mid level. Treating it as a fixed 3 will therefore *under*-predict mid and
late game, where a matched character lands most swings. The right fix is deriving
it from skill-vs-defence instead of taking it as input. Re-run `hitrate.py` on a
high-level character before trusting the deep rows.

Validated twice now — level 7 and level 20-32, on the same character — plus
hit-rate samples from a handful of others. Both passes found the per-kill maths
sound and a *play-rate input* wrong: first `SG_SWINGS`, then `SG_KPH`. Nothing
above level 32 has been checked against reality, and the 2-year / 2-month headline
figures live far past that.

## Regenerating the data

- `tables.tsv` — via the `xpdump` helper (source kept as
  `xpdump-Program.cs.txt`), pointed at `C:\Games\Asheron's Call`. Only needed if
  the dat changes.
- `mobs.tsv` — re-export from `ace_world`; the query is in Task.md entry 006.
