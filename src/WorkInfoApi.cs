// Public interop so Keep Clarity (or any mod) can show a villager's EP work
// bonuses in UI. KC reads this reflectively (soft-dep) — no hard reference either
// way. Returns a compact one-line summary; KC owns where/how it's rendered.
//
// EP owns the string, so when bonus formulas change here KC needs no update.

using System.Collections.Generic;
using EssentialProvisions.Features;
using UnityEngine;

namespace EssentialProvisions
{
    public static class WorkInfoApi
    {
        /// <summary>
        /// Compact summary of a villager's EP work-rate bonuses, e.g.
        /// "+10% edu · +8% mastery", or "" if none apply / features are off.
        /// </summary>
        public static string GetVillagerWorkSummary(Villager v)
        {
            if (v == null) return "";
            var parts = new List<string>(2);

            // Education (Learned Hands): level × per-level bonus.
            try
            {
                if (Config.EnableLearnedHands.Value)
                {
                    var edu = v.education;
                    int level = (edu != null && edu.currentLevel != null) ? edu.currentLevel.level : 0;
                    if (level > 0)
                    {
                        float pct = level * Config.LearnedHandsPerLevelBonus.Value * 100f;
                        if (pct > 0f) parts.Add($"+{pct:0}% edu");
                    }
                }
            }
            catch { }

            // Workplace Mastery: capped tenure in the current job.
            try
            {
                if (Config.EnableWorkplaceMastery.Value &&
                    WorkplaceMastery.TryGetCurrentMasteryBonus(v, out float masPct) && masPct > 0f)
                    parts.Add($"+{masPct:0}% mastery");
            }
            catch { }

            return parts.Count == 0 ? "" : string.Join(" · ", parts);
        }
    }
}
