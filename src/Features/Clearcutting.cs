// Folded from FFAutomation by idontcare (v1.0.0, active)
// Original DLL: FFAutomation_FF.dll
// EP fold curation: _research/folds/ffautomation.md
// Original prefs: EnableAutoHarvest + AutoHarvestTrees/Stones/Forageables + AutoHarvestRadius
// EP changes:
//   - Renamed section to "Clearcutting", pref keys renamed accordingly.
//   - **Event-driven scans, NOT continuous polling.** Original FFAutomation polls
//     every 5 seconds and continuously re-evaluates which resources should be
//     flagged. That caused two problems: (1) any decoration tree the player
//     planted got flagged within 5s, requiring repeated unflag actions, and
//     (2) transient danger events (raiders/wolves passing through) caused
//     unflag→reflag pulses on existing flagged resources.
//
//     EP triggers a scan on exactly three events:
//       1. The master toggle goes OFF → ON (first enable in session)
//       2. A new Town Center finishes Start() after the game is ready
//          (`gameReadyToPlay` gate skips save-load triggers)
//       3. The "Re-Scan Now" button in the KC panel is pressed
//
//   - **No re-flag pulse.** Once a resource is marked we never touch it again.
//     Manual unflag by the player is permanent for the session.
//   - Danger-zone radii (raider 120, wolf/bear 80) still gate the INITIAL flag
//     decision — we don't auto-flag a tree currently next to a wolf.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class Clearcutting
    {
        // Hardcoded danger-zone radii (squared) — mirror FFAutomation's values.
        // 120 units for raider camps + raiders, 80 units for wolves + bears.
        private const float RaiderDangerSqrd   = 14400f; // 120^2
        private const float WildlifeDangerSqrd = 6400f;  // 80^2

        // "Resources we have flagged this session." Once a resource is in here
        // we leave it alone — manual unflag survives.
        private static readonly HashSet<int> _markedTrees       = new HashSet<int>();
        private static readonly HashSet<int> _markedStones      = new HashSet<int>();
        private static readonly HashSet<int> _markedForageables = new HashSet<int>();

        // Per-scan scratch lists for danger positions, cleared and rebuilt each scan.
        private static readonly List<Vector2> _raiderPositions   = new List<Vector2>();
        private static readonly List<Vector2> _wildlifePositions = new List<Vector2>();

        // Event-driven state.
        private static bool _pendingScan;
        private static bool _wasEnabled;

        public static void Reset()
        {
            _markedTrees.Clear();
            _markedStones.Clear();
            _markedForageables.Clear();
            _raiderPositions.Clear();
            _wildlifePositions.Clear();
            _pendingScan = false;
            _wasEnabled  = false;
        }

        /// <summary>
        /// Called from TownCenter.Start postfix when a new TC has been placed
        /// AFTER the game is ready (so save-loads don't trigger us). Schedules
        /// a one-shot scan on the next OnUpdate tick.
        /// </summary>
        internal static void OnTownCenterPlaced()
        {
            _pendingScan = true;
        }

        public static void OnUpdate()
        {
            if (Plugin.IsForeignModLoaded("FFAutomation")) return;

            bool enabled = Config.EnableClearcutting.Value;

            // Detect master-toggle state changes.
            if (enabled && !_wasEnabled)
            {
                _pendingScan = true;
            }
            else if (!enabled && _wasEnabled)
            {
                // Master just turned off — unflag everything we'd flagged.
                var gmOff = UnitySingleton<GameManager>.Instance;
                var rmOff = gmOff?.resourceManager;
                if (rmOff != null)
                {
                    UnflagAll(rmOff.treeResourceInstancesRO,       _markedTrees);
                    UnflagAll(rmOff.stoneResourceInstancesRO,      _markedStones);
                    UnflagAll(rmOff.forageableResourceInstancesRO, _markedForageables);
                }
                _markedTrees.Clear();
                _markedStones.Clear();
                _markedForageables.Clear();
                _pendingScan = false;
            }
            _wasEnabled = enabled;

            if (!enabled) return;

            // "Re-Scan Now" button — bool that auto-resets, acts like a button.
            if (Config.ClearcuttingRescan.Value)
            {
                _pendingScan = true;
                Config.ClearcuttingRescan.Value = false;
                Plugin.Log.Msg("[Clearcutting] Manual re-scan requested.");
            }

            if (!_pendingScan) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var rm = gm.resourceManager;
            if (rm == null) return;

            try
            {
                RunScan(gm, rm);
                _pendingScan = false;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[Clearcutting] {ex.Message}");
                _pendingScan = false; // don't loop on the same error every frame
            }
        }

        // ----- Scan -----

        private static void RunScan(GameManager gm, ResourceManager rm)
        {
            if (!TryGetTownCenterPosition(rm, out Vector2 tc)) return;

            float radius = Config.ClearcuttingRadius.Value;
            float radiusSqrd = radius * radius;

            BuildDangerPositions(gm, rm);

            int flagged = 0;
            if (Config.ClearcuttingTrees.Value)
                flagged += ScanResources(rm.treeResourceInstancesRO, _markedTrees, tc, radiusSqrd);

            if (Config.ClearcuttingStones.Value)
                flagged += ScanResources(rm.stoneResourceInstancesRO, _markedStones, tc, radiusSqrd);

            if (Config.ClearcuttingForageables.Value)
                flagged += ScanResources(rm.forageableResourceInstancesRO, _markedForageables, tc, radiusSqrd);

            Plugin.Log.Msg($"[Clearcutting] Scan complete — {flagged} new resource(s) flagged for harvest.");
        }

        private static int ScanResources<T>(
            ReadOnlyCollection<T> resources, HashSet<int> marked,
            Vector2 tc, float radiusSqrd) where T : Resource
        {
            if (resources == null) return 0;
            int newFlags = 0;
            for (int i = 0; i < resources.Count; i++)
            {
                var r = resources[i];
                if (r == null) continue;
                int id = ((UnityEngine.Object)(object)r).GetInstanceID();
                if (marked.Contains(id)) continue;

                Vector3 pos = ((Component)(object)r).transform.position;
                if (!IsInScope(pos.x, pos.z, tc, radiusSqrd)) continue;

                ((Resource)(object)r).SetFlagged(true);
                marked.Add(id);
                newFlags++;
            }
            return newFlags;
        }

        private static bool IsInScope(float x, float z, Vector2 tc, float radiusSqrd)
        {
            float dx = x - tc.x;
            float dz = z - tc.y;
            if (dx * dx + dz * dz > radiusSqrd) return false;

            for (int i = 0; i < _raiderPositions.Count; i++)
            {
                var p = _raiderPositions[i];
                float rx = x - p.x;
                float rz = z - p.y;
                if (rx * rx + rz * rz <= RaiderDangerSqrd) return false;
            }
            for (int i = 0; i < _wildlifePositions.Count; i++)
            {
                var p = _wildlifePositions[i];
                float wx = x - p.x;
                float wz = z - p.y;
                if (wx * wx + wz * wz <= WildlifeDangerSqrd) return false;
            }
            return true;
        }

        // ----- TC + danger position lookups -----

        private static bool TryGetTownCenterPosition(ResourceManager rm, out Vector2 tc)
        {
            tc = default;
            var centers = rm.townCentersRO;
            if (centers != null && centers.Count > 0)
            {
                var center = centers[0];
                if (center != null)
                {
                    var p = ((Component)center).transform.position;
                    tc = new Vector2(p.x, p.z);
                    return true;
                }
            }

            // Fallback: TC under construction — find the BuildSite with a
            // TownCenter prefab. Handles the moments right after TC placement
            // before the TC GameObject is fully spawned.
            if (rm.buildSiteByType != null
                && rm.buildSiteByType.TryGetValue(typeof(BuildingBuildSite), out var sites))
            {
                for (int i = 0; i < sites.Count; i++)
                {
                    var site = sites[i];
                    if (site == null) continue;
                    var prefab = site.constructionData.prefabToConstruct;
                    if (prefab == null) continue;
                    if (prefab.GetComponent<TownCenter>() == null) continue;
                    var p = ((Component)site).transform.position;
                    tc = new Vector2(p.x, p.z);
                    return true;
                }
            }
            return false;
        }

        private static void BuildDangerPositions(GameManager gm, ResourceManager rm)
        {
            _raiderPositions.Clear();
            _wildlifePositions.Clear();

            var camps = rm.raiderCampsRO;
            if (camps != null)
            {
                for (int i = 0; i < camps.Count; i++)
                {
                    var c = camps[i];
                    if (c == null) continue;
                    var p = ((Component)c).transform.position;
                    _raiderPositions.Add(new Vector2(p.x, p.z));
                }
            }

            var cm = gm.combatManager;
            if (cm != null)
            {
                var raiders = cm.raidersRO;
                if (raiders != null)
                {
                    for (int i = 0; i < raiders.Count; i++)
                    {
                        var r = raiders[i];
                        if (r == null) continue;
                        var p = ((Component)r).transform.position;
                        _raiderPositions.Add(new Vector2(p.x, p.z));
                    }
                }
            }

            var am = gm.animalManager;
            if (am != null)
            {
                var wolves = am.wolvesRO;
                if (wolves != null)
                {
                    for (int i = 0; i < wolves.Count; i++)
                    {
                        var w = wolves[i];
                        if (w == null) continue;
                        var p = ((Component)w).transform.position;
                        _wildlifePositions.Add(new Vector2(p.x, p.z));
                    }
                }
                var bears = am.bearsRO;
                if (bears != null)
                {
                    for (int i = 0; i < bears.Count; i++)
                    {
                        var b = bears[i];
                        if (b == null) continue;
                        var p = ((Component)b).transform.position;
                        _wildlifePositions.Add(new Vector2(p.x, p.z));
                    }
                }
            }
        }

        // ----- Master-off cleanup -----

        private static void UnflagAll<T>(ReadOnlyCollection<T> resources, HashSet<int> marked) where T : Resource
        {
            if (resources == null) { marked.Clear(); return; }
            for (int i = 0; i < resources.Count; i++)
            {
                var r = resources[i];
                if (r == null) continue;
                int id = ((UnityEngine.Object)(object)r).GetInstanceID();
                if (marked.Contains(id))
                {
                    ((Resource)(object)r).SetFlagged(false);
                }
            }
        }

        // ----- Town Center placement trigger -----

        /// <summary>
        /// Postfix on TownCenter.Start. When a new TC is placed mid-game,
        /// schedule a one-shot scan. Skipped during save-load (when Start
        /// fires on pre-existing TCs) by the gameReadyToPlay gate — at load
        /// time the flag is still false; it flips true only after the player
        /// has full control.
        /// </summary>
        [HarmonyPatch(typeof(TownCenter), "Start")]
        internal static class TownCenterStartPatch
        {
            private static void Postfix()
            {
                try
                {
                    if (!Config.EnableClearcutting.Value) return;
                    if (!GameManager.gameReadyToPlay) return;
                    if (Plugin.IsForeignModLoaded("FFAutomation")) return;
                    OnTownCenterPlaced();
                    Plugin.Log.Msg("[Clearcutting] New Town Center placed — scan scheduled.");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[Clearcutting] TC.Start: {ex.Message}");
                }
            }
        }
    }
}
