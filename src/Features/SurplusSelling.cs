// Surplus Selling — auto-shunt excess stock above a keep-in-town threshold
// to trading posts. Equivalent to FFAutomation's "Auto Stock" but driven by
// observed consumption rate × months × headroom instead of fixed per-item ints.
//
// Pairs with Consumable Control:
//   - Consumable Control sets min quota on granaries/cellars/storehouses,
//     protecting a reserve from logistics outflow.
//   - Surplus Selling sets min quota on TRADING POSTS, drawing excess away
//     from town storages (which can only release what's above their own
//     reserve quota). Net effect: items flow granary → trading post when
//     town stock exceeds the keep-in-town target.
//
// Food handling:
//   - Default: skip food. FF rewards 10+ months food surplus with immigration;
//     auto-shipping food at lower thresholds suppresses that.
//   - Opt-in food sub-toggle adds two safety nets:
//       (1) aggregate: total_food_stock must exceed aggregate_target before
//           ANY food gets shipped
//       (2) per-type: each food item stays at ≥ 1 month of its own consumption
//           even if aggregate allows shipping
//     Prevents shipping bread out while berries / fruit are scarce → diet
//     diversity stays intact, scurvy etc. avoided.
//
// Mechanism: daily DayPassedEvent → compute target_tp_holding per item →
// SetMinQuotaForItem on each trading post.

