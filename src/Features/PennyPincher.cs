// Folded from FFAutomation by idontcare (v1.0.0, active)
// Original DLL: FFAutomation_FF.dll
// EP fold curation: _research/folds/ffautomation.md
// Original prefs: IdleRatCatchers (bool) + IdleGuardTowers (bool) — MERGED into one
// EP changes: Single master toggle `EnablePennyPincher` controls both behaviours.
//             FFAutomation's per-occupation granularity dropped — same conceptual
//             unit ("don't pay gold for workers with nothing to do"), so one switch.
//             Polling cadence (5s), threat detection, 2-day guard cooldown, and
//             selected-building skip all mirror FFAutomation exactly.
//             EP addition (not in FFAutomation): per-tower exemptions — Ctrl+<key>
//             on a selected tower marks it hands-off, persisted per save.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Auto-stands-down rat catchers when no rodent infestations are active,
    /// and guard towers in peacetime (with a 2-day cooldown after the last
    /// raid ends so towers don't flap on/off as threats are detected).
    ///
    /// Both behaviours fire under one toggle. Mirrors FFAutomation's
    /// `IdleWorkerFeature.UpdateRatCatchers` + `UpdateGuardTowers` exactly,
    /// minus the per-occupation toggles (we surface them as one switch in EP).
    /// </summary>
    internal static class PennyPincher
    {
        private const float PollIntervalSeconds = 5f;
        private const int   GuardCooldownDays   = 2;

        private static float _nextPoll = float.MinValue;
        private static bool  _lastThreatActive;
        private static int?  _raidEndedTotalDay;
        private static bool  _wasManagingGuardTowers;
        // Instance IDs of the guard towers EP itself stood down, so a sub-toggle-off re-mans
        // exactly those and never force-enables a tower the player disabled by hand.
        private static readonly HashSet<int> _stoodDownTowers = new HashSet<int>();

        // Player-exempted towers: Penny Pincher never touches these (no peacetime stand-down,
        // no threat re-man). Keyed by rounded world position — buildings never move, and FF
        // instance IDs are re-mapped every load, so position is the stable per-save identity
        // (same reasoning as WorkplaceMastery's composite-key sidecar). Persisted per TOWN in
        // UserData/EP_PennyPincher/<town folder>.txt, one key per line (Common/SaveIdentity.cs).
        private static readonly HashSet<string> _exemptTowers = new HashSet<string>();
        private static bool    _exemptHydrated;
        // Town whose exemption file has been read; "" until the town has a save folder.
        private static string  _exemptBoundKey = "";
        // A tower was toggled before the town's file could be read; write once it is.
        private static bool    _exemptDirty;
        private static bool    _exemptBindErrorLogged;
        private static string  _exemptKeyRaw = "";
        private static KeyCode _exemptKey    = KeyCode.P;

        public static void Reset()
        {
            _nextPoll               = float.MinValue;
            _lastThreatActive       = false;
            _raidEndedTotalDay      = null;
            _wasManagingGuardTowers = false;
            _stoodDownTowers.Clear();
            _exemptTowers.Clear();
            _exemptHydrated = false;
            _exemptBoundKey = "";
            _exemptDirty = false;
            _exemptBindErrorLogged = false;
        }

        public static void OnUpdate()
        {
            if (!Config.EnablePennyPincher.Value) return;
            if (Plugin.IsForeignModLoaded("FFAutomation")) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;

            try
            {
                if (!_exemptHydrated) HydrateExemptions();
                else if (_exemptBoundKey.Length == 0) TryBindExemptions();   // a few string reads until bound
                HandleExemptHotkey(gm);   // every frame — GetKeyDown is frame-scoped

                float time = Time.time;
                if (time < _nextPoll) return;
                _nextPoll = time + PollIntervalSeconds;

                UpdateRatCatchers(gm);

                bool manageGuardTowers = Config.PennyPincherGuardTowers.Value;
                if (manageGuardTowers)
                {
                    UpdateGuardTowers(gm);
                }
                else if (_wasManagingGuardTowers)
                {
                    // Sub-toggle just turned off — re-man any towers we'd stood down so
                    // they aren't left stranded unmanned, then hand control back to the player.
                    ReleaseGuardTowers(gm);
                    _lastThreatActive  = false;
                    _raidEndedTotalDay = null;
                }
                _wasManagingGuardTowers = manageGuardTowers;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PennyPincher] {ex.Message}");
            }
        }

        // ----- Rat catchers -----

        private static void UpdateRatCatchers(GameManager gm)
        {
            var rm = gm.resourceManager;
            if (rm == null) return;
            var catchers = rm.ratCatcherBuildingsRO;
            if (catchers == null) return;

            bool threatActive = HasAnyInfestation(rm);
            for (int i = 0; i < catchers.Count; i++)
            {
                var rc = catchers[i];
                if (rc == null) continue;
                var building = (Building)(object)rc;
                ToggleBuilding(gm, building, threatActive, cooldownElapsed: true);
            }
        }

        private static bool HasAnyInfestation(ResourceManager rm)
        {
            var all = rm.allBuildingsRO;
            if (all == null) return false;
            for (int i = 0; i < all.Count; i++)
            {
                var b = all[i];
                if (b == null) continue;
                var def = b.rodentPopulationDefinition;
                if (def == null) continue;
                if (def.maxRodentPopulation <= 0) continue;
                if (b.rodentPopulation >= def.minRodentPopulationToTriggerCatcher) return true;
            }
            return false;
        }

        // ----- Guard towers -----

        private static void UpdateGuardTowers(GameManager gm)
        {
            var rm = gm.resourceManager;
            if (rm == null) return;
            var towers = rm.guardTowersRO;
            if (towers == null) return;

            bool hasIncursion  = HasNonCampRaidTracker(gm);
            bool villageAware  = gm.combatManager?.villageAttackAwareness?.isVillageAttackAware ?? false;
            bool campAttacking = HasCampRaiderAttacking(gm);
            bool threatActive  = hasIncursion || villageAware || campAttacking;

            // Mark threat-end so we can run the 2-day cooldown before standing down.
            if (_lastThreatActive && !threatActive)
            {
                _raidEndedTotalDay = TotalGameDay(gm);
            }
            _lastThreatActive = threatActive;

            bool cooldownElapsed =
                !_raidEndedTotalDay.HasValue ||
                TotalGameDay(gm) - _raidEndedTotalDay.Value >= GuardCooldownDays;

            for (int i = 0; i < towers.Count; i++)
            {
                var tower = towers[i];
                if (tower == null) continue;
                var building = (Building)(object)tower;
                if (_exemptTowers.Contains(PosKey(building))) continue; // player said hands off
                bool wasEnabled = building.isWorkEnabled;
                ToggleBuilding(gm, building, threatActive, cooldownElapsed);
                // Record only the towers WE just stood down (was on → now off). EP never touches an
                // already-disabled tower, so this set holds exactly EP's stand-downs.
                if (wasEnabled && !building.isWorkEnabled) _stoodDownTowers.Add(building.GetInstanceID());
                else if (!wasEnabled && building.isWorkEnabled) _stoodDownTowers.Remove(building.GetInstanceID());
            }
        }

        /// <summary>
        /// Re-man only the guard towers EP itself stood down. Called once when the
        /// "Stand Down Guard Towers" sub-toggle is switched off, so the player isn't
        /// left with peacetime-disabled towers they now have to re-enable by hand —
        /// without force-enabling any tower the player chose to disable manually.
        /// </summary>
        private static void ReleaseGuardTowers(GameManager gm)
        {
            var towers = gm.resourceManager?.guardTowersRO;
            if (towers == null) { _stoodDownTowers.Clear(); return; }
            for (int i = 0; i < towers.Count; i++)
            {
                var tower = towers[i];
                if (tower == null) continue;
                var building = (Building)(object)tower;
                if (_stoodDownTowers.Contains(building.GetInstanceID()) && !building.isWorkEnabled)
                    building.SetWorkEnabled(true, true);
            }
            _stoodDownTowers.Clear();
        }

        // ----- Per-tower exemptions (Ctrl+<key> on a selected tower) -----

        /// <summary>
        /// Ctrl + configured key (default P) while a guard tower is selected toggles
        /// that tower's exemption. Silent no-op when no guard tower is selected, so
        /// the chord can coexist with other mods' bindings without noise.
        /// </summary>
        private static void HandleExemptHotkey(GameManager gm)
        {
            ResolveExemptKey();
            if (_exemptKey == KeyCode.None) return;
            if (!(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))) return;
            if (!Input.GetKeyDown(_exemptKey)) return;

            var towers = gm.resourceManager?.guardTowersRO;
            if (towers == null) return;
            for (int i = 0; i < towers.Count; i++)
            {
                var tower = towers[i];
                if (tower == null) continue;
                var building = (Building)(object)tower;
                if (!IsSelected(gm, building)) continue;
                ToggleExemption(building);
                return;
            }
        }

        private static void ToggleExemption(Building building)
        {
            string key = PosKey(building);
            if (_exemptTowers.Remove(key))
            {
                Common.Toast.Show("Guard tower re-enrolled in Penny Pincher.");
                Plugin.Log.Msg($"[PennyPincher] Tower at {key} re-enrolled (auto-managed again).");
            }
            else
            {
                _exemptTowers.Add(key);
                // If EP had stood this tower down, undo that now — exempt means EP never
                // touches it, including leaving behind its own past stand-down.
                if (_stoodDownTowers.Remove(building.GetInstanceID()) && !building.isWorkEnabled)
                    building.SetWorkEnabled(true, true);
                Common.Toast.Show("Guard tower exempt from Penny Pincher — it stays under your control.");
                Plugin.Log.Msg($"[PennyPincher] Tower at {key} exempted.");
            }
            SaveExemptions();
        }

        /// <summary>Rounded world position — the stable per-save tower identity (buildings
        /// never move; instance IDs are re-mapped every load).</summary>
        private static string PosKey(Building b)
        {
            var p = b.transform.position;
            return $"{Mathf.RoundToInt(p.x)}:{Mathf.RoundToInt(p.z)}";
        }

        private static void ResolveExemptKey()
        {
            string raw = Config.PennyPincherExemptKey.Value ?? "";
            if (raw == _exemptKeyRaw) return;
            _exemptKeyRaw = raw;
            _exemptKey = Enum.TryParse(raw.Trim(), true, out KeyCode k) ? k : KeyCode.P;
        }

        // ----- Exemption persistence (plain text sidecar, one position key per line) -----

        /// <summary>Same UserData derivation as MasteryStore.UserDir, leaf folder renamed.</summary>
        private static string ExemptDir
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(Path.Combine(root, "UserData"), "EP_PennyPincher");
            }
        }

        private static string ExemptFile(string name) => Path.Combine(ExemptDir, SafeFileName(name) + ".txt");

        private static void HydrateExemptions()
        {
            _exemptHydrated = true;
            _exemptTowers.Clear();
            _exemptBoundKey = "";
            TryBindExemptions();
        }

        // Read the town's exemptions and merge them into memory. A new town has no save
        // folder until its first save, so until then toggles live in memory; OnUpdate retries
        // this until the folder appears, then anything toggled in the meantime is written.
        private static void TryBindExemptions()
        {
            string town = Common.SaveIdentity.TownKey();
            if (town.Length == 0) return;
            try
            {
                var migratedFrom = new List<string>();
                var fromDisk = new HashSet<string>();
                if (File.Exists(ExemptFile(town)))
                {
                    ReadExemptionsInto(ExemptFile(town), fromDisk);
                }
                else
                {
                    // One-time migration from the names used before the town-folder key. The shared
                    // "AutoSave N" name is skipped: a tower position from another town could
                    // match one here and silently exempt it.
                    foreach (var legacy in Common.SaveIdentity.LegacyKeys(includeSharedAutosave: false))
                    {
                        if (!File.Exists(ExemptFile(legacy))) continue;
                        ReadExemptionsInto(ExemptFile(legacy), fromDisk);
                        migratedFrom.Add(legacy);
                    }
                }

                _exemptTowers.UnionWith(fromDisk);
                _exemptBoundKey = town;
                if (fromDisk.Count > 0)
                    Plugin.Log.Msg(migratedFrom.Count > 0
                        ? $"[PennyPincher] {fromDisk.Count} exempt guard tower(s) migrated to town '{town}' from {string.Join(", ", migratedFrom.ToArray())}."
                        : $"[PennyPincher] {fromDisk.Count} exempt guard tower(s) loaded for town '{town}'.");

                if (_exemptDirty || migratedFrom.Count > 0)
                {
                    _exemptDirty = false;
                    WriteExemptions(town);
                }
            }
            catch (Exception ex)
            {
                // Retried every frame while unbound, so log only the first failure.
                if (!_exemptBindErrorLogged)
                {
                    _exemptBindErrorLogged = true;
                    Plugin.Log.Warning($"[PennyPincher] Exemption load: {ex.Message}");
                }
            }
        }

        private static void ReadExemptionsInto(string file, HashSet<string> into)
        {
            foreach (var line in File.ReadAllLines(file))
                if (!string.IsNullOrWhiteSpace(line)) into.Add(line.Trim());
        }

        private static void SaveExemptions()
        {
            // OnUpdate binds before handling the hotkey, so being unbound here means the town
            // has no folder yet or its file couldn't be read. Either way, never write blind.
            string town = Common.SaveIdentity.TownKey();
            if (town.Length == 0 || _exemptBoundKey.Length == 0)
            {
                _exemptDirty = true;
                return;
            }
            WriteExemptions(town);
        }

        private static void WriteExemptions(string town)
        {
            try
            {
                Directory.CreateDirectory(ExemptDir);
                File.WriteAllLines(ExemptFile(town), _exemptTowers);
            }
            catch (Exception ex) { Plugin.Log.Warning($"[PennyPincher] Exemption save: {ex.Message}"); }
        }

        // Verbatim from MasteryStore.SafeFileName.
        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        private static bool HasNonCampRaidTracker(GameManager gm)
        {
            var trackers = gm.combatManager?.raidIncursionTrackersRO;
            if (trackers == null) return false;
            for (int i = 0; i < trackers.Count; i++)
            {
                var t = trackers[i];
                if (t == null) continue;
                if (!t.isRaidCampIncurson) return true; // (note: FF's field name has the typo "Incurson")
            }
            return false;
        }

        private static bool HasCampRaiderAttacking(GameManager gm)
        {
            var cm = gm.combatManager;
            if (cm == null) return false;
            var raiders = cm.raidersRO;
            if (raiders == null) return false;

            float chaseRangeSq = cm.raidCampRaiderChaseRange * cm.raidCampRaiderChaseRange;
            for (int i = 0; i < raiders.Count; i++)
            {
                var r = raiders[i];
                if (r == null) continue;
                var camp = r.defendable as RaiderCamp;
                if (camp == null) continue; // only camp-defending raiders need this check
                Vector3 raiderPos = ((Component)r).transform.position;
                Vector3 campPos   = ((Component)camp).transform.position;
                float dx = raiderPos.x - campPos.x;
                float dz = raiderPos.z - campPos.z;
                if (dx * dx + dz * dz > chaseRangeSq) return true; // raider strayed from camp = attacking village
            }
            return false;
        }

        // ----- Common toggle path -----

        /// <summary>
        /// Threat present → ensure work-enabled. Threat absent and cooldown done →
        /// stand down, unless the player has the building selected (don't pull
        /// the rug while they're configuring it manually). For rat catchers
        /// `cooldownElapsed` is always passed true — only guard towers throttle.
        /// </summary>
        private static void ToggleBuilding(GameManager gm, Building building, bool threatActive, bool cooldownElapsed)
        {
            bool isEnabled = building.isWorkEnabled;
            if (threatActive && !isEnabled)
            {
                building.SetWorkEnabled(true, true);
            }
            else if (!threatActive && cooldownElapsed && isEnabled && !IsSelected(gm, building))
            {
                building.SetWorkEnabled(false, true);
            }
        }

        // ----- Helpers -----

        private static int TotalGameDay(GameManager gm)
        {
            var tm = gm.timeManager;
            if (tm == null) return 0;
            var d = tm.currentDate;
            return d.year * 360 + d.dayOfYear;   // FF year = 360 days (12 × 30) — keeps the cooldown delta monotonic across year rollover
        }

        /// <summary>
        /// "Is the player currently looking at this building?" — same selection
        /// check pattern Keep Clarity uses (reflective field on InputManager).
        /// We never stand down a selected building so the player's configuration
        /// experience isn't disrupted.
        /// </summary>
        private static FieldInfo? _selectedObjField;
        private static bool IsSelected(GameManager gm, Building b)
        {
            if (_selectedObjField == null)
            {
                _selectedObjField = typeof(InputManager).GetField("selectedObject",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            var selected = _selectedObjField?.GetValue(gm.inputManager) as GameObject;
            if (selected == null) return false;
            if (selected == b.gameObject) return true;
            return selected.transform.IsChildOf(b.transform);
        }
    }
}
