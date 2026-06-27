// Single source of truth for "what are this villager's odds against a disease", mirroring FF's
// own math so the number shown in a tooltip is the number the game actually rolls.
//
// Per-check cure (Disease.RollCure, decompile L83948-58):
//   time     = immune.cureScore × Disease.GetCureEffectivenessMultiplier(immune)
//   baseProb = Disease.cureDifficultyCurve.Evaluate(time)
//   chance   = baseProb × (1 + cureChanceMultiplier) × (1 + techTree.geDiseaseCureChanceGlobalMultiplier)
// A roll fires every DiseaseManager.cureChanceDuration; the disease ends after Disease.duration, at
// which point a terminal RollFinalCure (finalCureChance) is the last-chance save before death.
//
// GetCureEffectivenessMultiplier returns 0 (gating the whole cure) when GetFactoredScore over
// cureFactors returns false — i.e. a REQUIRED cure factor evaluates to ~0 (condition unmet), OR the
// effectiveness floor cureEffectivenessMultiplierMin is 0 and the factor score is 0. Both are real,
// data-driven gates (vanilla rabies ships effMin == 0; see RabiesTreatable).
//
// Used by the rabies cure-roll log (RabiesTreatable), the public DiseaseInfoApi, and the roster dump.
// Pure reads, null-safe, never throws.

using UnityEngine;

namespace EssentialProvisions.Common
{
    internal static class CureMath
    {
        public const float BaseCureScore = 1f;       // ImmuneSystemComponent.Awake: cureScore = 1f
        // Synthetic "ideal treatment" cure score for best-case recovery = base 1 + Hospital building
        // bonus + best medicine bonus. VALIDATED from the live roster dump (building 50 + medicine 30
        // + base 1 = 81); the old 25 anchor badly understated a stocked Hospital's odds. (Independent of
        // RabiesTreatable's own curve anchor — rabies clamps at cureScore 25 regardless.)
        public const float TopTierCureScore = 81f;
        // Staffed basic Healer's House (T1): base 1 + its building bonus (~10), no/min medicine. Used to
        // tell "a Healer's House can cure this" (Healer) from "only the Hospital can" (Hospital). KC's
        // roster was extracted against this same cure score of 11.
        public const float HealersHouseCureScore = 11f;

        // Self-resolving: the terminal end-of-illness roll all but guarantees recovery and failing it
        // can't kill (or the def is flagged guaranteedRecovery at runtime), OR the minor ailments that
        // cure on the very first check regardless of treatment (per-roll ~100% at the base cure score).
        private const float SelfResolvingFinalCure = 0.99f;
        private const float SelfResolvingPerRoll = 0.9f;
        private const float IncurableEpsilon = 1e-4f;
        // A "Healer"-class disease whose per-check recovery at the Healer's-House cure score is below this
        // (while the Hospital ideal is >0) is Hospital-only.
        private const float HospitalRequiredCourse = 0.02f;

        // ===== Live-context cure odds (what the villager is actually rolling right now) =====
        // These keep their original outputs (KC binds to them via DiseaseInfoApi) — they delegate to
        // the explicit-input overloads with the live cureScore × effectiveness, exactly as RollCure does.

        public static float PerRollCureChance(Disease def, ImmuneSystemComponent immune)
        {
            if (def == null || immune == null) return 0f;
            try { return PerRollCureChance(def, immune.cureScore, def.GetCureEffectivenessMultiplier(immune)); }
            catch { return 0f; }
        }

        public static float CourseSurvivalChance(Disease def, ImmuneSystemComponent immune)
        {
            if (def == null || immune == null) return 0f;
            try { return CourseSurvivalChance(def, immune.cureScore, def.GetCureEffectivenessMultiplier(immune)); }
            catch { return 0f; }
        }

        // ===== Explicit-input cure odds (synthetic cureScore/eff; bypasses the live immune) =====

        /// <summary>Per-cure-check probability as a FRACTION (0..1) for an explicit cure score and
        /// effectiveness multiplier. Mirrors RollCure exactly.</summary>
        public static float PerRollCureChance(Disease def, float cureScore, float eff)
        {
            if (def == null || def.cureDifficultyCurve == null) return 0f;
            try
            {
                float baseP = def.cureDifficultyCurve.Evaluate(cureScore * eff);
                if (baseP <= 0f) return 0f;
                float p = baseP + baseP * def.cureChanceMultiplier + baseP * GlobalCureMult();
                return Mathf.Clamp01(p);
            }
            catch { return 0f; }
        }

        /// <summary>Chance to recover over the full course from the PER-CHECK rolls only:
        /// 1 − (1 − perRoll)^rolls. Does NOT include the terminal finalCureChance (that's folded in
        /// by the best-case path). Matches the existing live "overall recovery" semantics.</summary>
        public static float CourseSurvivalChance(Disease def, float cureScore, float eff)
        {
            float per = PerRollCureChance(def, cureScore, eff);
            if (per <= 0f) return 0f;
            int rolls = EstimatedTotalRolls(def);
            if (rolls <= 1) return Mathf.Clamp01(per);
            return Mathf.Clamp01(1f - Mathf.Pow(1f - per, rolls));
        }

