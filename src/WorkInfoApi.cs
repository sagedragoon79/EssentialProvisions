// Public interop so Keep Clarity (or any mod) can show a villager's EP work bonuses in its
// own UI. KC reads this reflectively (soft-dep) — no hard reference either way. EP owns the
// numbers; KC owns all labels, placement, and formatting.
//
// STABILITY CONTRACT: these public static signatures are the soft-dep boundary KC binds to
// reflectively. Once shipped, do NOT rename/remove or change signatures — add new methods
// instead. All methods are null-safe and never throw (return 0 / empty on any failure).
//
// Bonus values are in PERCENTAGE POINTS (10f == +10%). "No bonus" returns 0 (not -1/NaN) so
// KC can hide a line/badge when the value is 0.

using System.Collections.Generic;
using EssentialProvisions.Features;
using UnityEngine;

namespace EssentialProvisions
{
    public static class WorkInfoApi
    {
        /// <summary>Education-derived (Learned Hands) work-rate bonus, in percentage points
        /// (10f == +10%). 0 if Learned Hands is off or the villager has no education bonus.</summary>
        public static float GetEducationBonusPercent(Villager v)
        {
            if (v == null) return 0f;
            try
            {
                if (!Config.EnableLearnedHands.Value) return 0f;
                var edu = v.education;
                int level = (edu != null && edu.currentLevel != null) ? edu.currentLevel.level : 0;
                if (level <= 0) return 0f;
                float pct = level * LearnedHands.PerLevel * 100f;
                return pct > 0f ? pct : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>Workplace Mastery (current-job tenure) bonus, in percentage points
        /// (8f == +8%). 0 if Workplace Mastery is off or the villager has no tenure bonus.</summary>
        public static float GetMasteryBonusPercent(Villager v)
        {
            if (v == null) return 0f;
            try
            {
                if (!Config.EnableWorkplaceMastery.Value) return 0f;
                return WorkplaceMastery.TryGetCurrentMasteryBonus(v, out float pct) && pct > 0f ? pct : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>Total applied work-rate bonus, in percentage points, combined the SAME way
        /// the game applies it: education and mastery are each MULTIPLICATIVE postfixes on
        /// GetWorkRateMultiplier, so total = ((1+edu)(1+tenure) − 1). Use for KC's cumulative
        /// "+X%" per-worker badge — this matches the real in-game effect.</summary>
        public static float GetTotalWorkBonusPercent(Villager v)
        {
            if (v == null) return 0f;
            try
            {
                float e = GetEducationBonusPercent(v) / 100f;
                float m = GetMasteryBonusPercent(v) / 100f;
                float total = ((1f + e) * (1f + m) - 1f) * 100f;
                return total > 0f ? total : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>The villager's highest-mastery occupations (DISPLAY names, e.g. "Blacksmith"),
        /// sorted DESC by mastery, up to <paramref name="count"/>, excluding zero-mastery jobs.
        /// Parallel to <see cref="GetTopMasteryJobPercents"/> (same order). Empty if none / off /
        /// null villager.</summary>
        public static string[] GetTopMasteryJobNames(Villager v, int count)
        {
            try
            {
                var top = WorkplaceMastery.GetTopMasteries(v, count);
                var names = new string[top.Count];
                for (int i = 0; i < top.Count; i++) names[i] = top[i].Key;
                return names;
            }
            catch { return new string[0]; }
        }

        /// <summary>Per-occupation mastery bonus in percentage points (8f == +8%), in the SAME
        /// order as <see cref="GetTopMasteryJobNames"/>. KC zips the two into "Blacksmith +8%"
        /// rows. Empty if none / off / null villager.</summary>
        public static float[] GetTopMasteryJobPercents(Villager v, int count)
        {
            try
            {
                var top = WorkplaceMastery.GetTopMasteries(v, count);
                var pcts = new float[top.Count];
                for (int i = 0; i < top.Count; i++) pcts[i] = top[i].Value;
                return pcts;
            }
            catch { return new float[0]; }
        }

        /// <summary>What Workplace Mastery does for the villager's CURRENT job, as short English
        /// text, e.g. "Teaches 6% faster", "Hits 4% harder, takes 4% less damage", or
        /// "Works 12% faster" for crafting and gathering jobs. "" when the feature is off, the
        /// job has no effect (or its effect is switched off), or tenure hasn't earned a whole
        /// percent yet. Added October 2026; prefer it over <see cref="GetMasteryBonusPercent"/>
        /// for display, because that one reports the work-rate scale for every job.</summary>
        public static string GetMasteryEffectText(Villager v)
        {
            if (v == null) return "";
            try
            {
                WorkplaceMasteryEffects.EffectFor(v, out string text);
                return text ?? "";
            }
            catch { return ""; }
        }

        /// <summary>The size of that effect in percentage points (6f == 6%), on the job's own
        /// scale: up to 25 for production jobs, 10 for service and combat jobs, 5 for traders.
        /// Same conditions as <see cref="GetMasteryEffectText"/>; 0 when there's no effect.
        /// Added October 2026.</summary>
        public static float GetMasteryEffectPercent(Villager v)
        {
            if (v == null) return 0f;
            try { return WorkplaceMasteryEffects.EffectFor(v, out _); }
            catch { return 0f; }
        }

        /// <summary>DEPRECATED (kept for back-compat until KC migrates off it): compact one-line
        /// summary like "+10% edu · +8% mastery", or "" if none apply. New callers should use the
        /// numeric getters above.</summary>
        public static string GetVillagerWorkSummary(Villager v)
        {
            var parts = new List<string>(2);
            float edu = GetEducationBonusPercent(v);
            if (edu > 0f) parts.Add($"+{edu:0}% edu");
            float mas = GetMasteryBonusPercent(v);
            if (mas > 0f) parts.Add($"+{mas:0}% mastery");
            return parts.Count == 0 ? "" : string.Join(" · ", parts.ToArray());
        }
    }
}
