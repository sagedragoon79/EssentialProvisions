// Public interop so Keep Clarity (or any mod) can show a sick villager's recovery odds in its own
// UI — the villager info window / hospital panel "sick" line. EP owns the numbers (it already
// computes the cure math for the Rabies-Treatable feature); the consumer owns all labels, placement,
// and formatting. KC reads this reflectively (soft-dep) — no hard reference either way.
//
// STABILITY CONTRACT: these public static signatures are the soft-dep boundary KC binds to
// reflectively. Once shipped, do NOT rename/remove or change signatures — add new methods instead.
// All methods are null-safe and never throw (return 0 / empty on any failure).
//
// Values are in PERCENTAGE POINTS (5f == 5%). "Not sick / incurable / unknown" returns 0 (not
// -1/NaN) so a consumer can hide a line/badge when the value is 0. The string[]/float[] getters
// return PARALLEL arrays (same length, same order, one entry per active disease) — zip by index.

using System.Collections.Generic;
using EssentialProvisions.Common;
using UnityEngine;

namespace EssentialProvisions
{
    public static class DiseaseInfoApi
    {
        /// <summary>True if the villager currently has at least one active disease.</summary>
        public static bool IsSick(Villager v)
        {
            var list = PresentDiseases(v);
            return list != null && list.Count > 0;
        }

        /// <summary>Active-disease DISPLAY names (localized, e.g. "Rabies"), one per present disease.
        /// Parallel to <see cref="GetCureChancePercents"/> and <see cref="GetOverallRecoveryPercents"/>.
        /// Empty if the villager isn't sick.</summary>
        public static string[] GetActiveDiseaseNames(Villager v)
        {
            var list = PresentDiseases(v);
            if (list == null || list.Count == 0) return new string[0];
            var names = new List<string>(list.Count);
            foreach (var dc in list)
            {
                var def = dc != null ? dc.diseaseDef : null;
                if (def != null) names.Add(DisplayName(def));
            }
            return names.ToArray();
        }

        /// <summary>Per-cure-check probability (percent points), parallel to
        /// <see cref="GetActiveDiseaseNames"/>. This is exactly what the game rolls each check interval
        /// and reflects the villager's CURRENT treatment (a staffed healer / hospital / medicine raises
        /// it). 0 for an incurable disease.</summary>
        public static float[] GetCureChancePercents(Villager v)
        {
            return PerDisease(v, perRoll: true);
        }

        /// <summary>Estimated chance to recover over the disease's FULL course (percent points),
        /// compounded across every cure check — parallel to <see cref="GetActiveDiseaseNames"/>. The
        /// honest "will they pull through" number (e.g. ~18% for hospital-treated rabies, vs the ~5%
        /// per-check figure from <see cref="GetCureChancePercents"/>). 0 for an incurable disease.</summary>
        public static float[] GetOverallRecoveryPercents(Villager v)
        {
            return PerDisease(v, perRoll: false);
        }

        /// <summary>The villager's current cure score — base 1, raised by a staffed healer building,
        /// medicine, and (later) healer mastery. Shared across all the villager's diseases. This is the
        /// treatment-context input to the cure math; surface it if you want to show "why" the odds are
        /// what they are. 0 if not sick / unknown.</summary>
        public static float GetCureScore(Villager v)
        {
            var immune = Immune(v);
            try { return immune != null && IsSick(v) ? immune.cureScore : 0f; }
            catch { return 0f; }
        }

        /// <summary>The intrinsic cure PATH per active disease (independent of current treatment),
        /// parallel to <see cref="GetActiveDiseaseNames"/>. Each entry is one token for KC to map to
        /// action text: "Incurable" | "SelfResolving" | "Water" | "Thirst" | "Diet" | "Warmth" |
        /// "Soap" | "Healer" (basic Healer's House cures it) | "Hospital" (needs the T2 Hospital +
        /// medicine) — or, defensively, a raw factor name for an unmapped required cure factor. Empty if
        /// the villager isn't sick.</summary>
        public static string[] GetCureMethods(Villager v)
        {
            var list = PresentDiseases(v);
            if (list == null || list.Count == 0) return new string[0];
            var tokens = new List<string>(list.Count);
            foreach (var dc in list)
            {
                var def = dc != null ? dc.diseaseDef : null;
                if (def != null) tokens.Add(CureMath.ClassifyCureMethod(def));
            }
            return tokens.ToArray();
        }

        /// <summary>Full-course recovery chance at IDEAL treatment (best building + medicine, condition
        /// satisfied), percent points, parallel to <see cref="GetActiveDiseaseNames"/>. The "if you
        /// actually treat it" number — distinct from <see cref="GetOverallRecoveryPercents"/>, which is
        /// the CURRENT-state number. ~near-100 for Dehydration, lower for serious water-gated ones like
        /// Typhoid, 100 for self-resolving ailments, 0 for incurable. Empty if the villager isn't sick.</summary>
        public static float[] GetBestCaseRecoveryPercents(Villager v)
        {
            var list = PresentDiseases(v);
            if (list == null || list.Count == 0) return new float[0];
            var vals = new List<float>(list.Count);
            foreach (var dc in list)
            {
                var def = dc != null ? dc.diseaseDef : null;
                if (def != null) vals.Add(CureMath.BestCaseRecoveryFraction(def) * 100f);
            }
            return vals.ToArray();
        }

        /// <summary>Per active disease (parallel to <see cref="GetActiveDiseaseNames"/>): true if leaving
        /// it untreated can kill the villager (its terminal death chance is &gt; 0 and it isn't a
        /// self-resolving ailment). False for non-fatal ailments (Foot Wound, Bee Sting, etc.) so a
        /// consumer can de-emphasize them. Empty if the villager isn't sick.</summary>
        public static bool[] GetIsLethal(Villager v)
        {
            var list = PresentDiseases(v);
            if (list == null || list.Count == 0) return new bool[0];
            var vals = new List<bool>(list.Count);
            foreach (var dc in list)
            {
                var def = dc != null ? dc.diseaseDef : null;
                if (def != null) vals.Add(CureMath.IsLethal(def));
            }
            return vals.ToArray();
        }

        // ----- internals -----

        private static float[] PerDisease(Villager v, bool perRoll)
        {
            var list = PresentDiseases(v);
            var immune = Immune(v);
            if (list == null || list.Count == 0 || immune == null) return new float[0];
            var vals = new List<float>(list.Count);
            foreach (var dc in list)
            {
                var def = dc != null ? dc.diseaseDef : null;
                if (def == null) continue;
                float frac = perRoll ? CureMath.PerRollCureChance(def, immune)
                                     : CureMath.CourseSurvivalChance(def, immune);
                vals.Add(frac * 100f);
            }
            return vals.ToArray();
        }

        private static ImmuneSystemComponent? Immune(Villager v)
        {
            try { return v != null ? v.GetComponent<ImmuneSystemComponent>() : null; }
            catch { return null; }
        }

        private static List<DiseaseComponent>? PresentDiseases(Villager v)
        {
            var immune = Immune(v);
            try { return immune != null ? immune.presentDiseases : null; }
            catch { return null; }
        }

        private static string DisplayName(Disease def)
        {
            if (def == null) return "";
            try { var n = def.GetLocalizedDisplayName(); return string.IsNullOrEmpty(n) ? def.name : n; }
            catch { return def.name; }
        }
    }
}
