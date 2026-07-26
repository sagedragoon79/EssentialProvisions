// Public interop so Keep Clarity (or any mod) can show an orchard's harvest-capacity status on the
// Arborist building panel. EP owns the numbers (Harvest Watch); KC owns the label/placement. Reflective
// soft-dep — no hard reference either way, same pattern as WorkInfoApi / DiseaseInfoApi.
//
// STABILITY CONTRACT: these public static signatures are the soft-dep boundary. Once shipped, do NOT
// rename/remove or change signatures — add new methods instead. Null-safe, never throw (return 0 on any
// failure). Counts are whole trees. 0 == "nothing unharvested / unknown" so a consumer can hide the line.

namespace EssentialProvisions
{
    public static class HarvestInfoApi
    {
        /// <summary>How many fruit trees in this orchard's work area currently hold unharvested fruit
        /// (at or above the Harvest Watch "min fruit to flag" threshold). A persistently high number
        /// means the orchard is out-pacing its workers. 0 if not sick of work / feature off / null.</summary>
        public static int GetUnharvestedTreeCount(ArboristBuilding building)
        {
            if (building == null || !Config.EnableHarvestWatch.Value) return 0;
            return Features.HarvestWatch.CountUnharvestedTrees(building);
        }

        /// <summary>How many fruit trees in this orchard went unharvested and rotted at last year's
        /// year-end wipe — the "did my workers keep up last year" figure. 0 if none / feature off /
        /// no full year observed yet / null.</summary>
        public static int GetTreesRottedLastYear(ArboristBuilding building)
        {
            if (building == null || !Config.EnableHarvestWatch.Value) return 0;
            return Features.HarvestWatch.GetRottedLastYear(building);
        }
    }
}