        /// <summary>How many cure checks the disease gets over its full duration (≥1).</summary>
        public static int EstimatedTotalRolls(Disease def)
        {
            try
            {
                if (def == null) return 1;
                var dm = UnitySingleton<GameManager>.Instance?.diseaseManager;
                if (dm == null) return 1;
                float interval = dm.cureChanceDuration.totalDays;
                if (interval <= 0f) return 1;
                int rolls = Mathf.FloorToInt(def.duration.totalDays / interval);
                return rolls < 1 ? 1 : rolls;
            }
            catch { return 1; }
        }

        // ===== Classification + best case (for DiseaseInfoApi.GetCureMethods / GetBestCaseRecoveryPercents) =====

        /// <summary>The disease's intrinsic cure-path token for KC to map to action text. One of:
        /// "Incurable" | "SelfResolving" | "Water" | "Thirst" | "Diet" | "Warmth" | "Soap" | "Healer" |
        /// "Hospital". ORDER IS LOAD-BEARING: Incurable → condition-gated → SelfResolving → Healer/Hospital
        /// (default). The gate check MUST precede self-resolving: a condition-gated disease (e.g.
        /// Dehydration) can have a steep cure curve that cures fast ONCE the condition is met, which would
        /// otherwise trip the "cures on first check" self-resolving heuristic — but it's gated, not
        /// self-resolving. The Healer/Hospital split tells whether a basic Healer's House suffices.</summary>
        public static string ClassifyCureMethod(Disease def)
        {
            if (def == null) return "Incurable";
            try
            {
                if (IsIncurable(def)) return "Incurable";
                string? gate = GetCureGateToken(def);   // a required/effMin≤0 cure factor defines the disease
                if (gate != null) return gate;
                if (IsSelfResolving(def)) return "SelfResolving";
                return RequiresHospital(def) ? "Hospital" : "Healer";
            }
            catch { return "Healer"; }
        }

        /// <summary>A "Healer"-class disease that a staffed basic Healer's House (T1) effectively cannot
        /// cure — per-check recovery ~0 at the Healer's-House cure score — but a stocked Hospital (T2 +
        /// medicine) can. Incurable diseases never reach here, so the Hospital ideal is always &gt; 0.</summary>
        private static bool RequiresHospital(Disease def)
        {
            try
            {
                float eff = IdealEff(def);
                if (CourseSurvivalChance(def, HealersHouseCureScore, eff) >= HospitalRequiredCourse)
                    return false;   // a Healer's House already cures it
                return CourseSurvivalChance(def, TopTierCureScore, eff) > 0f;   // ...but the Hospital can
            }
            catch { return false; }
        }

        /// <summary>Per-check recovery (%) at a staffed basic Healer's House — diagnostic/validation use
        /// (drives the Healer vs Hospital split).</summary>
        public static float HealersHouseRecoveryPercent(Disease def)
            => def == null ? 0f : CourseSurvivalChance(def, HealersHouseCureScore, IdealEff(def)) * 100f;

        /// <summary>Full-course recovery chance at IDEAL treatment, as a FRACTION (0..1): best building +
        /// medicine, cure factors satisfied (effectiveness at its max), plus the terminal finalCureChance
        /// save. Incurable → 0; SelfResolving → 1 (recovers regardless of care).</summary>
        public static float BestCaseRecoveryFraction(Disease def)
        {
            if (def == null) return 0f;
            try
            {
                string cls = ClassifyCureMethod(def);
                if (cls == "Incurable") return 0f;
                if (cls == "SelfResolving") return 1f;
                float course = CourseSurvivalChance(def, TopTierCureScore, IdealEff(def));
                float terminal = Mathf.Clamp01(def.finalCureChance);   // end-of-illness last save
                return Mathf.Clamp01(course + (1f - course) * terminal);
            }
            catch { return 0f; }
        }

        // Effectiveness multiplier at ideal treatment. With satisfiable cure factors the factor score
        // reaches 1, so eff lerps to its MAX. With NO cure factors the score is always 0, so eff is
        // fixed at its MIN (the MAX is unreachable) — using MAX there would overstate recovery.
        private static float IdealEff(Disease def)
        {
            bool hasFactors = def.cureFactors != null && def.cureFactors.Count > 0;
            return hasFactors ? Mathf.Max(1f, def.cureEffectivenessMultiplierMax)
                              : Mathf.Max(0.01f, def.cureEffectivenessMultiplierMin);
        }

        /// <summary>No per-check cure path AND no terminal save — e.g. vanilla rabies (flat-0 curve).</summary>
        public static bool IsIncurable(Disease def)
        {
            if (def == null) return true;
            try
            {
                var c = def.cureDifficultyCurve;
                if (c == null || c.length == 0) return def.finalCureChance <= 0f;
                float maxT = SafeMaxCureScore(def);
                float probe = Mathf.Max(maxT, 100f);   // 100 ≫ any real cure score (~1 + 10 building + medicine)
                float top = Mathf.Max(c.Evaluate(maxT), c.Evaluate(probe));
                return top <= IncurableEpsilon && def.finalCureChance <= 0f;
            }
            catch { return false; }
        }

