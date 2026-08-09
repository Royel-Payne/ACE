# Admin/mod GUI — constraint sheet (053)

Research only. Nothing was changed, built or deployed. Every claim below is evidenced from the
local install or the ACE source; where something could not be verified it says so.

**Headline:** the plugin is buildable with existing, installed tooling, and there is already a
**working Decal plugin with source in this repo tree** to copy from. The two hard constraints are
**tier**, not technology: the online roster and coordinate/POI teleport both sit at
`AccessLevel.Developer`, above the Sentinel promotion planned in 052.

---

## 1. Framework and runtime — SETTLED

**Use VirindiViewService (VVS) via Decal.Adapter.** Both are installed and a working example exists.

| item | value | evidence |
|---|---|---|
| plugin API | `Decal.Adapter.dll` **2.9.7.5** | `C:\Games\Decal 3.0`, and two other copies |
| UI layer | `VirindiViewService.dll` **1.0.0.47** | `C:\Games\VirindiPlugins\VirindiViewService\`, ships `VirindiViewService.xml` (full API docs) |
| language / runtime | **C#, .NET Framework v2.0** | `ACBridge.csproj` → `<TargetFrameworkVersion>v2.0` |
| platform | **x86 only** | `<PlatformTarget>x86`; confirmed independently — loading `Decal.Adapter.dll` into an x64 process fails with *"assembly architecture is not compatible"* |
| output | `Library` (DLL), registered as a Decal plugin | `ACBridge.csproj`, `[FriendlyName]` + `PluginBase` |
| client | 32-bit `acclient.exe` | `C:\Games\Asheron's Call` |

**Precedent to copy: `C:\Games\Claude AC\ACBridge\`** — a complete, building Decal plugin
(`ACBridge.csproj`, `PluginCore.cs`, `Properties/AssemblyInfo.cs`). It already demonstrates the
plugin skeleton (`[FriendlyName("ACBridge")] class PluginCore : PluginBase`, `Startup()` /
`Shutdown()`), a 200 ms timer loop, `Core.WorldFilter` reads, and item events. Start from it.

**15 Decal plugins are already registered** on this machine, so the new one joins a crowded host.

---

## 2. Widget vocabulary — everything the design needs exists

Extracted from `VirindiViewService.xml` (`VirindiViewService.Controls.*`):

```
HudTabView      HudList         HudCombo        HudCheckBox
HudButton       HudImageButton  HudStaticText   HudTextBox
HudVScrollBar   HudHScrollBar   HudHSlider      HudProgressBar
HudPictureBox   HudFixedLayout  HudImageStack   HudChatbox
HudConsole      HudBrowser      HudEmulator     HudThemeElement
HudControl (base)
```

Mapped to 053's asks: **tabs** → `HudTabView`; **buttons** → `HudButton` / `HudImageButton`;
**checkboxes** → `HudCheckBox`; **text/labels** → `HudStaticText` / `HudTextBox`;
**scrollable lists** (roster, POI picker) → `HudList` (+ `HudVScrollBar`); **dropdown** →
`HudCombo`; **layout** → `HudFixedLayout`.

**Persistent panel alongside tabs — RESOLVED 2026-08-09: it WORKS.** Not by prototype, by
evidence from a shipping plugin. GoArrow's own embedded view XML (extracted from `GoArrow.dll` and
parsed) contains:

```
<control progid="DecalControls.FixedLayout">
    <control progid="DecalControls.Notebook"  left=0 top=112 width=272 height=158 />
    ... 15 sibling controls positioned above it (top=8, 40, 56, ...)
