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

        /// <summary>Granary: also accept Hay and Flax (Sensible Storage parity).</summary>
        [HarmonyPatch(typeof(Granary), "Awake")]
        internal static class GranaryAwakePatch
        {
            private static void Postfix(Granary __instance)
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
            private static void Postfix(Stockyard __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                SetAllowItem(__instance, ref _allowItemIronField, "Iron");
            }
        }

        /// <summary>
        /// Rename Granary → Silo in UI. Pattern adopted from Tended Wilds:
        /// Postfix on Building.SetBuildingDataRecordName (private method;
        /// called on construction + save load), cast to Resource, set
        /// displayName. Zero runtime cost since this runs only when the
        /// data record is set.
        /// </summary>
        [HarmonyPatch(typeof(Building), "SetBuildingDataRecordName")]
        internal static class BuildingNamePatch
        {
            private static void Postfix(Building __instance)
            {
                if (!Config.EnableBroadShelves.Value) return;
                if (!(__instance is Granary)) return;
                try
                {
                    var resource = __instance as Resource;
                    if (resource != null) resource.displayName = "Silo";
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[BroadShelves] Granary rename: {ex.Message}");
                }
            }
        }
    }
}
