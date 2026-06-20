// Planting Almanac — pure logic + model for crop-field rotation templates.
// No UI dependencies; Features/PlantingAlmanac.cs drives the window UI.
//
// A template is a per-field, multi-year rotation: Years[y] is the ordered list
// of crop recordNames ("WheatField", "CloverField", …) for schedule-year y.
// Apply replaces a field's schedule with the template via FF's PUBLIC scheduler
// API only (CropfieldScheduler.Clear + AttemptToAddCropToSchedule), so applied
// plans serialize exactly like hand-entered ones — no custom save hooks.
//
// Verified against the live scheduler (Assembly-CSharp decompile, 2026-05-28):
//   - AttemptToAddCropToSchedule(int scheduleIdx, string recordName) auto-computes
//     the start day and packs crops sequentially; returns null when it won't fit.
//     CAUTION: it dereferences the data record, so it NPEs on an unknown
//     recordName, and it does NOT check `locked` (unresearched). We guard both.
//   - Clear(ICropfieldScheduledItem, int yearIdx) removes an item (wiping it
//     first if it has begun).
//   - yearSchedulesRO / currentYearScheduleIdx for read + alignment.
//
// MVP scope: built-in presets + Apply only. User-saved JSON library is v2
// (Capture() below is ready for it; no file IO wired yet).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace EssentialProvisions.Common
{
    /// <summary>One saved rotation: Years[y] = ordered crop recordNames for that schedule year.</summary>
    internal sealed class CropTemplate
    {
        public string Name;
        public bool BuiltIn;
        public List<List<string>> Years;

        public CropTemplate(string name, bool builtIn, List<List<string>> years)
        {
            Name = name;
            BuiltIn = builtIn;
            Years = years ?? new List<List<string>>();
        }
    }

    /// <summary>Outcome of applying a template — surfaced to the player as a one-line summary.</summary>
    internal struct ApplyResult
    {
        public int Applied;
        public int SkippedNoRoom;     // didn't fit the year's day budget
        public int SkippedLocked;     // crop not yet researched
        public int SkippedInvalid;    // unknown recordName / not in this game (DLC)
        public int WipedInProgress;   // an already-started planting was cleared
        public int Warnings;          // Soil Wisdom rotation warnings after apply

        public int TotalSkipped => SkippedNoRoom + SkippedLocked + SkippedInvalid;

        /// <summary>Compact one-liner for the narrow on-screen status row.</summary>
        public string ShortSummary()
        {
            var s = $"{Applied} planted";
            if (TotalSkipped > 0)    s += $", {TotalSkipped} skipped";
            if (WipedInProgress > 0) s += $", {WipedInProgress} replaced";
            s += Warnings > 0 ? $", {Warnings} warning(s)" : ", clean";
            return s;
        }

        public string Summary(string templateName)
        {
            var s = $"Applied \"{templateName}\": {Applied} planting(s)";
            if (TotalSkipped > 0)
            {
                var parts = new List<string>();
                if (SkippedNoRoom > 0)  parts.Add($"{SkippedNoRoom} no room");
                if (SkippedLocked > 0)  parts.Add($"{SkippedLocked} not researched");
                if (SkippedInvalid > 0) parts.Add($"{SkippedInvalid} unavailable");
                s += $", skipped {TotalSkipped} ({string.Join(", ", parts.ToArray())})";
            }
            if (WipedInProgress > 0) s += $", replaced {WipedInProgress} in-progress";
            s += Warnings > 0 ? $" — {Warnings} rotation warning(s)" : " — no rotation warnings";
            return s;
        }
    }

    internal static class CropTemplateStore
    {
        // Sentinel stored in a template's year list for a field-maintenance /
        // fallow period (CropfieldWorkScheduledItem). On Apply it's re-added via
        // AttemptToAddFieldWorkToSchedule instead of AttemptToAddCropToSchedule.
        internal const string WorkItem = "Maintenance";

        // ----- Built-in presets -----
        // Each is a single field's 3-year rotation, authored from the verified
        // disease clusters (Knowledge: farming-crops-and-rotation.md §4.7/§9).
        // The "safe" presets contain no two crops that share a disease group, so
        // they pass CropRotationLogic.Validate clean.

        private static readonly List<CropTemplate> _builtIns = new List<CropTemplate>
        {
            // Grain + brassica + cleanse/fertility. No shared disease group.
            new CropTemplate("Disease-Safe Rotation", true, new List<List<string>>
            {
                new List<string> { "RyeField" },
                new List<string> { "CabbageField" },
                new List<string> { "CloverField" },
            }),
            // Fertility recovery: bean (restores) → clover (restores + cleanses) → hay (fodder).
            new CropTemplate("Soil Rebuild", true, new List<List<string>>
            {
                new List<string> { "BeanField" },
                new List<string> { "CloverField" },
                new List<string> { "HayField" },
            }),
            // Fiber + two foods, all disease loners/isolated here. Clean.
            new CropTemplate("Fiber & Food", true, new List<List<string>>
            {
                new List<string> { "FlaxField" },
                new List<string> { "LeekField" },
                new List<string> { "CarrotField" },
            }),
        };

        /// <summary>All templates: built-ins first, then the player's saved ones
        /// (rescanned from disk each call — cheap, only a handful of small files).</summary>
        internal static List<CropTemplate> All()
        {
            var list = new List<CropTemplate>(_builtIns);
            list.AddRange(LoadUserTemplates());
            return list;
        }

        // ----- User template persistence (shareable JSON) -----

        // Newtonsoft (shipped in FF's Managed folder) round-trips List<List<string>>
        // cleanly — unlike UnityEngine.JsonUtility, which silently drops nested
        // custom-class lists (it only wrote "name", losing every crop).
        private sealed class TemplateFile
        {
            public string name = "";
            public List<List<string>> years = new List<List<string>>();
        }

        /// <summary>&lt;game&gt;/Farthest Frontier (Mono)/UserData/EP_CropTemplates/</summary>
        internal static string UserDir
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(Path.Combine(root, "UserData"), "EP_CropTemplates");
            }
        }

        internal static List<CropTemplate> LoadUserTemplates()
        {
            var list = new List<CropTemplate>();
            try
            {
                if (!Directory.Exists(UserDir)) return list;
                foreach (var file in Directory.GetFiles(UserDir, "*.json"))
                {
                    try
                    {
                        var dto = JsonConvert.DeserializeObject<TemplateFile>(File.ReadAllText(file));
                        if (dto == null || string.IsNullOrEmpty(dto.name)) continue;
                        var years = new List<List<string>>();
                        if (dto.years != null)
                            foreach (var y in dto.years)
                                years.Add(new List<string>(y ?? new List<string>()));
                        list.Add(new CropTemplate(dto.name, false, years));
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[PlantingAlmanac] Skipped bad template '{Path.GetFileName(file)}': {ex.Message}"); }
                }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[PlantingAlmanac] LoadUserTemplates: {ex.Message}"); }
            return list;
        }

        /// <summary>Persist a template as JSON. Returns the saved name (may be
        /// de-duplicated with a numeric suffix). Empty string on failure.</summary>
        internal static string SaveUserTemplate(CropTemplate t)
        {
            try
            {
                Directory.CreateDirectory(UserDir);
                string name = DedupeName(t.Name);
                var dto = new TemplateFile { name = name, years = new List<List<string>>() };
                foreach (var year in t.Years)
                    dto.years.Add(new List<string>(year ?? new List<string>()));
                File.WriteAllText(Path.Combine(UserDir, SafeFileName(name) + ".json"),
                    JsonConvert.SerializeObject(dto, Formatting.Indented));
                return name;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] SaveUserTemplate: {ex.Message}");
                return "";
            }
        }

        /// <summary>Delete a user template's JSON file. Returns true if a file was removed.</summary>
        internal static bool DeleteUserTemplate(string name)
        {
            try
            {
                var path = Path.Combine(UserDir, SafeFileName(name) + ".json");
                if (File.Exists(path)) { File.Delete(path); return true; }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[PlantingAlmanac] DeleteUserTemplate: {ex.Message}"); }
            return false;
        }

        /// <summary>A readable default name from the rotation's crops, e.g. "Rye → Cabbage → Clover".</summary>
        internal static string ContentName(CropTemplate t)
        {
            var parts = new List<string>();
            if (t?.Years != null)
            {
                foreach (var year in t.Years)
                {
                    if (year == null) continue;
                    // First actual crop in the year (skip maintenance sentinels).
                    string? crop = null;
                    foreach (var rec in year)
                        if (!string.IsNullOrEmpty(rec) && rec != WorkItem) { crop = rec; break; }
                    if (crop != null) parts.Add(CropRotationLogic.Pretty(crop));
                }
            }
            return parts.Count > 0 ? string.Join(" → ", parts.ToArray()) : "Field Rotation";
        }

        private static string DedupeName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "Field Rotation";
            string candidate = baseName;
            int n = 2;
            while (File.Exists(Path.Combine(UserDir, SafeFileName(candidate) + ".json")))
                candidate = $"{baseName} ({n++})";
            return candidate;
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }

        // ----- Apply -----

        /// <summary>
        /// Replace <paramref name="field"/>'s entire schedule with the template,
        /// aligned so template year 0 lands on the field's current schedule year
        /// (matching Capture). Uses only the public scheduler API.
        /// </summary>
        internal static ApplyResult Apply(CropTemplate template, Cropfield field)
        {
            var r = new ApplyResult();
            if (template == null || field == null) return r;
            var scheduler = field.cropScheduler;
            if (scheduler == null) return r;
            var years = scheduler.yearSchedulesRO;
            if (years == null || years.Count == 0) return r;

            int n = years.Count;
            int start = scheduler.currentYearScheduleIdx;
            if (start < 0 || start >= n) start = 0;

            // 1) Clear every year. Snapshot the list first — Clear() mutates it.
            for (int off = 0; off < n; off++)
            {
                int yi = (start + off) % n;
                var live = years[yi]?.cropFieldScheduledItems;
                if (live == null) continue;
                var snapshot = new List<ICropfieldScheduledItem>(live);
                foreach (var item in snapshot)
                {
                    if (item == null) continue;
                    try
                    {
                        if (item.hasBegan) r.WipedInProgress++;
                        scheduler.Clear(item, yi);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[PlantingAlmanac] Clear failed (year {yi}): {ex.Message}");
                    }
                }
            }

            // 2) Add the template's crops, year by year.
            int applyYears = Math.Min(template.Years?.Count ?? 0, n);
            for (int off = 0; off < applyYears; off++)
            {
                int yi = (start + off) % n;
                var list = template.Years![off];
                if (list == null) continue;
                foreach (var recordName in list)
                {
                    if (string.IsNullOrEmpty(recordName)) continue;

                    // Field-maintenance / fallow period.
                    if (recordName == WorkItem)
                    {
                        try
                        {
                            var work = scheduler.AttemptToAddFieldWorkToSchedule(yi);
                            if (work == null) r.SkippedNoRoom++; else r.Applied++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.Warning($"[PlantingAlmanac] AttemptToAddFieldWorkToSchedule failed: {ex.Message}");
                            r.SkippedInvalid++;
                        }
                        continue;
                    }

                    // Guard: AttemptToAddCropToSchedule NPEs on an unknown record
                    // and happily schedules unresearched crops. Pre-check both —
                    // but NOTE: VegetableFieldsRecord.locked is a STATIC base-data
                    // flag (Rye/Clover/Hay etc. ship "locked"), not the per-save
                    // research state. The live unlock state lives in
                    // AgricultureManager.lockedCrops (seeded from record.locked at
                    // awake, then UnlockCrop() clears entries as you research). So
                    // we check that set, NOT rec.locked.
                    VegetableFieldsRecord? rec = null;
                    try { rec = ObjectDataStore.GetDataRecord<VegetableFieldsRecord>(recordName); }
                    catch { /* treated as invalid below */ }
                    if (rec == null) { r.SkippedInvalid++; continue; }
                    if (IsCropLocked(recordName)) { r.SkippedLocked++; continue; }

                    CropfieldPlantingScheduledItem? added = null;
                    try { added = scheduler.AttemptToAddCropToSchedule(yi, recordName); }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[PlantingAlmanac] AttemptToAddCropToSchedule({recordName}) failed: {ex.Message}");
                        r.SkippedInvalid++;
                        continue;
                    }
                    if (added == null) r.SkippedNoRoom++;
                    else r.Applied++;
                }
            }

            // 3) Refresh the field's "empty season" widget marker. The scheduler
            // updates field.widgetBlackboard.hasEmptyCropSeason inside Clear()/
            // Init()/Load(), but NOT in AttemptToAddCropToSchedule — there the
            // CALLER is responsible. Every vanilla add path recomputes it right
            // after the scheduler call (e.g. UICropInfoWindow.AddDragItem and
            // OnClonerPasteEvent both do exactly this line). We clear+re-add, so
            // without this the marker stays stuck "on" — the field reads as
            // unconfigured until a manual edit re-triggers the recompute. The
            // blackboard setter fires its changed-event only on a real value
            // change, so this is the precise nudge the marker widget needs.
            try { field.widgetBlackboard.hasEmptyCropSeason = scheduler.hasEmptyCropSeason; }
            catch (Exception ex) { Plugin.Log.Warning($"[PlantingAlmanac] hasEmptyCropSeason refresh failed: {ex.Message}"); }

            // 4) Post-apply validation (reuse Soil Wisdom's validator for consistency).
            try { r.Warnings = CropRotationLogic.Validate(field).Count; }
            catch { /* non-fatal */ }

            return r;
        }

        /// <summary>
        /// True if the crop is not yet unlocked this save. Reads the live
        /// AgricultureManager.lockedCrops set (lowercase recordName keys),
        /// which research clears via UnlockCrop — unlike the record's static
        /// `locked` flag, which stays true for crops that ship locked.
        /// </summary>
        private static bool IsCropLocked(string recordName)
        {
            try
            {
                var agri = UnitySingleton<GameManager>.Instance?.agricultureManager;
                var set = agri?.lockedCrops;
                return set != null && set.Contains(recordName.ToLower());
            }
            catch { return false; }
        }

        // ----- Capture (ready for v2 Save…) -----

        /// <summary>
        /// Snapshot a field's current rotation into a new template. Walks the
        /// scheduler directly (not CropRotationLogic.CollectPlantings, which is
        /// crops-only) so field-maintenance / fallow periods are captured too,
        /// in schedule order. Years are ordered starting at the field's current
        /// schedule year, round-tripping with Apply.
        /// </summary>
        internal static CropTemplate Capture(Cropfield field, string name)
        {
            var years = new List<List<string>>();
            var scheduler = field?.cropScheduler;
            var schedules = scheduler?.yearSchedulesRO;
            if (scheduler == null || schedules == null || schedules.Count == 0)
                return new CropTemplate(name, false, years);

            int n = schedules.Count;
            int start = scheduler.currentYearScheduleIdx;
            if (start < 0 || start >= n) start = 0;

            for (int off = 0; off < n; off++)
            {
                var schedule = schedules[(start + off) % n];
                var list = new List<string>();
                var items = schedule?.cropFieldScheduledItems;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item is CropfieldPlantingScheduledItem p && !string.IsNullOrEmpty(p.recordName))
                            list.Add(p.recordName);
                        else if (item is CropfieldWorkScheduledItem)
                            list.Add(WorkItem);
                    }
                }
                years.Add(list);
            }
            return new CropTemplate(name, false, years);
        }
    }
}