```

and a second instance with a Notebook at `top=40` under a six-control header strip. So a tab view
and a persistent panel are **siblings inside a `FixedLayout`**, positioned by absolute
`left/top/width/height`. A roster panel at `left=185` beside a notebook at `left=0 width=180` is
the same pattern rotated 90 degrees. **Design may mock the side-panel layout.**

**Layout is absolute pixels inside `FixedLayout`** — no flow or anchoring. Every control carries
explicit `left/top/width/height`, so the window size has to be decided up front.

---

## 3. Command invocation — CONFIRMED, with a correction

**Use `CoreManager.Current.Actions.InvokeChatParser(string)`.** Verified present in
`Decal.Adapter.dll`, along with `AddChatText` and `InvokeChat`.

**Correction to the entry.** 053 cites `DecalProxy.DispatchChatToBoxWithPluginIntercept` as the
precedent. That symbol is **absent from `Decal.Adapter.dll`** — it lives in **`ThwargFilter.dll`**,
i.e. it is ThwargFilter's own internal wrapper, not a Decal API. ThwargFilter also contains
`InvokeChatParser`, which is the real underlying call. Build against `InvokeChatParser`.

**The security model holds.** A command fired this way is sent **as the logged-in character**, so:

* the server re-checks `AccessLevel` at `CommandManager.GetCommandHandler` exactly as if typed;
* every privileged command is caught by the 045 audit hook (`Access != Player`) and lands in
  `#audit`.

So **the GUI is convenience, never the security boundary** — which is what 052 assumes. A GUI bug
cannot grant power the account does not have.

---

## 4. Roster — **NEARBY only via Decal; full roster exists but is Developer-tier**

This is the answer 053 flagged as highest value, and it constrains the design.

* **Decal `WorldFilter` is in-range only.** It indexes objects the client knows about
  (`Core.WorldFilter[id]`, `GetByObjectClass(ObjectClass.Monster)`, `Distance(...)` — all used in
  ACBridge). A player across the map is simply not in it. **No full roster from Decal.**
* **ACE does have a full roster command:** `listplayers`
  (`DeveloperCommands.cs:462`), `CommandHandlerFlag.None` so it works in-world, and its output is
  trivially parseable — one line per player, `"{Name} : {AccountId}"`.
* **But it is `AccessLevel.Developer`** — above Advocate *and above Sentinel*. So after the 052
  promotion, **Greylock still could not use it.**

**Recommendation (small server-side change, not part of this research):** add a Shadowgain command
— e.g. `/sg-roster` — at **Sentinel**, wrapping `PlayerManager.GetAllOnline()`. It is roughly
fifteen lines, mirrors `listplayers`' format so the plugin has one parser, and is automatically
audited by 045. Without it, the roster tab shows only nearby players for anyone below Developer.

---

## 5. Teleport command surface — tier is the whole story

| command | tier | takes |
|---|---|---|
| `tele` | **Advocate** | `[name] <lon> <lat>` — e.g. `37s,67w`, `0n0w`. Can target another player |
| `teleto` / `teletome` / `telereturn` | Sentinel | player name |
| `telepoi` | **Developer** | POI name |
| `teleloc` / `telexyz` / `teledungeon` / `teleallto` | **Developer** | raw cell/xyz, dungeon, all-players |

**Consequences:**

* **Coordinate teleport is available at Advocate** — `tele` is the only one that takes map
  coordinates, and it is the *lowest* tier of the set. Good news for the POI feature (see §6).
* **`telepoi` and `teleloc` are Developer** — a Sentinel GUI cannot call them. Do not design the
  POI button around `telepoi`.

**Hard gotcha — silent failure.** `HandleTele` opens with:

```csharp
if (session.Player.IsAdvocate && session.Player.AdvocateLevel < 5)
    return;
```

An Advocate below **AdvocateLevel 5** gets a **silent no-op** — no error, no message, nothing.
A GUI button would appear to do nothing at all. Either set `AdvocateLevel >= 5`, or promote past
Advocate (the check is gated on `IsAdvocate`), and **verify in client before shipping the button**.

---

## 6. POI data — **do not scrape the wiki; it is already a table in our world DB**

`telepoi` does not use the wiki. It reads `DatabaseManager.World.GetPointsOfInterestCache()`,
backed by **`ace_world.points_of_interest`**.

* **62 POIs**, columns `id, name, weenie_Class_Id, last_Modified`.
* **All 62 resolve to a real destination**: joining `weenie_properties_position` on
  `object_Id = weenie_Class_Id` yields `position_Type = 2` (Destination) with cell + origin X/Y for
  **62 of 62**. Verified by query.
