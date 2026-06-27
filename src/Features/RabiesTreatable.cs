// Rabies: Treatable (longshot) — OFF by default.
//
// Vanilla rabies (asset PD_Rabies) ships with a FLAT-0 cureDifficultyCurve, so its cure
// chance is 0 at every cureScore — no healer tier, medicine, or mastery can touch it
// (see memory: ff_disease_system_data + _research/mastery-coverage-map.md). Crucially the
// 0% lives in DATA, not a hard code gate: the per-roll cure is
//   Disease.cureDifficultyCurve.Evaluate(cureScore)   (decompile L83951)
// so replacing that flat-0 curve with a small POSITIVE curve makes rabies a longshot that
// SCALES with cureScore — a staffed Healer's House helps a little, a stocked Hospital (and,
// once built, healer mastery) helps more. cureFailDeathChance stays 1.0, so an untreated
// victim still dies; this just opens a slim window for good medical care.
//
// THREE gates must open for a rabies victim to actually have a chance:
//   1. cureDifficultyCurve (flat 0 → small positive).
//   2. cure-effectiveness multiplier (effMin 0 / unmet cureFactors → floored to 1 + factors
//      cleared) so time = cureScore × 1 feeds the curve.
//   3. TREATMENT-SEEKING: rabies has NO Bedridden symptom, so a victim never walks into a
//      healer and never earns the +10 cureScore — and that's the ONLY treatment route
//      (ShouldSeekBedRest() is gated solely on isBedridden, set only by a BedriddenSymptom).
//      We inject a Bedridden symptom (full disease duration, reusing a shared BedriddenSymptom
//      asset) so the victim is laid up in a healer's sick bed and actually gets treated.
//      THE COST (the yin/yang): they stop working and occupy 1 of the healer's 2 beds for the
//      illness; with no reachable staffed healer bed they're bedridden at home, get no +10, and
//      almost certainly still die.
//
// It's a deliberate vanilla balance change (Crate made rabies a hard death sentence on
// purpose), so it's its own opt-in toggle, separate from mastery. We override the shared
// PD_Rabies ScriptableObject once (caching originals to restore on toggle-off), found via
// Resources.FindObjectsOfTypeAll<Disease>() by asset name.

