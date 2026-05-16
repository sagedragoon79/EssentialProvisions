# Changelog

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