* Coverage is **towns and landmarks** — Arwic, Cragstone, Holtburg, Al-Arqas, Ayan Baqur, Bluespire,
  Fort Tethana, Glenden Wood, Hebian-to… **Dungeons are not in this table.**

**Conversion to what `tele` accepts** is in-tree:
`Source/ACE.Server/Entity/PositionExtensions.cs:226`, `GetMapCoordStr()` — Dereth is 204 map units
across (−102…+102), formatted `"{Y:0.0}N|S, {X:0.0}E|W"`. So the pipeline is:

```
points_of_interest.name -> weenie destination position -> GetMapCoords() -> "37.0s,67.0w" -> @tele
```

…and because `tele` is **Advocate**, POI teleport works for the mod tier with no Developer access.

**DONE — exported to `shadowgain/gui/poi.tsv`** (`name<TAB>coords`, e.g. `Arwic	33.2N,56.5E`),
ready to bundle. Conversion is a faithful port of `GetMapCoords`/`GetMapCoordStr`, sanity-checked
against real AC geography: Arwic `33.2N,56.5E`, Holtburg `42.0N,33.5E`, Cragstone `25.9N,48.3E`,
Al-Arqas `31.3S,13.1E` — all match the actual game locations.

**Only 50 of the 62 are usable, and this is a design constraint.** Twelve POIs sit in
**indoor/dungeon cells**, for which `GetMapCoords` returns **null** by design
(`(cell & 0xFFFF) >= 0x100`): Marketplace, Town Network (+`TN`/`TownNetwork`), the Hotel/Swank
aliases, Night Club, Storage, Underground. **`@tele` cannot reach any of them** — they need
Developer-tier `telepoi`. So the Advocate-tier POI dropdown covers outdoor towns and landmarks
only. The remaining 62 also contain aliases pointing at one destination (`Hotel`/`Hotel Swank`/
`HotelSwank`/`Swank`), which is worth deduplicating in the UI even though the raw list keeps them
for search.

**Wiki (`http://192.168.20.102:8091/wiki/`) is not needed for towns.** It remains the right source
**only if dungeons are wanted** — the `points_of_interest` table has none, and `teledungeon` is
Developer-tier with its own separate source. **Not investigated**, since the town set answers the
design as written. Flagging rather than assuming it is unnecessary.

---

## 7. Medium constraints

* **Fonts.** Decal is configured with `FontName = Times New Roman`, `FontType = 0`
  (`HKLM\SOFTWARE\WOW6432Node\Decal`). VVS carries its own theming (`HudThemeElement`,
  `HudViewDrawStyle`) so the GUI is not bound to that, but text echoed **into chat** still is.
* **Character set.** Anything the plugin sends to chat inherits the project's standing CP1252 rule
  (023/033): the client encodes with `WriteString16L`, the length prefix counts *characters* while
  writing *bytes*, so a non-CP1252 glyph desyncs the packet. **ASCII-safe strings only.**
* **Bitness/DPI.** 32-bit client; VVS windows are client-rendered, so they follow the client's
  resolution rather than desktop DPI. No modal-dialog control appears in the VVS list — a
  confirm-before-shutdown step likely needs a **two-click arm/confirm pattern** built from
  `HudButton` + `HudStaticText` rather than a real modal. **Unverified.**
* **Persistence across relog** — not investigated. VVS ships `vvs.s3db` (SQLite) and
  `System.Data.SQLite.dll`, which suggests VVS itself persists view state; whether that extends to
  plugin settings is unconfirmed.

### Known hazard, from this codebase

ACBridge **disabled its `ServerDispatch` hook** with this comment:

> *"suspected cause of the client silently closing after ~1min. Raw network message interop is the
> riskiest tier; re-enable only after harder validation."*

**The GUI should stay on the documented Decal event/`Actions` surface and avoid raw network
interop entirely.** There is no reason for an admin UI to touch it, and there is local evidence it
destabilises the client.

---

## Corrections to the record

1. **`DispatchChatToBoxWithPluginIntercept` is not a Decal API** (§3). It is ThwargFilter's own
   method. The real call is `InvokeChatParser`.
