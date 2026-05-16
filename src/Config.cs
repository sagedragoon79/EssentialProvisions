using MelonLoader;

namespace EssentialProvisions
{
    /// <summary>
    /// Central registry for every MelonPreferences entry. Feature files only
    /// ever read from these properties — they never create their own entries.
    /// All toggles default to false: this is an opt-in QoL pack.
    ///
    /// Layout convention:
    ///   ----- &lt;EP Section Name&gt; (folded from &lt;mod&gt; by &lt;author&gt;) -----
    ///   public static MelonPreferences_Entry&lt;...&gt; Enable&lt;Section&gt; { get; private set; } = null!;
    ///   public static MelonPreferences_Entry&lt;...&gt; &lt;Tunable&gt;       { get; private set; } = null!;
    ///
    /// Then in Initialize():
    ///   Enable&lt;Section&gt; = _root.CreateEntry(...);
    ///   &lt;Tunable&gt;       = _root.CreateEntry(...);
    ///
    /// Each fold also appends a corresponding registration block in
    /// KeepClarityIntegration.RegisterEntries() under its bucket
    /// (Alerts / Agriculture / Visual Aids).
    /// </summary>
    public static class Config
    {
        private static MelonPreferences_Category _root = null!;

        // ===== Alerts bucket =====

        // ----- Labor Shortage (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableLaborShortage { get; private set; } = null!;

        // ----- Long Travels (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableLongTravels    { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  LongTravelsThreshold { get; private set; } = null!;

        // ----- Idle Hands (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableIdleHands    { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  IdleHandsThreshold { get; private set; } = null!;

        // ===== Agriculture bucket =====

        // ----- Blight Watch (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableBlightWatch { get; private set; } = null!;

        // ----- Soil Wisdom (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableSoilWisdom { get; private set; } = null!;

        // ===== Visual Aids bucket =====

        // ----- Service Bounds (folded from FFQoL by idontcare) -----
        public static MelonPreferences_Entry<bool> EnableServiceBounds { get; private set; } = null!;

        // ===== Automation bucket =====

        // ----- Penny Pincher (folded from FFAutomation by idontcare) -----
        public static MelonPreferences_Entry<bool> EnablePennyPincher { get; private set; } = null!;

