# Changelog

## 1.3.1 — 2026-06-19

### Changed
- **Efficient Labor** — now re-scans for idle villagers **every N in-game days**
  (default 5, range 1–30, `EfficientLaborPollDays`) instead of every single day.
  Idle status barely shifts day-to-day, so this is much lighter on large maps with
  no real loss in responsiveness; the first scan still runs immediately on
  enable/load, and idle villagers are released back to their jobs on the same
  cadence. Surfaced in the Keep Clarity panel under Efficient Labor.

## 1.3.0 — 2026-06-19

### Added
- **Workplace Mastery** (new feature, OFF by default) — villagers work a little
  faster the longer they hold the same job. Each villager earns a multiplicative
  work-rate bonus of **+2% per in-game year of tenure, capped at +10%** by default
  (tunable: per-year bonus 1–3% via `WorkplaceMasteryPerYearPct`, cap 0–25 years
  via `WorkplaceMasteryYearsCap`). It **stacks on Learned Hands** — a worker's
  output is `base × (1 + education) × (1 + tenure)`. Tenure is tracked per
  occupation (switching jobs banks the old job's tenure and starts a fresh clock
  on the new one) and **persists per save** in
  `UserData/EP_VillagerMastery/<save>.json`. Hover a villager's profession icon in
  the worker-assignment picker to see their tenure breakdown. Existing villagers
  start at 0 — Farthest Frontier stores no job-start date to seed from, so tenure
  accrues from when you enable the feature.
- **Keep Clarity villager-window readout** — EP exposes a small public interop
  (`WorkInfoApi.GetVillagerWorkSummary`) that Keep Clarity reads to show a
  villager's combined EP work bonuses (e.g. `+10% edu · +8% mastery`) in the
  villager info window. Soft-dep; does nothing without Keep Clarity installed.

### Changed
- **Surplus Selling** — now recomputes trading-post stock targets **every N days**
  (default 5, range 1–30, `SurplusSellingPollDays`) instead of every single day.
  The 30-day rolling consumption average barely moves day-to-day, so this is much
  lighter on large maps with no loss in responsiveness; the first stocking pass
  still runs immediately once a trading post and consumption data exist. It also
  now clears its managed trading-post targets when consumption data goes away
  (e.g. you clear the tracked-items list), and warns once if it's enabled without
  Consumable Control (which supplies the rate data it needs).

### Fixed
- **Planting Almanac** — applying a template now clears the crop field's "empty
  season" warning marker immediately. Previously the marker lingered until you
  manually deleted and re-added a crop, because the schedule-add API doesn't
  refresh that flag (the game's own paste path does it in the caller — now so do
  we).

## 1.2.0 — 2026-06-06

### Added
- **Learned Hands** (new feature, OFF by default) — educated villagers now work
  faster. FF's official guide says educated workers are more efficient, but the
  game's assembly never actually implements a work-rate term that reads
  education — it only gates a few jobs (Healer, School-teacher, Apothecary). This
  closes that gap: each educated villager gets a multiplicative work-rate bonus
  (default **+10% per education level**) applied to *every* job, from resource
  collection to manufacturing. Tunable: per-level bonus 0–50% (`LearnedHandsPerLevelBonus`),
  plus a cfg-only verbose log toggle (`LearnedHandsVerbose`). The bonus is
  per-worker — a building's output scales with its workers' education, and the
  effect is retroactive to already-educated villagers.

## 1.1.0 — 2026-05-29

### Added
- **Planting Almanac** (new feature, OFF by default) — reusable crop-rotation
  templates on the crop field window:
  - A native-styled dropdown of templates: curated disease-safe built-in presets
    (Disease-Safe Rotation, Soil Rebuild, Fiber & Food) plus your own saved
    rotations.
  - **Apply** (styled after the native Paste button) writes the rotation to the
    open field via the game's own scheduler — captures crops *and* field
    maintenance periods — then redraws the window live.
  - **Save** (styled after Copy) captures the open field's rotation, with an
    optional typed name (auto-names from the crops otherwise), and writes a
    shareable JSON to `UserData/EP_CropTemplates/`. The library is global to the
    player (not per-save), so saved rotations show up in every game and can be
    shared by copying the JSON files.
  - **Delete** (styled after Clear) removes the selected saved template;
    built-in presets are protected.
  - Soil Wisdom's rotation-status line is relocated beside the Planting Almanac
    title when both features are enabled.
  - Hotkey/camera suppression while typing a name (via `HotkeyManager`) so the
    crop window stays open and keystrokes don't bleed into the game.