2. **Task.md 041 states "VirindiTank is native C++, not .NET".** That is wrong — `utank2-i.dll`
   loads as a **managed .NET assembly** (`AssemblyName.GetAssemblyName` succeeds, version 1.0.0.0).
   It never mattered, because 041 was solved from config files without disassembly, but the note
   would mislead anyone who later tried.

## Open questions for Design

1. **Roster below Developer** — accept nearby-only, or add `/sg-roster` at Sentinel? (§4)
2. **Persistent panel beside tabs** — needs a build-time spike. (§2)
3. **Dungeon POIs** — wanted? If so the wiki becomes relevant again. (§6)
4. **`AdvocateLevel >= 5`** — must be satisfied or `tele` silently does nothing. (§5)
5. **Modal confirmations** — no modal control in VVS; is arm/confirm acceptable? (§7)

---

## Portal Storm — investigation (054 item 4)

**Cowork's source read is confirmed, in full.**

* `stormthresh`, `stormnumstormed`, `lbinterval`, `lbthresh` are **empty stubs** — bodies contain
  only a comment and `// TODO: output`. All `AccessLevel.Admin`. `lbthresh`/`lbinterval` describe
  *server-farm* load balancing, a multi-server retail concept with no meaning on one ACE instance.
* The only working storm command is **`portalstorm`** (`DeveloperCommands.cs:3956`,
  **Developer**): a **self-test**. It fires the client events on *the caller* and, at level 2,
  teleports them to a hard-coded `0x7F7F001C` (0,0).
* **So there is no auto-fire congestion system and no "storm landblock X" command to wrap.**

**But the hard part already exists.** All four client events —
`GameEventPortalStormBrewing` / `Imminent` / `PortalStorm` / `Subsided` — are implemented and
proven by that test command. The missing piece is only *selection and delivery*.

**Scope for the build (entry 055):** a Shadowgain command, e.g. `/sg-portalstorm [landblock] [count]`:

| decision | recommendation |
|---|---|
| target selection | current landblock by default; accept an explicit landblock; "most congested" is a nice-to-have, not v1 |
| where players land | **lifestone / sanctuary, never 0,0** — the test command's 0,0 is a debug artifact, not a destination |
| how many | `count`, defaulting to all in the landblock |
| sequencing | replay Brewing → Imminent → PortalStorm+teleport → Subsided with delays, so it reads as an event rather than a yank |
| access | **Admin only**, audited by 045, **inline arm/confirm** (VVS has no modal) |

**Design note:** include Portal Storm in the Admin tab of the mockup as a high-impact action with
inline confirm, flagged as depending on the 055 build.

---

## Indoor POIs — not a blocker, and there is a better design (2026-08-09)

Chris, on the 12 unreachable POIs: *"Marketplace has its own command, Town Network always required
a portal... I bet there's a way to handle those indoor cells, surely we can portal inside a
dungeon. Not a breaking issue right now."*

**Partly corrected, and the conclusion is stronger than the premise.**

* **There is no `/marketplace` or `/mp` command in this build** — checked every handler; the only
  `mp` is a mana abbreviation inside a developer command. That is retail memory. It does not
  matter: the Marketplace and Town Network are reached by **in-world portals and gems**, so
  *players* get there normally. Only a GUI button cannot.
* **Indoor teleport is entirely possible.** `teleloc` takes a **raw cell + x y z**, which is
  exactly what all 12 indoor POIs already carry in the export data. It is a **tier** problem
  (Developer), not a capability problem.

### Recommended when this is revisited: `/sg-tele <poi>` at Sentinel

Resolve the POI name **server-side** to its stored position and teleport. Strictly better than the
`tele`-plus-map-coordinates route this document assumed:

| | `tele` + coords (current plan) | `/sg-tele <poi>` |
|---|---|---|
| coverage | 50 of 62 (outdoor only) | **all 62**, indoor included |
| `AdvocateLevel < 5` silent no-op | **hits it** | sidestepped entirely |
| client-side maths | plugin does coordinate conversion | plugin sends a **name** |
| `poi.tsv` | load-bearing — wrong coords = wrong teleport | demoted to a display list for the dropdown |

Belongs with the 055-era work, where the tier decisions are made together. **Not blocking:** the
50-POI dropdown works today.
