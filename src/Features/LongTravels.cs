// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableTravelTimeAlert (bool), TravelTimeAlertThreshold (int 20-80, default 60)
// EP changes: section renamed "Long Travels"; pref keys renamed EnableLongTravels /
//             LongTravelsThreshold. Behaviour and threshold logic unchanged.

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
    /// travelling instead of producing. Driven by `WorkerAlertManager` shared
    /// with Idle Hands. Icon sprite reuses FF's "ICN_Wagon01_64" — wagon
    /// imagery makes the "travel" semantic legible at a glance.
    /// </summary>
    internal static class LongTravels
    {
        private static readonly WorkerAlertManager _manager = new WorkerAlertManager(
            batchSize:       3,
            renderID:        "ep_long_travels",
            iconSpriteName:  "ICN_Wagon01_64",
            alertCloneName:  "EP_LongTravelsAlert",
            getMetric:       GetAvgTravelPct,
            getThreshold:    () => Config.LongTravelsThreshold.Value,
            isEnabled:       () => Config.EnableLongTravels.Value
                                   && !Plugin.IsForeignModLoaded("FFQoL"),
            tooltipPrefix:   "High travel time"
        );

        public static void OnUpdate() => _manager.OnUpdate();
        public static void Reset()    => _manager.ClearCache();

        /// <summary>
        /// Average `pctTimeTravelling` across all live adult workers of the
        /// building. Mirrors FFQoL exactly — children/infants/dead villagers
        /// excluded so a building with no real adult workers reports 0
        /// instead of a misleading non-zero average.
        /// </summary>
        private static float GetAvgTravelPct(Building building)
        {
            var workers = building?.workersRO;
            if (workers == null) return 0f;

            float total = 0f;
            int counted = 0;
            int n = workers.Count;
            for (int i = 0; i < n; i++)
            {
                var villager = workers[i] as Villager;
                if (villager == null) continue;
                if (villager.isDead || villager.IsChild() || villager.IsInfant()) continue;
                total += villager.pctTimeTravelling;
                counted++;
            }
            if (counted <= 0) return 0f;
            return total / counted;
        }

        [HarmonyPatch(typeof(UIBuildingSubWidgetAlerts), "Init")]
        internal static class InitPatch
        {
            private static void Postfix(UIBuildingSubWidgetAlerts __instance)
            {
                try { _manager.InitWidget(__instance); }
                catch (Exception ex) { Plugin.Log.Error($"[LongTravels] Init: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(UIBuildingSubWidgetAlerts), "Release")]
        internal static class ReleasePatch
        {
            private static void Prefix(UIBuildingSubWidgetAlerts __instance)
            {
                try { _manager.ReleaseWidget(__instance); }
                catch (Exception ex) { Plugin.Log.Error($"[LongTravels] Release: {ex}"); }
            }
        }
    }
}