        // ----- Clearcutting (folded from FFAutomation by idontcare) -----
        public static MelonPreferences_Entry<bool>  EnableClearcutting        { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  ClearcuttingTrees         { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  ClearcuttingStones        { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  ClearcuttingForageables   { get; private set; } = null!;
        public static MelonPreferences_Entry<float> ClearcuttingRadius        { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>  ClearcuttingRescan        { get; private set; } = null!;

        // ----- Efficient Labor (folded from NoMoreSlackingOff + FFAutomation IdleFarmers) -----
        public static MelonPreferences_Entry<bool>   EnableEfficientLabor       { get; private set; } = null!;
        public static MelonPreferences_Entry<string> EfficientLaborOccupations  { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>   EfficientLaborVerbose      { get; private set; } = null!;

        // ----- Consumable Control (rate-based "Town Reserve" only — equivalent to FFAutomation Town Reserve) -----
        public static MelonPreferences_Entry<bool>   EnableConsumableControl     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>    ConsumableReserveMonths     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>    ConsumableHeadroomPercent   { get; private set; } = null!;
        public static MelonPreferences_Entry<string> ConsumableTrackedItems      { get; private set; } = null!;

        // ----- Surplus Selling (rate-based "Auto Stock" — shunt excess to trading post) -----
        public static MelonPreferences_Entry<bool>   EnableSurplusSelling        { get; private set; } = null!;
        public static MelonPreferences_Entry<int>    SurplusSellingMonths        { get; private set; } = null!;
        public static MelonPreferences_Entry<bool>   EnableSurplusSellingFood    { get; private set; } = null!;
        public static MelonPreferences_Entry<int>    SurplusSellingFoodMonths    { get; private set; } = null!;

        // ----- Self Preservation (folded from Cowardly Villagers by Olleus, with Hunter exclusion) -----
        public static MelonPreferences_Entry<bool>  EnableSelfPreservation   { get; private set; } = null!;
        public static MelonPreferences_Entry<float> SelfPreservationRadius   { get; private set; } = null!;

        // ----- Fast Forward (folded from Fast Frontier by Justin848) -----
        public static MelonPreferences_Entry<bool> EnableFastForward { get; private set; } = null!;

        // ----- Short Walks (inspired by Autocommute by Cleve; uses vanilla Hungarian for re-housing) -----
        public static MelonPreferences_Entry<bool> EnableShortWalks { get; private set; } = null!;

        // ----- Broad Shelves (inspired by Sensible Storage; extends storage acceptance) -----
        public static MelonPreferences_Entry<bool> EnableBroadShelves { get; private set; } = null!;

        // ----- Project Prep (folded from FFAutomation ConstructionReserve) -----
        public static MelonPreferences_Entry<bool> EnableProjectPrep   { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepLogs     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepPlanks   { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepStone    { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepClay     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepSand     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepIron     { get; private set; } = null!;
        public static MelonPreferences_Entry<int>  ProjectPrepCoal     { get; private set; } = null!;

        public static void Initialize()
        {
            _root = MelonPreferences.CreateCategory("EssentialProvisions");

            // ----- Labor Shortage -----
            EnableLaborShortage = _root.CreateEntry(
                "EnableLaborShortage", false,
                display_name: "Labor Shortage",
                description: "Show an alert badge on the top bar when laborers/builders fall below the recommended count.");

            // ----- Long Travels -----
            EnableLongTravels = _root.CreateEntry(
                "EnableLongTravels", false,
                display_name: "Long Travels",
                description: "Flag production buildings whose workers spend too much time traveling instead of producing.");

            LongTravelsThreshold = _root.CreateEntry(
                "LongTravelsThreshold", 60,
                display_name: "Long Travels Threshold (%)",
                description: "Trigger the Long Travels alert when travel time as a fraction of total work time exceeds this percent.");

            // ----- Idle Hands -----
            EnableIdleHands = _root.CreateEntry(
                "EnableIdleHands", false,
                display_name: "Idle Hands",
                description: "Flag production buildings whose workers spend too much time idle.");

            IdleHandsThreshold = _root.CreateEntry(
                "IdleHandsThreshold", 60,
                display_name: "Idle Hands Threshold (%)",
                description: "Trigger the Idle Hands alert when idle time as a fraction of total work time exceeds this percent.");

            // ----- Blight Watch -----
            EnableBlightWatch = _root.CreateEntry(
                "EnableBlightWatch", false,
                display_name: "Blight Watch",
                description: "Pin diseased crop fields on the map until you've addressed the disease.");

            // ----- Soil Wisdom -----
            EnableSoilWisdom = _root.CreateEntry(
                "EnableSoilWisdom", false,
                display_name: "Soil Wisdom",
                description: "Highlight risky crop sequences in the crop info window — disease/season conflicts, rotation warnings.");

            // ----- Service Bounds -----
            EnableServiceBounds = _root.CreateEntry(
                "EnableServiceBounds", false,
                display_name: "Service Bounds",
                description: "Click any placed building to see its effect radius (water / desirability / market / school / work area).");

            // ----- Penny Pincher -----
            EnablePennyPincher = _root.CreateEntry(
                "EnablePennyPincher", false,
                display_name: "Penny Pincher",
                description: "Auto-stand-down rat catchers when no infestations exist, and guard towers in peacetime — save gold on workers with nothing to do. Auto re-enables when threats reappear.");

            // ----- Clearcutting -----
            EnableClearcutting = _root.CreateEntry(
                "EnableClearcutting", false,
                display_name: "Clearcutting",
                description: "Auto-flag harvestable resources (trees / stones / forageables) within a radius of your Town Center. Skips resources near raiders, wolves, and bears so villagers don't get killed harvesting.");

            ClearcuttingTrees = _root.CreateEntry(
                "ClearcuttingTrees", true,
                display_name: "Clearcutting: Trees",
                description: "Auto-flag trees in radius for harvest.");

            ClearcuttingStones = _root.CreateEntry(
                "ClearcuttingStones", false,
                display_name: "Clearcutting: Stones",
                description: "Auto-flag stones in radius for harvest.");

            ClearcuttingForageables = _root.CreateEntry(
                "ClearcuttingForageables", false,
                display_name: "Clearcutting: Forageables",
                description: "Auto-flag forageables (berry bushes, herbs, mushrooms) in radius for harvest.");

            ClearcuttingRadius = _root.CreateEntry(
                "ClearcuttingRadius", 300f,
                display_name: "Clearcutting Radius",
                description: "Distance from Town Center within which resources are auto-flagged. Range 100-500.");

            ClearcuttingRescan = _root.CreateEntry(
                "ClearcuttingRescan", false,
                display_name: "Re-Scan Now",
                description: "Toggle ON to trigger a one-shot Clearcutting scan immediately. EP resets this to OFF after firing — acts like a button. Useful after expanding settlement, changing radius, or planting new trees you DO want flagged.");

            // ----- Efficient Labor -----
            EnableEfficientLabor = _root.CreateEntry(
                "EnableEfficientLabor", false,
                display_name: "Efficient Labor",
                description: "When a villager in an allowed occupation has nothing to do, register them as a low-priority laborer so they help with hauling/gathering instead of standing idle. Their actual job stays the priority — only fires when truly idle. Covers the 'farmers in winter,' 'sawyers with no logs,' 'foundrymen with no ore' cases.");

            EfficientLaborOccupations = _root.CreateEntry(
                "EfficientLaborOccupations",
                // Broad inclusion: all crafters, outdoor workers, builders.
                // Excludes combat (Guard/Soldier/TransitionToSoldier), always-on-call service
                // (Healer/Teacher/Priest/Trader/Grocer/Publican/Librarian/Scholar/Guildmaster),
                // already-laborer roles (Laborer/WorkCampLaborer/NightsoilMan),
                // special states (Child/Disabled/Deserter/None), DLC niches (Groomer),
                // and RatCatcher (Penny Pincher covers it).
                "Hunter,Builder,Woodcutter,Sawyer,Farmer,Baker,Tanner,Miller,Miner,Foundryman,Blacksmith,Fletcher,Fisherman,Cobbler,Smoker,Weaver,CharcoalMaker,Potter,BoatBuilder,Forager,Brewer,Wainwright,BasketMaker,Herder,FurnitureMaker,SoapMaker,Chandler,Glassmaker,Brickmaker,Cheesemaker,Cooper,Apothecary,Armourer,Arborist,Preservist,Papermaker,BookBinder",
                display_name: "Efficient Labor: Allowed Occupations",
                description: "Comma-separated list of FF Occupation enum names. Villagers in these occupations will be redirected to laborer work when truly idle. Edit this cfg field directly to add/remove occupations — not surfaced in the KC panel to keep it uncluttered. Restart the game for changes to apply.");

            EfficientLaborVerbose = _root.CreateEntry(
                "EfficientLaborVerbose", false,
                display_name: "Efficient Labor: Verbose Logging",
                description: "When true, logs each villager (occupation) as they're registered to / cleared from the laborer pool. Default false logs only a once-per-day summary line, and only on days where something changed. cfg-only; not surfaced in the KC panel.");

            // ----- Consumable Control -----
            EnableConsumableControl = _root.CreateEntry(
                "EnableConsumableControl", false,
                display_name: "Consumable Control",
                description: "Auto-configure 'town reserve' min quotas on your granaries / root cellars / storehouses based on observed daily consumption × months. Trading posts and any other outbound logistics can only access surplus above the floor — villagers continue eating freely. Auto-scales with population.");

            ConsumableReserveMonths = _root.CreateEntry(
                "ConsumableReserveMonths", 3,
                display_name: "Consumable Control: Reserve Months",
                description: "Months of consumption to keep on hand for each tracked item across food + general storages.");

            ConsumableHeadroomPercent = _root.CreateEntry(
                "ConsumableHeadroomPercent", 10,
                display_name: "Consumable Control: Growth Headroom (%)",
                description: "Padding applied to the rate-based target, to anticipate population growth (births, immigration). 10% means the actual reserve = computed × 1.10. Range 0–50.");

            // ----- Surplus Selling -----
            EnableSurplusSelling = _root.CreateEntry(
                "EnableSurplusSelling", false,
                display_name: "Surplus Selling",
                description: "Auto-shunt excess stock above the keep-in-town threshold to your trading post(s), based on observed daily consumption × months. Non-food items only by default. Pairs naturally with Consumable Control — if both enabled, granaries protect the reserve floor while trading posts absorb the surplus.");

            SurplusSellingMonths = _root.CreateEntry(
                "SurplusSellingMonths", 6,
                display_name: "Surplus Selling: Keep-in-Town Months (non-food)",
                description: "How many months of stock to keep in town storages for each non-food tracked item. Anything beyond this gets sent to trading posts.");

            EnableSurplusSellingFood = _root.CreateEntry(
                "EnableSurplusSellingFood", false,
                display_name: "Surplus Selling: Include Food",
                description: "Opt-in: also auto-ship food surplus. Higher default keep-in-town months than non-food because FF rewards 10+ months of food surplus with immigration. Diet-variety safety nets are always on when this is enabled: total food must exceed the aggregate threshold AND each individual food type stays at ≥ 1 month of its own consumption.");

            SurplusSellingFoodMonths = _root.CreateEntry(
                "SurplusSellingFoodMonths", 10,
                display_name: "Surplus Selling: Keep-in-Town Months (food)",
                description: "How many months of stock to keep in town storages for each food item. Default 10 matches FF's immigration threshold — recommended floor.");

            // ----- Self Preservation -----
            EnableSelfPreservation = _root.CreateEntry(
                "EnableSelfPreservation", false,
                display_name: "Self Preservation",
                description: "Villagers flee from nearby raiders without waiting for fighting to break out near them. Broader detection radius than vanilla. Hunters are excluded (they keep vanilla short-range behaviour so they'll engage when on the hunt). Raiders defending their own encampment don't trigger retreat — they're not threatening the town.");

            SelfPreservationRadius = _root.CreateEntry(
                "SelfPreservationRadius", 90f,
                display_name: "Self Preservation: Detection Radius",
                description: "Distance in tiles within which an aggressive raider triggers retreat. Default 90 matches the original Cowardly Villagers mod. Vanilla is 50.");

            // ----- Fast Forward -----
            EnableFastForward = _root.CreateEntry(
                "EnableFastForward", false,
                display_name: "Fast Forward",
                description: "Unlock game speeds above vanilla's 3x cap. Adds intermediate steps and a 50x ceiling. Above 20x, hunter and combat AI may misbehave — a warning is logged. Toggle takes effect immediately; no restart required.");

            // ----- Short Walks -----
            EnableShortWalks = _root.CreateEntry(
                "EnableShortWalks", false,
                display_name: "Short Walks",
                description: "Once a month, re-optimize villagers' home assignments to live closer to their workplaces (re-housing, not re-jobbing). Uses FF's built-in Hungarian algorithm — pairs naturally with the Long Travels alert. Soldiers, Guards, and TransitionToSoldier are excluded so their garrison linkage isn't disturbed. Villagers whose workplace IS their residence (live-at-work occupations) are also automatically skipped.");

            // ----- Broad Shelves -----
            EnableBroadShelves = _root.CreateEntry(
                "EnableBroadShelves", false,
                display_name: "Broad Shelves",
                description: "Expand storage acceptance + cosmetic rename: Stockyards accept Iron, Granaries accept Hay and Flax, and Granaries are renamed 'Silo' in all UI. Inspired by Sensible Storage (Olleus). Zero performance cost — flag flips at building Awake.");

            ConsumableTrackedItems = _root.CreateEntry(
                "ConsumableTrackedItems",
                // Curated default: foods, heating, clothing, hygiene/health/light, housing luxuries
                "Bread,RootVegetable,Beans,Greens,Berries,Fruit,Nuts,Mushroom,Roots,Honey,Eggs,Fish,SmokedFish,Meat,SmokedMeat,Cheese,Pastry,Preserves,PreservedVeg," +
                "Firewood,Coal," +
                "HideCoat,Shoes,LinenClothes," +
                "Soap,Medicine,Candle," +
                "Furniture,Pottery,Books",
                display_name: "Consumable Control: Tracked Items",
                description: "Comma-separated list of ItemID names that Consumable Control will manage. Edit the cfg field directly to add/remove items — not surfaced in the KC panel. Restart for changes to apply.");

            // ----- Project Prep -----
            EnableProjectPrep = _root.CreateEntry(
                "EnableProjectPrep", false,
                display_name: "Project Prep",
                description: "Reserve construction materials from being consumed by builds. Each per-resource floor is a 'never go below this' threshold — when stockpile drops to the reserve, construction queues stop pulling that material. Useful when you want to keep emergency stone for walls, logs for repairs, etc.");

            ProjectPrepLogs   = _root.CreateEntry("ProjectPrepLogs",   0, display_name: "Project Prep: Logs Reserve",
                description: "Minimum logs to keep on hand — construction stops pulling logs below this floor. 0 = no reserve.");
            ProjectPrepPlanks = _root.CreateEntry("ProjectPrepPlanks", 0, display_name: "Project Prep: Planks Reserve",
                description: "Minimum planks to keep on hand. 0 = no reserve.");
            ProjectPrepStone  = _root.CreateEntry("ProjectPrepStone",  0, display_name: "Project Prep: Stone Reserve",
                description: "Minimum stone to keep on hand. 0 = no reserve.");
            ProjectPrepClay   = _root.CreateEntry("ProjectPrepClay",   0, display_name: "Project Prep: Clay Reserve",
                description: "Minimum clay to keep on hand. 0 = no reserve.");
            ProjectPrepSand   = _root.CreateEntry("ProjectPrepSand",   0, display_name: "Project Prep: Sand Reserve",
                description: "Minimum sand to keep on hand. 0 = no reserve.");
            ProjectPrepIron   = _root.CreateEntry("ProjectPrepIron",   0, display_name: "Project Prep: Iron Reserve",
                description: "Minimum iron to keep on hand. 0 = no reserve.");
            ProjectPrepCoal   = _root.CreateEntry("ProjectPrepCoal",   0, display_name: "Project Prep: Coal Reserve",
                description: "Minimum coal to keep on hand. 0 = no reserve.");
        }
    }
}
