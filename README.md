# DSP Planner Export — BepInEx plugin

A tiny [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Dyson Sphere
Program** that serves your live game state over a localhost HTTP server, so the
[**DSP Ultimate Suite** planner](https://skieller-software.github.io/dsp-ultimate-suite/)
can show **Live mode**: your real research progress, plan-vs-actual production
rates, and exact blueprint footprints.

> The plugin is optional. The planner works fully without it — Live mode just lights
> up the features that compare the plan to your running factory.

## Install

### Option A — r2modman / Thunderstore Mod Manager (recommended)
1. Install [r2modman](https://thunderstore.io/c/dyson-sphere-program/p/ebkr/r2modman/)
   (or the Thunderstore app) and select **Dyson Sphere Program**.
2. Search for **DSP Planner Export** and click **Install** (BepInEx is pulled in as a
   dependency).
3. **Start the game from the mod manager.**

### Option B — manual
1. Install **BepInEx 5.x** into Dyson Sphere Program (the standard DSP modding setup).
2. Download `dsp-planner-plugin.zip` from the
   [**latest release**](https://github.com/Skieller-Software/dsp-planner-plugin/releases/latest).
3. Unzip it into your game folder so the DLL lands at
   `…\Dyson Sphere Program\BepInEx\plugins\DSPPlannerExport.dll`.
4. Launch the game. The BepInEx console should log:
   `DSP Planner Export: serving http://localhost:8765/state, /protos, /rates`

## Use it with the planner
Open the planner at <https://skieller-software.github.io/dsp-ultimate-suite/> with the
game running:
- **Research tab → Live: on** — repaints the tech trees as you research in-game.
- **Planner tab → Plan vs actual → Live: on** — compares the plan's per-item rates with
  what your factories actually produce/consume.
- **Mapper tab → Load protos (plugin)** — pulls exact model indices, footprints,
  z-steps and sorter slots for precise blueprint export.

## What it serves (localhost:8765)
| Endpoint | Data |
|----------|------|
| `GET /state`   | researched tech / upgrade levels |
| `GET /protos`  | full item/building proto dump (footprints, z-step, sorter slots) |
| `GET /rates`   | cumulative production/consumption counters (→ items/min) |
| `GET /deficits`, `/events` (SSE), `/config` | live deficits, push updates, settings |

The HTTP thread never touches Unity objects — the main thread refreshes JSON snapshots
~once per second and the listener serves the latest strings (thread-safe). The page
fetches `http://localhost` from the planner because the plugin sends
`Access-Control-Allow-Origin: *`.

**Building your own tool?** See the [integration guide](INTEGRATION.md) for the full endpoint contract (payloads, CORS, SSE, detection).

## Build from source
CI can't build this (it needs the game's assemblies), so releases are built locally.

**Prerequisites:** BepInEx 5.x in your DSP install, and the .NET SDK.

```
dotnet build -c Release -p:DSPManaged="C:\…\Dyson Sphere Program\DSPGAME_Data\Managed"
```
BepInEx + HarmonyX restore from the official BepInEx NuGet feed (see `nuget.config`) as
compile-time references; the game's BepInEx install provides them at runtime. Only the
game-install path is machine-specific — edit `DSPManaged` in the `.csproj` or pass it on
the command line. Copy `bin\Release\DSPPlannerExport.dll` into `BepInEx\plugins\`.

## Troubleshooting
- **No connection / "no plugin on :8765":** make sure DSP is running with the plugin
  installed; check the BepInEx console for the "serving …" line.
- **Listener fails to start:** another app may hold port 8765, or Windows needs a URL
  ACL — change `Port` in `Plugin.cs`, or run the game once as administrator.
- `/rates` counters reset when the plugin reloads; the planner detects the tick going
  backwards and re-seeds automatically.

## Compatibility
Built against DSP on Unity 2022.3 (verified 2026-06). If a future game patch renames an
API, the compiler points at the exact spot; the planner side needs no change.

## License
MIT — see [LICENSE](LICENSE).
