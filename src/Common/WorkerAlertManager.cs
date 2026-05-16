// Shared infrastructure originally from FFQoL by idontcare (v1.0.0).
// Used by Features/LongTravels.cs and Features/IdleHands.cs.
// Each feature creates its own static WorkerAlertManager instance with a
// feature-specific metric, threshold, icon-sprite name, and tooltip.
//
// Behaviour mirrors FFQoL exactly:
//   - InitWidget clones the production-halted alert sprite from the widget,
//     swaps the icon to a feature-specific one, registers it via the widget's
//     DynamicSpriteRoot.AddDynamicSprite. The widget is then refresh-driven.
//   - ReleaseWidget tears the clone down on widget destroy.
//   - OnUpdate, called from Plugin.OnUpdate every frame, processes a rolling
//     batch (3 widgets/frame) of cached widgets via Refresh.
//   - Refresh reads the metric, applies the per-building 1-month hysteresis
//     (alert only shows once the metric has exceeded the threshold for >= 1
//     continuous month), and toggles the sprite's objectIsDisabled flag.
//   - Excludes Markets, Trading Posts, and Apiaries — they don't have
//     meaningful "worker travel time / idle time" semantics.

using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace EssentialProvisions.Common
{
    internal struct WidgetCache
    {
        internal Building Building;
        internal GameObject? Clone;
        internal DynamicSprite? Sprite;
        internal float LastMetricValue;
    }

    /// <summary>
    /// Threshold helper. Clamps the percent into [20, 80] and divides by 100
    /// so callers can compare a 0..1 metric against the user's slider value.
    /// </summary>
    internal static class AlertThreshold
    {
        internal static float ToFraction(int thresholdPercent)
            => Math.Max(20, Math.Min(80, thresholdPercent)) / 100f;

        internal static bool ShouldShow(float value, int thresholdPercent)
            => value > ToFraction(thresholdPercent);
    }

    /// <summary>
    /// Round-robin batched poll. Add/Remove tracked items; ProcessNextBatch
    /// processes a fixed-size slice each call, advancing the index. Cheap to
    /// drive from per-frame Update without spending the whole frame on a
    /// 200-building map.
    /// </summary>
    internal sealed class AlertPollState<T> where T : class
    {
        private readonly List<T> _list = new List<T>();
        private readonly int _batchSize;
        private int _index;

        internal int Count => _list.Count;

        internal AlertPollState(int batchSize) { _batchSize = batchSize; }

        internal void Add(T item) { _list.Add(item); }

        internal bool Remove(T item)
        {
            int idx = _list.IndexOf(item);
            if (idx < 0) return false;
            int last = _list.Count - 1;
            if (idx != last) _list[idx] = _list[last];
            _list.RemoveAt(last);
            if (_index >= _list.Count) _index = 0;
            return true;
        }

        internal void ProcessNextBatch(Action<T> process)
        {
            int count = _list.Count;
            if (count == 0) return;
            if (_index >= count) _index = 0;
            int end = Math.Min(_index + _batchSize, count);
            for (int i = _index; i < end; i++) process(_list[i]);
            _index = (end < count) ? end : 0;
        }

        internal void Clear() { _list.Clear(); _index = 0; }
    }

    internal sealed class WorkerAlertManager
    {
        // Reflected fields on UIBuildingSubWidgetAlerts. FFQoL discovered
        // these private names work cross-version; we cache them once and
        // reuse for the lifetime of the AppDomain.
        private static readonly FieldInfo? BuildingField =
            typeof(UIBuildingSubWidgetAlerts).GetField("building",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ProductionHaltedParentField =
            typeof(UIBuildingSubWidgetAlerts).GetField("productionHaltedParent",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? NoWorkersParentField =
            typeof(UIBuildingSubWidgetAlerts).GetField("noWorkersParent",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly string _renderID;
        private readonly string _iconSpriteName;
        private readonly string _alertCloneName;
        private readonly Func<Building, float> _getMetric;
        private readonly Func<int>  _getThreshold;
        private readonly Func<bool> _isEnabled;
        private readonly string _tooltipPrefix;
        private readonly Func<CEDateTime?> _getClock;

        private Sprite? _iconSprite;
        private bool _spriteSearchFailed;
        private bool _allHidden;

        private readonly Dictionary<UIBuildingSubWidgetAlerts, WidgetCache> _cache
            = new Dictionary<UIBuildingSubWidgetAlerts, WidgetCache>();
        private readonly AlertPollState<UIBuildingSubWidgetAlerts> _pollState;
        private readonly Dictionary<Building, CEDateTime> _thresholdOnset
            = new Dictionary<Building, CEDateTime>();

        internal WorkerAlertManager(
            int batchSize,
            string renderID,
            string iconSpriteName,
            string alertCloneName,
            Func<Building, float> getMetric,
            Func<int> getThreshold,
            Func<bool> isEnabled,
            string tooltipPrefix)
        {
            _renderID       = renderID;
            _iconSpriteName = iconSpriteName;
            _alertCloneName = alertCloneName;
            _getMetric      = getMetric;
            _getThreshold   = getThreshold;
            _isEnabled      = isEnabled;
            _tooltipPrefix  = tooltipPrefix;
            _pollState      = new AlertPollState<UIBuildingSubWidgetAlerts>(batchSize);
            _getClock       = DefaultGetClock;
        }

        private static CEDateTime? DefaultGetClock()
        {
            var gm = UnitySingleton<GameManager>.Instance;
            var tm = gm?.timeManager;
            if (tm == null) return null;
            return tm.currentDate;
        }

        internal void ClearCache()
        {
            _cache.Clear();
            _pollState.Clear();
            _iconSprite         = null;
            _spriteSearchFailed = false;
            _allHidden          = false;
            _thresholdOnset.Clear();
        }

        internal void OnUpdate()
        {
            if (!_isEnabled())
            {
                HideAll();
                return;
            }
            _allHidden = false;
            _pollState.ProcessNextBatch(Refresh);
        }

        private void HideAll()
        {
            if (_allHidden) return;
            foreach (var entry in _cache.Values)
            {
                if (entry.Sprite != null)
                {
                    if (!entry.Sprite.objectIsDisabled)
                    {
                        entry.Sprite.objectIsDisabled = true;
                        entry.Sprite.bIsDirty = true;
                    }
                }
                else if (entry.Clone != null)
                {
                    entry.Clone.SetActive(false);
                }
            }
            _allHidden = true;
        }

        private void Refresh(UIBuildingSubWidgetAlerts widget)
        {
            if (!_cache.TryGetValue(widget, out var cached) || cached.Clone == null) return;

            float value = _getMetric(cached.Building);
            cached.LastMetricValue = value;
            _cache[widget] = cached;

            bool show = UpdateOnsetAndShouldShow(
                cached.Building,
                AlertThreshold.ShouldShow(value, _getThreshold()));

            if (cached.Sprite != null)
            {
                bool disabled = !show;
                if (cached.Sprite.objectIsDisabled != disabled)
                {
                    cached.Sprite.objectIsDisabled = disabled;
                    cached.Sprite.bIsDirty = true;
                }
            }
            else
            {
                cached.Clone.SetActive(show);
            }
        }

        /// <summary>
        /// 1-month hysteresis. The alert only fires once the building has
        /// continuously breached the threshold for >= 1 month — prevents
        /// flickering on borderline buildings whose metric drifts around
        /// the threshold daily.
        /// </summary>
        private bool UpdateOnsetAndShouldShow(Building building, bool exceedsThreshold)
        {
            if (!exceedsThreshold)
            {
                _thresholdOnset.Remove(building);
                return false;
            }
            var now = _getClock();
            if (!now.HasValue) return false;
            if (!_thresholdOnset.ContainsKey(building))
            {
                _thresholdOnset[building] = now.Value;
            }
            var span = now.Value - _thresholdOnset[building];
            return span.totalMonths >= 1f;
        }

        private void TryLoadSprite()
        {
            if (_iconSprite != null || _spriteSearchFailed) return;
            var allSprites = Resources.FindObjectsOfTypeAll<Sprite>();
            foreach (var sprite in allSprites)
            {
                if (sprite != null && sprite.name == _iconSpriteName)
                {
                    _iconSprite = sprite;
                    return;
                }
            }
            _spriteSearchFailed = true;
            MelonLogger.Warning(
                $"[{_alertCloneName}] Icon sprite '{_iconSpriteName}' not found in Resources — alert icons will not appear.");
        }

        internal void InitWidget(UIBuildingSubWidgetAlerts widget)
        {
            if (!_isEnabled() || BuildingField == null) return;

            TryLoadSprite();
            if (_iconSprite == null) return;

            var building = BuildingField.GetValue(widget) as Building;
            if (building == null) return;
            if (building.workersRO == null) return;
            // FFQoL parity: skip non-worker buildings whose "travel/idle" metrics are meaningless
            if (building is MarketBuilding) return;
            if (building is TradingPost)    return;
            if (building is Apiary)         return;

            // If we already have a cached widget (re-Init after game reload), tear it down first.
            bool hadExisting = _cache.ContainsKey(widget);
            if (hadExisting)
            {
                var prev = _cache[widget];
                if (prev.Sprite != null && ((UISubWidget)widget).dynamicSpriteRoot != null)
                {
                    ((UISubWidget)widget).dynamicSpriteRoot.RemoveDynamicSprite(prev.Sprite);
                }
                if (prev.Clone != null) UnityEngine.Object.Destroy(prev.Clone);
            }

            // Source GameObject to clone — prefer productionHaltedParent (the wagon-style
            // alert template), fall back to noWorkersParent. Either gives us a working
            // alert visual we can re-skin with our icon.
            var template = (ProductionHaltedParentField?.GetValue(widget) as GameObject)
                        ?? (NoWorkersParentField?.GetValue(widget) as GameObject);
            if (template == null) return;

            var clone = UnityEngine.Object.Instantiate(template, ((Component)widget).transform);
            clone.name = _alertCloneName;

            var dynSprite = clone.GetComponentInChildren<DynamicSprite>(true)
                         ?? clone.GetComponent<DynamicSprite>();
            if (dynSprite != null)
            {
                // Swap any ICN_-prefixed sprite on Image components in the clone with our icon.
                var images = clone.GetComponentsInChildren<Image>(true);
                foreach (var img in images)
                {
                    if (img.sprite != null && img.sprite.name.StartsWith("ICN_"))
                    {
                        img.sprite = _iconSprite;
                        img.color = Color.white;
                    }
                }
                dynSprite.renderID    = _renderID;
                dynSprite.bIsDirty    = true;
                dynSprite.spriteColor = Color.white;
            }

            // Tooltip: best-effort. If the cloned GO has a tooltip provider, set its rows.
            // We don't crash if the provider type isn't available — alert visual still works.
            TrySetupTooltip(clone, building);

            var spriteForWidget = clone.GetComponentInChildren<DynamicSprite>(true);
            var spriteRoot      = ((UISubWidget)widget).dynamicSpriteRoot;
            if (spriteForWidget != null && spriteRoot != null)
            {
                spriteRoot.AddDynamicSprite(spriteForWidget, true);
                spriteForWidget.objectIsDisabled = true; // start hidden; Refresh will toggle
            }
            else
            {
                clone.SetActive(false);
            }

            _cache[widget] = new WidgetCache {
                Building = building,
                Clone    = clone,
                Sprite   = spriteForWidget,
            };
            if (!hadExisting) _pollState.Add(widget);
        }

        internal void ReleaseWidget(UIBuildingSubWidgetAlerts widget)
        {
            if (!_cache.TryGetValue(widget, out var cached)) return;

            if (cached.Sprite != null && ((UISubWidget)widget).dynamicSpriteRoot != null)
            {
                ((UISubWidget)widget).dynamicSpriteRoot.RemoveDynamicSprite(cached.Sprite);
            }
            if (cached.Clone != null) UnityEngine.Object.Destroy(cached.Clone);
            _cache.Remove(widget);
            _pollState.Remove(widget);
        }

        /// <summary>
        /// The cloned alert template (productionHaltedParent or noWorkersParent)
        /// carries its source's tooltip data, but more importantly it carries
        /// a live `OnLocalize` event subscription that re-populates the row
        /// lists from `tooltipRowKeyLocalizationTags` after we clear them.
        /// So one-shot clearing doesn't stick — the localization callback fires
        /// and brings "Production Halted" / "no workers" text right back.
        ///
        /// The fix that does stick is to hook `onPreSendProviderToReceiver` —
        /// it's invoked by FF moments before the tooltip is displayed, after
        /// any localization callbacks have run. We clear + add our row from
        /// there, so the displayed text is always our row regardless of what
        /// localization did. Same pattern Soil Wisdom uses for its hover rows.
        /// </summary>
        private void TrySetupTooltip(GameObject clone, Building building)
        {
            try
            {
                var provider = clone.GetComponentInChildren<GenericTooltipDataProvider>(true);
                if (provider == null) return;

                // Initial population so the first hover (before any localization
                // event has fired) shows our row, not empty.
                provider.toolTipRowKeyNames.Clear();
                provider.toolTipRowValues.Clear();
                provider.AddKeyValue(_tooltipPrefix, "");

                // The decisive fix: replace the pre-send hook so the row list
                // is overwritten with our row every time the tooltip is about
                // to be shown. Closes over the provider + prefix.
                var captured = provider;
                var prefix   = _tooltipPrefix;
                provider.onPreSendProviderToReceiver = () =>
                {
                    try
                    {
                        captured.toolTipRowKeyNames.Clear();
                        captured.toolTipRowValues.Clear();
                        captured.AddKeyValue(prefix, "");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[{_alertCloneName}] tooltip pre-send: {ex.Message}");
                    }
                };
            }
            catch (Exception ex)
            {
                // Tooltip is decoration; never let a missing/changed game type
                // turn into a crash that hides the alert.
                MelonLogger.Warning($"[{_alertCloneName}] tooltip setup: {ex.Message}");
            }
        }
    }
}
