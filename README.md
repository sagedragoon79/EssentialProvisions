# Essential Provisions

*A curated, opt-in quality-of-life bundle for Farthest Frontier.*

Seventeen features across four categories, every one OFF by default. Open the in-game settings panel, pick what you want, ignore the rest.

## Why a bundle?

Stacking ten standalone mods to get the QoL features you actually want comes with friction: each mod ships its own preference UI, each adds another Harmony conflict surface, and most go unmaintained when their author moves on. Essential Provisions folds the best small QoL ideas from the FF mod community into one coordinated package — consistent UX, shared safeguards against double-firing patches, and a single in-game settings panel.

If you've already got one of the source mods installed, EP detects it at startup and silently defers — the relevant features stay off so nothing collides.

## Features

### Misc

- **Fast Forward** — Unlock game speeds above vanilla's 3x cap. Speeds 5x through 50x become available. Above 20x, hunter and combat AI may misbehave (you'll get a log warning). Toggle is live, no restart needed.
- **Service Bounds** — Click any placed building to see its effect radius — water provision, market reach, desirability bonus, school enrollment range, work area. The same circle FF shows during placement, available on demand for buildings already built.
- **Clearcutting** — Auto-flag trees, stones, and forageables for harvest in a radius around your Town Center. Skips danger zones near raiders, wolves, and bears. Triggers on toggle-on, new TC placement, or a one-shot "Re-Scan Now" button — never continuously pulses, so the decoration trees you plant don't get auto-tagged for chopping.

### Villager Improvements

