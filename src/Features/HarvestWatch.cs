// Harvest Watch (original EP feature, Agriculture Alerts) — worker-capacity feedback for orchards.
//
// PURPOSE: when you expand an Arborist's work area and/or worker slots (e.g. via other mods), there's
// no vanilla signal telling you whether your workers are actually keeping up. Fruit that isn't picked
// is silently wiped once a year (FruitTreeResource.UpdateFruitAndLogs removes all unreserved fruit on
// removeFruitOnThisDay == 360). Harvest Watch measures that loss: at each year-end it reports, per
// orchard, how many fruit trees still held unharvested fruit when the wipe hit — your "add a worker /
// shrink the area" signal. It also exposes a LIVE unharvested-tree count (via HarvestInfoApi) for KC
// to render on the building panel.
//
// DETECTION (verified against the decompile):
//   - Prefix FruitTreeResource.UpdateFruitAndLogs: on the wipe day, if the tree still holds
//     >= HarvestWatchMinFruit unreserved fruit, record its instanceID as "rotted this year".
//   - Year rollover (timeManager.currentDate.year increments) → attribute rotted trees to each
//     ArboristBuilding via its public treesInRadius list, log a per-orchard summary with a trend vs
//     last year + the building's worker count, then reset for the new year.
//
// Patches a private FF method, so it's patched MANUALLY in Initialize() under try/catch (EP pattern —
// a bad attribute TargetMethod would abort PatchAll). Never throws into game code.
//
// NOTE: the Work Camp (forestry) backlog gauge and a per-tree world indicator are the planned next
// phase — see _research. This file is the arborist rot-tally core.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class HarvestWatch
    {
        private const int DefaultWipeDay = 360;   // FruitTreeResource.removeFruitOnThisDay default

        private static bool _patched;
        private static FieldInfo? _removeFruitDayField;
        private static Item? _itemFruit;

        // Trees (instanceID) that lost unharvested fruit at this year's wipe; cleared each new year.
        private static readonly HashSet<int> _rottedTreeIds = new HashSet<int>();
        // Per-orchard (instanceID) rotted count from the PREVIOUS year, for the trend arrow.
        private static readonly Dictionary<int, int> _lastYearRotted = new Dictionary<int, int>();
        private static int _lastSeenYear = -1;
        private static bool _announced;                          // one-time "feature active" confirmation
        private static int _lastLiveLogAbsDay = int.MinValue;    // throttle for the ~monthly live snapshot

        public static void Reset()
        {
            _rottedTreeIds.Clear();
            _lastYearRotted.Clear();
            _lastSeenYear = -1;
            _announced = false;
            _lastLiveLogAbsDay = int.MinValue;
            _itemFruit = null;   // re-resolve next session
        }

        // One-time manual Harmony patch (private FF method → manual, not attribute).
        public static void Initialize()
        {
            if (_patched) return;
            try
            {
                var m = AccessTools.Method(typeof(FruitTreeResource), "UpdateFruitAndLogs");
                if (m != null)
                {
                    new HarmonyLib.Harmony("EssentialProvisions.HarvestWatch")
                        .Patch(m, prefix: new HarmonyMethod(typeof(HarvestWatch), nameof(FruitWipePrefix)));
                    _patched = true;
                }
                else
                {
                    Plugin.Log.Warning("[HarvestWatch] FruitTreeResource.UpdateFruitAndLogs not found — rot tally disabled (live count still works).");
                }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[HarvestWatch] patch setup failed: {ex.Message}"); }
        }

        // Runs daily per fruit tree, just before FF may wipe its unharvested fruit. On the wipe day,
        // flag the tree if it still holds enough unharvested fruit to count as neglected. Never throws.
        private static void FruitWipePrefix(FruitTreeResource __instance)
        {
            try
            {
                if (!Config.EnableHarvestWatch.Value || __instance == null) return;
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm?.timeManager == null) return;
                if (gm.timeManager.currentDate.dayOfYear != WipeDay(__instance)) return;   // only on the wipe day

                var fruit = FruitItem(__instance);
                var storage = __instance.storage;
                if (fruit == null || storage == null) return;

                uint unreserved = storage.GetNumberOfUnreservedItems(fruit);
                if (unreserved >= (uint)MinFruit())
                    _rottedTreeIds.Add(__instance.GetInstanceID());
            }
            catch { /* feedback only — never disturb the wipe */ }
        }

        public static void OnUpdate()
        {
            if (!Config.EnableHarvestWatch.Value) return;
            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm?.timeManager == null) return;
            var date = gm.timeManager.currentDate;

            if (!_announced)
            {
                int orchards = gm.resourceManager?.arboristBuildingsRO?.Count ?? 0;
                Plugin.Log.Msg($"[HarvestWatch] Active — watching {orchards} orchard(s). Live counts log ~monthly; the rotted-tree tally fires at the year-end fruit wipe (~day 360). Fruit ripens over the season, so spring counts are low.");
                _announced = true;
            }

            // Year rollover → the day-360 fruit wipes have happened → report the rot tally.
            if (_lastSeenYear < 0) _lastSeenYear = date.year;
            else if (date.year != _lastSeenYear) { _lastSeenYear = date.year; ReportYearEnd(gm); }

            // ~Monthly live snapshot, so there's standalone visibility well before year-end.
            int absDay = date.year * 360 + date.dayOfYear;
            if (absDay - _lastLiveLogAbsDay >= 30) { _lastLiveLogAbsDay = absDay; LogLiveSnapshot(gm); }
        }

        // One low-spam line every ~30 days: total orchards + how many trees currently hold unharvested
        // fruit, naming the worst orchard. Gives immediate, ongoing feedback before the year-end tally.
        private static void LogLiveSnapshot(GameManager gm)
        {
            try
            {
                var rm = gm.resourceManager;
                if (rm?.arboristBuildingsRO == null) return;
                int orchards = 0, totalUnharvested = 0, worst = 0;
                Vector3 worstPos = default;
                foreach (var ab in rm.arboristBuildingsRO)
                {
                    if (ab == null) continue;
                    orchards++;
                    int c = CountUnharvestedTrees(ab);
                    totalUnharvested += c;
                    if (c > worst) { worst = c; try { worstPos = ((Component)(object)ab).transform.position; } catch { } }
                }
                if (orchards == 0) return;
                string tail = worst > 0 ? $" (worst: Orchard ({worstPos.x:0}, {worstPos.z:0}) — {worst})." : ".";
                Plugin.Log.Msg($"[HarvestWatch] Live — {orchards} orchard(s); {totalUnharvested} tree(s) currently holding unharvested fruit{tail}");
            }
            catch (Exception ex) { Plugin.Log.Warning($"[HarvestWatch] live snapshot: {ex.Message}"); }
        }

        // Attribute the year's rotted trees to each orchard and log a per-building summary with trend.
        private static void ReportYearEnd(GameManager gm)
        {
            try
            {
                var rm = gm.resourceManager;
                if (rm?.arboristBuildingsRO == null) { _rottedTreeIds.Clear(); return; }

                var thisYear = new Dictionary<int, int>();
                foreach (var ab in rm.arboristBuildingsRO)
                {
                    if (ab == null) continue;
                    int id = ab.GetInstanceID();
                    int rotted = CountRottedInArborist(ab);
                    thisYear[id] = rotted;
                    if (rotted <= 0) continue;

                    string trend = "";
                    if (_lastYearRotted.TryGetValue(id, out int prev))
                        trend = rotted > prev ? $" (▲ from {prev})"
                              : rotted < prev ? $" (▼ from {prev})"
                              : " (same as last year)";

                    int workers = ab.workersRO?.Count ?? 0;
                    Plugin.Log.Msg($"[HarvestWatch] {Label(ab)}: {rotted} fruit tree(s) went unharvested and rotted this year{trend}"
                        + $" — {workers}/{ab.maxWorkers} workers. Add a worker or shrink the work area to keep up.");
                }

                // Roll this year's counts into "last year" for next time.
                _lastYearRotted.Clear();
                foreach (var kv in thisYear) _lastYearRotted[kv.Key] = kv.Value;
                _rottedTreeIds.Clear();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[HarvestWatch] year-end report: {ex.Message}");
                _rottedTreeIds.Clear();
            }
        }

        // ----- counts (also used by HarvestInfoApi) -----

        // Orchard trees that rotted unharvested THIS year (used by the year-end report).
        private static int CountRottedInArborist(ArboristBuilding ab)
        {
            try
            {
                var trees = ab?.treesInRadius;
                if (trees == null) return 0;
                int n = 0;
                foreach (var t in trees)
                    if (t != null && _rottedTreeIds.Contains(t.GetInstanceID())) n++;
                return n;
            }
            catch { return 0; }
        }

        // LIVE: orchard trees that currently hold >= MinFruit unharvested fruit (for KC's panel).
        internal static int CountUnharvestedTrees(ArboristBuilding ab)
        {
            try
            {
                var trees = ab?.treesInRadius;
                if (trees == null) return 0;
                uint min = (uint)MinFruit();
                int n = 0;
                foreach (var t in trees)
                {
                    if (t == null || t.storage == null) continue;
                    var fruit = FruitItem(t);
                    if (fruit == null) continue;
                    if (t.storage.GetNumberOfUnreservedItems(fruit) >= min) n++;
                }
                return n;
            }
            catch { return 0; }
        }

        // Trees that rotted unharvested in this orchard LAST year (the trend baseline).
        internal static int GetRottedLastYear(ArboristBuilding ab)
        {
            try { return ab != null && _lastYearRotted.TryGetValue(ab.GetInstanceID(), out int v) ? v : 0; }
            catch { return 0; }
        }

        // ----- helpers -----

        private static int MinFruit() => Mathf.Max(1, Config.HarvestWatchMinFruit.Value);

        private static int WipeDay(FruitTreeResource tree)
        {
            try
            {
                if (_removeFruitDayField == null)
                    _removeFruitDayField = AccessTools.Field(typeof(FruitTreeResource), "removeFruitOnThisDay");
                if (_removeFruitDayField != null) return (int)_removeFruitDayField.GetValue(tree);
            }
            catch { }
            return DefaultWipeDay;
        }

        // The fruit Item, resolved once via the tree's workBucketManager (reflection — accessor isn't public).
        private static Item? FruitItem(FruitTreeResource tree)
        {
            if (_itemFruit != null) return _itemFruit;
            try
            {
                var wbm = Traverse.Create(tree).Property("workBucketManager").GetValue()
                          ?? Traverse.Create(tree).Field("workBucketManager").GetValue();
                if (wbm != null)
                    _itemFruit = Traverse.Create(wbm).Field("itemFruit").GetValue<Item>()
                                 ?? Traverse.Create(wbm).Property("itemFruit").GetValue<Item>();
            }
            catch { }
            return _itemFruit;
        }

        private static string Label(ArboristBuilding ab)
        {
            try
            {
                var p = ((Component)(object)ab).transform.position;
                return $"Orchard ({p.x:0}, {p.z:0})";
            }
            catch { return "Orchard"; }
        }
    }
}
