# Give every job a Workplace Mastery benefit

> **STATUS: SHIPPED in v1.7.0 (October 21, 2026), commit 48b0a76, user-tested.**
> Below is the record of what was built and why.
>
> **Decisions the user made:** the recommended caps (production 25%, service and care 10%,
> combat 10% damage and 10% damage reduction, trade 5%); combat behind its own sub-toggle,
> `WorkplaceMasteryCombat`, default on; **hunters get the combat damage boost** (damage only,
> not damage reduction); publicans and librarians raise entertainment points; no global carry
> or walking bonus; trader mastery steps aside while Merchant's Gambit is on. One change from
> the plan below: the **Grocer walks faster** instead of carrying more, because each market
> trip serves one home and already carries far more than that home needs.
>
> **Done:**
> - Fix 1, save identity: `src/Common/SaveIdentity.cs`, shared by Workplace Mastery and Penny
>   Pincher. The bug's real shape: manual saves were fine; every session loaded from an autosave
>   shared one `AutoSave 1.json` across all towns. The fix also needed `SaveManager.lastGameFolder`
>   (FF nulls the save name after every autosave) and a deferred write for a new town's first
>   save (its event fires before FF creates the folder).
> - Fix 2, stale docs: the `WorkplaceMastery.cs` header and a dated correction note in
>   `_research/mastery-coverage-map.md` (that folder is gitignored).
> - All 16 effects plus hunter damage: `src/Features/WorkplaceMasteryEffects.cs`. Every target
>   is patched by hand under its own try/catch; all 30 names, fields, and parameters were
>   checked against the installed `Assembly-CSharp.dll` with a reflection-only load. Changes from
>   the hooks below: entertainment and spirituality redo HappinessManager's final float sum
>   (the per-building getters are `int`, so 10% would round away); pet births carry a
>   fractional bonus per herd and spawn extras through `Herd.SpawnLivestock` (the guaranteed
>   minimum also rounds); the teacher shortens the lesson timer only, then restores vanilla's
>   `remainingTeachingTime`, so nothing compounds.
> - `WorkInfoApi.GetMasteryEffectText` and `GetMasteryEffectPercent`, additive.
> - Setting text for every job, plus the combat sub-toggle in Config and the KC panel.
> - Coordination notes: `FFUIOverhaul/_handoffs/2026-10-15_ep-mastery-effects.md`,
>   `WardenOfTheWilds/_handoffs/2026-10-15_ep-mastery-touches-fishing-hunters-kennels.md`,
>   `SovereignBoons/_handoffs/2026-10-15_ep-reads-merchants-gambit-pref.md`.
>
> **To test:** on load, the log shows `Job effects attached (16/16)`. Then compare a veteran
> against a fresh worker: time a build, count ore per month at one mine, time a lesson, watch a
> grocer's walking speed, and check trade prices with Merchant's Gambit on and off. A villager
> with no tenure shows no change; effects grow 1/25 of their cap per year.

**Date:** October 15, 2026
**From:** the Keep Clarity thread
**Owner:** EP (Workplace Mastery)
**Goal, in the user's words:** "I want all the jobs to benefit in some way by mastery."

## Summary

Workplace Mastery has one effect today, a work-speed bonus, and it only reaches
two game ticks. Sixteen working jobs never touch either tick, so their tenure
counts up and does nothing. This handoff covers four things:

- What each job gets today.
- A proposed effect for each of the 16 jobs, with a hook verified to exist in
  the game code.
- Two fixes to make first.
- The decisions the user still needs to make.

Line numbers refer to
`C:\Users\saged\ClaudeCodeLocalSessions\AssemblyCSharp_Decompiled_2026-06-10\Assembly-CSharp.decompiled.cs`.
That decompile matches the installed game build (Assembly-CSharp.dll dated June
10, 2026). Everything here comes from reading that code. Nothing has been timed
in game.

## How mastery works today

- **Rate:** +1% per in-game year in a job, capped at +25% after 25 years
  (`WorkplaceMastery.WorkOutputPerYear`, `TenureCapYears`). Each job keeps its
  own tally. The feature is on/off only, with no sliders, and it multiplies with
  Learned Hands.
- **Accrual:** tenure builds for every working job, including Laborer, Builder,
  and Soldier. Only Child, Disabled, Deserter, TransitionToSoldier, and None are
  excluded. Keep Clarity's worker picker and Staff by Mastery already rank
  villagers by tenure, so it matters for hiring even where it has no effect yet.
