// Workplace Mastery — villagers work a little faster the longer they hold the same
// job. Default +2% per in-game year of tenure, capped at +10% (5 years). Tunable
// 1-3%/year and a 0-25 year cap. Original EP feature, OFF by default.
//
// HOW IT STACKS: a SECOND Harmony postfix on the same chokepoint Learned Hands uses —
// HappinessManager.GetWorkRateMultiplier(Villager) (the 1-arg overload). Both postfixes
// multiply __result, and multiplication commutes, so order is irrelevant:
// final = base × (1 + education) × (1 + tenure).
//
// COVERAGE (verified against the decompile, _research/mastery-coverage-map.md): the
// work-rate multiplier is consumed in exactly ONE work tick —
// ReservableItemStorage.DetachItemsFromReservation — which gates resource EXTRACTION
// (wood/mining/foraging), field TILLING/PLANTING/MAINTENANCE, clearing/repair/explore/
// excavate/salvage, and hunter BUTCHERING. It does NOT reach MANUFACTURING throughput
// (the old "covers manufacturing via the 4-arg delegation" claim was wrong — the 4-arg
// only paints the work-rate icon). Crafting is covered separately by ManufacturePatch
// below (a postfix on ManufactureWorkOrder.AdjustAddedWorkUnits) which applies BOTH the
// education and tenure terms so educated/veteran crafters finally speed up too.
//
// TENURE MODEL: per villager we keep {occupation -> committed days} plus the currently-
// open tenure (occupation + the absolute in-game day it began). "Open" days are derived
// on read as (today - start); they're committed into the per-occupation tally only on a
// job change or at save time. Absolute day = year*360 + dayOfYear (FF year = 360 days),
// which is monotonic and wrap-free (the game's year boundary is day 77, so raw dayOfYear
// deltas can't be used). years = days / 360.
//
// TRACKING is event-driven: VillagerBecameOccupationEvent opens a tenure, Villager-
// LeftOccupationEvent closes it (FF raises them as a pair on every job change; Became
// carries the new job, Left the old). No per-frame work and no DayPassed subscription —
// the derived-day model needs neither. The subscribe/unsubscribe lifecycle is managed
// from OnUpdate (mirroring ConsumableControl) so it arms once the game is live and tears
// down cleanly when the feature is toggled off.
//
// PERSISTENCE: a per-save JSON sidecar (Common/MasteryStore.cs), loaded on
// GameFinishedLoadingEvent and written on StartSaveGameEvent (the only save-side hook FF
// raises). FF has no stable per-villager id, so the sidecar is keyed by a composite of
// the one absolute per-villager date FF persists (birthday) + immutable sex/joinReason +
// name. RETROACTIVITY: existing villagers' job tenure can't be reconstructed (FF stores
// no job-start date), so without a sidecar everyone starts at 0 and accrues going forward.
//
// UI (v1): a per-cell "top occupation + tenure" value in the villager picker
// (UIVillagerCell.UpdateContent). UIVillagerCell is an *internal* game type, so the UI is
// patched MANUALLY in Initialize() under try/catch — if a future build renames it, only
// the cosmetic column is lost; the auto-PatchAll of every other EP patch (incl. the work-
// rate bonus) is untouched. Column header + alignment + hover breakdown come in a follow-
// up once the VillagerCell(Clone) prefab layout is dumped in-game.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using EssentialProvisions.Common;

namespace EssentialProvisions.Features
{
    internal static class WorkplaceMastery
    {
        private const float DaysPerYear = 360f;   // TimeManager.DAYS_PER_YEAR (12 mo × 30 d)
        private const int OccNone = 59;           // VillagerOccupation.Occupation.None

        // Occupations that don't practice a trade: they must neither accrue nor display mastery
        // tenure (else KC lights up a "+X% Mastery Bonus" on, e.g., a child's job line). Child grows
        // up; Disabled *pauses* (prior tenure is committed first, then preserved — not lost — and
        // resumes on recovery); Deserter has left the settlement; TransitionToSoldier is a transient
        // state; None is the no-job sentinel. Gated on BOTH the read path (hides childhood days banked
        // by older saves) and the accrual path (never stores them going forward).
        private const int OccTransitionToSoldier = 13;
        private const int OccChild = 21;
        private const int OccDeserter = 29;
        private const int OccDisabled = 35;
        private static readonly HashSet<int> NonWorkingOccupations =
            new HashSet<int> { OccTransitionToSoldier, OccChild, OccDeserter, OccDisabled, OccNone };
        private static bool AccruesMastery(int occ) => !NonWorkingOccupations.Contains(occ);

