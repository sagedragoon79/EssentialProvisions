// Learned Hands — educated villagers get a small work-rate bonus at every job.
//
// Closes a real vanilla gap: FF's official guide says educated workers perform
// more efficiently, but the assembly has no work-rate term that reads education
// (Knowledge/FF-Modding-Knowledge/game-systems/education-system.md §5). Education
// is currently *only* a job-eligibility gate (Healer/School-teacher/Apothecary).
//
// Mechanism: Harmony postfix on
//   HappinessManager.GetWorkRateMultiplier(Villager villager)
// (decompile L377683). That multiplier is consumed in exactly ONE work tick —
// ReservableItemStorage.DetachItemsFromReservation (L163242) — which covers
// resource EXTRACTION (wood/mining/foraging), field tilling/planting/maintenance,
// clearing/repair/explore/excavate/salvage, and hunter butchering.
//
// IMPORTANT — it does NOT reach MANUFACTURING throughput. The earlier note that the
// 4-arg overload "delegates to the 1-arg so a 1-arg patch catches manufacturing too"
// was wrong: the 4-arg (L237224/L237322) only paints the work-rate icon, while the
// real crafting tick is ManufactureWorkOrder.AdjustAddedWorkUnits and never calls
// the multiplier (see _research/mastery-coverage-map.md). So the education bonus now
// reaches crafting through WorkplaceMastery.ManufacturePatch, which applies BOTH the
// education term (gated on this feature) and the tenure term in one place.
//
// Multiply __result by 1 + level × perLevel. Scales with the dormant tier
// framework: today level 1 = +10%; if Crate ships higher tiers the formula
// extends with no code change. Stateless. No save/load state. Live toggle.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;

namespace EssentialProvisions.Features
{
    internal static class LearnedHands
    {
        // Hardcoded education bonus per level (on/off feature, no slider — rebalance by
        // patching). Also read by WorkplaceMastery.ManufacturePatch for the crafting hook.
        internal const float PerLevel = 0.10f;   // +10% per education level

        // One-shot flag: log the first time the bonus actually applies, so the
        // feature's effect is observable without per-frame spam. Cleared on Reset.
        private static bool _firstApplicationLogged;
        private static bool _errorLogged;
        private static readonly HashSet<int> _verboseSeen = new HashSet<int>();

        public static void Reset()
        {
            _firstApplicationLogged = false;
            _errorLogged = false;
            _verboseSeen.Clear();
        }

        [HarmonyPatch(typeof(HappinessManager), nameof(HappinessManager.GetWorkRateMultiplier),
            new[] { typeof(Villager) })]
        internal static class WorkRatePatch
        {
            private static void Postfix(Villager villager, ref float __result)
            {
                if (!Config.EnableLearnedHands.Value) return;
                // High-frequency work path — never throw into game code (EP rule).
                try
                {
                    if (villager == null) return;

                    var edu = villager.education;
                    if (edu == null || edu.currentLevel == null) return;
                    int level = edu.currentLevel.level;
                    if (level <= 0) return;

                    float perLevel = PerLevel;
                    float before = __result;
                    __result *= 1f + level * perLevel;

                    // First call where the bonus actually fires — confirms end-to-end.
                    if (!_firstApplicationLogged)
                    {
                        _firstApplicationLogged = true;
                        Plugin.Log.Msg($"[LearnedHands] First application: education level {level}, " +
                            $"base mult {before:F3} → {__result:F3} (+{(__result / before - 1f) * 100f:F1}%) " +
                            $"with perLevel={perLevel:F2}.");
                    }

                    // Verbose: one log per unique villager (still bounded).
                    if (Config.LearnedHandsVerbose.Value && _verboseSeen.Add(villager.GetInstanceID()))
                    {
                        Plugin.Log.Msg($"[LearnedHands][diag] {SafeName(villager)} (level {level}): {before:F3} → {__result:F3}");
                    }
                }
                catch (Exception ex)
                {
                    if (!_errorLogged)
                    {
                        _errorLogged = true;
                        Plugin.Log.Warning($"[LearnedHands] WorkRatePatch error (suppressed further): {ex.Message}");
                    }
                }
            }
        }

        private static string SafeName(Villager v)
        {
            try { return v.name ?? "<unnamed>"; }
            catch { return "<error>"; }
        }
    }
}