## 1.0.2 — 2026-05-28

### Fixed
- **Broad Shelves** — the storage additions now actually apply. The `allowItem`
  flags were flipped in a Postfix, after `StorageBuilding.Awake` had already
  built the building's allowable-items list; moved to a Prefix so Granaries
  accept Hay/Flax and Stockyards accept Iron. The Granary→"Silo" rename also
  now sticks in the selection panel (re-applied on selection).
- **Surplus Selling** — rewritten to use the trading post's real stocking lever
  (`SetTargetStockAmount` + keep-in-stock) instead of storage min-quotas, which
  the post ignored and zeroed. Also fixed a double-count that progressively
  over-shipped and drained town storage below the keep-in-town floor. It now
  reads the post's actual trade inventory (`traderStorage`).
- **Consumable Control** — the reserve floor is now applied on the first day
  after you enable it (as soon as there's consumption data), instead of only on
  the monthly tick. Previously, enabling mid-month left food unprotected for up
  to a month, so a trading post could pull a protected item to zero.

### Changed
- **Consumable Control** — quota recompute is monthly with a per-storage value
  cache that skips unchanged writes (cheap on large maps; behavior unchanged).
- **Efficient Labor** — added `Groomer` (Dog/Cat Kennel workers) to the default
  occupation list, so idle kennel workers help haul like other idle crafters.

### Added
- **`InventoryDiagnostics`** cfg flag — when true, Consumable Control and
  Surplus Selling log per-item detail (rate, target, town-stock breakdown,
  what was written) to `Latest.log` for verification. cfg-only.

## 1.0.1 — 2026-05-22

### Fixed
- **Soil Wisdom** — the "⚠ N rotation issues — hover for details" line now
  actually shows its hover tooltip. The tooltip component was created from
  scratch without the game's `tooltipPrefab`, so FF silently skipped it; it now
  borrows the prefab/parent from a working tooltip already in the crop window.

### Changed
- **Consumable Control** — quota recompute moved from a daily tick to a monthly
  tick (the 30-day rolling average barely shifts day-to-day), and a per-storage
  value cache now skips redundant `SetMinQuotaForItem` writes when the target is
  unchanged. Big reduction in per-day work on large maps; behavior unchanged.

## 1.0.0 — 2026-05-15

First public release. Sixteen opt-in QoL features across four categories,
every one OFF by default. Live-toggle via Keep Clarity's settings panel
(press F10) or `MelonPreferences/EssentialProvisions.cfg`.

### Misc
- **Fast Forward** — speeds 5x–50x above vanilla's 3x cap
- **Service Bounds** — show building effect radius on demand
- **Clearcutting** — auto-flag trees/stones/forageables in Town Center radius

### Villager Improvements
- **Short Walks** — monthly re-housing so villagers live closer to work
- **Long Travels** — alert icon on production buildings with long worker commutes
- **Self Preservation** — villagers flee from nearby raiders without waiting
- **Labor Shortage** — top-bar badge when laborers/builders fall below recommended
- **Efficient Labor** — idle crafters/outdoor workers help out as laborers
- **Penny Pincher** — auto-stand-down rat catchers and peacetime guard towers
- **Idle Hands** — alert icon on production buildings with high idle time

### Inventory Management
- **Broad Shelves** — Stockyards accept Iron; Granaries accept Hay/Flax (renamed "Silo")
- **Surplus Selling** — auto-shunt excess stock to trading post
- **Consumable Control** — auto-configure trading post reserves from observed consumption
- **Project Prep** — construction material reserves for Logs/Planks/Stone/Clay/Sand/Iron/Coal

### Agriculture Alerts
- **Soil Wisdom** — rotation summary + disease/season tooltips on crop info window
- **Blight Watch** — persistent map pin on diseased fields until cleaned

### Infrastructure
- Foreign-mod detection: if any of FFQoL, FFAutomation, No More Slacking Off,
  Cowardly Villagers, or Fast Frontier are loaded, the overlapping EP features
  stay off automatically — no Harmony conflicts, no double-firing alerts.
- Keep Clarity SettingsAPI integration via reflection (soft-dep); panel grouping
  matches the four categories above.
- All features individually disable-able, no save-file changes, safe to add or
  remove at any time.
