# Changelog

## 1.10.0 — 2026-06-24
- New `GET /power` endpoint (#27): per-planet power-grid stats — generation capacity,
  draw and stored (accumulator) energy summed across each planet's power networks —
  feeding the planner's "plan vs actual" power card. Energies are per-tick (×60 ≈ /s).
- New `GET /research` endpoint (#26): the active research and queued techs (hash
  progress + next level), for live research-timeline planning.

## 1.9.0 — 2026-06-19
- In-game advisor HUD: configurable researched-upgrade tips (faster belts/sorters,
  higher machine tiers, higher proliferator) surfaced in the overlay (T99).
- Publishing reworked: one-command local publish via `tcli` (`publish.ps1`), with the
  Thunderstore token stored encrypted in Windows Credential Manager.

## 1.8.0 — 2026-06-15
- First public release of the plugin as a standalone repo.
- Endpoints: `/state` (research), `/protos` (item/building protos incl. footprint,
  z-step, sorter slots), `/rates` (production/consumption counters), `/deficits`,
  `/events` (SSE push), `/config`.
- In-game live-deficit HUD overlay (IMGUI).

(Earlier 1.x versions shipped only inside the private planner repo.)
