// Consumable Control — auto-configure "Town Reserve" min quotas on storage
// buildings based on observed daily consumption × months × headroom.
//
// FFAutomation has two related features — Town Reserve and Auto Stock. THIS
// is Town Reserve only:
//   "Keep at least N months of supply on hand. Trading posts (and other
//    logistics-based outbound consumers) can only pull surplus above the
//    floor. Villagers eat freely from the same storages because their
//    consumption bypasses the logistics-request system."
//
// Auto Stock (actively shunt surplus → trading post) is a separate future fold.
//
// Mechanism:
//   - Subscribe to ItemConsumedEvent — accumulate per-ItemID into a 30-day
//     rolling window.
//   - Subscribe to DayPassedEvent — roll the window (cheap queue op).
//   - Subscribe to MonthPassedEvent — recompute targets and push min quotas
//     to applicable storages. Monthly cadence chosen because a 30-day
//     rolling average barely shifts day-to-day; the writes are the expensive
//     part. A per-(storage,item) value cache skips redundant writes when
//     the recomputed value matches the last one we pushed.
//   - target = ceil(daily_rate × 30 × ReserveMonths × (1 + headroom%))
//   - per-storage = ceil(target / applicable_storage_count) so total town
//     reserve ≈ target.
//   - Apply via StorageQuotaHandler.SetMinQuotaForItem on:
//       Food items → granaries + root cellars + storehouses (incl. T2 variants)
//       Non-food items → storehouses only
//     Trading posts are deliberately excluded — they should not hoard reserve.
//   - On disable / reset, clear only the (storage, ItemID) quotas EP wrote.