using System;
using System.Collections.Generic;
using EssentialProvisions.Common;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class RabiesTreatable
    {
        private const string RabiesAssetName = "PD_Rabies";

        private static bool _applied;
        private static Disease? _rabies;
        // Cached originals to restore vanilla on toggle-off. There are TWO data gates that
        // both force rabies to 0%: the flat-0 cureDifficultyCurve AND a 0 cure-effectiveness
        // multiplier (RollCure: time = cureScore × GetCureEffectivenessMultiplier; that helper
        // returns 0 when effMin is 0 / cureFactors are unmet, zeroing the curve input). We
        // clear BOTH so the positive curve actually receives a non-zero cureScore.
        private static AnimationCurve? _originalCurve;
        private static float _originalEffMin;
        private static List<FactorInfo>? _originalCureFactors;
        private static List<SymptomInfo>? _originalSymptoms;
        private static bool _symptomsReplaced;   // did Apply actually swap symptoms? (only on the bed-found path)
        private static BedriddenSymptom? _bedridden;   // shared asset, reused

        // X = cureScore (base 1; +10 in a staffed healer building; +medicine; +mastery later).
        // Y = cure probability per 14-day roll. ~0% with no healer, scaling to ~5% at a
        // top-tier cureScore. Over rabies's 4 rolls (60-day duration) that's a low-but-real
        // chance — a longshot, not a reprieve. On/off feature, no slider; retune here if wanted.
        private static AnimationCurve MakeCurve() => AnimationCurve.Linear(0f, 0f, 25f, 0.05f);

        public static void Reset()
        {
            Restore();            // scene change: hand the SO back to vanilla if we'd overridden it
            _applied = false;
            _originalCurve = null;
            _originalCureFactors = null;
            _originalSymptoms = null;
            _symptomsReplaced = false;
            _rabies = null;       // re-resolve next session
            _bedridden = null;
        }

        public static void OnUpdate()
        {
            bool enabled = Config.EnableRabiesTreatable.Value;
            if (!GameManager.gameReadyToPlay) return;
            if (enabled && !_applied) Apply();
            else if (!enabled && _applied) Restore();
        }

        private static Disease? FindRabies()
        {
            if (_rabies != null) return _rabies;
            try
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<Disease>())
                    if (d != null && d.name == RabiesAssetName) { _rabies = d; break; }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[RabiesTreatable] find: {ex.Message}"); }
            return _rabies;
        }

        // A shared BedriddenSymptom asset (other diseases use it) — reuse, don't construct.
        private static BedriddenSymptom? FindBedridden()
        {
            if (_bedridden != null) return _bedridden;
            try
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<BedriddenSymptom>())
                    if (s != null) { _bedridden = s; break; }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[RabiesTreatable] find bedridden: {ex.Message}"); }
            return _bedridden;
        }

        private static void Apply()
        {
            var d = FindRabies();
            if (d == null) return;   // disease SOs not loaded yet — retry next tick
            try
            {
                _originalCurve = d.cureDifficultyCurve;
                _originalEffMin = d.cureEffectivenessMultiplierMin;
                _originalCureFactors = d.cureFactors;

                d.cureDifficultyCurve = MakeCurve();
                // Open the second gate: floor the cure-effectiveness multiplier at 1 and clear
                // the (unsatisfiable) cure-factors, so time = cureScore × 1 = cureScore feeds
                // the curve. Without this, GetCureEffectivenessMultiplier returns 0 and the
                // curve override is moot.
                d.cureEffectivenessMultiplierMin = 1f;
                d.cureFactors = new List<FactorInfo>();

                // Gate 3: inject a Bedridden symptom (full duration) so the victim seeks a
                // healer and earns the +10 cureScore — without this the curve only ever rolls
                // at cureScore 1. New list (don't mutate the original) so Restore is clean.
                bool bedInjected = false;
                var bed = FindBedridden();
                if (bed != null)
                {
                    _originalSymptoms = d.symptoms;
                    var list = d.symptoms != null ? new List<SymptomInfo>(d.symptoms) : new List<SymptomInfo>();
                    bool hasBed = false;
                    foreach (var si in list) if (si.symptom is BedriddenSymptom) { hasBed = true; break; }
                    if (!hasBed)
                        list.Add(new SymptomInfo { symptom = bed, magnitude = 1f, duration = d.duration });
                    d.symptoms = list;
                    _symptomsReplaced = true;
                    bedInjected = true;
                }

                _applied = true;
                Plugin.Log.Msg(bedInjected
                    ? "[RabiesTreatable] Rabies is now a treatable longshot — positive cure curve, opened the cure-effectiveness gate, and added a Bedridden symptom so victims seek the healer (cure scales with healer quality; victims are laid up while ill)."
                    : "[RabiesTreatable] Rabies cure math opened, but no BedriddenSymptom asset found — victims won't seek the healer, so cure stays a sub-1% innate chance. (Report this.)");
            }
            catch (Exception ex) { Plugin.Log.Warning($"[RabiesTreatable] apply: {ex.Message}"); }
        }

        private static void Restore()
        {
            if (!_applied || _rabies == null) return;
            try
            {
                if (_originalCurve != null) _rabies.cureDifficultyCurve = _originalCurve;
                _rabies.cureEffectivenessMultiplierMin = _originalEffMin;
                if (_originalCureFactors != null) _rabies.cureFactors = _originalCureFactors;
                // Only restore symptoms if Apply actually replaced them (bed-found path). On the
                // no-BedriddenSymptom fallback _originalSymptoms is null and symptoms were never
                // touched — writing null here would wipe the shared SO and NRE FF's ApplySymptoms.
                if (_symptomsReplaced) { _rabies.symptoms = _originalSymptoms; _symptomsReplaced = false; }
                _applied = false;
                Plugin.Log.Msg("[RabiesTreatable] Restored vanilla rabies (incurable, no bedridden symptom).");
            }
            catch (Exception ex) { Plugin.Log.Warning($"[RabiesTreatable] restore: {ex.Message}"); }
        }

        // ----- Cure-roll diagnostics (one-time Harmony patch from Plugin.OnInitializeMelon) -----
        //
        // The data override above only changes WHETHER rabies can be cured; the rolls themselves
        // fire inside FF's Disease/DiseaseComponent (decompile L84144 OnCureChance → RollCure,
        // L84156 OnDiseaseExpired → RollFinalCure → OnCureFailure). Those are silent, so a victim's
        // fate was previously a black box ("did Orman ever get close?"). We postfix the per-interval
        // cure roll and prefix the two terminal outcomes to narrate it. Self-gated to PD_Rabies +
        // the toggle, so it's a handful of lines per (rare) victim — not log spam — and never throws.
        //
        // NOTE on "surviving a roll": each RollCure is a CHANCE TO BE CURED (~2-5% here), not a
        // survival check — failing one just means "still ill, roll again next interval". Death only
        // happens at the very end (RollFinalCure fails → RollCureFailureDeathChance, ~100% for rabies).
        private static bool _diagPatched;

        public static void Initialize()
        {
            if (_diagPatched) return;
            try
            {
                var h = new HarmonyLib.Harmony("EssentialProvisions.RabiesTreatable.Diag");

                var rollCure = AccessTools.Method(typeof(Disease), "RollCure", new[] { typeof(ImmuneSystemComponent) });
                if (rollCure != null)
                    h.Patch(rollCure, postfix: new HarmonyMethod(typeof(RabiesTreatable), nameof(RollCurePostfix)));
                else
                    Plugin.Log.Warning("[RabiesTreatable] Disease.RollCure not found — cure-roll logging disabled (treatment unaffected).");

                // Prefix (not postfix) the outcomes: read diseaseDef/name BEFORE the body tears the
                // disease down (RemoveDisease) or the death Kill() runs, so attribution stays intact.
                var onSuccess = AccessTools.Method(typeof(DiseaseComponent), "OnCureSuccess");
                if (onSuccess != null)
                    h.Patch(onSuccess, prefix: new HarmonyMethod(typeof(RabiesTreatable), nameof(CureSuccessPrefix)));

                var onFailure = AccessTools.Method(typeof(DiseaseComponent), "OnCureFailure");
                if (onFailure != null)
                    h.Patch(onFailure, prefix: new HarmonyMethod(typeof(RabiesTreatable), nameof(CureFailurePrefix)));

                _diagPatched = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[RabiesTreatable] diagnostic patch setup failed (logging only; treatment unaffected): {ex.Message}");
            }
        }

        private static bool IsRabies(Disease? d) => d != null && d.name == RabiesAssetName;

        private static string NameOf(GameObject? go)
        {
            try { var v = go != null ? go.GetComponent<Villager>() : null; return v != null ? v.villagerName : "(unknown)"; }
            catch { return "(unknown)"; }
        }

        private static Disease? DefOf(DiseaseComponent dc)
        {
            try { return dc != null ? dc.diseaseDef : null; }   // diseaseDef is public on DiseaseComponent
            catch { return null; }
        }

        // Per-interval cure attempt. __result is the REAL pass/fail; we recompute the probability the
        // same way the game does (curve(cureScore × effMult) × (1 + cureChanceMultiplier), L83950-52)
        // purely for display. Tech-tree global cure bonus (normally 0) isn't folded into the printed %.
        private static void RollCurePostfix(Disease __instance, ImmuneSystemComponent immuneSystem, bool __result)
        {
            try
            {
                if (!Config.EnableRabiesTreatable.Value || !IsRabies(__instance) || immuneSystem == null) return;
                float eff = __instance.GetCureEffectivenessMultiplier(immuneSystem);
                float prob = CureMath.PerRollCureChance(__instance, immuneSystem);   // shared math
                Plugin.Log.Msg($"[RabiesTreatable] Cure roll — {NameOf(immuneSystem.gameObject)}: "
                    + $"cureScore {immuneSystem.cureScore:0.#} × eff {eff:0.00} → {prob * 100f:0.0}% — "
                    + (__result ? "CURED!" : "failed (still ill)"));
            }
            catch { /* diagnostics only — never disrupt the cure */ }
        }

        private static void CureSuccessPrefix(DiseaseComponent __instance)
        {
            try
            {
                if (!Config.EnableRabiesTreatable.Value || __instance == null || !IsRabies(DefOf(__instance))) return;
                Plugin.Log.Msg($"[RabiesTreatable] {NameOf(__instance.gameObject)} RECOVERED from rabies — beat the longshot.");
            }
            catch { }
        }

        private static void CureFailurePrefix(DiseaseComponent __instance)
        {
            try
            {
                if (!Config.EnableRabiesTreatable.Value || __instance == null || !IsRabies(DefOf(__instance))) return;
                Plugin.Log.Msg($"[RabiesTreatable] {NameOf(__instance.gameObject)} succumbed to rabies — cure failed at the end of the illness.");
            }
            catch { }
        }
    }
}