using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class SurplusSelling
    {
        // Food classification — must match ConsumableControl's set exactly.
        // (Slightly redundant; could be promoted to Common/ later if it diverges.)
        private static readonly HashSet<ItemID> FoodItems = new HashSet<ItemID>
        {
            ItemID.Bread, ItemID.RootVegetable, ItemID.Beans, ItemID.Greens,
            ItemID.Berries, ItemID.Fruit, ItemID.Nuts, ItemID.Mushroom,
            ItemID.Roots, ItemID.Honey, ItemID.Eggs, ItemID.Fish, ItemID.SmokedFish,
            ItemID.Meat, ItemID.SmokedMeat, ItemID.Cheese, ItemID.Pastry,
            ItemID.Preserves, ItemID.PreservedVeg,
        };

        // (Trading post instance ID, ItemID) we've written. Cleanup on disable
        // touches only these; manual quotas elsewhere preserved.
        private struct PostItemKey : IEquatable<PostItemKey>
        {
            public int PostID;
            public ItemID Item;
            public PostItemKey(int p, ItemID i) { PostID = p; Item = i; }
            public bool Equals(PostItemKey o) => PostID == o.PostID && Item == o.Item;
            public override bool Equals(object o) => o is PostItemKey k && Equals(k);
            public override int GetHashCode() => unchecked(PostID * 397 ^ (int)Item);
        }
        private static readonly HashSet<PostItemKey> _writtenQuotas
            = new HashSet<PostItemKey>();

        private static bool _subscribed;
        private static bool _wasEnabled;

        public static void Reset()
        {
            UnsubscribeIfSubscribed();
            ClearAllOurQuotas();
            _wasEnabled = false;
        }

        public static void OnUpdate()
        {
            bool enabled = Config.EnableSurplusSelling.Value;

            if (!enabled && _wasEnabled)
            {
                ClearAllOurQuotas();
                UnsubscribeIfSubscribed();
            }
            _wasEnabled = enabled;
            if (!enabled) return;

            if (!GameManager.gameReadyToPlay) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var em = gm?.eventManager;
            if (em == null) return;

            if (!_subscribed)
            {
                em.AddListener<DayPassedEvent>(OnDayPassed);
                _subscribed = true;
                Plugin.Log.Msg("[SurplusSelling] Active. Excess above keep-in-town target will flow to trading post(s) on each day-tick.");
            }
        }

        private static void UnsubscribeIfSubscribed()
        {
            if (!_subscribed) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var em = gm?.eventManager;
            em?.RemoveListener<DayPassedEvent>(OnDayPassed);
            _subscribed = false;
        }

        // ----- Day tick: compute targets, push quotas -----

        private static void OnDayPassed(DayPassedEvent evt)
        {
            if (!Config.EnableSurplusSelling.Value) return;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var rm = gm.resourceManager;
            if (rm == null) return;
            var posts = rm.tradingPostsRO;
            if (posts == null || posts.Count == 0) return;

            int nonFoodMonths = Math.Max(1, Config.SurplusSellingMonths.Value);
            int foodMonths    = Math.Max(1, Config.SurplusSellingFoodMonths.Value);
            float headroom    = 1f + Math.Max(0, Math.Min(50, Config.ConsumableHeadroomPercent.Value)) / 100f;
            bool foodEnabled  = Config.EnableSurplusSellingFood.Value;

            // Pull the rate tracker state out of ConsumableControl. Surplus
            // Selling shares trackers — no point maintaining duplicates.
            var rates = ConsumableControl.GetCurrentRatesSnapshot();
            if (rates == null || rates.Count == 0) return;

            // Food aggregate safety net: if total food stock < aggregate target,
            // skip ALL food this tick. Prevents shipping bread out while berries
            // are scarce — keeps diet variety intact.
            bool foodAggregateOK = false;
            if (foodEnabled)
            {
                float totalFoodRate = 0f;
                int   totalFoodStock = 0;
                foreach (var kv in rates)
                {
                    if (!FoodItems.Contains(kv.Key)) continue;
                    totalFoodRate += kv.Value;
                    totalFoodStock += GetTownStock(rm, kv.Key);
                }
                int aggregateTarget = (int)Math.Ceiling(totalFoodRate * 30f * foodMonths * headroom);
                foodAggregateOK = totalFoodStock >= aggregateTarget;
                if (!foodAggregateOK)
                {
                    Plugin.Log.Msg($"[SurplusSelling] Food aggregate {totalFoodStock} below target {aggregateTarget}; skipping food this tick.");
                }
            }

            // Items we previously wrote that we're NOT writing this tick →
            // need to clear those quotas so they don't stay stuck.
            var stillRelevant = new HashSet<PostItemKey>();

            int updates = 0;
            foreach (var kv in rates)
            {
                ItemID itemID = kv.Key;
                float dailyRate = kv.Value;
                if (dailyRate <= 0f) continue;

                bool isFood = FoodItems.Contains(itemID);
                if (isFood && !foodEnabled) continue;
                if (isFood && !foodAggregateOK) continue;

                int months = isFood ? foodMonths : nonFoodMonths;
                int keepInTown = (int)Math.Ceiling(dailyRate * 30f * months * headroom);

                // Per-type variety floor for food: never ship a food item below
                // 1 month of its own consumption × headroom, even if aggregate allows.
                if (isFood)
                {
                    int perTypeFloor = (int)Math.Ceiling(dailyRate * 30f * 1f * headroom);
                    if (perTypeFloor > keepInTown) keepInTown = perTypeFloor;
                }

                int totalTown = GetTownStock(rm, itemID);
                int excess = totalTown - keepInTown;
                if (excess <= 0) continue;

                // Target TP holding = current TP stock + excess to draw out.
                // Vanilla FF allows one trading post per town, so distribution
                // is trivial; the loop below is defensive in case mods or a
                // future patch allow more (each post then gets the full target,
                // not divided, since they each independently pull what they need).
                if (!gm.workBucketManager.itemByItemIDRO.TryGetValue(itemID, out var itemObjForTp)) continue;
                int currentTpStock = 0;
                for (int i = 0; i < posts.Count; i++)
                {
                    var p = posts[i];
                    if (p == null) continue;
                    try { currentTpStock += (int)p.storage.GetItemCount(itemObjForTp); }
                    catch { /* tolerate */ }
                }
                int targetTpHolding = currentTpStock + excess;

                for (int i = 0; i < posts.Count; i++)
                {
                    var post = posts[i];
                    if (post == null) continue;
                    var qh = post.quotaHandler;
                    if (qh == null) continue;
                    try
                    {
                        qh.SetMinQuotaForItem(itemID, targetTpHolding);
                        var key = new PostItemKey(post.GetInstanceID(), itemID);
                        _writtenQuotas.Add(key);
                        stillRelevant.Add(key);
                        updates++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[SurplusSelling] SetMinQuotaForItem({itemID}) failed: {ex.Message}");
                    }
                }
            }

            // Sweep: clear quotas we wrote previously but didn't refresh this
            // tick (item no longer in surplus, or rate dropped to zero).
            var toClear = new List<PostItemKey>();
            foreach (var key in _writtenQuotas)
                if (!stillRelevant.Contains(key)) toClear.Add(key);
            if (toClear.Count > 0)
            {
                var lookup = BuildTradingPostLookup(rm);
                foreach (var key in toClear)
                {
                    _writtenQuotas.Remove(key);
                    if (!lookup.TryGetValue(key.PostID, out var qh)) continue;
                    try { qh.ClearMinQuotaForItem(key.Item); }
                    catch { /* tolerate */ }
                }
            }

            if (updates > 0 || toClear.Count > 0)
                Plugin.Log.Msg($"[SurplusSelling] Day tick: +{updates} quota update(s), -{toClear.Count} cleared, {_writtenQuotas.Count} active.");
        }

        // ----- Helpers -----

        /// <summary>Sum item count across all granaries + cellars + storehouses + trading posts.</summary>
        private static int GetTownStock(ResourceManager rm, ItemID itemID)
        {
            int total = 0;
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return 0;
            if (!gm.workBucketManager.itemByItemIDRO.TryGetValue(itemID, out var itemObj))
                return 0;

            if (rm.granariesRO != null)
                foreach (var g in rm.granariesRO)
                    if (g?.storage != null) total += (int)g.storage.GetItemCount(itemObj);
            if (rm.rootCellarsRO != null)
                foreach (var c in rm.rootCellarsRO)
                    if (c?.storage != null) total += (int)c.storage.GetItemCount(itemObj);
            if (rm.storehousesRO != null)
                foreach (var s in rm.storehousesRO)
                    if (s?.storage != null) total += (int)s.storage.GetItemCount(itemObj);
            if (rm.tradingPostsRO != null)
                foreach (var t in rm.tradingPostsRO)
                    if (t?.storage != null) total += (int)t.storage.GetItemCount(itemObj);
            return total;
        }

        private static Dictionary<int, StorageQuotaHandler> BuildTradingPostLookup(ResourceManager rm)
        {
            var lookup = new Dictionary<int, StorageQuotaHandler>();
            if (rm.tradingPostsRO != null)
                foreach (var p in rm.tradingPostsRO)
                    if (p?.quotaHandler != null) lookup[p.GetInstanceID()] = p.quotaHandler;
            return lookup;
        }

        private static void ClearAllOurQuotas()
        {
            if (_writtenQuotas.Count == 0) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm != null)
            {
                var lookup = BuildTradingPostLookup(rm);
                foreach (var key in _writtenQuotas)
                {
                    if (!lookup.TryGetValue(key.PostID, out var qh)) continue;
                    try { qh.ClearMinQuotaForItem(key.Item); }
                    catch { /* tolerate */ }
                }
            }
            _writtenQuotas.Clear();
        }
    }
}