        // Hardcoded tuning (on/off feature, no sliders — rebalance by patching).
        internal const float TenureCapYears = 25f;     // tenure stops earning past 25 years
        internal const float WorkOutputPerYear = 0.01f; // +1%/yr on gather/farm/craft → +25% at cap

        // In-memory tenure, keyed by villager instanceID (matches how EfficientLabor /
        // ConsumableControl / LearnedHands key villagers within a session). The disk
        // sidecar is keyed by composite key; we bridge the two at load/save time.
        private static readonly Dictionary<int, MasteryRecord> _byInstance = new Dictionary<int, MasteryRecord>();

        private static bool _subscribed;
        private static bool _wasEnabled;
        private static bool _hydrated;            // sidecar loaded for the current save
        private static bool _firstApplicationLogged;
        private static bool _workRateErrorLogged;
        private static bool _mfgFirstLogged;
        private static bool _mfgErrorLogged;

        public static void Reset()
        {
            UnsubscribeIfSubscribed();
            _byInstance.Clear();
            _wasEnabled = false;
            _hydrated = false;
            _firstApplicationLogged = false;
            _workRateErrorLogged = false;
            _mfgFirstLogged = false;
            _mfgErrorLogged = false;
        }

        // One-time setup from Plugin.OnInitializeMelon. Manually patches the picker UI
        // (see file header for why it's not an attribute patch).
        public static void Initialize()
        {
            try
            {
                var h = new HarmonyLib.Harmony("EssentialProvisions.WorkplaceMastery.UI");

                var cellType = AccessTools.TypeByName("UIVillagerCell");
                var cellM = cellType != null ? AccessTools.Method(cellType, "UpdateContent") : null;
                if (cellM != null)
                    h.Patch(cellM, postfix: new HarmonyMethod(typeof(WorkplaceMastery), nameof(CellPostfix)));
                else
                    Plugin.Log.Warning("[WorkplaceMastery] UIVillagerCell.UpdateContent not found — Mastery column disabled (work-rate bonus unaffected).");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[WorkplaceMastery] UI patch setup failed (column disabled, bonus unaffected): {ex.Message}");
            }
        }

        // ----- Subscription lifecycle (managed from OnUpdate, like ConsumableControl) -----

        public static void OnUpdate()
        {
            bool enabled = Config.EnableWorkplaceMastery.Value;
            if (!enabled && _wasEnabled)
                UnsubscribeIfSubscribed();   // keep _byInstance so a re-enable doesn't lose this session's tenure
            _wasEnabled = enabled;
            if (!enabled) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var em = gm.eventManager;
            if (em == null) return;

            if (!_subscribed)
            {
                em.AddListener<GameFinishedLoadingEvent>(OnGameFinishedLoading);
                em.AddListener<StartSaveGameEvent>(OnStartSave);
                em.AddListener<VillagerBecameOccupationEvent>(OnBecame);
                em.AddListener<VillagerLeftOccupationEvent>(OnLeft);
                _subscribed = true;

                // We only reach here once GameManager.gameReadyToPlay is true (checked
                // above), so the world — including starting villagers — exists. Hydrate
                // once per session. This MUST NOT gate on gm.isGameFinishedLoading: that
                // flag (and GameFinishedLoadingEvent) only fire on a SAVE-LOAD, never on a
                // brand-new game, so gating on it left new settlements un-hydrated and
                // their starting-job villagers untracked. On a save-load,
                // OnGameFinishedLoading also fires and re-hydrates with correct timing;
                // Hydrate is idempotent (it clears and rebuilds), so the double call is
                // harmless. A toggle off→on (already hydrated) just re-aligns to live jobs.
                if (!_hydrated) Hydrate();
                else Resync();
            }
        }

        private static void UnsubscribeIfSubscribed()
        {
            if (!_subscribed) return;
            var em = UnitySingleton<GameManager>.Instance?.eventManager;
            if (em != null)
            {
                em.RemoveListener<GameFinishedLoadingEvent>(OnGameFinishedLoading);
                em.RemoveListener<StartSaveGameEvent>(OnStartSave);
                em.RemoveListener<VillagerBecameOccupationEvent>(OnBecame);
                em.RemoveListener<VillagerLeftOccupationEvent>(OnLeft);
            }
            _subscribed = false;
        }

        // ----- Lifecycle handlers -----

        private static void OnGameFinishedLoading(GameFinishedLoadingEvent e) => Hydrate();

