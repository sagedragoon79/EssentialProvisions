// Original EP feature (not a fold) — requested by players 2026-07-28.
//
// TWO vanilla gates keep Apiaries penned in. This feature lifts both.
//
// GATE 1 — THE UNLOCK (the one players actually complain about).
// The Apiary button stays locked in the build menu until you own a crop field.
// BuildManager.Awake() walks GlobalAssets.buildingSetupData.buildingData and, for
// each BuildingData, registers one Prerequisite per entry in
// prerequisiteIdentifiers into buildingNameToPrereqsDict. BuildManager.AddCropfield()
// later flips those marked with the cropfield tag to met — and RemoveCropfield()
// flips them BACK when your last field is gone. So the Apiary is crop-field-locked
// both ways. We strip the crop-field entries out of the Apiary's own
// prerequisiteIdentifiers list in a PREFIX on BuildManager.Awake, before the
// registration loop reads it, so the prerequisite is never created in the first
// place. Any other prerequisite (tech, town-center tier) is left untouched — this
// removes the crop-field condition, it does not make the Apiary free.
// Identifier strings are OR-lists split on '|' (BuildManager.GetParsedPrerequisiteIdentifiers),
// so we filter per-token and only drop a whole entry if every token was crop-field.
//
// GATE 2 — THE PLACEMENT. Vanilla also refuses to SITE an apiary unless the honey
// heatmap reads above zero there (required flag HasHoney, checked per grid cell
// against AgricultureManager.GetHoneyProductionPercent(rect) > 0f), and refuses to
// let it overlap fruit trees (Overlap_FruitTrees). Placeable already exposes a
// virtual `excludeValidityCheckFlags` whose whole purpose is opting a placeable out
// of named checks — FF's own bridge and road placeables override it. We postfix the
// base getter and add both flags for PlaceableApiary only. They flow through
// UpdatePlaceableValidity -> CheckValidityForRect and TerrainGrid.UpdateCellValidity
// (Placeable.UpdatePlacementValidity feeds both), so the checks stop being evaluated
// AND the preview grid paints green. One bitwise OR on a property read — nothing
// added to the per-cell validity math itself.
//
// WORTH KNOWING: honey OUTPUT is untouched. Base honey comes from the terrain biome
// (slope and water zero it); crop fields (honeyModifierForFieldProximity) and
// orchards (fruitTreeHoneyBonus) are MULTIPLIERS on that base, so they amplify honey
// but cannot create it on a zero cell. A hive sited on genuinely barren ground will
// build and produce nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Unpens the Apiary: no crop field needed to unlock it, and no honey-ground or
    /// fruit-tree restriction on where it can be sited. Other unlock prerequisites
    /// (tech, town-center tier) still apply; honey production math is untouched.
    /// </summary>
    internal static class RoamingHives
    {
        private const string ApiaryIdentifier = "Apiary";

        // Tokens that mean "you must own a crop field". Matched case-insensitively
        // against each '|'-separated token of a prerequisite entry. FF's cropfield
        // tag and building identifier have differed in casing across versions, so we
        // match on the stem rather than an exact string.
        private static readonly string[] CropFieldTokens = { "cropfield", "crop_field" };

        private static bool _strippedThisLoad;

        public static void Reset()
        {
            // BuildManager.Awake runs once per map load; allow the strip to run again
            // for the next map. (The BuildingData asset is shared and lives across
            // loads, so re-stripping an already-stripped list is a harmless no-op.)
            _strippedThisLoad = false;
        }

        private static bool IsCropFieldToken(string token)
        {
            var t = token.Trim().ToLowerInvariant();
            foreach (var probe in CropFieldTokens)
                if (t.Contains(probe)) return true;
            return false;
        }

        /// <summary>
        /// Strip crop-field prerequisites from the Apiary's BuildingData before
        /// BuildManager registers them. Logs what it found and what it left behind —
        /// the prerequisite lists live in an Editor-baked asset, not the assembly, so
        /// the log is the only way to see the real data.
        /// </summary>
        private static void StripCropFieldPrerequisite()
        {
            var data = GlobalAssets.buildingSetupData?.GetBuildingData(ApiaryIdentifier);
            if (data == null)
            {
                Plugin.Log.Warning("[RoamingHives] Apiary BuildingData not found — unlock left at vanilla.");
                return;
            }

            var prereqs = data.prerequisiteIdentifiers;
            if (prereqs == null || prereqs.Count == 0)
            {
                Plugin.Log.Msg("[RoamingHives] Apiary has no build prerequisites to strip.");
                return;
            }

            string before = string.Join(", ", prereqs.ToArray());
            var kept = new List<string>();
            foreach (var raw in prereqs)
            {
                if (string.IsNullOrEmpty(raw)) { kept.Add(raw); continue; }
                // Entry is an OR-list; keep the non-crop-field alternatives.
                var surviving = raw.Split('|').Where(tok => !IsCropFieldToken(tok)).ToArray();
                if (surviving.Length == 0) continue;               // entry was purely crop-field → drop it
                kept.Add(string.Join("|", surviving));
            }

            if (kept.Count == prereqs.Count)
            {
                Plugin.Log.Msg($"[RoamingHives] Apiary prerequisites [{before}] contain no crop-field gate — nothing to strip.");
                return;
            }

            prereqs.Clear();
            prereqs.AddRange(kept);
            string after = kept.Count > 0 ? string.Join(", ", kept.ToArray()) : "(none)";
            Plugin.Log.Msg($"[RoamingHives] Apiary crop-field unlock removed. Prerequisites were [{before}] — now [{after}].");
        }

        [HarmonyPatch(typeof(BuildManager), "Awake")]
        internal static class UnlockPatch
        {
            private static void Prefix()
            {
                try
                {
                    if (!Config.EnableRoamingHives.Value) return;
                    if (_strippedThisLoad) return;
                    _strippedThisLoad = true;
                    StripCropFieldPrerequisite();
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[RoamingHives] Unlock strip failed: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(Placeable), "excludeValidityCheckFlags", MethodType.Getter)]
        internal static class ExcludePlacementChecksPatch
        {
            private static void Postfix(Placeable __instance, ref PlacementGridValidityCheckFlags __result)
            {
                try
                {
                    if (!Config.EnableRoamingHives.Value) return;
                    if (!(__instance is PlaceableApiary)) return;
                    __result |= PlacementGridValidityCheckFlags.HasHoney
                              | PlacementGridValidityCheckFlags.Overlap_FruitTrees;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[RoamingHives] {ex.Message}");
                }
            }
        }
    }
}
