// Folded from FFAutomation by idontcare (v1.0.0, active)
// Original DLL: FFAutomation_FF.dll
// EP fold curation: _research/folds/ffautomation.md
// Original prefs: EnableConstructionReserve + per-resource Logs/Planks/Stone/Clay/Sand/Iron/Coal ints
// EP changes:
//   - Renamed section to "Project Prep"; pref keys ProjectPrep<Resource>
//   - Otherwise mirrors FFAutomation's mechanism exactly (Harmony Transpiler + Prefix
//     on LogisticsGlobalTaskSearch.SetupNeededItemStorages)
//
// Mechanism: FF's construction system queries `ReservableItemStorage.GetNumberOfUnreservedItems(item)`
// to decide how many items it can pull from a given storage container for a build. The Transpiler
// finds that call and post-processes the count via ApplyReserve, which subtracts from a per-item
// "remaining surplus" budget. The Prefix calls PrepareForSearch to reset and recompute the surplus
// (total available minus the configured reserve) before the search runs.
//
// Important: this is a Transpiler (IL injection), more fragile to FF updates than Prefix/Postfix.
// If FF renames or restructures GetNumberOfUnreservedItems, the Transpiler fails gracefully — logs
// a warning and disables the feature, but the game keeps running normally.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class ProjectPrep
    {
        // Reserve config: dict of ItemID → reserve count. Built from current
        // Config values on each PrepareForSearch call (so KC panel slider edits
        // take effect on the next construction query, no restart needed).
        // ItemID is FF's strongly-typed enum — using it directly so the
        // Transpiler-injected IL leaves a properly-typed value on the stack.
        private static readonly Dictionary<ItemID, uint> _reserveByItemID = new Dictionary<ItemID, uint>();

        // Per-search transient state: how much surplus remains for each item
        // after subtracting the reserve. Refilled in PrepareForSearch (Prefix),
        // drained by ApplyReserve calls injected by the Transpiler.
        private static readonly Dictionary<ItemID, uint> _remainingSurplus = new Dictionary<ItemID, uint>();

        // Maps pref → FF item record name. The name is parsed against the ItemID
        // enum at index-build time. Centralized so adding new resources is a
        // one-line table entry.
        private struct PrefEntry
        {
            public Func<int> Get;
            public string ItemName;
            public PrefEntry(Func<int> get, string itemName) { Get = get; ItemName = itemName; }
        }
        private static PrefEntry[] _prefMap = null!;

        public static void Reset()
        {
            _reserveByItemID.Clear();
            _remainingSurplus.Clear();
        }

        /// <summary>
        /// Called from Plugin.OnInitializeMelon AFTER Config.Initialize so the
        /// pref entries exist. Wires the pref-to-item-name table.
        /// </summary>
        public static void Initialize()
        {
            _prefMap = new PrefEntry[]
            {
                new PrefEntry(() => Config.ProjectPrepLogs.Value,   "Logs"),
                new PrefEntry(() => Config.ProjectPrepPlanks.Value, "Planks"),
                new PrefEntry(() => Config.ProjectPrepStone.Value,  "Stone"),
                new PrefEntry(() => Config.ProjectPrepClay.Value,   "Clay"),
                new PrefEntry(() => Config.ProjectPrepSand.Value,   "Sand"),
                new PrefEntry(() => Config.ProjectPrepIron.Value,   "Iron"),
                new PrefEntry(() => Config.ProjectPrepCoal.Value,   "Coal"),
            };
        }

        // ----- Reserve index build -----

        private static void BuildIndex()
        {
            _reserveByItemID.Clear();
            if (_prefMap == null) return;

            foreach (var entry in _prefMap)
            {
                int value = entry.Get();
                if (value <= 0) continue;
                if (Enum.TryParse<ItemID>(entry.ItemName, ignoreCase: true, out var id))
                {
                    _reserveByItemID[id] = (uint)value;
                }
                // else: ItemID enum doesn't have this name in this game version — skip.
            }
        }

        // ----- Surplus math (mirrors FFAutomation's ConstructionReserveLogic) -----

        private static uint ComputeSurplus(uint total, uint reserve)
            => total <= reserve ? 0u : total - reserve;

        private static uint DrawFromSurplus(uint count, uint remaining, out uint newRemaining)
        {
            if (remaining == 0u) { newRemaining = 0u; return 0u; }
            uint drawn = Math.Min(count, remaining);
            newRemaining = remaining - drawn;
            return drawn;
        }

        // ----- Called from Prefix -----

        public static void PrepareForSearch()
        {
            try
            {
                _remainingSurplus.Clear();
                if (!Config.EnableProjectPrep.Value) return;
                if (Plugin.IsForeignModLoaded("FFAutomation")) return;

                // Rebuild the reserve index every search so KC panel edits
                // apply immediately (no restart). 7 entries — cheap.
                BuildIndex();
                if (_reserveByItemID.Count == 0) return;

                var gm = UnitySingleton<GameManager>.Instance;
                var wbm = gm?.workBucketManager;
                if (wbm == null) return;

                // For each reserved ItemID, sum currently-unreserved totals
                // across all work-bucket containers, then compute surplus.
                foreach (var kv in _reserveByItemID)
                {
                    ItemID itemID  = kv.Key;
                    uint   reserve = kv.Value;

                    if (!TryGetItemForId(wbm, itemID, out var itemObj)) continue;
                    uint total = SumUnreserved(wbm, itemObj);
                    _remainingSurplus[itemID] = ComputeSurplus(total, reserve);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[ProjectPrep] PrepareForSearch: {ex.Message}");
                _remainingSurplus.Clear();
            }
        }

        // ----- Called by injected Transpiler IL after each GetNumberOfUnreservedItems -----

        public static uint ApplyReserve(uint count, ItemID itemID)
        {
            if (!Config.EnableProjectPrep.Value) return count;
            if (Plugin.IsForeignModLoaded("FFAutomation")) return count;
            if (!_reserveByItemID.ContainsKey(itemID)) return count;

            if (!_remainingSurplus.TryGetValue(itemID, out uint remaining)) return 0u;
            uint drawn = DrawFromSurplus(count, remaining, out uint newRemaining);
            _remainingSurplus[itemID] = newRemaining;
            return drawn;
        }

        // ----- Reflective WorkBucketManager access -----
        //
        // FFAutomation directly references WorkBucketManager.itemByItemIDRO,
        // GetHasItemWorkBucketByItem, WorkBucket.workObjsRO, IRegistersForWork
        // .GetStorageForContainer, etc. EP uses reflection so that if any of
        // these member names drift in a future FF update we degrade to
        // "feature off" rather than a build-time failure.

        // ----- Direct WorkBucketManager / storage access -----
        //
        // We're already byte-compatible with FFAutomation here — same API surface.
        // No need for reflection given we have a hard reference to Assembly-CSharp.

        private static bool TryGetItemForId(WorkBucketManager wbm, ItemID itemID, out Item itemObj)
        {
            itemObj = null!;
            if (wbm == null) return false;
            if (!wbm.itemByItemIDRO.TryGetValue(itemID, out var item)) return false;
            itemObj = item;
            return itemObj != null;
        }

        private static uint SumUnreserved(WorkBucketManager wbm, Item itemObj)
        {
            var bucket = wbm.GetHasItemWorkBucketByItem(wbm, itemObj);
            if (bucket == null) return 0u;
            uint total = 0u;
            foreach (var workObj in bucket.workObjsRO)
            {
                if (workObj == null) continue;
                var storage = workObj.GetStorageForContainer(bucket);
                if (storage == null) continue;
                total += storage.GetNumberOfUnreservedItems(itemObj);
            }
            return total;
        }

        // ============================================================
        // === Harmony patch ===
        // ============================================================

        /// <summary>
        /// Prefix calls PrepareForSearch to refill the per-item surplus budget.
        /// Transpiler injects ApplyReserve calls into the original method body
        /// so each GetNumberOfUnreservedItems return value is decremented by
        /// the available surplus before being returned to the construction query.
        /// </summary>
        [HarmonyPatch(typeof(LogisticsGlobalTaskSearch), "SetupNeededItemStorages")]
        internal static class Patch
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                try { PrepareForSearch(); }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[ProjectPrep] Prefix: {ex.Message}");
                    _remainingSurplus.Clear();
                }
            }

            /// <summary>
            /// Locate the call to `ReservableItemStorage.GetNumberOfUnreservedItems(Item)`
            /// inside the target method's IL, and inject a follow-up call that runs
            /// `ApplyReserve(count, item.itemID)` on the returned count. Same shape
            /// as FFAutomation's transpiler — even keeps the warning-and-disable
            /// path if the match fails (game updated and renamed something).
            /// </summary>
            [HarmonyTranspiler]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var getUnreservedMethod = AccessTools.Method(
                    typeof(ReservableItemStorage),
                    "GetNumberOfUnreservedItems",
                    new[] { typeof(Item) });
                var itemIdGetter = AccessTools.PropertyGetter(typeof(Item), "itemID");
                var applyReserve = AccessTools.Method(
                    typeof(ProjectPrep),
                    nameof(ApplyReserve),
                    new[] { typeof(uint), typeof(ItemID) });

                if (getUnreservedMethod == null || itemIdGetter == null || applyReserve == null)
                {
                    Plugin.Log.Warning("[ProjectPrep] Transpiler: required methods not resolvable — construction reserves DISABLED for this session.");
                    return instructions;
                }

                var matcher = new CodeMatcher(instructions);
                matcher.MatchForward(
                    useEnd: false,
                    new CodeMatch(i =>
                        (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt)
                        && i.operand is MethodInfo m && m == getUnreservedMethod));

                if (!matcher.IsValid)
                {
                    Plugin.Log.Warning("[ProjectPrep] Transpiler: GetNumberOfUnreservedItems call not found in IL — construction reserves DISABLED. Likely caused by a game update.");
                    return instructions;
                }

                // After GetNumberOfUnreservedItems returns, the stack top is the count.
                // The instruction before the call loaded the Item argument; clone it,
                // call get_itemID on it, then call ApplyReserve(count, itemID).
                var loadItemInstruction = matcher.InstructionAt(-1).Clone();
                matcher.Advance(1);
                matcher.Insert(
                    loadItemInstruction,
                    new CodeInstruction(OpCodes.Call, itemIdGetter),
                    new CodeInstruction(OpCodes.Call, applyReserve));

                return matcher.InstructionEnumeration();
            }
        }
    }
}