        // Load the sidecar and bind it to live villagers by composite key, then align
        // each villager's open tenure to the job they're actually in right now.
        private static void Hydrate()
        {
            try
            {
                _byInstance.Clear();
                var rm = UnitySingleton<GameManager>.Instance?.resourceManager;
                if (rm?.villagersRO == null) { _hydrated = true; return; }

                string save = SaveName();
                var loaded = MasteryStore.Load(save);
                int now = AbsDay();
                int tracked = 0;
                // A loaded record is handed to the FIRST villager bearing its composite
                // key; any other villager sharing that key gets a FRESH record. FF has no
                // stable per-villager id (see CompositeKey), so same-key villagers (twins
                // born the same day, identical names, etc.) are possible — without this
                // guard two villagers would alias one MasteryRecord reference and corrupt
                // each other's tenure every tick.
                var usedKeys = new HashSet<string>();
                foreach (var v in rm.villagersRO)
                {
                    if (v == null) continue;
                    string key = CompositeKey(v);
                    var rec = (loaded.TryGetValue(key, out var found) && usedKeys.Add(key))
                        ? found : new MasteryRecord();
                    AlignToLiveJob(rec, SafeOcc(v), now);
                    _byInstance[v.GetInstanceID()] = rec;
                    tracked++;
                }
                _hydrated = true;
                Plugin.Log.Msg($"[WorkplaceMastery] Loaded {loaded.Count} tenure record(s); tracking {tracked} villager(s) for save \"{save}\".");
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] Hydrate: {ex.Message}"); _hydrated = true; }
        }

        // Re-align tracking to current jobs without touching the disk sidecar (used when
        // the feature is toggled back ON mid-session, after we'd unsubscribed and may have
        // missed job changes).
        private static void Resync()
        {
            try
            {
                var rm = UnitySingleton<GameManager>.Instance?.resourceManager;
                if (rm?.villagersRO == null) return;
                int now = AbsDay();
                foreach (var v in rm.villagersRO)
                {
                    if (v == null) continue;
                    int id = v.GetInstanceID();
                    if (!_byInstance.TryGetValue(id, out var rec)) { rec = new MasteryRecord(); _byInstance[id] = rec; }
                    AlignToLiveJob(rec, SafeOcc(v), now);
                }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] Resync: {ex.Message}"); }
        }

        // If a record's open tenure doesn't match the villager's live job, commit the
        // open days and reopen against the live job.
        private static void AlignToLiveJob(MasteryRecord rec, int liveOcc, int now)
        {
            if (now < 0) return;   // timeManager not ready — don't poison the open-tenure anchor to -1
            if (rec.CurrentOcc == liveOcc) return;
            CommitOpen(rec, now);
            rec.CurrentOcc = liveOcc;
            rec.CurrentStartAbsDay = AccruesMastery(liveOcc) ? now : -1;
        }

        private static void OnStartSave(StartSaveGameEvent e)
        {
            try
            {
                var rm = UnitySingleton<GameManager>.Instance?.resourceManager;
                if (rm?.villagersRO == null) return;
                int now = AbsDay();
                var byKey = new Dictionary<string, MasteryRecord>();
                foreach (var v in rm.villagersRO)
                {
                    if (v == null) continue;
                    if (!_byInstance.TryGetValue(v.GetInstanceID(), out var rec)) continue;
                    CommitOpen(rec, now);   // fold open days into committed; resets start to now
                    // First-writer-wins on a key collision (deterministic). Colliding-key
                    // villagers can't both round-trip — there's no stable id to tell them
                    // apart on reload — but this avoids a silent last-writer overwrite.
                    string key = CompositeKey(v);
                    if (!byKey.ContainsKey(key)) byKey[key] = rec;
                }
                MasteryStore.Save(SaveName(), byKey);
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] OnStartSave: {ex.Message}"); }
        }

        private static void OnBecame(VillagerBecameOccupationEvent e)
        {
            if (e == null) return;
            var v = e.villager;
            if (v == null) return;
            int id = v.GetInstanceID();
            if (!_byInstance.TryGetValue(id, out var rec)) { rec = new MasteryRecord(); _byInstance[id] = rec; }
            int now = AbsDay();
            CommitOpen(rec, now);
            rec.CurrentOcc = (int)e.occupation;
            rec.CurrentStartAbsDay = AccruesMastery(rec.CurrentOcc) ? now : -1;
        }

