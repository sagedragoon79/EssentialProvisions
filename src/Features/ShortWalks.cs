// Inspired by Autocommute by Cleve (v1.6.0). Different mechanism:
//   - Autocommute reassigns JOBS via Munkres assignment (Accord.Math).
//   - Short Walks re-assigns HOMES via FF's own built-in Hungarian
//     (VillagerHomeManager.MaintainHomeAssignmentWithWorkDist).
//
// Why home re-assignment, not job re-assignment:
//   - FF already has Hungarian home-assignment code (Burst-compiled), but
//     only runs it for villagers with NULL workResidence — newly arrived
//     immigrants etc.
//   - Existing villagers whose workplace situation has shifted (got a new
//     job, original workplace got demolished, town layout changed around
//     them) keep their old shelter forever. They're the source of Long
//     Travels alerts.
//   - We clear workResidence on selected villagers monthly; vanilla picks
//     them up within ~6 game seconds and re-Hungarians the lot.
//   - Re-housing preserves specialization (veteran sawyer stays a sawyer,
//     just lives somewhere new) and uses zero homegrown algorithm code.
//
// Exclusions:
//   - Soldiers, Guards, TransitionToSoldier — garrison linkage matters,
//     moving them could trigger desertion or status loss
//   - "Lives at workplace" heuristic — workResidence == placeOfWork → skip
//     (catches healer-in-healers-house, priest-in-church, etc. without
//     enumerating every special occupation)
//   - Infants (vanilla also excludes these)
//
// Note: VillagerHomeManager.timeBetweenHomeChecks = 6f, so vanilla naturally
// picks up our cleared villagers without us forcing its timer.

using System;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class ShortWalks
    {
        // Occupation ordinals (top-level Occupation enum, same one used elsewhere).
        private const int OccupationGuard               = 9;
        private const int OccupationTransitionToSoldier = 13;
        private const int OccupationSoldier             = 45;

        private static bool _subscribed;
        private static bool _wasEnabled;

        public static void Reset()
        {
            UnsubscribeIfSubscribed();
            _wasEnabled = false;
        }

        public static void OnUpdate()
        {
            bool enabled = Config.EnableShortWalks.Value;
            if (!enabled && _wasEnabled) UnsubscribeIfSubscribed();
            _wasEnabled = enabled;
            if (!enabled) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var em = gm?.eventManager;
            if (em == null) return;

            if (!_subscribed)
            {
                em.AddListener<MonthPassedEvent>(OnMonthPassed);
                _subscribed = true;
                Plugin.Log.Msg("[ShortWalks] Active. Re-housing optimization will run on each month-tick.");
            }
        }

        private static void UnsubscribeIfSubscribed()
        {
            if (!_subscribed) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var em = gm?.eventManager;
            em?.RemoveListener<MonthPassedEvent>(OnMonthPassed);
            _subscribed = false;
        }

        private static void OnMonthPassed(MonthPassedEvent evt)
        {
            if (!Config.EnableShortWalks.Value) return;
            try
            {
                int marked = ClearWorkResidencesForReoptimization();
                if (marked > 0)
                {
                    Plugin.Log.Msg($"[ShortWalks] Cleared workResidence for {marked} villager(s) — vanilla Hungarian will re-house them within ~6 in-game seconds.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[ShortWalks] OnMonthPassed: {ex.Message}");
            }
        }

        /// <summary>
        /// For each eligible villager, set workResidence = null. Vanilla's
        /// VillagerHomeManager.Update polls every 6s for villagers with null
        /// workResidence and runs Hungarian assignment over them. So this is
        /// all we need to do — vanilla's existing algorithm does the work.
        /// </summary>
        private static int ClearWorkResidencesForReoptimization()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm == null) return 0;
            var villagers = rm.villagersRO;
            if (villagers == null) return 0;

            int count = 0;
            for (int i = 0; i < villagers.Count; i++)
            {
                var v = villagers[i];
                if (v == null) continue;
                if (v.IsInfant()) continue;

                // Must have BOTH a workplace and a current workResidence to be
                // eligible. Without workplace, no distance to optimize;
                // without workResidence, vanilla handles them already.
                if (v.placeOfWork.IsNull()) continue;
                if (v.workResidence.IsNull()) continue;

                // Skip combat/garrison occupations.
                int occ = (int)v.GetOccupation();
                if (occ == OccupationSoldier
                    || occ == OccupationGuard
                    || occ == OccupationTransitionToSoldier) continue;

                // Skip "lives at workplace" — same GameObject for both
                // references means moving them would break the work linkage.
                // Catches healer/priest/librarian/teacher and any future
                // FF occupation Crate adds with workplace-as-home semantics.
                if (SameGameObject(v.workResidence, v.placeOfWork)) continue;

                // Clear. Vanilla's workResidence setter handles the
                // OnWorkResidenceChanged callback (old shelter releases the
                // villager) on its own.
                v.workResidence = null;
                count++;
            }
            return count;
        }

        private static bool SameGameObject(IResidence residence, IPlaceOfWork place)
        {
            var rComp = residence as Component;
            var pComp = place as Component;
            if (rComp == null || pComp == null) return false;
            return rComp.gameObject == pComp.gameObject;
        }
    }
}
