using System;
using System.Reflection;
using MelonLoader;

namespace EssentialProvisions
{
    /// <summary>
    /// Optional integration with Keep Clarity's settings panel. If
    /// KeepClarity.dll isn't installed, every method here is a no-op and EP
    /// runs unchanged (prefs still readable from MelonPreferences cfg). If KC
    /// IS installed, our prefs render with rich labels, tooltips, sliders,
    /// per-bucket groups, and VisibleWhen gating so sub-prefs hide when
    /// their master toggle is off.
    ///
    /// All access to Keep Clarity is reflective — no compile-time reference,
    /// so this file ships standalone without adding KeepClarity.dll as a
    /// hard build dependency.
    ///
    /// Pattern is the canonical KC integration template (see
    /// WardenOfTheWilds/KeepClarityIntegration.cs).
    /// </summary>
    internal static class KeepClarityIntegration
    {
        private static bool _resolved;
        private static bool _present;
        private static MethodInfo? _registerMod;
        private static MethodInfo? _registerEntry;
        private static Type? _settingsMetaType;

        private const string ModId = "EssentialProvisions";
        private const string ModDisplayName = "Essential Provisions";

        // Bucket group strings — used as the SettingsMeta.Group field.
        private const string GroupMisc        = "Misc";
        private const string GroupVillagers   = "Villager Improvements";
        private const string GroupInventory   = "Inventory Management";
        private const string GroupAgriAlerts  = "Agriculture Alerts";
        private const string GroupAlmanac     = "Planting Almanac";

        public static void TryRegisterAll()
        {
            if (!ResolveApi()) return;
            try
            {
                RegisterMod();
                RegisterEntries();
                MelonLogger.Msg("[EssentialProvisions] Registered with Keep Clarity settings panel");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[EssentialProvisions] Keep Clarity registration failed: {ex.Message}");
            }
        }