        private static void OnLeft(VillagerLeftOccupationEvent e)
        {
            var v = e?.villager;
            if (v == null) return;
            if (!_byInstance.TryGetValue(v.GetInstanceID(), out var rec)) return;
            CommitOpen(rec, AbsDay());
            rec.CurrentOcc = OccNone;
            rec.CurrentStartAbsDay = -1;
        }

        // ----- Tenure math -----

        // Monotonic, wrap-free absolute in-game day. (Year boundary is day 77, so raw
        // dayOfYear deltas wrap; year*360 + dayOfYear never does.)
        private static int AbsDay()
        {
            var tm = UnitySingleton<GameManager>.Instance?.timeManager;
            if (tm == null) return -1;
            var d = tm.currentDate;
            return d.year * 360 + d.dayOfYear;
        }

        // Fold the open tenure's elapsed days into committed Days[occ], then reset the
        // open-tenure start to 'now' so the same days aren't counted twice.
        private static void CommitOpen(MasteryRecord rec, int now)
        {
            if (rec.CurrentStartAbsDay < 0 || !AccruesMastery(rec.CurrentOcc) || now < 0) return;
            int d = now - rec.CurrentStartAbsDay;
            if (d > 0)
            {
                rec.Days.TryGetValue(rec.CurrentOcc, out var prev);
                rec.Days[rec.CurrentOcc] = prev + d;
            }
            rec.CurrentStartAbsDay = now;
        }

        // Tenure-years a villager has in a specific occupation (committed + live open).
        private static float YearsFor(int instanceID, int occ)
        {
            if (!_byInstance.TryGetValue(instanceID, out var rec)) return 0f;
            rec.Days.TryGetValue(occ, out var committed);
            int open = 0;
            if (rec.CurrentOcc == occ && rec.CurrentStartAbsDay >= 0)
            {
                int now = AbsDay();
                if (now >= rec.CurrentStartAbsDay) open = now - rec.CurrentStartAbsDay;
            }
            return (committed + open) / DaysPerYear;
        }

        // Interop accessor (for WorkInfoApi): the current-job mastery bonus as a
        // percent (e.g. 8 for +8%), or false if none. Mirrors WorkRatePatch's math.
        internal static bool TryGetCurrentMasteryBonus(Villager v, out float bonusPct)
        {
            bonusPct = 0f;
            if (v == null || v.occupation == null) return false;
            int occ = (int)v.occupation.GetOccupation();
            if (!AccruesMastery(occ)) return false;
            float years = YearsFor(v.GetInstanceID(), occ);
            if (years <= 0f) return false;
            bonusPct = Mathf.Min(years, TenureCapYears) * (WorkOutputPerYear * 100f); // → percent
            return bonusPct > 0f;
        }

        // Interop (for WorkInfoApi): a villager's highest-mastery occupations, sorted DESC by
        // bonus, up to `count`, excluding zero. Each entry = (display name, bonus percent points,
        // e.g. "Blacksmith" + 8f). Empty when the feature is off / no tenure / null villager.
        internal static List<KeyValuePair<string, float>> GetTopMasteries(Villager v, int count)
        {
            var result = new List<KeyValuePair<string, float>>();
            if (v == null || count <= 0 || !Config.EnableWorkplaceMastery.Value) return result;
            int id = v.GetInstanceID();
            if (!_byInstance.TryGetValue(id, out var rec)) return result;

            var occs = new HashSet<int>(rec.Days.Keys);
            if (AccruesMastery(rec.CurrentOcc)) occs.Add(rec.CurrentOcc);

            var ranked = new List<KeyValuePair<int, float>>();
            foreach (var occ in occs)
            {
                if (!AccruesMastery(occ)) continue;   // hide childhood/disabled tenure banked by older saves
                float years = YearsFor(id, occ);
                float pct = Mathf.Min(years, TenureCapYears) * (WorkOutputPerYear * 100f);
                if (pct > 0f) ranked.Add(new KeyValuePair<int, float>(occ, pct));
            }
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < ranked.Count && i < count; i++)
                result.Add(new KeyValuePair<string, float>(PrettyOcc(ranked[i].Key), ranked[i].Value));
            return result;
        }

        private static int SafeOcc(Villager v)
        {
            try { return v.occupation != null ? (int)v.occupation.GetOccupation() : OccNone; }
            catch { return OccNone; }
        }