- **Hook 1, `WorkRatePatch`** (postfix on
  `HappinessManager.GetWorkRateMultiplier(Villager)`): its only gameplay
  consumer is `ReservableItemStorage.DetachItemsFromReservation` (L165264). Only
  `CollectResourcesSubTask` (L193467) can pass `ignoreDetachmentCosts: false`,
  and the bonus only matters when the item has a detach cost. Hauling finished
  goods has no cost. Seven of the 146 `SearchDefinition`s opt out: every crop
  harvest and orchard fruit (`SearchDefinitionManager`, L31689–32303).
- **Hook 2, `ManufacturePatch`** (postfix on
  `ManufactureWorkOrder.AdjustAddedWorkUnits`): reached through
  `ManufactureProductsSubTask` (L197644), which every
  `VillagerOccupationManufacturer` subclass runs.

### Coverage by job

| Coverage | Jobs |
|---|---|
| Full (crafting) | Apothecary, Armourer, Baker, Basket Maker, Blacksmith, Bookbinder, Brewer, Brickmaker, Chandler, Charcoal Maker, Cheesemaker, Cobbler, Cooper, Fletcher, Foundryman, Furniture Maker, Glassmaker, Miller, Papermaker, Potter, Preservist, Sawyer, Smoker, Soap Maker, Tanner, Weaver, Woodcutter (firewood), Boat Builder (Dock, unused in vanilla) |
| Partial | Laborer and Work Camp Laborer (felling, rocks, clearing, wild forage, clay, exploring, excavating, salvaging, burying; not hauling) · Forager (foraging) · Farmer (tilling, planting, upkeep, apiary honey and wax; not harvest) · Arborist (planting and felling fruit trees; not picking) · Hunter (butchering; not hunting or traps) · Herder (slaughter work orders; not herding, milking, or feeding) · Wainwright (building wagons; not driving) · Nightsoil Man (composting; not collecting) |
| None | Builder, Miner, Fisherman, Soldier, Guard, Healer, Teacher, Scholar, Priest, Publican, Librarian, Grocer, Trader, Guildmaster, Rat Catcher, Groomer |

Why the jobs with no effect miss both hooks:

- **Builder:** construction detaches with `ignoreDetachmentCosts: true`
  (`WorkOnConstructionSubTask`, L202758). Repairs take the same path:
  `ConstructionGlobalTaskSearch` (L202998) serves
  `WorkBucketIdentifier.BuildingNeedingUpkeepWork`.
- **Miner:** `Mine.UpdateOreProduction` (L351824) adds
  `orePerMinerPerMonth × numOccupants`, a headcount rate.
- **Fisherman:** fishing runs on a catch timer in `FishFromShoreSubTask`
  (L195527).
- **Soldier and Guard:** combat never reads the work rate. Tower guards fire
  through the tower's own `CombatComponent`.
- **Service jobs:** each one's output is a building-level number, not
  per-worker speed.

## Fix these first

### 1. Tenure persistence: likely save-identity bug (verify)

`WorkplaceMastery.SaveName()` keys `MasteryStore` by
`Path.GetFileNameWithoutExtension(SaveManager.activeSaveFileName)`. It
special-cases `f == "AutoSave 1" || f == "AutoSave 2"`.

`activeSaveFileName` has three shapes for one town: `Town_ts/Town`,
`Town_ts/Town.sav`, and `Town_ts/AutoSave 1`. See the "Per-save files" entry
in the FF modding guide memory (`ff_modding_guide.md`, about line 207). The
autosave check compares the whole string, so it likely never matches the
folder-prefixed form. If that's right:

- Every town's autosaves share one `AutoSave 1` tenure file.
- Reloading a manual save after autosaves reads a stale file, so mastery
  appears to revert.

This is EP's open item in the fleet save-identity audit.

To fix it, use the pattern from Manifest Delivery v1.0.21:

- Key on the town with `SaveManager.GameFolder(name).TrimEnd('/')`.
- Latch the last non-empty value; the static goes empty during save and scene
  transitions.
- Migrate the old file names once so existing tenure isn't lost.
- Share the helper with `PennyPincher`, which copies `SaveName` verbatim.

### 2. Stale docs

- The header comment in `src/Features/WorkplaceMastery.cs` (lines 1–3) says
  +2% per year, capped at +10%, tunable. The constants are +1% per year, capped
  at +25%, with no sliders.
- `_research/mastery-coverage-map.md` says mining and repairs are covered.
  Neither is; see the coverage table above. Add a dated correction note rather
  than rewriting the doc.

## Proposed effects for the 16 jobs

