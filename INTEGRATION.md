# Integration guide — reading the plugin from your own tool

This plugin serves your **live Dyson Sphere Program** state over a plain localhost
HTTP server. It was built for the [DSP Ultimate Suite planner](https://skieller-software.github.io/dsp-ultimate-suite/),
but the endpoints are open and CORS-enabled, so **any tool — a website, a script, another
mod — can read them**. This page documents the contract for that.

> TL;DR for a web app: with the game running and the plugin installed,
> `fetch('http://localhost:8765/state')` works from any origin. No key, no setup.

## Basics

- **Base URL:** `http://localhost:8765` (port is `Port` in `Plugin.cs`; default 8765).
- **Methods:** `GET` for all data endpoints (`POST` only for `/config`).
- **Format:** JSON (`Content-Type: application/json`), except `/events` which is
  `text/event-stream`.
- **CORS:** every response sends `Access-Control-Allow-Origin: *`,
  `Access-Control-Allow-Methods: GET, POST, OPTIONS`,
  `Access-Control-Allow-Headers: Content-Type`, `Cache-Control: no-store`.
  `OPTIONS` preflights return `204`. So a browser tool on any origin can call it directly.
- **Scope & trust:** localhost only — it is not exposed to your LAN and has no auth,
  by design (local-machine trust). Don't assume it's reachable from another device.
- **Versioning:** every payload carries an integer `"version"`. Treat a higher-than-known
  version defensively (new fields may appear; existing ones are kept stable within a
  major version). The current versions are noted per endpoint below.

## Detecting the plugin

Try `GET /state`. If the request resolves and the body parses as JSON with a `version`
field, the plugin is present. `"running": true` means a **save is loaded**; `false`
means the player is at the main menu (most data endpoints return empty/`running:false`
there). A failed/aborted fetch means the plugin isn't running — degrade gracefully.

```js
async function pluginPresent() {
  try {
    const r = await fetch('http://localhost:8765/state', { cache: 'no-store' });
    if (!r.ok) return false;
    const j = await r.json();
    return typeof j.version === 'number';   // present; j.running tells you if in-game
  } catch { return false; }                 // not running / not installed
}
```

## Endpoints

### `GET /state` — researched tech & upgrade levels  *(version 1)*
```json
{ "version": 1, "running": true,
  "states": [ { "id": 1131, "level": 1, "max": 1, "unlocked": true }, ... ] }
```
`id` is the in-game proto id. **Every tech and every upgrade level is its own proto**;
`unlocked` is the researched flag. Use this to know what the player has unlocked.

### `GET /protos` — static item & building data  *(version 2)*
Built once and cached. Every item has `id`, `name`, `grid` (UI grid index). **Buildings**
additionally carry layout data:
```json
{ "version": 2, "items": [
  { "id": 1005, "name": "Stone", "grid": 1203 },
  { "id": 2303, "name": "Assembling Machine Mk.I", "grid": 2204,
    "model": 65, "w": 3, "h": 3, "zstep": 0.0,
    "slots": [2, 1, 2, 1] } ] }
```
- `w`,`h` — blueprint **footprint** in grid cells (e.g. 3×3 assembler).
- `zstep` — vertical stack step for stackable buildings (0 if none).
- `slots` — per-side sorter slot counts as `[N, E, S, W]`.

This is the largest payload — **fetch it once and cache it** (it doesn't change during a
session).

### `GET /rates` — cumulative production/consumption counters  *(version 1)*
```json
{ "version": 1, "running": true, "gameTick": 123456,
  "items": [ { "id": 1101, "p": 12345, "c": 678 }, ... ] }
```
`p` = total produced, `c` = total consumed, **accumulated across all factories since the
plugin loaded** (not per-second). To get a live **rate**, poll twice and diff:

```js
// 60 ticks/s → per-minute rate = Δcount / Δticks * 3600
const perMin = (p2 - p1) / (tick2 - tick1) * 3600;
```
**Reset handling:** the counters restart when the plugin reloads. If `gameTick` (or a
counter) goes *backwards* between polls, discard the previous sample and re-seed.

### `GET /deficits` — items consumed faster than produced  *(version 2)*
Computed once/second from the `/rates` deltas (the in-game HUD reads the same data, so
the two never disagree).
```json
{ "version": 2, "running": true, "gameTick": 200000, "windowTicks": 60,
  "mode": "sustained", "sustainSeconds": 5, "minMagnitude": 1,
  "items": [ { "id": 1005, "name": "Stone", "produce": 455.7, "consume": 577.2,
              "net": -121.5, "streak": 7, "flagged": true }, ... ] }
```
`flagged` applies the configured policy — `sustained` (deficit held ≥ `sustainSeconds`
samples, so buffer flicker is ignored) or `instant` (net-negative this sample).

### `GET /techs` — static tech tree  *(version 1)*
Per tech: `id`, `name`, level span, `hash`/`hashByLevel`, real prerequisite edges
`pre` (drawn arrows) and `preImplicit` (required but not drawn), plus matrix cost
(`items` + `points`, total cost ≈ `points * hash / 3600`) and `preItem` (items needed
before the game reveals the tech). Useful for research planning / tree auditing.

### `GET /research` — current research & queue  *(version 1)*
What's being researched right now and what's queued behind it — the live feed for a
research-timeline planner.
```json
{ "version": 1, "running": true, "current": 1602,
  "hashUploaded": 1840, "hashNeeded": 5400, "level": 3,
  "queue": [1603, 1604, 1112] }
```
`current` is the active tech id (`0` if none); `hashUploaded`/`hashNeeded`/`level` are
present only while a tech is active (`level` is the next level for repeatable techs).
`queue` lists the upcoming tech ids in order. Pair with `/techs` for names and costs.

### `GET /power` — per-planet power grid  *(version 1)*
Generation capacity, draw and stored (accumulator) energy summed across each planet's
power networks — "plan vs actual" for the power planner.
```json
{ "version": 1, "running": true,
  "planets": [ { "planet": 102000601, "name": "Planet of Misery",
                 "capacity": 7200000, "consumption": 5130000, "stored": 18000000 } ] }
```
Energies are **per-tick** (×60 ≈ per second); the planner converts. Only planets with a
power system report; `planets` is empty until a save is loaded.

### `GET /events` — Server-Sent Events push
Streams `event: state` / `event: rates` with the **same JSON payloads** as the endpoints
above, but only when a snapshot **changes** (checked ~once/second), plus `: keepalive`
comments. Prefer this over polling where available:
```js
const es = new EventSource('http://localhost:8765/events');
es.addEventListener('state', e => onState(JSON.parse(e.data)));
es.addEventListener('rates', e => onRates(JSON.parse(e.data)));
es.onerror = () => { /* fall back to polling /state and /rates every ~3 s */ };
```

### `GET` / `POST /config` — HUD & deficit settings  *(version 2)*
`GET` returns the current in-game HUD/deficit configuration (toggle key, thresholds…);
`POST` updates it. Most external tools won't need this.

## Practical notes
- **Cache `/protos`**; poll `/rates` (or subscribe to `/events`); read `/state` on
  connect and on tech changes.
- At the main menu `running` is `false` and live data is empty — show a "load a save"
  state rather than erroring.
- Game time is **60 ticks/second**; all `gameTick` values are in ticks.
- Field additions are backward-compatible within a `version`; only a `version` bump
  signals a breaking change.

## Questions / breaking-change requests
Open an issue on the [planner tracker](https://github.com/Skieller-Software/dsp-ultimate-suite-issues)
— if you're building on these endpoints and need a stability guarantee or a new field,
say so and it can be coordinated.