        // ----- Composite key (no stable villager id; birthday is the only absolute
        //       per-villager date FF persists, so it anchors the key) -----
        private static string CompositeKey(Villager v)
        {
            try
            {
                var bd = v.villagerHealth.GetBirthday();
                return $"{bd.year}-{bd.dayOfYear}|{(v.isMale ? "M" : "F")}|{(int)v.joinReason}|{v.villagerName}";
            }
            catch
            {
                return "iid:" + v.GetInstanceID();   // last-ditch; not stable across loads
            }
        }

        private static string SaveName()
        {
            try
            {
                var f = SaveManager.activeSaveFileName;
                if (string.IsNullOrEmpty(f)) f = SaveManager.activeSettlementName;
                if (string.IsNullOrEmpty(f)) return "";
                if (f == "AutoSave 1" || f == "AutoSave 2")
                    return string.IsNullOrEmpty(SaveManager.activeSettlementName) ? f : SaveManager.activeSettlementName;
                return System.IO.Path.GetFileNameWithoutExtension(f);
            }
            catch { return ""; }
        }

        // ----- Work-rate bonus (stacks on Learned Hands; same 1-arg chokepoint) -----

        [HarmonyPatch(typeof(HappinessManager), nameof(HappinessManager.GetWorkRateMultiplier), new[] { typeof(Villager) })]
        internal static class WorkRatePatch
        {
            private static void Postfix(Villager villager, ref float __result)
            {
                if (!Config.EnableWorkplaceMastery.Value) return;
                // This runs on a high-frequency work path; never let it throw into game
                // code. (EP rule: patches never propagate exceptions.)
                try
                {
                    if (villager == null || villager.occupation == null) return;

                    int occ = (int)villager.occupation.GetOccupation();
                    if (!AccruesMastery(occ)) return;

                    float years = YearsFor(villager.GetInstanceID(), occ);
                    if (years <= 0f) return;

                    float bonus = Mathf.Min(years, TenureCapYears) * WorkOutputPerYear;
                    if (bonus <= 0f) return;

                    float before = __result;
                    __result *= 1f + bonus;

                    if (!_firstApplicationLogged)
                    {
                        _firstApplicationLogged = true;
                        Plugin.Log.Msg($"[WorkplaceMastery] First application: occ {PrettyOcc(occ)}, {years:F2}y tenure, " +
                            $"base {before:F3} → {__result:F3} (+{bonus * 100f:F1}%).");
                    }
                }
                catch (Exception ex)
                {
                    if (!_workRateErrorLogged)
                    {
                        _workRateErrorLogged = true;
                        Plugin.Log.Warning($"[WorkplaceMastery] WorkRatePatch error (suppressed further): {ex.Message}");
                    }
                }
            }
        }

        // ----- Manufacturing throughput (crafting) — applies BOTH education and tenure -----
        //
        // The work-rate hook above never reaches crafting (see header). Crafting throughput is
        // ManufactureWorkOrder.AddWorkUnits → AdjustAddedWorkUnits, which returns the work
        // units applied this tick. We postfix it and add the competence bonus, gating the
        // education term on Learned Hands and the tenure term on Workplace Mastery (same rates
        // as the work-rate hook, so the two stay consistent). assignedWorker IS the crafting
        // Villager (Villager : IManufacturer). Because the method returns a uint and a tick can
        // be as small as 1 unit, a naive multiply would floor a +10% bonus away — so we
        // accumulate the fractional bonus per work-order and emit whole units as they accrue.
        private static readonly ConditionalWeakTable<ManufactureWorkOrder, StrongBox<float>> _mfgAccum
            = new ConditionalWeakTable<ManufactureWorkOrder, StrongBox<float>>();