        private static bool ResolveApi()
        {
            if (_resolved) return _present;
            _resolved = true;

            var apiType = Type.GetType("FFUIOverhaul.Settings.SettingsAPI, KeepClarity");
            if (apiType == null) { _present = false; return false; }
            _settingsMetaType = Type.GetType("FFUIOverhaul.Settings.SettingsMeta, KeepClarity");
            if (_settingsMetaType == null) { _present = false; return false; }

            _registerMod = apiType.GetMethod("RegisterMod", BindingFlags.Public | BindingFlags.Static);
            foreach (var m in apiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Register" && m.IsGenericMethodDefinition) { _registerEntry = m; break; }

            _present = _registerMod != null && _registerEntry != null;
            return _present;
        }

        private static void RegisterMod()
        {
            _registerMod!.Invoke(null, new object?[] {
                ModId,
                ModDisplayName,
                "Curated opt-in QoL bundle. Information overlays, alerts, pace-of-play helpers. Every feature is OFF by default. " +
                "Folded with credit from the original community modders — see README for the full provenance table.",
                /*version — pulled live from MelonInfo so it never drifts from the DLL*/
                (Plugin.Instance?.Info?.Version ?? "1.3.0"),
                /*iconResourcePath*/ null,
                /*accentRgb — warm amber (provisions / supply theme)*/ new[] { 0.85f, 0.65f, 0.30f, 1f },
                /*order*/ 20
            });
        }

        private static object NewMeta(string? label = null, string? tooltip = null,
            object? min = null, object? max = null, string? group = null,
            bool restartRequired = false, int order = 0, Func<bool>? visibleWhen = null)
        {
            var m = Activator.CreateInstance(_settingsMetaType!);
            void Set(string field, object? value)
            {
                var f = _settingsMetaType!.GetField(field);
                if (f != null) f.SetValue(m, value);
            }
            Set("Label", label);
            Set("Tooltip", tooltip);
            Set("Min", min);
            Set("Max", max);
            Set("Group", group);
            Set("RestartRequired", restartRequired);
            Set("Order", order);
            Set("VisibleWhen", visibleWhen);
            return m!;
        }

        private static void Reg<T>(string category, MelonPreferences_Entry<T> entry, object meta)
        {
            var closed = _registerEntry!.MakeGenericMethod(typeof(T));
            closed.Invoke(null, new object?[] { ModId, ModDisplayName, category, entry, meta });
        }

        private static void RegisterEntries()
        {
            // ============================================================
            // === Misc ===
            // ============================================================

            Reg(GroupMisc, Config.EnableFastForward,
                NewMeta(
                    label: "Fast Forward",
                    tooltip: "Unlock game speeds above vanilla's 3x cap. Speeds up to 50x become available. Above 20x, hunter and combat AI may misbehave — a warning is logged. Toggle takes effect immediately.",
                    order: 10));

            Reg(GroupMisc, Config.EnableServiceBounds,
                NewMeta(
                    label: "Service Bounds",
                    tooltip: "Click any placed building to see its effect radius — water, desirability, market reach, school range, work area. Same circle FF shows during placement.",
                    order: 20));

            Reg(GroupMisc, Config.EnableClearcutting,
                NewMeta(
                    label: "Clearcutting",
                    tooltip: "Auto-flag harvestable resources in a radius around your Town Center. Skips danger zones around raiders, wolves, and bears.",
                    order: 30));
            Reg(GroupMisc, Config.ClearcuttingTrees,
                NewMeta(label: "    Trees",
                    tooltip: "Include trees in auto-flagging.",
                    visibleWhen: () => Config.EnableClearcutting.Value, order: 31));
            Reg(GroupMisc, Config.ClearcuttingStones,
                NewMeta(label: "    Stones",
                    tooltip: "Include stones in auto-flagging.",
                    visibleWhen: () => Config.EnableClearcutting.Value, order: 32));
            Reg(GroupMisc, Config.ClearcuttingForageables,
                NewMeta(label: "    Forageables",
                    tooltip: "Include forageables (berry bushes, herbs, mushrooms) in auto-flagging.",
                    visibleWhen: () => Config.EnableClearcutting.Value, order: 33));
            Reg(GroupMisc, Config.ClearcuttingRadius,
                NewMeta(label: "    Clearcutting Radius",
                    tooltip: "Distance from Town Center within which to auto-flag.",
                    min: 100f, max: 500f,
                    visibleWhen: () => Config.EnableClearcutting.Value, order: 34));
            Reg(GroupMisc, Config.ClearcuttingRescan,
                NewMeta(label: "    Re-Scan Now",
                    tooltip: "Trigger a fresh scan immediately. Auto-resets to OFF after firing.",
                    visibleWhen: () => Config.EnableClearcutting.Value, order: 35));

            // ============================================================
            // === Villager Improvements ===
            // ============================================================

            Reg(GroupVillagers, Config.EnableShortWalks,
                NewMeta(
                    label: "Short Walks",
                    tooltip: "Once a month, re-optimize villagers' home assignments so they live closer to their workplaces. Soldiers, Guards, and live-at-workplace occupations are skipped.",
                    order: 10));

            Reg(GroupVillagers, Config.EnableLongTravels,
                NewMeta(
                    label: "Long Travels",
                    tooltip: "Flag production buildings whose workers spend too much time traveling instead of producing.",
                    order: 20));
            Reg(GroupVillagers, Config.LongTravelsThreshold,
                NewMeta(label: "    Long Travels Threshold (%)",
                    tooltip: "Trigger the alert when travel time exceeds this fraction of total work time.",
                    min: 20, max: 80,
                    visibleWhen: () => Config.EnableLongTravels.Value, order: 21));

            Reg(GroupVillagers, Config.EnableSelfPreservation,
                NewMeta(
                    label: "Self Preservation",
                    tooltip: "Villagers flee from nearby raiders without waiting for combat to start. Hunters keep vanilla short-range behavior. Camp-defending raiders are ignored.",
                    order: 30));
            Reg(GroupVillagers, Config.SelfPreservationRadius,
                NewMeta(label: "    Detection Radius",
                    tooltip: "Tiles within which an aggressive raider triggers retreat.",
                    min: 30f, max: 200f,
                    visibleWhen: () => Config.EnableSelfPreservation.Value, order: 31));

            Reg(GroupVillagers, Config.EnableLaborShortage,
                NewMeta(
                    label: "Labor Shortage",
                    tooltip: "Show an alert badge on the top bar when laborers and builders fall below the recommended count.",
                    order: 40));

            Reg(GroupVillagers, Config.EnableEfficientLabor,
                NewMeta(
                    label: "Efficient Labor",
                    tooltip: "Idle villagers in crafting and outdoor roles help out as laborers when their own job has nothing to do. Their actual job stays the priority — they snap back the moment real work appears.",
                    order: 50));
            Reg(GroupVillagers, Config.EfficientLaborPollDays,
                NewMeta(label: "    Re-scan Every N Days",
                    tooltip: "How often (in-game days) to scan for idle villagers to lend out and release those who found work. Idle status barely changes day-to-day, so 5 is plenty; lower = snappier, higher = lighter on big maps.",
                    min: 1, max: 30,
                    visibleWhen: () => Config.EnableEfficientLabor.Value, order: 51));

            Reg(GroupVillagers, Config.EnablePennyPincher,
                NewMeta(
                    label: "Penny Pincher",
                    tooltip: "Auto-stand-down rat catchers when no infestations are active, and guard towers when no raids are present. Auto re-enables when threats reappear. Won't touch a building you currently have selected.",
                    order: 60));
            Reg(GroupVillagers, Config.PennyPincherGuardTowers,
                NewMeta(label: "    Stand Down Guard Towers",
                    tooltip: "Also stand down guard towers in peacetime (re-manned when raids approach). Turn off to keep guard towers fully under your control while still auto-managing rat catchers — turning it off immediately re-mans any towers it had stood down.",
                    visibleWhen: () => Config.EnablePennyPincher.Value, order: 61));

            Reg(GroupVillagers, Config.EnableIdleHands,
                NewMeta(
                    label: "Idle Hands",
                    tooltip: "Flag production buildings whose workers spend too much time idle.",
                    order: 70));
            Reg(GroupVillagers, Config.IdleHandsThreshold,
                NewMeta(label: "    Idle Hands Threshold (%)",
                    tooltip: "Trigger the alert when idle time exceeds this fraction of total work time.",
                    min: 20, max: 80,
                    visibleWhen: () => Config.EnableIdleHands.Value, order: 71));

            Reg(GroupVillagers, Config.EnableLearnedHands,
                NewMeta(
                    label: "Learned Hands",
                    tooltip: "Educated villagers work faster — gathering, field work, AND crafting. +10% per education level. Closes a vanilla gap the official guide describes but the game never implements. Stacks on Workplace Mastery. On/off — no slider.",
                    order: 80));

            Reg(GroupVillagers, Config.EnableWorkplaceMastery,
                NewMeta(
                    label: "Workplace Mastery",
                    tooltip: "Villagers work faster the longer they hold the same job — gathering, field work, AND crafting. +1%/yr of tenure, capped at +25% (25 yr). Stacks on Learned Hands. Tenure is per-occupation, persists per-save; existing villagers start fresh. Adds a Mastery readout to the villager picker. On/off — no slider.",
                    order: 90));

            Reg(GroupVillagers, Config.EnableRabiesTreatable,
                NewMeta(
                    label: "Rabies: Treatable (longshot)",
                    tooltip: "Makes vanilla's guaranteed-fatal rabies a slim, healer-dependent chance. A rabid villager falls bedridden and is treated in a sick bed — ~9% in a Healer's House up to ~18% with a stocked Hospital. The cost: they stop working and occupy a sick bed while ill, and die at home if no healer bed is reachable. Deliberate balance change. OFF by default.",
                    order: 100));

            // ============================================================
            // === Inventory Management ===
            // ============================================================

            Reg(GroupInventory, Config.EnableBroadShelves,
                NewMeta(
                    label: "Broad Shelves",
                    tooltip: "Stockyards accept Iron. Granaries accept Hay and Flax. Granary is renamed 'Silo' in all UI. Requires save reload to apply to existing storage buildings.",
                    order: 10));

            Reg(GroupInventory, Config.EnableSurplusSelling,
                NewMeta(
                    label: "Surplus Selling",
                    tooltip: "Auto-shunt excess stock to your trading post based on observed daily consumption × months. Non-food only by default — food has a separate sub-toggle with diet-variety safeguards. Requires Consumable Control enabled too: it gathers the daily-rate data Surplus Selling uses to size shipments.",
                    order: 20));
            Reg(GroupInventory, Config.SurplusSellingMonths,
                NewMeta(label: "    Keep-in-Town Months (non-food)",
                    tooltip: "Months of stock to keep in town for each non-food tracked item. Excess flows to trading post.",
                    min: 1, max: 24,
                    visibleWhen: () => Config.EnableSurplusSelling.Value, order: 21));
            Reg(GroupInventory, Config.EnableSurplusSellingFood,
                NewMeta(label: "    Include Food",
                    tooltip: "Also auto-ship food surplus. Safeguards: total food must exceed aggregate threshold AND each food type stays at ≥ 1 month of its own consumption.",
                    visibleWhen: () => Config.EnableSurplusSelling.Value, order: 22));
            Reg(GroupInventory, Config.SurplusSellingFoodMonths,
                NewMeta(label: "    Keep-in-Town Months (food)",
                    tooltip: "Months of food stock to keep in town. Default 10 matches FF's immigration threshold.",
                    min: 1, max: 24,
                    visibleWhen: () => Config.EnableSurplusSelling.Value && Config.EnableSurplusSellingFood.Value, order: 23));
            Reg(GroupInventory, Config.SurplusSellingPollDays,
                NewMeta(label: "    Recalculate Every N Days",
                    tooltip: "How often (in-game days) to recompute trading-post stock targets. The 30-day rolling average barely moves day-to-day, so daily is wasteful; 5 is plenty responsive. Lower = snappier, higher = lighter on big maps.",
                    min: 1, max: 30,
                    visibleWhen: () => Config.EnableSurplusSelling.Value, order: 24));

            Reg(GroupInventory, Config.EnableConsumableControl,
                NewMeta(
                    label: "Consumable Control",
                    tooltip: "Auto-configure trading post stocking minimums based on observed daily consumption × months. Trading post workers won't pull an item from storage to stock the post unless town stock is above the reserve. (Items already in the trading post are unavailable to villagers regardless.) Auto-scales with population.",
                    order: 30));
            Reg(GroupInventory, Config.ConsumableReserveMonths,
                NewMeta(label: "    Reserve Months",
                    tooltip: "Months of supply that must be in town storage before workers will pull an item to stock the trading post.",
                    min: 1, max: 12,
                    visibleWhen: () => Config.EnableConsumableControl.Value, order: 31));
            Reg(GroupInventory, Config.ConsumableHeadroomPercent,
                NewMeta(label: "    Growth Headroom (%)",
                    tooltip: "Padding applied to the rate-based target to anticipate population growth (births, immigration). Shared between Consumable Control and Surplus Selling.",
                    min: 0, max: 50,
                    visibleWhen: () => Config.EnableConsumableControl.Value || Config.EnableSurplusSelling.Value, order: 32));

            Reg(GroupInventory, Config.EnableProjectPrep,
                NewMeta(
                    label: "Project Prep",
                    tooltip: "Reserve construction materials from being drained by production buildings. Sliders below set per-material floors. Construction can still access the reserve; production cannot. 0 = no reserve.",
                    order: 40));
            Reg(GroupInventory, Config.ProjectPrepLogs,
                NewMeta(label: "    Logs Reserve",
                    tooltip: "Minimum logs to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 41));
            Reg(GroupInventory, Config.ProjectPrepPlanks,
                NewMeta(label: "    Planks Reserve",
                    tooltip: "Minimum planks to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 42));
            Reg(GroupInventory, Config.ProjectPrepStone,
                NewMeta(label: "    Stone Reserve",
                    tooltip: "Minimum stone to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 43));
            Reg(GroupInventory, Config.ProjectPrepClay,
                NewMeta(label: "    Clay Reserve",
                    tooltip: "Minimum clay to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 44));
            Reg(GroupInventory, Config.ProjectPrepSand,
                NewMeta(label: "    Sand Reserve",
                    tooltip: "Minimum sand to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 45));
            Reg(GroupInventory, Config.ProjectPrepIron,
                NewMeta(label: "    Iron Reserve",
                    tooltip: "Minimum iron to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 46));
            Reg(GroupInventory, Config.ProjectPrepCoal,
                NewMeta(label: "    Coal Reserve",
                    tooltip: "Minimum coal to keep on hand for construction.",
                    min: 0, max: 1000,
                    visibleWhen: () => Config.EnableProjectPrep.Value, order: 47));

            // ============================================================
            // === Agriculture Alerts ===
            // ============================================================

            Reg(GroupAgriAlerts, Config.EnableSoilWisdom,
                NewMeta(
                    label: "Soil Wisdom",
                    tooltip: "Highlight risky crop sequences in the crop info window — disease conflicts and seasonal frost/heat risks.",
                    order: 10));

            Reg(GroupAgriAlerts, Config.EnableBlightWatch,
                NewMeta(
                    label: "Blight Watch",
                    tooltip: "Pin diseased crop fields on the map until the disease is addressed.",
                    order: 20));

            // ============================================================
            // === Planting Almanac ===
            // ============================================================

            Reg(GroupAlmanac, Config.EnablePlantingAlmanac,
                NewMeta(
                    label: "Planting Almanac",
                    tooltip: "Adds one-click crop-rotation presets to the crop field window. Ships curated disease-safe rotations; applying one replaces the field's whole schedule via the game's own scheduler. Takes effect the next time you open a crop field window.",
                    order: 10));

            // Note: EfficientLaborOccupations + ConsumableTrackedItems are intentionally
            // NOT registered with KC. Power users edit those keys directly in the cfg;
            // panel stays clean.
        }
    }
}