using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class ConsumableControl
    {
        /// <summary>30-day rolling consumption tracker (mutated daily).</summary>
        private sealed class ConsumptionTracker
        {
            private readonly Queue<uint> _dailyTotals = new Queue<uint>(30);
            public uint TodayTotal;
            private uint _rollingSum;

            public void RollDay()
            {
                _rollingSum += TodayTotal;
                _dailyTotals.Enqueue(TodayTotal);
                if (_dailyTotals.Count > 30) _rollingSum -= _dailyTotals.Dequeue();
                TodayTotal = 0u;
            }

            public int Days => _dailyTotals.Count;

            public float AverageDailyRate()
                => _dailyTotals.Count == 0 ? 0f : (float)_rollingSum / _dailyTotals.Count;
        }

        // ItemIDs we treat as "food" for storage targeting — these go in
        // granaries + root cellars + storehouses. Everything else goes in
        // storehouses only.
        private static readonly HashSet<ItemID> FoodItems = new HashSet<ItemID>
        {
            ItemID.Bread, ItemID.RootVegetable, ItemID.Beans, ItemID.Greens,
            ItemID.Berries, ItemID.Fruit, ItemID.Nuts, ItemID.Mushroom,
            ItemID.Roots, ItemID.Honey, ItemID.Eggs, ItemID.Fish, ItemID.SmokedFish,
            ItemID.Meat, ItemID.SmokedMeat, ItemID.Cheese, ItemID.Pastry,
            ItemID.Preserves, ItemID.PreservedVeg,
        };

        // Per-item rolling tracker; populated from ConsumableTrackedItems cfg.
        private static readonly Dictionary<ItemID, ConsumptionTracker> _trackers
            = new Dictionary<ItemID, ConsumptionTracker>();

        // (storage instance ID, ItemID) we've written. Cleanup on disable
        // touches only these — manual quotas elsewhere are preserved.
        private struct StorageItemKey : IEquatable<StorageItemKey>
        {
            public int StorageID;
            public ItemID Item;
            public StorageItemKey(int s, ItemID i) { StorageID = s; Item = i; }
            public bool Equals(StorageItemKey o) => StorageID == o.StorageID && Item == o.Item;
            public override bool Equals(object o) => o is StorageItemKey k && Equals(k);
            public override int GetHashCode() => unchecked(StorageID * 397 ^ (int)Item);
        }
        private static readonly HashSet<StorageItemKey> _writtenQuotas
            = new HashSet<StorageItemKey>();

        // Last value we pushed per (storage, item) — used to skip redundant
        // SetMinQuotaForItem calls when the recompute lands on the same number.
        private static readonly Dictionary<StorageItemKey, int> _lastWrittenValue
            = new Dictionary<StorageItemKey, int>();

        private static bool _subscribed;
        private static bool _wasEnabled;
        private static string _lastParsedItemsRaw = "";

        // ----- Temporary diagnostic state (1.1.0+) — surface CC's silence ----
        // One-shot path traces (bitmask) + first-time ItemConsumedEvent per item.
        private static int _traceMask;
        private static readonly HashSet<ItemID> _firstConsumed = new HashSet<ItemID>();

        private static void TraceOnce(int bit, string msg)
        {
            if ((_traceMask & bit) != 0) return;
            _traceMask |= bit;
            try { Plugin.Log.Msg($"[ConsumableControl][trace] {msg}"); } catch { }
        }

        // Regular cadence is monthly, but we also do one catch-up apply on the
        // first day after enabling that has consumption data — otherwise the
        // reserve floor wouldn't exist for up to a month after you turn the
        // feature on, leaving food unprotected (e.g. a trading post could pull
        // every bean). Set false on enable; flipped true once we've applied.
        private static bool _initialApplyDone;

        public static void Reset()
        {
            UnsubscribeIfSubscribed();
            ClearAllOurQuotas();
            _trackers.Clear();
            _wasEnabled = false;
            _lastParsedItemsRaw = "";
            _initialApplyDone = false;
            _traceMask = 0;
            _firstConsumed.Clear();
        }

        /// <summary>
        /// Surplus Selling reads the same rate trackers Consumable Control
        /// maintains — no point duplicating consumption telemetry across
        /// features. Returns a snapshot of (ItemID → daily rate) for items
        /// with ≥1 day of data; empty if Consumable Control isn't running.
        /// </summary>
        internal static Dictionary<ItemID, float> GetCurrentRatesSnapshot()
        {
            var snapshot = new Dictionary<ItemID, float>(_trackers.Count);
            foreach (var kv in _trackers)
            {
                if (kv.Value.Days == 0) continue;
                float rate = kv.Value.AverageDailyRate();
                if (rate <= 0f) continue;
                snapshot[kv.Key] = rate;
            }
            return snapshot;
        }

        public static void OnUpdate()
        {
            TraceOnce(1, "OnUpdate alive (called at least once)");

            bool enabled = Config.EnableConsumableControl.Value;

            if (!enabled && _wasEnabled)
            {
                ClearAllOurQuotas();
                UnsubscribeIfSubscribed();
            }
            _wasEnabled = enabled;
            if (!enabled) { TraceOnce(2, "early-return: EnableConsumableControl is false"); return; }
            TraceOnce(4, "EnableConsumableControl is true");

            if (!GameManager.gameReadyToPlay) { TraceOnce(8, "early-return: gameReadyToPlay false (still waiting)"); return; }
            TraceOnce(16, "gameReadyToPlay reached");
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) { TraceOnce(32, "early-return: GameManager.Instance is null"); return; }
            var em = gm.eventManager;
            if (em == null) { TraceOnce(64, "early-return: gameManager.eventManager is null"); return; }
            TraceOnce(128, "managers available");

            var raw = Config.ConsumableTrackedItems.Value;
            if (raw != _lastParsedItemsRaw)
            {
                TraceOnce(256, $"RebuildTrackers invoked (raw {(raw?.Length ?? 0)} chars)");
                RebuildTrackers(raw ?? "");
                _lastParsedItemsRaw = raw ?? "";
                TraceOnce(512, $"trackers built: {_trackers.Count}");
            }

            if (!_subscribed)
            {
                TraceOnce(1024, "about to subscribe + log Tracking summary");
                em.AddListener<ItemConsumedEvent>(OnItemConsumed);
                em.AddListener<DayPassedEvent>(OnDayPassed);
                em.AddListener<MonthPassedEvent>(OnMonthPassed);
                _subscribed = true;
                _initialApplyDone = false;
                Plugin.Log.Msg($"[ConsumableControl] Tracking {_trackers.Count} item(s). Initial reserve applies on the first day with data; refreshes monthly.");
            }
        }

        private static void UnsubscribeIfSubscribed()
        {
            if (!_subscribed) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var em = gm?.eventManager;
            if (em != null)
            {
                em.RemoveListener<ItemConsumedEvent>(OnItemConsumed);
                em.RemoveListener<DayPassedEvent>(OnDayPassed);
                em.RemoveListener<MonthPassedEvent>(OnMonthPassed);
            }
            _subscribed = false;
        }

        private static void RebuildTrackers(string raw)
        {
            _trackers.Clear();
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var part in raw.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;
                if (Enum.TryParse<ItemID>(trimmed, ignoreCase: true, out var id) && id != ItemID.Unassigned && id != ItemID.MAX)
                {
                    if (!_trackers.ContainsKey(id))
                        _trackers[id] = new ConsumptionTracker();
                }
                else
                {
                    Plugin.Log.Warning($"[ConsumableControl] Unknown ItemID in cfg: '{trimmed}' (ignored)");
                }
            }
        }

        // ----- Event handlers -----

        private static void OnItemConsumed(ItemConsumedEvent evt)
        {
            if (evt?.item == null) return;
            var id = evt.item.itemID;
            if (_firstConsumed.Add(id))
            {
                bool tracked = _trackers.ContainsKey(id);
                try { Plugin.Log.Msg($"[ConsumableControl][trace] first ItemConsumedEvent: {id} (amount {evt.amountConsumed}, tracked={tracked})"); } catch { }
            }
            if (_trackers.TryGetValue(id, out var tracker))
                tracker.TodayTotal += evt.amountConsumed;
        }

        private static void OnDayPassed(DayPassedEvent evt)
        {
            // Daily: roll the rolling-average window. Cheap — one queue
            // enqueue/dequeue per tracked item. The regular recompute/push is
            // monthly (a 30-day rolling avg barely moves day-to-day), EXCEPT
            // for the one-time catch-up below so the reserve doesn't take up to
            // a month to first appear after you enable the feature.
            foreach (var t in _trackers.Values) t.RollDay();

            if (!_initialApplyDone && HasAnyData())
            {
                ApplyReserves("initial");
                _initialApplyDone = true;
            }

            // Read-only daily diagnostic so behaviour is observable without
            // waiting for the monthly apply tick.
            if (Config.InventoryDiagnostics.Value) LogDiagnosticSnapshot();
        }

        private static bool HasAnyData()
        {
            foreach (var t in _trackers.Values)
                if (t.Days > 0 && t.AverageDailyRate() > 0f) return true;
            return false;
        }

        /// <summary>
        /// Read-only: log each tracked item's observed rate and the reserve target
        /// it WOULD compute, alongside the min-quota currently set on storages.
        /// Does not change any quotas — purely for verification.
        /// </summary>
        private static void LogDiagnosticSnapshot()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm == null) return;

            int reserveMonths = Math.Max(1, Config.ConsumableReserveMonths.Value);
            float headroom = 1f + Math.Max(0, Math.Min(50, Config.ConsumableHeadroomPercent.Value)) / 100f;
            int foodStorageCount    = CollectFoodStorages(rm).Count;
            int generalStorageCount = CollectGeneralStorages(rm).Count;

            foreach (var kv in _trackers)
            {
                var tracker = kv.Value;
                float rate = tracker.AverageDailyRate();
                int target = (int)Math.Ceiling(rate * 30f * reserveMonths * headroom);
                int count = FoodItems.Contains(kv.Key) ? foodStorageCount : generalStorageCount;
                int perStorage = count > 0 ? (int)Math.Ceiling((double)target / count) : 0;
                Plugin.Log.Msg($"[ConsumableControl][diag] {kv.Key}: rate {rate:0.00}/day ({tracker.Days}d data), reserve target {target} ({reserveMonths}mo) → {perStorage}/storage × {count} storage(s).");
            }
        }

        private static void OnMonthPassed(MonthPassedEvent evt) => ApplyReserves("month");

        private static void ApplyReserves(string cause)
        {
            if (!Config.EnableConsumableControl.Value) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm == null) return;

            int reserveMonths = Math.Max(1, Config.ConsumableReserveMonths.Value);
            float headroom = 1f + Math.Max(0, Math.Min(50, Config.ConsumableHeadroomPercent.Value)) / 100f;

            var foodStorages    = CollectFoodStorages(rm);
            var generalStorages = CollectGeneralStorages(rm);

            int writtenCount = 0;
            int skippedCount = 0;
            foreach (var kv in _trackers)
            {
                var itemID = kv.Key;
                var tracker = kv.Value;
                if (tracker.Days == 0) continue;
                float dailyRate = tracker.AverageDailyRate();
                if (dailyRate <= 0f) continue;

                int totalTarget = (int)Math.Ceiling(dailyRate * 30f * reserveMonths * headroom);
                if (totalTarget <= 0) continue;

                var targets = FoodItems.Contains(itemID) ? foodStorages : generalStorages;
                if (targets.Count == 0) continue;

                int perStorage = (int)Math.Ceiling((double)totalTarget / targets.Count);

                if (Config.InventoryDiagnostics.Value)
                    Plugin.Log.Msg($"[ConsumableControl][diag] APPLY {itemID}: rate {dailyRate:0.00}/day → reserve {totalTarget} ({reserveMonths}mo), {perStorage}/storage × {targets.Count} storage(s).");

                foreach (var storage in targets)
                {
                    if (storage == null) continue;
                    var qh = storage.quotaHandler;
                    if (qh == null) continue;
                    var key = new StorageItemKey(storage.GetInstanceID(), itemID);

                    // Skip redundant writes when the value hasn't changed.
                    if (_lastWrittenValue.TryGetValue(key, out var prev) && prev == perStorage)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        qh.SetMinQuotaForItem(itemID, perStorage);
                        _writtenQuotas.Add(key);
                        _lastWrittenValue[key] = perStorage;
                        writtenCount++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[ConsumableControl] SetMinQuotaForItem({itemID}) on {storage.GetType().Name} failed: {ex.Message}");
                    }
                }
            }

            if (writtenCount > 0 || skippedCount > 0)
                Plugin.Log.Msg($"[ConsumableControl] {cause} apply: {writtenCount} quota update(s), {skippedCount} unchanged-skipped, across {foodStorages.Count} food + {generalStorages.Count} general storage(s).");
        }

        // ----- Storage collection -----

        /// <summary>Granaries + Root Cellars + Storehouses (T2 variants ride along
        /// because they share the same C# type as their T1 versions).</summary>
        private static List<StorageBuilding> CollectFoodStorages(ResourceManager rm)
        {
            var list = new List<StorageBuilding>();
            if (rm.granariesRO != null)
                foreach (var g in rm.granariesRO) if (g != null) list.Add(g);
            if (rm.rootCellarsRO != null)
                foreach (var c in rm.rootCellarsRO) if (c != null) list.Add(c);
            if (rm.storehousesRO != null)
                foreach (var s in rm.storehousesRO) if (s != null) list.Add(s);
            return list;
        }

        /// <summary>Storehouses only — for non-food items (firewood, clothing, etc).</summary>
        private static List<StorageBuilding> CollectGeneralStorages(ResourceManager rm)
        {
            var list = new List<StorageBuilding>();
            if (rm.storehousesRO != null)
                foreach (var s in rm.storehousesRO) if (s != null) list.Add(s);
            return list;
        }

        /// <summary>
        /// Clear only quotas EP wrote. Manual quotas (set by the player via FF's
        /// keep-in-stock UI on any item we didn't touch) are preserved.
        /// </summary>
        private static void ClearAllOurQuotas()
        {
            if (_writtenQuotas.Count == 0) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm != null)
            {
                var lookup = new Dictionary<int, StorageQuotaHandler>();
                if (rm.granariesRO != null)
                    foreach (var g in rm.granariesRO)
                        if (g?.quotaHandler != null) lookup[g.GetInstanceID()] = g.quotaHandler;
                if (rm.rootCellarsRO != null)
                    foreach (var c in rm.rootCellarsRO)
                        if (c?.quotaHandler != null) lookup[c.GetInstanceID()] = c.quotaHandler;
                if (rm.storehousesRO != null)
                    foreach (var s in rm.storehousesRO)
                        if (s?.quotaHandler != null) lookup[s.GetInstanceID()] = s.quotaHandler;

                foreach (var key in _writtenQuotas)
                {
                    if (!lookup.TryGetValue(key.StorageID, out var qh)) continue;
                    try { qh.ClearMinQuotaForItem(key.Item); }
                    catch { /* destroyed mid-frame — ignore */ }
                }
            }
            _writtenQuotas.Clear();
            _lastWrittenValue.Clear();
        }
    }
}