        [HarmonyPatch(typeof(ManufactureWorkOrder), "AdjustAddedWorkUnits")]
        internal static class ManufacturePatch
        {
            private static void Postfix(ManufactureWorkOrder __instance, uint amount, ref uint __result)
            {
                bool edu = Config.EnableLearnedHands.Value;
                bool tenure = Config.EnableWorkplaceMastery.Value;
                if ((!edu && !tenure) || amount == 0u || __instance == null) return;
                try
                {
                    var v = __instance.assignedWorker as Villager;
                    if (v == null) return;

                    float factor = 1f;

                    if (edu)
                    {
                        var e = v.education;
                        int level = (e != null && e.currentLevel != null) ? e.currentLevel.level : 0;
                        if (level > 0) factor *= 1f + level * LearnedHands.PerLevel;
                    }

                    if (tenure && v.occupation != null)
                    {
                        int occ = (int)v.occupation.GetOccupation();
                        if (AccruesMastery(occ))   // parity with WorkRatePatch — no Child/Disabled/etc. crafting tenure
                        {
                            float years = YearsFor(v.GetInstanceID(), occ);
                            if (years > 0f)
                                factor *= 1f + Mathf.Min(years, TenureCapYears) * WorkOutputPerYear;
                        }
                    }

                    if (factor <= 1f) return;

                    var box = _mfgAccum.GetOrCreateValue(__instance);
                    box.Value += amount * (factor - 1f);
                    if (box.Value >= 1f)
                    {
                        uint whole = (uint)Mathf.FloorToInt(box.Value);
                        box.Value -= whole;
                        __result += whole;

                        if (!_mfgFirstLogged)
                        {
                            _mfgFirstLogged = true;
                            Plugin.Log.Msg($"[WorkplaceMastery] First manufacturing bonus applied: ×{factor:F3} (+{whole} work unit this tick).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!_mfgErrorLogged)
                    {
                        _mfgErrorLogged = true;
                        Plugin.Log.Warning($"[WorkplaceMastery] ManufacturePatch error (suppressed further): {ex.Message}");
                    }
                }
            }
        }

        // ----- UI: Mastery breakdown on the villager-picker hover tooltip -----
        //
        // The picker's columns already fill the fixed-width window (~953 of ~963px), so
        // there's no room for a 6th column without shrinking a vanilla one or widening the
        // window. Instead we surface tenure where it reads naturally: the profession-icon
        // tooltip. UIVillagerCell.UpdateContent sets that provider's rows on every (re)bind
        // and THIS postfix runs after it, so appended mastery rows stay fresh across pooled-
        // cell rebinds. The provider is an existing, already-wired GenericTooltipDataProvider
        // (a from-scratch one would need its own hover-receiver), so this is the robust path.

        private static System.Reflection.FieldInfo? _profTooltipField;

        private static void CellPostfix(object __instance, UIVillagerData villagerData)
        {
            if (!Config.EnableWorkplaceMastery.Value) return;
            try
            {
                if (__instance == null) return;
                var v = villagerData != null ? villagerData.villager : null;
                if (v == null) return;

                var provider = GetProfessionTooltip(__instance);
                if (provider?.toolTipRowKeyNames == null) return;

                // Vanilla added the occupation string (row 0); append the tenure breakdown
                // below it. UpdateContent re-clears + re-adds on the next bind and this
                // postfix re-runs, so the rows never go stale.
                foreach (var row in BreakdownRows(v))
                    provider.toolTipRowKeyNames.Add(row);
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] cell tooltip: {ex.Message}"); }
        }

        private static GenericTooltipDataProvider? GetProfessionTooltip(object cell)
        {
            if (_profTooltipField == null)
                _profTooltipField = AccessTools.Field(cell.GetType(), "professionIconTooltipProvider");
            return _profTooltipField?.GetValue(cell) as GenericTooltipDataProvider;
        }

        // Tooltip rows: a header, then the top 3 occupations by tenure (descending).
        private static List<string> BreakdownRows(Villager v)
        {
            var rows = new List<string>();
            int id = v.GetInstanceID();
            if (!_byInstance.TryGetValue(id, out var rec)) { rows.Add("Mastery: none yet"); return rows; }

            var occs = new HashSet<int>(rec.Days.Keys);
            if (AccruesMastery(rec.CurrentOcc)) occs.Add(rec.CurrentOcc);

            var ranked = new List<KeyValuePair<int, float>>();
            foreach (var occ in occs)
            {
                if (!AccruesMastery(occ)) continue;   // hide childhood/disabled tenure banked by older saves
                float y = YearsFor(id, occ);
                if (y > 0f) ranked.Add(new KeyValuePair<int, float>(occ, y));
            }
            if (ranked.Count == 0) { rows.Add("Mastery: none yet"); return rows; }

            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));
            rows.Add("— Workplace Mastery —");
            int shown = 0;
            foreach (var kv in ranked)
            {
                rows.Add($"{PrettyOcc(kv.Key)}: {FormatTenure(kv.Value)}");
                if (++shown >= 3) break;
            }
            return rows;
        }

        private static string FormatTenure(float years)
        {
            if (years >= 1f) return $"{Mathf.FloorToInt(years)}y";
            return $"{Mathf.RoundToInt(years * DaysPerYear)}d";
        }

        private static string PrettyOcc(int occ)
        {
            try { return Enum.GetName(typeof(VillagerOccupation.Occupation), (VillagerOccupation.Occupation)occ) ?? occ.ToString(); }
            catch { return occ.ToString(); }
        }
    }
}