Shared rules (a proposal; the caps are the user's call):

- **Scale:** keep one tenure scale, `t = min(years, 25) / 25`. Each effect has
  its own cap, and the effect is `t × cap`. Crafting and gathering keep their
  +25%.
- **Building-level effects:** use the average `t` of the building's current
  workers, or of the occupants inside for mines and towers.
- **Suggested caps:**
  - +10% for service, knowledge, and care effects.
  - +10% damage and +10% damage reduction for combat.
  - 5% for trade prices.

| Job | Effect | Hook (verified) | Notes |
|---|---|---|---|
| Builder | Builds and repairs faster | Postfix `Villager.IsTimeToDetachItems(out uint numIntervalsPassed)` (L388314). Add fractional extra intervals, carried per villager. | The same timer drives gathering, crafting, and pub drinking (`CollectResourcesSubTask`, `ManufactureProductsSubTask`, `ConsumeBeerSubTask`). Gate on Builder, and ideally on a flag set by a prefix on `WorkOnConstructionSubTask.OnArrivedUpdate`, so nothing counts twice. |
| Miner | More ore per month | Prefix `Mine.UpdateOreProduction(float elapsedSec)` (protected, L351824). Scale `elapsedSec` by the average mastery of the miners inside. | Covers MineralSiteMine, ClayPitBuilding, SandPitBuilding, and StonePitBuilding. Stacks with the Miners' Guild multiplier. |
| Fisherman | Catches sooner | Postfix `FishFromShoreSubTask.StartFishing()` (private, L195717). Divide the rolled `catchFishTime`. | Also `FishFromBoatSubTask.StartFishing()` (L195399; docks are dormant). WotW's `FishingShackPatches` writes `timeBetweenFishMin/Max` for Angler and Creeler, so scale the rolled time and never write those fields. |
| Soldier | More damage, less damage taken | Postfix `TechTreeManager.GetCombatMultipler(Villager, CombatModify.Type)` (L215605). | `Attack` feeds melee and ranged damage (`CombatComponent`, L67954 and L67968). `Defense` reduces incoming damage (`DamageableComponent`, L75356), and vanilla always returns 0 for it. So one postfix covers both, which replaces the earlier `TakeDamage` prefix idea. Hunters reach the default branch; gate them out, since WotW owns hunter combat. It runs per hit, so read from the day cache. |
| Guard | Towers hit harder | Postfix `CombatComponent.GetRangedDamage(RangedWeaponData)` (protected, L67962) when the component sits on a `CombatResidence` (`GuardTower`). Scale by the average mastery of the guards inside. | Tower shots pass no villager (`GetComponent<Villager>()` is null on a tower), so the soldier hook misses them. Each guard inside adds a target (`maxNumTargets = numOccupants`, L340506). Field guards get the soldier hook if you include them. |
| Healer | Patients heal faster; optionally, better cure odds | Postfix the `HealersHouse.healFactor` getter (L346129). For cure odds, see `_research/nonproducer-mastery-design.md` (a `TriageCenter` cureScore modifier, L364603). | Any cure-odds change must also go into `Common/CureMath.cs`, so `DiseaseInfoApi` (and Keep Clarity's sick-info line) stays accurate. |
| Teacher | Lessons finish sooner | Prefix `School.StartLessonTeaching(Action)` (L356211). Shorten the private `remainingTeachingTime`; the teacher is the private `teachingTeacher`. | `StopLessonTeaching` saves the remaining time so the lesson can resume. Shorten only a fresh lesson, or the bonus compounds. Optional: lesson planning (`remainingPlanningTime`, `planningTeacher`). |
| Scholar | More research points | Postfix `Academy.GetDailyWorkerKPUs()` (private, L328230). Scale by the average worker mastery. | `CalcMonthlyKnowledgePointUnits()`, the UI forecast, calls it too, so the forecast stays consistent. |
| Priest | More spirituality | Postfix `Building.spiritualityPoints` (virtual, L331766) and the `Temple` override (L360132). | `HappinessManager.UpdateSpiritualityBuildingPoints` (L382690) sums coverage × points town-wide. Scale the points, not the radius; the radius feeds cached residence lists. |
| Publican | More entertainment | Postfix the `Building.entertainmentPoints` getter for pubs. | `UpdateEntertainmentBuildingPoints` (L382615) sums them the same way. Alternatives: more tax per beer (`Pub.UpdateYearlyTaxMult`, L353294) or less beer used. |
| Librarian | Fewer books lost | Prefix `Library.OnMonthPassedEvent`. Vanilla adds `min(stock, booksLostPerMonth(population))` to `bookLostBookAccumulator` (L348015); subtract that amount × the bonus first. | Alternative: entertainment points, as for the pub. |
| Grocer | Carries more per trip | Postfix `VillagerOccupationGrocer.GetCarryCapacityModifier()` (L395547) or `Villager.GetCarryCapacity()` (L387455). | Market numbers are in the `ff_market_logistics.md` memory. |
| Trader | Better prices | Postfix `TradeManager.GetItemPriceModifier(Item, bool isSelling)` (L225725). Raise what the player gets when selling (`isSelling: true`, per the trading post UI at L264011) and lower what they pay when buying. | Use the best trader at any trading post. Sovereign Boons' Merchant's Gambit also patches trade prices; agree on the order with that thread and keep the cap small. |
| Guildmaster | Stronger guild bonus | Postfix `GuildManager.GetMultiplierForNumberOfGuildmasters()` (L111076). Scale by the average guildmaster mastery. | Feeds every guild multiplier through `AddGuildmasterMult`, and those already multiply crafting and mining output, which mastery also boosts. Keep the cap modest. |
| Rat Catcher | Clears rats faster | In `CatchRatsSubTask` (L193056), scale the `timeAtTarget` accumulation against `timeToKillRats`. | Alternative: the `RatCatcherBuilding.workRadius` getter (L353402, patrol radius); check whether it's cached first. |
| Groomer | More pet births | `Herd.CheckSubSeason()` (protected, L39770). Births use `minimumBirthsByAdultCount` × (1 + `geHerdBirthRateMultipliers[herdSetupData]`); scale by the home building's staff mastery. | `Kennel : LivestockBuilding`, so pets are a herd. The same hook gives Herders a stronger second benefit. WotW is the de facto Pets DLC mod, so tell the WotW thread. |

## Optional: a small bonus for every job

No effect above touches hauling or walking. To cover them for every job:

- **Carry:** postfix `Villager.GetCarryCapacity()` (L387455), with a small cap
  such as 5%. Don't use `itemDetachUnitsPerInterval`: it feeds both hauling and
  crafting, so it would count twice.
- **Walking:** postfix the `Character.movementSpeed` getter (L379935), with a
  small cap such as 3%. Don't write `curOccupationalSpeedBonus`: vanilla Hunter
  and Soldier code and WotW's `HunterCabinEnhancement` all write that field. The
  getter is hot, so read from the day cache.

## Partial jobs: leave as is

These jobs already benefit, so the remaining gaps can wait:

- **Crop harvest and fruit picking** would need a bespoke harvest hook: high
  risk for little gain.
- **Hunting** belongs to WotW.
- **Wagon driving** belongs to Manifest Delivery, which sets wagon speed and
  capacity per mode. A mastery bonus would double up.

## Implementation notes

- **Cache first:** build the day-tick mastery cache before the effects (Phase 0
  in `_research/nonproducer-mastery-design.md`). Compute each villager's `t`
  once per day and on job changes, so the combat, walking, and carry hooks cost
  one dictionary lookup.
- **EP rules:** patches never throw into game code, and errors log once. The
  feature stays behind `EnableWorkplaceMastery`, OFF by default. Whether combat
  gets its own toggle is open.
- **Keep Clarity contracts:** don't rename or remove `WorkInfoApi` members.
  Optionally, add `string GetMasteryEffectText(Villager)`, returning localized
  text such as "Lessons 6% faster", so Keep Clarity can show what mastery does
  for each job. It shows a generic "+X% Mastery" today.
- **Setting text:** update the setting's description (`src/Config.cs`, around
  line 382) to say what mastery does for each job. Keep Clarity's language packs
  translate EP's setting text
  (`EssentialProvisions/opt/EnableWorkplaceMastery/...`), so tell the Keep
  Clarity thread when the English changes.

## Open decisions for the user

1. What cap should each effect have?
2. Should combat get its own toggle? Should hunters be included, or left to
   WotW?
3. For publicans and librarians: entertainment points, or the beer and book
   economy?
4. Should every job get the small carry and walking bonus?
5. For traders: stack with Merchant's Gambit, or step aside when it's active?

## Fleet coordination

- **WotW:** fishing timers, the hunters' speed field, pets and kennels.
- **Sovereign Boons:** Merchant's Gambit trade prices.
- **Manifest Delivery:** wagons; leave driving alone.
- **Keep Clarity:** shows mastery and reads tenure for hiring. Tell it about any
  new `WorkInfoApi` member or setting text.

## Workflow

- Build Debug with `dotnet build -c Debug -p:Platform=x64`, then restart
  Farthest Frontier to load it.
- Check each effect in game where you can measure it: time a build, count ore
  per month, or watch a lesson timer.
- Don't bump the version, commit, or release until the user tests and asks.
