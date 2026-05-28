// Inspired by Sensible Storage by Olleus (v1.1.0). Expanded scope:
//   - Olleus's mod adds Hay + Flax to Granary, plus Bow/Crossbow/Arrow to Treasury
//   - EP adds Hay + Flax to Granary (same as Olleus) + Iron to Stockyard (new)
//   - Plus: Granary renamed "Silo" in UI (per SageDragoon's request)
//
// Mechanism:
//   - StorageBuilding has [SerializeField] private bool _allowItem<Name> for
//     every item type. Granary's prefab ships with _allowItemHay=false,
//     _allowItemFlax=false. Stockyard ships with _allowItemIron=false.
//     We reflectively flip them to true on each building's Awake.
//   - Rename via Postfix on Building.SetBuildingDataRecordName setting
//     Resource.displayName (pattern adopted from Tended Wilds' Forager's
//     Garden → Forager's Greenhouse rename).
//
// Cost: zero runtime overhead — flags are set once per building's Awake.

using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace EssentialProvisions.Features
{
    internal static class BroadShelves
    {
        // Cached reflected fields — looked up once, reused.
        private static FieldInfo? _allowItemHayField;
        private static FieldInfo? _allowItemFlaxField;
        private static FieldInfo? _allowItemIronField;

        public static void Reset()
        {
            // Reflection caches don't need clearing across scene loads.
        }

        // ----- Field cache helpers -----

        private static FieldInfo? GetAllowItemField(string itemName, ref FieldInfo? cache)
        {
            if (cache != null) return cache;
            cache = typeof(StorageBuilding).GetField($"_allowItem{itemName}",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (cache == null)
                Plugin.Log.Warning($"[BroadShelves] Reflection couldn't find _allowItem{itemName} on StorageBuilding — feature degraded for that item.");
            return cache;
        }

        private static void SetAllowItem(StorageBuilding building, ref FieldInfo? cache, string itemName)
        {
            var field = GetAllowItemField(itemName, ref cache);
            if (field == null) return;
            try { field.SetValue(building, true); }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[BroadShelves] Failed to set _allowItem{itemName}: {ex.Message}");
            }
        }

        // ----- Patches -----

        // NOTE: these MUST be Prefixes, not Postfixes. StorageBuilding.Awake
        // calls AddAllowableItems(), which reads the allowItem* flags ONCE and
        // builds the building's allowableItems list. That happens inside the
        // base.Awake() call at the top of Granary/Stockyard.Awake. A Postfix
        // fires after the list is already built from the original (false)
        // flags, so flipping a flag then has no effect — the building never
        // learns it can store the new item. A Prefix runs before the method
        // body, so the flag is already true when AddAllowableItems reads it.

        /// <summary>Granary: also accept Hay and Flax (Sensible Storage parity).</summary>
        [HarmonyPatch(typeof(Granary), "Awake")]
        internal static class GranaryAwakePatch
        {
            private static void Prefix(Granary __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                SetAllowItem(__instance, ref _allowItemHayField,  "Hay");
                SetAllowItem(__instance, ref _allowItemFlaxField, "Flax");
            }
        }

        /// <summary>Stockyard: also accept Iron (new addition vs Olleus).</summary>
        [HarmonyPatch(typeof(Stockyard), "Awake")]
        internal static class StockyardAwakePatch
        {
            private static void Prefix(Stockyard __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                SetAllowItem(__instance, ref _allowItemIronField, "Iron");
            }
        }

        // ----- Granary → "Silo" rename -----
        //
        // The displayName setter on Resource propagates to the building's
        // widgetBlackboard (lazily created) and fires onDisplayNameChanged,
        // which the UI binds to for its title. Tended Wilds renames Forager's
        // Garden the same way and it works — but TW's rename fires on a tier-2
        // *upgrade*, by which point the building + window are fully live.
        //
        // A Granary never upgrades, so SetBuildingDataRecordName fires only
        // once at placement — early enough that the rename didn't stick in the
        // selection panel. We therefore set displayName in TWO places:
        //   1. SetBuildingDataRecordName postfix — catches placement + save
        //      load, and updates the floating in-world name widget.
        //   2. OnSelected prefix — re-applies "Silo" immediately before the
        //      info window is (re)built, so the bottom panel title is always
        //      correct no matter when the blackboard/window came online.

        private const string SiloName = "Silo";

        private static void ApplySiloName(Building building)
        {
            try
            {
                var resource = building as Resource;
                if (resource != null && resource.displayName != SiloName)
                    resource.displayName = SiloName;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[BroadShelves] Granary rename: {ex.Message}");
            }
        }

        /// <summary>Set the name on construction / save load (also drives the in-world widget).</summary>
        [HarmonyPatch(typeof(Building), "SetBuildingDataRecordName")]
        internal static class BuildingNamePatch
        {
            private static void Postfix(Building __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                if (!(__instance is Granary)) return;
                ApplySiloName(__instance);
            }
        }

        /// <summary>
        /// Re-apply just before the selection info window is built. OnSelected
        /// is declared on Building and creates/shows the UIInfoWindow; running
        /// as a Prefix guarantees displayName is "Silo" (and onDisplayNameChanged
        /// fires) before the panel reads the title.
        /// </summary>
        [HarmonyPatch(typeof(Building), "OnSelected")]
        internal static class BuildingSelectedNamePatch
        {
            private static void Prefix(Building __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                if (!(__instance is Granary)) return;
                ApplySiloName(__instance);
            }
        }
    }
}
