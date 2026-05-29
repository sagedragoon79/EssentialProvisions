# Changelog

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
