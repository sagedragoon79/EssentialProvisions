// Workplace Mastery persistence — a per-save JSON sidecar holding each villager's
// accumulated job tenure. Mirrors CropTemplateStore exactly: Newtonsoft (FF ships
// it in Managed), plain-field DTOs, all IO wrapped in try/catch → Plugin.Log.Warning,
// never throws. Lives in Common/ (engine) so Features/WorkplaceMastery.cs (the feature
// + UI) stays focused.
//
// WHY A SIDECAR (not the .sav): Farthest Frontier has NO stable per-villager id in its
// save format — villagers are an ordinal list keyed only by a per-session instanceID
// (re-mapped every load) and a prefab-scoped guid; villagerName is mutable. So we can't
// piggyback the .sav, and we key our own file by a COMPOSITE synthetic key (see
// WorkplaceMastery.CompositeKey) derived from the one per-villager ABSOLUTE date FF does
// persist — the birthday. The file is keyed per TOWN (UserData/EP_VillagerMastery/<town
// folder>.json, see Common/SaveIdentity.cs) because tenure is per-town history, unlike
// Planting Almanac's global library.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace EssentialProvisions.Common
{
    /// <summary>One villager's tenure: committed whole-days per occupation, plus the
    /// currently-open tenure (occupation enum value + the absolute in-game day it began).
    /// Open days are derived on read as (today − start); committed on job change / save.</summary>
    internal sealed class MasteryRecord
    {
        public int CurrentOcc = 59;          // VillagerOccupation.Occupation.None
        public int CurrentStartAbsDay = -1;  // absolute in-game day the open tenure began (-1 = none open)
        public Dictionary<int, int> Days = new Dictionary<int, int>();  // occupation -> committed whole days
    }

    internal static class MasteryStore
    {
        // ----- DTO (Newtonsoft; plain PUBLIC fields, like CropTemplateStore.TemplateFile.
        //       JsonUtility silently drops nested lists/dicts, hence Newtonsoft.) -----
        private sealed class OccDays { public int occ; public int total; }

        private sealed class VillagerEntry
        {
            public string key = "";
            public int currentOcc = 59;
            public int currentStartAbsDay = -1;
            public List<OccDays> days = new List<OccDays>();
        }

        private sealed class MasteryFile { public List<VillagerEntry> villagers = new List<VillagerEntry>(); }

        /// <summary>&lt;game&gt;/Farthest Frontier (Mono)/UserData/EP_VillagerMastery/ — same
        /// derivation as CropTemplateStore.UserDir, leaf folder renamed.</summary>
        internal static string UserDir
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(Path.Combine(root, "UserData"), "EP_VillagerMastery");
            }
        }

        private static string PathFor(string saveName) =>
            Path.Combine(UserDir, SafeFileName(saveName) + ".json");

        /// <summary>True if a sidecar exists for this name. Never throws.</summary>
        internal static bool Exists(string saveName)
        {
            try { return !string.IsNullOrEmpty(saveName) && File.Exists(PathFor(saveName)); }
            catch { return false; }
        }

        /// <summary>Total committed days across all occupations.</summary>
        internal static int CommittedDays(MasteryRecord rec)
        {
            int total = 0;
            if (rec?.Days != null)
                foreach (var d in rec.Days.Values) total += d;
            return total;
        }

        /// <summary>
        /// Merge several sidecars into one map, keeping, per villager key, the record with
        /// the most committed days. Tenure only grows within a town's timeline, so the
        /// largest record is the most recent one. Used once per town to migrate files
        /// written before the town-folder key (October 2026). Names of the files actually found
        /// are appended to <paramref name="usedNames"/>.
        /// </summary>
        internal static Dictionary<string, MasteryRecord> LoadMerged(IEnumerable<string> saveNames, List<string> usedNames)
        {
            var merged = new Dictionary<string, MasteryRecord>();
            foreach (var name in saveNames)
            {
                if (!Exists(name)) continue;
                usedNames?.Add(name);
                foreach (var kv in Load(name))
                {
                    if (!merged.TryGetValue(kv.Key, out var have) || CommittedDays(kv.Value) > CommittedDays(have))
                        merged[kv.Key] = kv.Value;
                }
            }
            return merged;
        }

        /// <summary>Load a save's sidecar into a composite-key → record map. Returns an
        /// empty map (never null, never throws) on a missing or corrupt file.</summary>
        internal static Dictionary<string, MasteryRecord> Load(string saveName)
        {
            var result = new Dictionary<string, MasteryRecord>();
            try
            {
                if (string.IsNullOrEmpty(saveName)) return result;
                var file = PathFor(saveName);
                if (!File.Exists(file)) return result;

                var dto = JsonConvert.DeserializeObject<MasteryFile>(File.ReadAllText(file));
                if (dto?.villagers == null) return result;
                foreach (var e in dto.villagers)
                {
                    if (string.IsNullOrEmpty(e.key)) continue;
                    var rec = new MasteryRecord { CurrentOcc = e.currentOcc, CurrentStartAbsDay = e.currentStartAbsDay };
                    if (e.days != null)
                        foreach (var od in e.days)
                            if (od != null) rec.Days[od.occ] = od.total;
                    result[e.key] = rec;
                }
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] Load: {ex.Message}"); }
            return result;
        }

        /// <summary>Write a composite-key → record map to the save's sidecar (never throws).</summary>
        internal static void Save(string saveName, Dictionary<string, MasteryRecord> records)
        {
            try
            {
                if (string.IsNullOrEmpty(saveName) || records == null) return;
                Directory.CreateDirectory(UserDir);
                var dto = new MasteryFile();
                foreach (var kv in records)
                {
                    if (kv.Value == null) continue;
                    var e = new VillagerEntry
                    {
                        key = kv.Key,
                        currentOcc = kv.Value.CurrentOcc,
                        currentStartAbsDay = kv.Value.CurrentStartAbsDay,
                    };
                    foreach (var d in kv.Value.Days)
                        e.days.Add(new OccDays { occ = d.Key, total = d.Value });
                    dto.villagers.Add(e);
                }
                File.WriteAllText(PathFor(saveName),
                    JsonConvert.SerializeObject(dto, Formatting.Indented));
            }
            catch (Exception ex) { Plugin.Log.Warning($"[WorkplaceMastery] Save: {ex.Message}"); }
        }

        // Verbatim from CropTemplateStore.SafeFileName.
        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString().Trim();
        }
    }
}
