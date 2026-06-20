// Surplus Selling — auto-shunt excess stock above a keep-in-town threshold
// to trading posts. Equivalent to FFAutomation's "Auto Stock" but driven by
// observed consumption rate × months × headroom instead of fixed per-item ints.
//
// IMPORTANT — uses the trading post's NATIVE stocking lever, not storage quotas:
//   The trading post does NOT stock via StorageQuotaHandler min-quotas (that's
//   the storage-building reserve system). It stocks via its own targetStockCounts
//   + keepInStock toggle (TradingPost.SetTargetStockAmount / SetKeepInStock).
//   CheckWorkAvailabilityForTraderStockingItem creates the "stock the post" haul
//   request only when targetStockCounts[item] != 0, hauling the item into
//   traderStorage from anywhere in town. Worse, the post's own ProcessQuotasForItem
//   zeroes any min-quota we set. So we set the post's target stock directly.
//
//   target_post_holding = total_town_stock − keep_in_town
//   (SetTargetStockAmount is ABSOLUTE — the post holds up to this many — so the
//   target already accounts for what's in the post; no "+ current post stock".)
//   At equilibrium storage holds keep_in_town and the post holds the surplus.
//
// Authority: while enabled, Surplus Selling OWNS the target-stock + keep-in-stock
// setting for every item it manages. It overwrites manual settings on those items
// each day-tick. To hand-manage an item, keep Surplus Selling disabled.
//
// Food handling:
//   - Default: skip food. FF rewards 10+ months food surplus with immigration.
//   - Opt-in food sub-toggle adds two safety nets:
//       (1) aggregate: total food stock must exceed an aggregate target before
//           ANY food gets shipped;
//       (2) per-type: each food item stays at ≥ 1 month of its own consumption.
//
// Mechanism: daily DayPassedEvent → compute target per item → SetTargetStockAmount
// + SetKeepInStock(true) on each trading post (split across posts if modded to >1).

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
        private static readonly HashSet<ItemID> FoodItems = new HashSet<ItemID>
        {
            ItemID.Bread, ItemID.RootVegetable, ItemID.Beans, ItemID.Greens,
            ItemID.Berries, ItemID.Fruit, ItemID.Nuts, ItemID.Mushroom,
            ItemID.Roots, ItemID.Honey, ItemID.Eggs, ItemID.Fish, ItemID.SmokedFish,
            ItemID.Meat, ItemID.SmokedMeat, ItemID.Cheese, ItemID.Pastry,
            ItemID.Preserves, ItemID.PreservedVeg,
        };

        // (Trading post instance ID, ItemID) whose target-stock we set. On
        // disable / sweep we reset only these back to 0 + keep-in-stock false.
        private struct PostItemKey : IEquatable<PostItemKey>
        {
            public int PostID;
            public ItemID Item;
            public PostItemKey(int p, ItemID i) { PostID = p; Item = i; }
            public bool Equals(PostItemKey o) => PostID == o.PostID && Item == o.Item;
            public override bool Equals(object o) => o is PostItemKey k && Equals(k);
            public override int GetHashCode() => unchecked(PostID * 397 ^ (int)Item);
        }
        private static readonly HashSet<PostItemKey> _managed = new HashSet<PostItemKey>();

        private static bool _subscribed;
        private static bool _wasEnabled;

        // Poll throttle: DayPassedEvent fires daily, but the 30-day rolling
        // average barely moves day-to-day, so we only recompute every N days
        // (Config.SurplusSellingPollDays). _ranOnce makes the first tick after
        // enabling run immediately, then the cadence kicks in.
        private static int _dayCounter;
        private static bool _ranOnce;

        // One-shot guard for the "enable Consumable Control too" warning, so it's
        // surfaced once per session instead of every day-tick.
        private static bool _warnedNoData;

        public static void Reset()
        {
            UnsubscribeIfSubscribed();
            ClearAllManaged();
            _wasEnabled = false;
            _dayCounter = 0;
            _ranOnce = false;
            _warnedNoData = false;
        }

        public static void OnUpdate()
        {
            bool enabled = Config.EnableSurplusSelling.Value;

            if (!enabled && _wasEnabled)
            {
                ClearAllManaged();
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
                _dayCounter = 0;
                _ranOnce = false;
                Plugin.Log.Msg($"[SurplusSelling] Active. Excess above keep-in-town target is set as trading-post stock; recomputed every {Math.Max(1, Config.SurplusSellingPollDays.Value)} day(s).");
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

        // ----- Day tick: compute targets, set trading-post stock -----

        private static void OnDayPassed(DayPassedEvent evt)
        {
            if (!Config.EnableSurplusSelling.Value) return;

            // Don't let the poll throttle advance until there's actually work to do
            // — a trading post to stock AND consumption-rate data to size it. If we
            // counted ticks (or spent the "first run immediately" budget) while no
            // post exists, building one later would wait up to a full interval
            // before the first stocking pass. Gate on those first, throttle second.
            var gm = UnitySingleton<GameManager>.Instance;
            if (gm == null) return;
            var rm = gm.resourceManager;
            if (rm == null) return;
            var posts = rm.tradingPostsRO;
            if (posts == null || posts.Count == 0) return;

            bool diag = Config.InventoryDiagnostics.Value;

            // Share ConsumableControl's rate trackers — no duplicate telemetry.
            var rates = ConsumableControl.GetCurrentRatesSnapshot();
            if (rates == null || rates.Count == 0)
            {
                // No rate data → reset any posts we were managing, so stale target-stock +
                // keep-in-stock settings don't keep generating haul-to-post requests
                // forever (e.g. the player cleared the tracked-items list while a post was
                // being managed). No-op once cleared.
                if (_managed.Count > 0) ClearAllManaged();

                // Surplus Selling can't size shipments without Consumable Control's
                // consumption telemetry. If CC is off, say so once (not diag-gated) so the
                // dependency isn't silent on the most likely first-time use.
                if (!Config.EnableConsumableControl.Value && !_warnedNoData)
                {
                    _warnedNoData = true;
                    Plugin.Log.Warning("[SurplusSelling] No consumption data — enable Consumable Control too; it gathers the daily-rate data Surplus Selling needs to size shipments.");
                }
                else if (diag)
                {
                    Plugin.Log.Msg("[SurplusSelling][diag] No consumption rate data yet (is Consumable Control tracking items?). Nothing to do.");
                }
                return;
            }

            // Throttle: run on the first qualifying tick after enabling, then every
            // N days. The expensive work (per-item town-stock walk + post writes)
            // is below this gate; the cheap presence/rate checks above are not, so
            // a freshly-built post gets its first stocking pass on the next tick.
            int interval = Math.Max(1, Config.SurplusSellingPollDays.Value);
            _dayCounter++;
            if (_ranOnce && _dayCounter < interval) return;
            _ranOnce = true;
            _dayCounter = 0;

            int nonFoodMonths = Math.Max(1, Config.SurplusSellingMonths.Value);
            int foodMonths    = Math.Max(1, Config.SurplusSellingFoodMonths.Value);
            float headroom    = 1f + Math.Max(0, Math.Min(50, Config.ConsumableHeadroomPercent.Value)) / 100f;
            bool foodEnabled  = Config.EnableSurplusSellingFood.Value;

            int postCount = posts.Count(p => p != null);
            if (postCount == 0) return;

            // Food aggregate safety net: skip ALL food this tick if total food
            // stock is below the aggregate target.
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
                if (diag || !foodAggregateOK)
                    Plugin.Log.Msg($"[SurplusSelling]{(foodAggregateOK ? "[diag]" : "")} Food aggregate {totalFoodStock} vs target {aggregateTarget} → {(foodAggregateOK ? "shipping food allowed" : "skipping food this tick")}.");
            }

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

                // Per-type variety floor for food: never let storage drop below
                // 1 month of this item's own consumption, even if months were < that.
                if (isFood)
                {
                    int perTypeFloor = (int)Math.Ceiling(dailyRate * 30f * 1f * headroom);
                    if (perTypeFloor > keepInTown) keepInTown = perTypeFloor;
                }

                if (!gm.workBucketManager.itemByItemIDRO.TryGetValue(itemID, out var itemObj)) continue;

                int townStock = GetTownStock(rm, itemID);
                int surplus = townStock - keepInTown;

                // SetTargetStockAmount is ABSOLUTE — the post should hold the
                // surplus; storage keeps keepInTown. No "+ current post stock".
                int targetTotal = Math.Max(0, surplus);
                int perPost = (int)Math.Ceiling((double)targetTotal / postCount);

                int storageStock = townStock - GetPostStock(rm, itemObj);

                if (diag)
                    Plugin.Log.Msg($"[SurplusSelling][diag] {itemID}: rate {dailyRate:0.00}/day, keep {keepInTown} ({months}mo), town {townStock} (storage {storageStock} + post {townStock - storageStock}), surplus {surplus} → post target {(targetTotal > 0 ? perPost.ToString() : "0 (clear)")}");

                if (targetTotal <= 0)
                    continue; // not in surplus → handled by the sweep below (cleared)

                for (int i = 0; i < posts.Count; i++)
                {
                    var post = posts[i];
                    if (post == null) continue;
                    // Only manage items the post can actually trade.
                    if (post.keepInStockDict == null || !post.keepInStockDict.ContainsKey(itemObj.name)) continue;
                    try
                    {
                        post.SetTargetStockAmount(itemObj, (uint)perPost);
                        post.SetKeepInStock(itemObj, true);
                        var key = new PostItemKey(post.GetInstanceID(), itemID);
                        _managed.Add(key);
                        stillRelevant.Add(key);
                        updates++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Warning($"[SurplusSelling] SetTargetStockAmount({itemID}) failed: {ex.Message}");
                    }
                }
            }

            // Sweep: items we managed last tick but not this one (no longer in
            // surplus, rate dropped) → reset their post target to 0.
            var toClear = new List<PostItemKey>();
            foreach (var key in _managed)
                if (!stillRelevant.Contains(key)) toClear.Add(key);
            if (toClear.Count > 0)
            {
                var postLookup = BuildPostLookup(rm);
                foreach (var key in toClear)
                {
                    _managed.Remove(key);
                    if (!postLookup.TryGetValue(key.PostID, out var post)) continue;
                    if (!gm.workBucketManager.itemByItemIDRO.TryGetValue(key.Item, out var itemObj)) continue;
                    ResetPostItem(post, itemObj);
                }
            }

            if (updates > 0 || toClear.Count > 0)
                Plugin.Log.Msg($"[SurplusSelling] Day tick: {updates} post target(s) set, {toClear.Count} cleared, {_managed.Count} active.");
        }

        // ----- Helpers -----

        /// <summary>Total town stock: granaries + cellars + storehouses + trading-post traderStorage.</summary>
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
            total += GetPostStock(rm, itemObj);
            return total;
        }

        /// <summary>Sum of an item across every trading post's traderStorage (the trade inventory).</summary>
        private static int GetPostStock(ResourceManager rm, Item itemObj)
        {
            int total = 0;
            if (rm.tradingPostsRO != null)
                foreach (var t in rm.tradingPostsRO)
                    if (t?.traderStorage != null) total += (int)t.traderStorage.GetItemCount(itemObj);
            return total;
        }

        private static Dictionary<int, TradingPost> BuildPostLookup(ResourceManager rm)
        {
            var lookup = new Dictionary<int, TradingPost>();
            if (rm.tradingPostsRO != null)
                foreach (var p in rm.tradingPostsRO)
                    if (p != null) lookup[p.GetInstanceID()] = p;
            return lookup;
        }

        private static void ResetPostItem(TradingPost post, Item itemObj)
        {
            try
            {
                post.SetTargetStockAmount(itemObj, 0u);
                post.SetKeepInStock(itemObj, false);
            }
            catch { /* tolerate teardown */ }
        }

        private static void ClearAllManaged()
        {
            if (_managed.Count == 0) return;
            var gm = UnitySingleton<GameManager>.Instance;
            var rm = gm?.resourceManager;
            if (rm != null)
            {
                var postLookup = BuildPostLookup(rm);
                foreach (var key in _managed)
                {
                    if (!postLookup.TryGetValue(key.PostID, out var post)) continue;
                    if (gm!.workBucketManager.itemByItemIDRO.TryGetValue(key.Item, out var itemObj))
                        ResetPostItem(post, itemObj);
                }
            }
            _managed.Clear();
        }
    }
}
