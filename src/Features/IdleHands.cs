// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableIdleTimeAlert (bool), IdleTimeAlertThreshold (int 20-80, default 60)
// EP changes: section renamed "Idle Hands"; pref keys renamed EnableIdleHands /
//             IdleHandsThreshold. Behaviour and threshold logic unchanged.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using EssentialProvisions.Common;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Per-production-building alert: workers spend &gt;= threshold% of their time
    /// idle (state group 0 in FF's worker-state telemetry). Driven by
    /// `WorkerAlertManager` shared with Long Travels. Icon sprite reuses FF's
    /// "ICN_Villagers01_64" — villager-cluster imagery for the "standing
    /// around" semantic.
    /// </summary>
    internal static class IdleHands
    {
        private static readonly WorkerAlertManager _manager = new WorkerAlertManager(
            batchSize:       3,
            renderID:        "ep_idle_hands",
            iconSpriteName:  "ICN_Villagers01_64",
            alertCloneName:  "EP_IdleHandsAlert",
            getMetric:       GetAvgIdlePct,
            getThreshold:    () => Config.IdleHandsThreshold.Value,
            isEnabled:       () => Config.EnableIdleHands.Value
                                   && !Plugin.IsForeignModLoaded("FFQoL"),
            tooltipPrefix:   "High idle time"
        );

        public static void OnUpdate() => _manager.OnUpdate();
        public static void Reset()    => _manager.ClearCache();

        /// <summary>
        /// FFQoL reads idle pct from `Building.GetWorkerAverageStateCategoryTimeData`.
        /// State group 0 == idle in FF's enum. Mirror exactly so behaviour matches.
        /// </summary>
        private static float GetAvgIdlePct(Building building)
        {
            if (building == null) return 0f;
            var stateData = building.GetWorkerAverageStateCategoryTimeData(out _);
            if (stateData == null) return 0f;
            for (int i = 0; i < stateData.Count; i++)
            {
                if ((int)stateData[i].stateGroup == 0)
                    return stateData[i].stateTime;
            }
            return 0f;
        }

        [HarmonyPatch(typeof(UIBuildingSubWidgetAlerts), "Init")]
        internal static class InitPatch
        {
            private static void Postfix(UIBuildingSubWidgetAlerts __instance)
            {
                try { _manager.InitWidget(__instance); }
                catch (Exception ex) { Plugin.Log.Error($"[IdleHands] Init: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(UIBuildingSubWidgetAlerts), "Release")]
        internal static class ReleasePatch
        {
            private static void Prefix(UIBuildingSubWidgetAlerts __instance)
            {
                try { _manager.ReleaseWidget(__instance); }
                catch (Exception ex) { Plugin.Log.Error($"[IdleHands] Release: {ex}"); }
            }
        }
    }
}
