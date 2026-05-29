// Crop-rotation rule engine. Mirrors FFQoL's `CropRotationLogic` /
// `CropRotationValidator` exactly — same disease groups, same gap thresholds,
// same seasonal-risk checks. Pure logic; no UI dependencies. Used by
// Features/SoilWisdom.cs.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EssentialProvisions.Common
{
    /// <summary>
    /// One scheduled crop in a field's planting sequence. Built by
    /// CollectPlantings from `Cropfield.cropScheduler.yearSchedulesRO`,
    /// rotated to start at the field's current year.
    /// </summary>
    internal struct Planting
    {
        internal string RecordName;
        internal int StartDay;
        internal int YearIndex;
    }

    /// <summary>
    /// One detected rotation violation: a pair of plantings (A, B) that
    /// breached a disease group's RequiredGap, with their year indices.
    /// Re-used for both raw and merged violations — for merged ones,
    /// YearA = YearLow and YearB = YearHigh.
    /// </summary>
    internal struct RotationViolation
    {
        internal string RecordA;
        internal int YearA;
        internal string RecordB;
        internal int YearB;
    }

    /// <summary>
    /// Set of crops that share a disease — planting two from the group too
    /// close together risks disease carryover. RequiredGap is the number of
    /// non-group crops needed between two group plantings.
    /// </summary>
    internal sealed class DiseaseGroup
    {
        internal readonly HashSet<string> Crops;
        internal readonly int RequiredGap;

        internal DiseaseGroup(int requiredGap, params string[] crops)
        {
            RequiredGap = requiredGap;
            Crops = new HashSet<string>(crops, StringComparer.Ordinal);
        }
    }

    internal static class CropRotationLogic
    {
        // FFQoL's hardcoded disease groups (5 total). Each group's RequiredGap
        // is the minimum number of NON-group plantings between two group
        // plantings.
        internal static readonly DiseaseGroup[] Groups = new[] {
            new DiseaseGroup(2, "TurnipField",   "CabbageField"),
            new DiseaseGroup(2, "BeanField",     "PeaField", "BuckwheatField", "CarrotField"),
            new DiseaseGroup(3, "BeanField",     "PeaField", "BuckwheatField"),
            new DiseaseGroup(2, "WheatField",    "BuckwheatField", "CarrotField"),
            new DiseaseGroup(3, "WheatField",    "RyeField"),
        };

        /// <summary>
        /// Find every pair of plantings within `group` whose gap (count of
        /// non-group plantings between them, treating the schedule as a
        /// repeating cycle) is below `group.RequiredGap`.
        /// </summary>
        internal static List<RotationViolation> FindDiseaseViolations(List<Planting> sequence, DiseaseGroup group)
        {
            var indices = new List<int>();
            for (int i = 0; i < sequence.Count; i++)
            {
                if (group.Crops.Contains(sequence[i].RecordName))
                    indices.Add(i);
            }
            var violations = new List<RotationViolation>();
            if (indices.Count < 2) return violations;

            int count = sequence.Count;
            for (int j = 0; j < indices.Count; j++)
            {
                int idxA = indices[j];
                int idxB = indices[(j + 1) % indices.Count];
                int rawSpan = (idxB - idxA + count) % count - 1;
                int nonGroupCount = 0;
                for (int k = 1; k <= rawSpan; k++)
                {
                    if (!group.Crops.Contains(sequence[(idxA + k) % count].RecordName))
                        nonGroupCount++;
                }
                if (nonGroupCount < group.RequiredGap)
                {
                    violations.Add(new RotationViolation {
                        RecordA = sequence[idxA].RecordName, YearA = sequence[idxA].YearIndex,
                        RecordB = sequence[idxB].RecordName, YearB = sequence[idxB].YearIndex,
                    });
                }
            }
            return violations;
        }

        // FF's day calendar: spring < day 171, summer 171-264, then autumn → winter.
        // Thresholds below are FFQoL's empirical "this is a bad season for this
        // crop" boundaries — we mirror them exactly.

        internal static bool IsFrostRisk(int percentDiesOnFrost, int startDay)
        {
            if (percentDiesOnFrost < 20) return false;
            return startDay < 171; // spring planting
        }

        internal static bool IsHeatRisk(int percentDiesOfHeatStress, int midpoint)
        {
            if (percentDiesOfHeatStress < 35) return false;
            return midpoint >= 171 && midpoint < 265; // summer growing window
        }

        /// <summary>
        /// Merge violation entries that describe the same crop pair in either
        /// order (A→B and B→A) — collapse to one entry with min/max year
        /// indices spanning the full risky window.
        /// </summary>
        internal static List<RotationViolation> MergeViolations(List<RotationViolation> raw)
        {
            var merged = new Dictionary<string, RotationViolation>();
            foreach (var v in raw)
            {
                string a = v.RecordA, b = v.RecordB;
                int ya = v.YearA, yb = v.YearB;
                if (string.CompareOrdinal(a, b) > 0) // canonicalize to alpha order
                {
                    var ts = a; a = b; b = ts;
                    var ti = ya; ya = yb; yb = ti;
                }
                int yMin = Math.Min(ya, yb);
                int yMax = Math.Max(ya, yb);
                string key = a + "|" + b;
                if (merged.TryGetValue(key, out var existing))
                {
                    yMin = Math.Min(yMin, Math.Min(existing.YearA, existing.YearB));
                    yMax = Math.Max(yMax, Math.Max(existing.YearA, existing.YearB));
                }
                merged[key] = new RotationViolation {
                    RecordA = a, YearA = yMin,
                    RecordB = b, YearB = yMax,
                };
            }
            return new List<RotationViolation>(merged.Values);
        }

        /// <summary>
        /// Walk a Cropfield's planted-year schedule starting from its current
        /// year and build a flat list of plantings.
        /// </summary>
        internal static List<Planting> CollectPlantings(Cropfield field)
        {
            var result = new List<Planting>();
            if (field == null) return result;
            var scheduler = field.cropScheduler;
            if (scheduler == null) return result;
            ReadOnlyCollection<CropYearSchedule> schedules = scheduler.yearSchedulesRO;
            if (schedules == null || schedules.Count == 0) return result;

            int startIdx = scheduler.currentYearScheduleIdx;
            int n = schedules.Count;
            for (int yearOffset = 0; yearOffset < n; yearOffset++)
            {
                var schedule = schedules[(startIdx + yearOffset) % n];
                if (schedule == null) continue;
                var items = schedule.cropFieldScheduledItems;
                if (items == null) continue;
                for (int j = 0; j < items.Count; j++)
                {
                    var planting = items[j] as CropfieldPlantingScheduledItem;
                    if (planting == null) continue;
                    if (string.IsNullOrEmpty(planting.recordName)) continue;
                    result.Add(new Planting {
                        RecordName = planting.recordName,
                        StartDay   = planting.plannedStartDay,
                        YearIndex  = yearOffset + 1, // human-readable Y1, Y2, …
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Run the full validator on a field. Returns a flat list of
        /// human-readable warning strings; empty list = no issues.
        /// </summary>
        internal static List<string> Validate(Cropfield field)
        {
            var warnings = new List<string>();
            var plantings = CollectPlantings(field);
            if (plantings.Count == 0) return warnings;

            // Per-planting seasonal checks
            foreach (var p in plantings)
            {
                CheckSeason(p, warnings);
            }

            // Disease-group violations
            var raw = new List<RotationViolation>();
            foreach (var group in Groups)
            {
                raw.AddRange(FindDiseaseViolations(plantings, group));
            }
            foreach (var m in MergeViolations(raw))
            {
                warnings.Add(FormatMergedViolation(m.RecordA, m.YearA, m.RecordB, m.YearB));
            }
            return warnings;
        }

        private static void CheckSeason(Planting p, List<string> warnings)
        {
            VegetableFieldsRecord record;
            try { record = ObjectDataStore.GetDataRecord<VegetableFieldsRecord>(p.RecordName); }
            catch { return; }
            if (record == null) return;

            int frostPct = record.percentDiesOnFrost;
            int heatPct  = record.basePercentDiesOfHeatStress;
            int startDay = p.StartDay;
            int midpoint = startDay + (record.daysOfPlanting + record.daysToMature) / 2;

            if (IsFrostRisk(frostPct, startDay))
            {
                warnings.Add("Frost risk: " + Pretty(p.RecordName) + " (Y" + p.YearIndex + ") — " + frostPct + "% mortality on frost");
            }
            if (IsHeatRisk(heatPct, midpoint))
            {
                warnings.Add("Heat risk: " + Pretty(p.RecordName) + " (Y" + p.YearIndex + ") — " + heatPct + "% mortality at peak heat");
            }
        }

        private static string FormatMergedViolation(string a, int yLow, string b, int yHigh)
        {
            string years = (yLow == yHigh) ? "Y" + yLow : "Y" + yLow + "–Y" + yHigh;
            return "Disease risk: " + Pretty(a) + " & " + Pretty(b) + " (" + years + ") — too close in rotation";
        }

        /// <summary>
        /// "TurnipField" → "Turnip". Strips the "Field" suffix that FF uses
        /// internally so warnings read naturally.
        /// </summary>
        internal static string Pretty(string recordName)
        {
            if (string.IsNullOrEmpty(recordName)) return recordName;
            const string suffix = "Field";
            return recordName.EndsWith(suffix)
                ? recordName.Substring(0, recordName.Length - suffix.Length)
                : recordName;
        }
    }
}