- **Short Walks** — Once a month, re-optimize villagers' home assignments so they live closer to their workplaces. Uses FF's built-in Hungarian algorithm. Soldiers, Guards, and live-at-workplace occupations (healer in healer's house, priest in church, etc.) are automatically skipped — their garrison and workplace linkages stay intact.
- **Long Travels** — A small icon appears on production buildings whose workers spend too much time traveling instead of producing. Tunable threshold.
- **Self Preservation** — Villagers flee from nearby raiders without waiting for combat to break out near them. Broader detection radius than vanilla. Hunters keep their short-range vanilla behavior so they continue to engage prey on the hunt and respond when ordered to fight. Camp-defending raiders don't trigger flee — only raiders actively threatening your town.
- **Labor Shortage** — Top-bar badge when your laborer and builder counts fall below the recommended count for your settlement size. Surfaces the common late-game pitfall of "everyone assigned to professions, no one hauling logs."
- **Efficient Labor** — Idle villagers in crafting and outdoor roles temporarily help out as laborers when their own job has nothing to do. Sawyer with no logs goes hauling. Farmer in winter goes hauling. Their actual job stays the priority — they snap back the moment real work appears at their workplace.
- **Learned Hands** — Educated villagers work faster at every job. FF's own guide says educated workers are more efficient, but the game never actually implements it — education only gates a handful of jobs. This adds the missing work-rate bonus: each educated villager gets a multiplicative speed-up (default +10% per education level) on everything they do, from chopping wood to baking bread. Per-worker, so a building's output scales with its workers' education, and it applies retroactively to villagers already educated. Per-level bonus is tunable.
- **Penny Pincher** — Auto-stand-down rat catchers when no infestations are active, and guard towers in peacetime. Re-enables automatically when threats reappear, with a 2-day cooldown after raids end to avoid flickering. Won't touch a building you currently have selected.
- **Idle Hands** — A small icon appears on production buildings whose workers spend too much time idle. Tunable threshold.

### Inventory Management

- **Broad Shelves** — Stockyards accept Iron. Granaries accept Hay and Flax. Granary is renamed "Silo" throughout the UI.
- **Surplus Selling** — Auto-shunt excess stock above your keep-in-town threshold to your trading post, based on observed daily consumption rate × months. Non-food only by default. An opt-in food sub-toggle includes two safeguards: total food stock must exceed an aggregate threshold (defaults to 10 months, matching FF's immigration bonus), and no individual food type is ever shipped below 1 month of its own consumption — so you can't accidentally ship out all the bread while berries are scarce.
- **Consumable Control** — Auto-configure trading post stocking minimums based on observed daily consumption × months × growth headroom. Trading post workers won't pull an item from storage to stock the post unless your town supply is above the reserve. (Anything already sitting in the trading post is unavailable to villagers regardless — that's vanilla FF behavior.) Auto-scales with population, no manual per-item retuning when your town grows.
- **Project Prep** — Construction material reserves for Logs, Planks, Stone, Clay, Sand, Iron, and Coal. When stockpile drops to the configured floor, production buildings stop pulling that material. Construction can still access the reserve — exactly what "reserved for construction" should mean.

### Agriculture Alerts

- **Soil Wisdom** — Crop info window gains a rotation summary line plus hover tooltip detailing disease and seasonal risks. Catches all five vanilla disease groups (Turnip/Cabbage, legume rotations, grain rotations, etc.) and flags frost-vulnerable spring plantings and heat-vulnerable summer crops.
- **Blight Watch** — Pin diseased crop fields on the map until the disease is addressed. The pin stays visible until the field is clean, instead of fading after the initial event.

### Planting Almanac

- **Planting Almanac** — Reusable crop-rotation templates right on the crop field window. Pick from curated disease-safe presets (built and verified against EP's own rotation rules) or your own saved rotations, and **Apply** to set the field's whole schedule — crops *and* field-maintenance periods — in one click. **Save** captures the open field's rotation (optionally name it) as a shareable JSON in `UserData/EP_CropTemplates/`; the library is global to the player, so your rotations follow you to every game and can be shared by copying the files. **Delete** removes your own templates (presets are protected). An original EP feature, not a fold.

## Installation

1. Install [MelonLoader](https://melonwiki.xyz/) (Mono build) for Farthest Frontier.
2. Download `EssentialProvisions.dll` from [Releases](https://github.com/sagedragoon79/EssentialProvisions/releases).
3. Copy it to: `<game folder>\Farthest Frontier (Mono)\Mods\`
4. Launch the game.

## Configuration

EP integrates with **Keep Clarity** for an in-game settings panel — press `F10` to open it. Features can be toggled live without restarting; some changes (like Broad Shelves' building flag flips) require a save reload to apply to existing buildings.

Without Keep Clarity installed, EP still works — configuration goes through `MelonPreferences/EssentialProvisions.cfg` directly.

## Compatibility

- Built for **Farthest Frontier v1.1.0 (Mono)**.
- No save-file changes — safe to add or remove at any time.
- Foreign-mod detection: if you have FFQoL, FFAutomation, No More Slacking Off, Cowardly Villagers, or Fast Frontier installed separately, EP detects them at startup and the overlapping features stay off automatically.
- Tested alongside: Keep Clarity, Warden of the Wilds, Tended Wilds, Forageable Transplantation, Rivers Restored, Manifest Delivery.

## Credits

Essential Provisions builds on the work of several community modders. Mod authors retain credit for the original ideas — EP adapts, curates, and extends them into a unified package. If you're a mod author whose work is folded here and you'd prefer it removed, please open an issue — happy to strip cleanly.

- **idontcare** — **FFQoL** (Labor Shortage, Long Travels, Idle Hands, Blight Watch, Soil Wisdom, Service Bounds) and **FFAutomation** (Penny Pincher, Clearcutting, Efficient Labor's Winter Labor subset, Project Prep)
- **Cuteling** — **No More Slacking Off** (primary mechanism behind Efficient Labor)
- **Olleus** — **Sensible Storage** (Broad Shelves' Granary additions) and **Cowardly Villagers** (Self Preservation)
- **Justin848** — **Fast Frontier Speed Mod** (Fast Forward)
- **Cleve** — **Autocommute** (inspiration for Short Walks' approach)

A few features are original EP designs built directly on FF's own internal systems: Consumable Control and Surplus Selling drive FF's trading-post quota system from observed consumption telemetry, Short Walks leverages FF's already-existing (but underused) Hungarian assignment infrastructure to expand its scope to all villagers, the Planting Almanac builds reusable crop-rotation templates on the crop-field window, and Learned Hands restores the educated-worker efficiency bonus the official guide describes but the assembly never implemented.

## Build

```
dotnet build src/EssentialProvisions.csproj -p:Configuration=Release -p:Platform=x64 -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Farthest Frontier"
```

Output: `src/bin/Release/net46/EssentialProvisions.dll` — auto-staged to `<game>\Farthest Frontier (Mono)\Mods\` on every successful build.

## Author

SageDragoon · [Steam Workshop](https://steamcommunity.com/profiles/sagedragoon79) · [GitHub](https://github.com/sagedragoon79)
