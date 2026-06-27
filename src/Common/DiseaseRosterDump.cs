// One-shot validation dump (diagnostic, gated by Config.DiseaseRosterDump, default OFF; cfg-only like
// InventoryDiagnostics / *Verbose). On the first map load after enabling, logs every loaded Disease's
// classification + best-case recovery + the raw cure data, so the DiseaseInfoApi GetCureMethods /
// GetBestCaseRecoveryPercents derivation can be checked against the known roster (Rabies=Incurable,
// Cholera=Water, Dehydration=Thirst, Bee Sting=SelfResolving, …). The per-disease asset values
// (cureFactors, curve, finalCureChance) aren't in the decompile, so this dump is the way to confirm
// the derivation against live data before shipping.

using System;
using System.Text;
using UnityEngine;

namespace EssentialProvisions.Common
{
    internal static class DiseaseRosterDump
    {
        private static bool _dumped;

        public static void Reset() => _dumped = false;

        public static void DumpOnce()
        {
            if (_dumped) return;
            if (!Config.DiseaseRosterDump.Value) return;
            if (!GameManager.gameReadyToPlay) return;
            try
            {
                var diseases = Resources.FindObjectsOfTypeAll<Disease>();
                if (diseases == null || diseases.Length == 0) return;
                _dumped = true;

                // Best-case cure score actually achievable in THIS game (validates the 25f anchor).
                float bestBuilding = 0f, bestMedicine = 0f;
                try
                {
                    foreach (var tc in UnityEngine.Object.FindObjectsOfType<TriageCenter>())
                    {
                        if (tc == null) continue;
                        if (tc.cureScoreBonus > bestBuilding) bestBuilding = tc.cureScoreBonus;
                        var supplies = tc.rankedSupplies;
                        if (supplies != null)
                            foreach (var s in supplies)
                                if (s.cureScoreBonus > bestMedicine) bestMedicine = s.cureScoreBonus;
                    }
                }
                catch { /* best-case scan is informational only */ }

                Plugin.Log.Msg($"[DiseaseRoster] ==== {diseases.Length} disease defs · best-case cureScore scan: base {CureMath.BaseCureScore} + building {bestBuilding} + medicine {bestMedicine} = {CureMath.BaseCureScore + bestBuilding + bestMedicine} (EP anchor {CureMath.TopTierCureScore}) ====");
                foreach (var d in diseases)
                {
                    if (d == null) continue;
                    string token = CureMath.ClassifyCureMethod(d);
                    float best = CureMath.BestCaseRecoveryFraction(d) * 100f;
                    float hh = CureMath.HealersHouseRecoveryPercent(d);
                    float maxT = SafeMaxT(d);
                    var sb = new StringBuilder();
                    sb.Append($"[DiseaseRoster] {SafeDisplay(d)} ({d.name}) | class={token} best={best:0.#}% hh={hh:0.#}% lethal={CureMath.IsLethal(d)} | ");
                    sb.Append($"finalCure={d.finalCureChance:0.##} failDeath={d.cureFailDeathChance:0.##} guarRec={d.guaranteedRecovery} | ");
                    sb.Append($"curveMaxT={maxT:0.#} curve@probe={SafeCurveTop(d, maxT):0.####} effMin={d.cureEffectivenessMultiplierMin:0.##} effMax={d.cureEffectivenessMultiplierMax:0.##} | cureFactors:");
                    if (d.cureFactors != null && d.cureFactors.Count > 0)
                        foreach (var fi in d.cureFactors)
                            sb.Append($" [{(fi.diseaseFactor != null ? fi.diseaseFactor.name : "null")} req={fi.isRequired} w={fi.weight}]");
                    else sb.Append(" (none)");
                    Plugin.Log.Msg(sb.ToString());
                }
                Plugin.Log.Msg("[DiseaseRoster] ==== end ====");
            }
            catch (Exception ex) { Plugin.Log.Warning($"[DiseaseRoster] dump failed: {ex.Message}"); }
        }

        private static string SafeDisplay(Disease d)
        {
            try { var n = d.GetLocalizedDisplayName(); return string.IsNullOrEmpty(n) ? d.name : n; }
            catch { return d.name; }
        }

        private static float SafeMaxT(Disease d)
        {
            try { return d.GetMaxCureScore(); }
            catch { return 0f; }
        }

        private static float SafeCurveTop(Disease d, float maxT)
        {
            try { return d.cureDifficultyCurve != null ? d.cureDifficultyCurve.Evaluate(Mathf.Max(maxT, 100f)) : 0f; }
            catch { return 0f; }
        }
    }
}
