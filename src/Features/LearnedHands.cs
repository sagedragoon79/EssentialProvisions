// Learned Hands — educated villagers get a small work-rate bonus at every job.
//
// Closes a real vanilla gap: FF's official guide says educated workers perform
// more efficiently, but the assembly has no work-rate term that reads education
// (Knowledge/FF-Modding-Knowledge/game-systems/education-system.md §5). Education
// is currently *only* a job-eligibility gate (Healer/School-teacher/Apothecary).
//
// Mechanism: single Harmony postfix on
//   HappinessManager.GetWorkRateMultiplier(Villager villager)
// (decompile L377683). Originally the handoff proposed the 4-arg overload, but
// the callgraph shows the 1-arg overload covers EVERY path that matters:
//   - Resource collection (woodcutters/miners/foragers, L163242) calls the 1-arg
//     DIRECTLY — the 4-arg patch would never fire for these.
//   - Manufacturing (L237224/L237322) calls the 4-arg, which DELEGATES to the
//     1-arg internally at L377711 — so a 1-arg patch catches them too.
// Patching only the 1-arg covers both with no risk of double-application.
//
// Multiply __result by 1 + level × perLevel. Scales with the dormant tier
// framework: today level 1 = +10%; if Crate ships higher tiers the formula
// extends with no code change. Stateless. No save/load state. Live toggle.

using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;

namespace EssentialProvisions.Features
{
    internal static class LearnedHands
    {
        // One-shot trace flags so we can confirm the patch is wired up without
        // spamming the log every frame. Cleared on Reset.
        private static bool _firstCallLogged;          // ANY call (educated or not) — proves the patch is bound
        private static bool _firstApplicationLogged;   // First call where the bonus actually applied
        private static readonly HashSet<int> _verboseSeen = new HashSet<int>();

        public static void Reset()
        {
            _firstCallLogged = false;
            _firstApplicationLogged = false;
            _verboseSeen.Clear();
        }

        [HarmonyPatch(typeof(HappinessManager), nameof(HappinessManager.GetWorkRateMultiplier),
            new[] { typeof(Villager) })]
        internal static class WorkRatePatch
        {
            private static void Postfix(Villager villager, ref float __result)
            {
                // Confirms the patch is bound & being called — fires regardless of cfg/education.
                if (!_firstCallLogged)
                {
                    _firstCallLogged = true;
                    int dbgLevel = -1;
                    try { dbgLevel = villager?.education?.currentLevel?.level ?? -1; } catch { }
                    try { Plugin.Log.Msg($"[LearnedHands][trace] First call to postfix — villager level {dbgLevel}, base mult {__result:F3}, cfgEnabled={Config.EnableLearnedHands.Value}."); }
                    catch { }
                }

                if (!Config.EnableLearnedHands.Value) return;
                if (villager == null) return;

                var edu = villager.education;
                if (edu == null || edu.currentLevel == null) return;
                int level = edu.currentLevel.level;
                if (level <= 0) return;

                float perLevel = Config.LearnedHandsPerLevelBonus.Value;
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
        }

        private static string SafeName(Villager v)
        {
            try { return v.name ?? "<unnamed>"; }
            catch { return "<error>"; }
        }
    }
}