        /// <summary>Recovers on its own regardless of treatment (Bee Sting, Food Poisoning, …): either
        /// flagged guaranteedRecovery at runtime, or a near-certain terminal save that can't kill.</summary>
        public static bool IsSelfResolving(Disease def)
        {
            if (def == null) return false;
            try
            {
                if (def.guaranteedRecovery) return true;   // runtime-set auto-recovery (per active disease)
                if (def.finalCureChance >= SelfResolvingFinalCure && def.cureFailDeathChance <= 0f) return true;
                // Minor ailments (Bee Sting, Food Poisoning, Gout, …) cure on the FIRST cure check no
                // matter what: their cure curve is authored over a tiny domain (max key time ~1) with a
                // huge value, so per-roll ≈ 100% even at the base cure score of 1 with no treatment.
                // (Confirmed from the live roster dump — these don't use finalCureChance.) Detect directly.
                return PerRollCureChance(def, BaseCureScore, 1f) >= SelfResolvingPerRoll;
            }
            catch { return false; }
        }

        /// <summary>True if leaving the disease untreated can kill the villager: it can reach the terminal
        /// death gate (i.e. it isn't self-resolving) AND cureFailDeathChance > 0. False for non-fatal
        /// ailments (Foot Wound, failDeath 0) and for self-resolving ones that cure before the gate is
        /// ever reached (Bee Sting, despite a nonzero failDeath in the data — it cures on the first check).</summary>
        public static bool IsLethal(Disease def)
        {
            if (def == null) return false;
            try { return def.cureFailDeathChance > 0f && !IsSelfResolving(def); }
            catch { return false; }
        }

        /// <summary>The condition gating the cure, as a player-actionable token ("Water"/"Thirst"/"Diet"/
        /// "Warmth"/"Soap", or the raw factor name if unmapped), or null if the cure isn't condition-gated.
        /// Gated when a cure factor is REQUIRED (GetFactoredScore returns false until met) OR the
        /// effectiveness floor is 0 (an unsatisfied cure factor then zeroes effectiveness). Non-actionable
        /// modifiers (e.g. age) are skipped so we surface something the player can DO. Confirmed against
        /// the live roster: factor assets are DF_Water_Inverted / CF_Thirst / DF_ResidenceStocked_Soap /
        /// CF_ResidenceStocked_Firewood / *_Diet_* — NOT the bare DF_Water/DF_Thirst names.</summary>
        public static string? GetCureGateToken(Disease def)
        {
            if (def == null || def.cureFactors == null) return null;
            try
            {
                bool effGated = def.cureEffectivenessMultiplierMin <= 0f;
                string? required = null, requiredRaw = null, softActionable = null;
                foreach (var fi in def.cureFactors)
                {
                    if (fi.diseaseFactor == null) continue;
                    string? tok = MapDfToken(fi.diseaseFactor.name);
                    bool actionable = tok != null && tok != "Age";
                    if (fi.isRequired)
                    {
                        if (actionable && required == null) required = tok;   // best: a required, player-actionable gate
                        if (requiredRaw == null) requiredRaw = tok;           // fallback: any required factor
                    }
                    else if (effGated && actionable && softActionable == null)
                    {
                        softActionable = tok;   // effMin<=0 ⇒ an unsatisfied actionable factor zeroes the cure
                    }
                }
                return required ?? requiredRaw ?? softActionable;   // null ⇒ not condition-gated (→ Healer)
            }
            catch { return null; }
        }

        /// <summary>Map a cure-factor asset name to KC's action token. Substring match (asset names carry
        /// suffixes like "_Inverted"/"_ResidenceStocked"). Returns "Age" for the non-actionable age
        /// modifier (callers skip it), and the raw name for anything unmapped so KC can still surface it.</summary>
        public static string? MapDfToken(string? dfName)
        {
            if (string.IsNullOrEmpty(dfName)) return null;
            string n = dfName!;   // IsNullOrEmpty guarantees non-null (net46 BCL lacks the flow annotation)
            if (n.Contains("Water")) return "Water";                              // DF_Water / DF_Water_Inverted
            if (n.Contains("Thirst")) return "Thirst";                            // CF_Thirst / DF_Thirst
            if (n.Contains("Firewood") || n.Contains("Exposure")) return "Warmth";
            if (n.Contains("Soap")) return "Soap";                                // DF_ResidenceStocked_Soap (hygiene)
            if (n.Contains("Diet") || n.Contains("RecentlyConsumed") || n.Contains("Scurvy")) return "Diet";
            if (n.Contains("Age")) return "Age";                                  // non-actionable recovery modifier
            return n;
        }

        private static float SafeMaxCureScore(Disease def)
        {
            try { return def.GetMaxCureScore(); }
            catch { return 0f; }
        }

        private static float GlobalCureMult()
        {
            try { return UnitySingleton<GameManager>.Instance.techTreeManager.geDiseaseCureChanceGlobalMultiplier; }
            catch { return 0f; }
        }
    }
}
