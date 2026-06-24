// Folded from:
//   NoMoreSlackingOff by Cuteling (v0.0.2, abandoned) — primary mechanism: villager-level
//     idle detection + LaborerResourceCollectionSearchEntry registration
//   FFAutomation by idontcare (v1.0.0, active) — conceptual subset (Winter Labor /
//     IdleFarmers — narrow case of the same idea, building-centric mechanism not used here)
//
// EP fold curation: _research/folds/nmso.md
// Original prefs from NMSO: ~36 per-occupation bool toggles + implicit master
// Original prefs from FFAutomation: IdleFarmers (bool) + WinterLaborDaysEarlySpring (int)
// EP changes:
//   - Single master toggle (`EnableEfficientLabor`) + a comma-separated occupation
//     allow-list (`EfficientLaborOccupations`) instead of 36 individual bools.
//     Allow-list is cfg-file-editable but NOT surfaced in KC panel — power users
//     fine-tune via the cfg; casual players use the broad default (~37 occupations).
//   - Mechanism is NMSO's exact pattern: daily poll, find villagers in allowed
//     occupations with idle task (WorkTaskType == 63), register them as low-priority
//     (-50) laborer search entries, daily CleanSlackers to remove villagers who
//     picked up real work.
//   - FFAutomation's seasonal building-disable (zero-out crop field workers, disable
//     arborist buildings) NOT mirrored — NMSO's villager-level redirect achieves the
//     same player intent without the seasonal day-counting complexity. Farmers in
//     winter are simply idle villagers; NMSO catches them naturally.
//   - DROPPED FFAutomation's `WinterLaborDaysEarlySpring` buffer (NMSO's redirect-when-
//     truly-idle handles the spring transition automatically — villagers leave
//     laborer mode the instant their actual field has planting work).

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// When a villager in an allowed occupation has nothing to do (their assigned
    /// task is WorkTaskType.Idle, integer 63), register them as a low-priority
    /// laborer in the default task manager. They pick up hauling/gathering work
    /// until their actual job has something to do, then they switch back.
    ///
    /// Implementation mirrors NMSO's `LaborerResourceCollectionSearchEntry` pattern
    /// directly. Daily polling cadence (day-of-year change), not per-frame.
    /// </summary>
    internal static class EfficientLabor
    {
        private const int IdleTaskTypeInt    = 63; // WorkTaskType.Idle (NMSO uses the int comparison too)
        private const int LaborerTaskTypeInt = 1;  // WorkTaskType.Laborer-equivalent (used in CleanSlackers state check)
        private const int LaborerPriority    = -50; // lower than default — villager only laborer when truly nothing-to-do

        private static HashSet<int> _allowedOccupationInts = new HashSet<int>();
        private static string _lastParsedOccupationsRaw = "";

        private static readonly List<Villager> _slackers = new List<Villager>();
        private static readonly List<LaborerResourceCollectionSearchEntry> _entries = new List<LaborerResourceCollectionSearchEntry>();

        private static int _lastCheckedAbsDay = -1;
        private static bool _wasEnabled;

        public static void Reset()
        {
            ClearAllSlackers(); // best-effort — managers may already be gone on scene change
            _slackers.Clear();
            _entries.Clear();
            _lastCheckedAbsDay = -1;
            _wasEnabled = false;
            _allowedOccupationInts.Clear();
            _lastParsedOccupationsRaw = "";
        }

        public static void OnUpdate()
        {
            if (Plugin.IsForeignModLoaded("NoMoreSlackingOff", "FFAutomation")) return;

            bool enabled = Config.EnableEfficientLabor.Value;

            // Handle master-off transition: tear down all our slackers cleanly.
            if (!enabled && _wasEnabled)
            {
                ClearAllSlackers();
                _slackers.Clear();
                _entries.Clear();
            }
            _wasEnabled = enabled;
            if (!enabled) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var rm = gm.resourceManager;
            if (rm == null) return;
            var tm = gm.timeManager;
            if (tm == null) return;

            // Re-parse the occupation allow-list whenever the cfg value changes.
            // Cheap — only does work when the raw string actually differs.
            var raw = Config.EfficientLaborOccupations.Value;
            if (raw != _lastParsedOccupationsRaw)
            {
                _allowedOccupationInts = ParseOccupationList(raw);
                _lastParsedOccupationsRaw = raw;
                Plugin.Log.Msg($"[EfficientLabor] Allowed occupations: {_allowedOccupationInts.Count} parsed from cfg.");
            }
            if (_allowedOccupationInts.Count == 0) return;

            // Poll every N in-game days (Config.EfficientLaborPollDays, default 5).
            // Idle status barely shifts day-to-day, so scanning every single day is
            // wasteful. Absolute day = year*360 + dayOfYear is monotonic and wrap-free
            // (raw dayOfYear resets each year). Runs on the first tick after enabling /
            // loading, then every interval days.
            var date = tm.currentDate;
            int absDay = date.year * 360 + date.dayOfYear;
            int interval = Math.Max(1, Config.EfficientLaborPollDays.Value);
            if (_lastCheckedAbsDay >= 0 && absDay - _lastCheckedAbsDay < interval) return;
            _lastCheckedAbsDay = absDay;

            try
            {
                var addedByOcc = new Dictionary<int, int>();
                int added   = ScanForNewSlackers(gm, rm, addedByOcc);
                int removed = CleanSlackers(gm);

                if (added > 0 || removed > 0)
                {
                    Plugin.Log.Msg(
                        $"[EfficientLabor] Day {date.dayOfYear}: +{added} idle villager(s) → laborer pool" +
                        (added > 0 ? $" ({FormatOccupationBreakdown(addedByOcc)})" : "") +
                        $", -{removed} returned to work, {_slackers.Count} now active.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[EfficientLabor] {ex.Message}");
            }
        }

        // ----- Allowed-occupation parsing -----

        // Cached at first use; the Occupation enum lives in a game namespace we
        // don't want to hard-bind to. Reflection lookup is one-time and durable
        // across game updates that might shuffle namespaces.
        private static Type? _occupationType;

        private static Type? GetOccupationType()
        {
            if (_occupationType != null) return _occupationType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                _occupationType = types.FirstOrDefault(t => t.IsEnum && t.Name == "Occupation");
                if (_occupationType != null) return _occupationType;
            }
            return null;
        }

        private static HashSet<int> ParseOccupationList(string raw)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            var occType = GetOccupationType();
            if (occType == null)
            {
                Plugin.Log.Warning("[EfficientLabor] Could not locate FF's Occupation enum — feature disabled.");
                return result;
            }
            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                try
                {
                    var value = Enum.Parse(occType, trimmed, ignoreCase: true);
                    result.Add((int)value);
                }
                catch
                {
                    Plugin.Log.Warning($"[EfficientLabor] Unknown occupation in cfg: '{trimmed}' (ignored)");
                }
            }
            return result;
        }

        // ----- Slacker management (mirrors NMSO) -----

        private static int ScanForNewSlackers(GameManager gm, ResourceManager rm, Dictionary<int, int> addedByOccupation)
        {
            var villagers = rm.villagersRO;
            if (villagers == null) return 0;

            bool verbose = Config.EfficientLaborVerbose.Value;
            int added = 0;

            for (int i = 0; i < villagers.Count; i++)
            {
                var v = villagers[i];
                if (v == null) continue;
                if (_slackers.Contains(v)) continue;

                int occInt = (int)v.GetOccupation();
                if (!_allowedOccupationInts.Contains(occInt)) continue;

                var taskReceiver = ((Component)v).GetComponent<TaskReceiverComponent>();
                if (taskReceiver == null) continue;
                var currentTask = taskReceiver.currentTask;
                if (currentTask == null) continue;
                if ((int)currentTask.type != IdleTaskTypeInt) continue;

                // Villager qualifies: register them as a low-priority laborer.
                var entry = new LaborerResourceCollectionSearchEntry(
                    (ITaskReceiver)(object)v,
                    gm.searchDefManager.GetLaborerSearchDefs(),
                    LaborerPriority,
                    (WorkTaskIdentifier)0);
                gm.defaultTaskManager.AddTaskSearchEntry((ITaskReceiver)(object)v, (TaskSearchEntry)(object)entry);

                _slackers.Add(v);
                _entries.Add(entry);
                added++;
                addedByOccupation.TryGetValue(occInt, out int n);
                addedByOccupation[occInt] = n + 1;

                if (verbose)
                    Plugin.Log.Msg($"[EfficientLabor]   + {GetOccupationName(occInt)} villager → laborer pool");
            }
            return added;
        }

        private static string FormatOccupationBreakdown(Dictionary<int, int> byOcc)
        {
            var parts = new List<string>(byOcc.Count);
            foreach (var kv in byOcc)
                parts.Add(kv.Value > 1 ? $"{GetOccupationName(kv.Key)}×{kv.Value}" : GetOccupationName(kv.Key));
            return string.Join(", ", parts.ToArray());
        }

        private static string GetOccupationName(int occInt)
        {
            var t = GetOccupationType();
            if (t == null) return occInt.ToString();
            try { return Enum.GetName(t, occInt) ?? occInt.ToString(); }
            catch { return occInt.ToString(); }
        }

        /// <summary>
        /// Daily pass to remove villagers whose state has changed away from "idle"
        /// — they've got actual work again, so we lift the laborer registration.
        /// Mirrors NMSO's predicate exactly: a slacker is removed when their
        /// current task type is non-laborer AND non-idle (i.e., they picked up
        /// real work).
        /// </summary>
        private static int CleanSlackers(GameManager gm)
        {
            bool verbose = Config.EfficientLaborVerbose.Value;
            int removed = 0;
            for (int i = 0; i < _slackers.Count; )
            {
                bool shouldRemove = false;
                try
                {
                    var v = _slackers[i];
                    if (v == null) { shouldRemove = true; }
                    else
                    {
                        var tr = ((Component)v).GetComponent<TaskReceiverComponent>();
                        var task = tr?.currentTask;
                        if (task == null) { /* idle-with-no-task — keep them registered */ }
                        else
                        {
                            int typeInt = (int)task.type;
                            int stateInt = (int)v.GetState();
                            // NMSO's predicate: remove if doing real work (not idle, not laborer).
                            // Or, edge case: in state 8 or 4 with a laborer task, also remove.
                            bool inWorkingState = stateInt != 8 && stateInt != 4 && typeInt == LaborerTaskTypeInt;
                            bool doingOtherWork = typeInt != IdleTaskTypeInt && typeInt != LaborerTaskTypeInt;
                            if (inWorkingState || doingOtherWork) shouldRemove = true;
                        }
                    }
                }
                catch
                {
                    shouldRemove = true; // villager destroyed mid-frame, or other transient — drop
                }

                if (shouldRemove)
                {
                    if (verbose && _slackers[i] != null)
                        Plugin.Log.Msg($"[EfficientLabor]   - {GetOccupationName((int)_slackers[i].GetOccupation())} villager → back to work");

                    try
                    {
                        if (_slackers[i] != null)
                        {
                            gm.defaultTaskManager.RemoveTaskSearchEntry(
                                (ITaskReceiver)(object)_slackers[i],
                                (TaskSearchEntry)(object)_entries[i]);
                        }
                    }
                    catch { /* manager teardown race — ignore */ }

                    _slackers.RemoveAt(i);
                    _entries.RemoveAt(i);
                    removed++;
                    continue;
                }
                i++;
            }
            return removed;
        }

        /// <summary>
        /// Tear down every active slacker registration. Called on master-toggle
        /// going from ON to OFF, and on Reset (scene change).
        /// </summary>
        private static void ClearAllSlackers()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var taskMgr = gm.defaultTaskManager;
            if (taskMgr == null) return;
            for (int i = 0; i < _slackers.Count; i++)
            {
                try
                {
                    if (_slackers[i] != null && _entries[i] != null)
                    {
                        taskMgr.RemoveTaskSearchEntry(
                            (ITaskReceiver)(object)_slackers[i],
                            (TaskSearchEntry)(object)_entries[i]);
                    }
                }
                catch { /* tolerate teardown */ }
            }
        }
    }
}
