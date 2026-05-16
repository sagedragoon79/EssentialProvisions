// Folded from Cowardly Villagers by Olleus (v1.0.0, abandoned)
// Original DLL: Cowardly Villagers_FF.dll
// EP fold: section renamed "Self Preservation"; Hunter exclusion added per
// SageDragoon — hunters need vanilla short-range behaviour so they engage
// during hunts and respond when ordered to fight. Camp-defending raiders
// don't trigger flee (matches Olleus's original intent).
//
// Mechanism: Prefix on VillagerRetreatSearchEntry.IsAggressiveInvaderNearby.
// Vanilla checks a 50-unit radius (sqr 2500) against aggressive raiders.
// We broaden to a configurable radius (default 90 = sqr 8100) and exclude
// raiders whose `defendable` is a RaiderCamp.
//
// Falls back to vanilla on any reflection/access error so a game update
// can't soft-break the patch.

using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class SelfPreservation
    {
        // Occupation.Hunter ordinal (from the Occupation enum we dumped earlier).
        // Hardcoded so we don't need to reflectively resolve the enum every call.
        private const int OccupationHunter = 1;

        // Reflective soft-dep on Sovereign Boons' Levy's Arms. If SB is loaded and
        // a villager is currently armed by it, we skip the flee logic so the militia
        // holds ground instead of fleeing at melee distance from raiders.
        // Stable API — `SovereignBoons.Boons.LevysArms.IsArmed(Villager)` public bool.
        private static bool _levysArmsResolved;
        private static MethodInfo? _levysArmsIsArmed;

        private static bool IsArmedBySovereignBoons(Villager v)
        {
            if (!_levysArmsResolved)
            {
                _levysArmsResolved = true;
                var t = Type.GetType("SovereignBoons.Boons.LevysArms, SovereignBoons");
                if (t != null)
                    _levysArmsIsArmed = t.GetMethod("IsArmed",
                        BindingFlags.Public | BindingFlags.Static,
                        binder: null,
                        types: new[] { typeof(Villager) },
                        modifiers: null);
            }
            if (_levysArmsIsArmed == null) return false;
            try { return (bool)_levysArmsIsArmed.Invoke(null, new object[] { v }); }
            catch { return false; }
        }

        public static void Reset()
        {
            // Stateless — nothing to clear. Kept for symmetry with other features.
        }

        [HarmonyPatch(typeof(VillagerRetreatSearchEntry), "IsAggressiveInvaderNearby")]
        internal static class IsAggressiveInvaderNearbyPatch
        {
            private static bool Prefix(VillagerRetreatSearchEntry __instance,
                                       out Raider aggressiveInvaderNearby,
                                       ref bool __result)
            {
                aggressiveInvaderNearby = null!;
                try
                {
                    if (!Config.EnableSelfPreservation.Value) return true; // run vanilla
                    if (Plugin.IsForeignModLoaded("CowardlyVillagers", "Cowardly Villagers")) return true;

                    var villager = Traverse.Create(__instance).Field("villager").GetValue<Villager>();
                    if (villager == null) return true;

                    // Hunter exclusion — they need vanilla short-range behaviour so they
                    // don't flee from boar/wolf during a hunt or refuse to engage raiders
                    // we want them to fight.
                    if ((int)villager.GetOccupation() == OccupationHunter) return true;

                    // Sovereign Boons interop: if Levy's Arms has armed this villager
                    // as militia, report "no threats nearby" so they stand and fight
                    // instead of retreating at our broadened radius.
                    if (IsArmedBySovereignBoons(villager))
                    {
                        __result = false;
                        return false; // skip vanilla — armed villagers fear nothing
                    }

                    var gm = UnitySingleton<GameManager>.Instance;
                    var cm = gm?.combatManager;
                    if (cm?.raidersRO == null) return true;

                    float radius = Config.SelfPreservationRadius.Value;
                    float radiusSq = radius * radius;
                    Vector3 vpos = villager.transform.position;

                    foreach (var raider in cm.raidersRO)
                    {
                        if (raider == null) continue;
                        if (raider.combatComp == null) continue;
                        if (raider.combatComp.teamDef != cm.aggressiveRaiderTeamDefinition) continue;

                        // Skip raiders defending their own encampment — they're not
                        // attacking the town, so retreating from them is pointless.
                        if (raider.defendable is RaiderCamp) continue;

                        Vector3 delta = raider.transform.position - vpos;
                        if (delta.sqrMagnitude <= radiusSq)
                        {
                            aggressiveInvaderNearby = raider;
                            __result = true;
                            return false; // skip vanilla
                        }
                    }

                    __result = false;
                    return false; // skip vanilla — no aggressive raider in our radius
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[SelfPreservation] {ex.Message}");
                    aggressiveInvaderNearby = null!;
                    return true; // fall back to vanilla on any error
                }
            }
        }
    }
}
