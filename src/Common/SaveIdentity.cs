// Per-town identity for EP's per-save sidecar files: Workplace Mastery tenure
// (UserData/EP_VillagerMastery) and Penny Pincher tower exemptions
// (UserData/EP_PennyPincher). Same approach as Manifest Delivery v1.0.21.
//
// WHY THIS EXISTS. SaveManager.activeSaveFileName takes several shapes for ONE town:
//   "Lametree_2026238203354/Lametree"       loading a manual save (no extension)
//   "Lametree_2026238203354/Lametree.sav"   SaveInternal rebuilds it WITH the extension
//                                           when it was empty
//   "Lametree_2026238203354/AutoSave 1"     loading an autosave
//   null                                    a new town before its first save, and again
//                                           after EVERY autosave (MoveSavMapFiles nulls it)
// Before the October 2026 fix, EP keyed files on Path.GetFileNameWithoutExtension(...) and special-cased
// "AutoSave 1"/"AutoSave 2" by comparing the WHOLE string, which never matches the
// folder-prefixed form. So every session loaded from an autosave read and wrote one shared
// "AutoSave 1.json" (or "AutoSave 2.json") used by every town. Tenure earned there never
// reached the town's own file, so it appeared to revert on the next manual load, and two
// towns' autosave sessions overwrote each other. Two towns with the same name also shared
// one file.
//
// THE KEY. The town's save FOLDER, e.g. "Lametree_2026238203354". FF names it
// settlement + "_" + a UTC timestamp, so it's unique per town, and it's identical for the
// town's manual saves and autosaves. Sources, in the order SaveManager.SaveInternal itself
// uses: GameFolder(activeSaveFileName), then SaveManager.lastGameFolder (covers the null
// after an autosave and a new town's first save). The last non-empty value is latched;
// Reset() clears the latch on every scene change so a new game never inherits the previous
// town's key.
//
// SEMANTICS. One file per town, shared by all of its saves. Loading an OLDER save of the
// same town doesn't rewind what's in the file. That was already true for manual saves before
// this change; Workplace Mastery's open-tenure math clamps a future start day to zero, so
// nothing goes negative.

using System.Collections.Generic;
using System.IO;

namespace EssentialProvisions.Common
{
    internal static class SaveIdentity
    {
        private const string AutoSaveOne = "AutoSave 1";   // SaveManager.autoSaveFileOneName
        private const string AutoSaveTwo = "AutoSave 2";

        private static string _latched = "";

        /// <summary>Clear the latch. Call on every scene change.</summary>
        internal static void Reset() => _latched = "";

        /// <summary>
        /// The current town's identity, e.g. "Lametree_2026238203354". Returns "" for a town
        /// that has never been saved; callers keep their data in memory until this turns
        /// non-empty. Never throws.
        /// </summary>
        internal static string TownKey()
        {
            string key = "";
            try
            {
                string raw = SaveManager.activeSaveFileName;
                key = FolderOf(raw);
                if (key.Length == 0) key = TrimSeparators(SaveManager.lastGameFolder);
                // A save with no folder at all (very old saves): fall back to the old key
                // rather than never writing.
                if (key.Length == 0 && !string.IsNullOrEmpty(raw)) key = OldStyleName(raw);
            }
            catch { key = ""; }

            if (key.Length > 0)
            {
                _latched = key;
                return key;
            }
            return _latched;
        }

        /// <summary>
        /// File names EP used before the town-folder key for the current save, most specific first, for a
        /// one-time migration into the town file: the loaded save's own name, then the
        /// settlement name. With <paramref name="includeSharedAutosave"/>, the shared
        /// "AutoSave 1"/"AutoSave 2" name comes last when the loaded save is an autosave.
        /// Those shared files hold several towns' data, so only callers that match entries by
        /// their own identity (Workplace Mastery's per-villager key) should read them.
        /// </summary>
        internal static List<string> LegacyKeys(bool includeSharedAutosave)
        {
            var keys = new List<string>();
            try
            {
                string raw = SaveManager.activeSaveFileName;
                string name = string.IsNullOrEmpty(raw) ? "" : Path.GetFileNameWithoutExtension(raw);
                bool isAutoSave = IsAutoSaveName(name);

                if (name.Length > 0 && !isAutoSave) AddUnique(keys, name);
                AddUnique(keys, SaveManager.activeSettlementName);
                if (includeSharedAutosave && isAutoSave) AddUnique(keys, name);
            }
            catch { }
            return keys;
        }

        private static bool IsAutoSaveName(string name) => name == AutoSaveOne || name == AutoSaveTwo;

        private static string FolderOf(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            // GameFolder returns everything up to and including the first '/', or "" when
            // there's no separator.
            return TrimSeparators(SaveManager.GameFolder(raw));
        }

        private static string TrimSeparators(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Trim('/', '\\');

        // What the old code intended for a name with no folder.
        private static string OldStyleName(string raw)
        {
            string name = Path.GetFileNameWithoutExtension(raw) ?? "";
            if (IsAutoSaveName(name))
            {
                string town = SaveManager.activeSettlementName;
                return string.IsNullOrEmpty(town) ? name : town;
            }
            return name;
        }

        private static void AddUnique(List<string> keys, string key)
        {
            if (!string.IsNullOrEmpty(key) && !keys.Contains(key)) keys.Add(key);
        }
    }
}
