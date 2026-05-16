// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableCropDiseaseAlert (bool, default false)
// EP changes: section renamed "Blight Watch"; pref key renamed EnableBlightWatch.
//             Behaviour unchanged — pins FF's existing disease alert visible on
//             crop fields until the player addresses the disease.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Pin the existing FF crop-field disease alert so it stays visible
    /// while a disease is active, instead of fading after the initial event.
    /// We don't create a new visual — FF already has the disease alert built
    /// into UISubWidgetCropFieldAlerts (`diseaseParent` GameObject + its
    /// DynamicSprite). This feature just keeps the widget reflecting the
    /// crop field's actual disease state via a 2-second poll, plus an event
    /// hook on the game's own disease-state-change call.
    /// </summary>
    internal static class BlightWatch
    {
        private struct WidgetCache
        {
            internal Cropfield? CropField;
            internal GameObject? DiseaseParent;
            internal DynamicSprite? DiseaseSprite;
        }

        // Reflected fields — cached once, reused.
        private static readonly FieldInfo? _cropFieldField =
            typeof(UISubWidgetCropFieldAlerts).GetField("cropField",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? _diseaseParentField =
            typeof(UISubWidgetCropFieldAlerts).GetField("diseaseParent",
                BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? _hasLostCropsToDiseaseField =
            typeof(UISubWidgetCropFieldAlerts).GetField("hasLostCropsToDisease",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly Dictionary<UISubWidgetCropFieldAlerts, WidgetCache> _cache
            = new Dictionary<UISubWidgetCropFieldAlerts, WidgetCache>();

        private static float _nextPollTime;

        public static void Reset()
        {
            _cache.Clear();
            _nextPollTime = 0f;
        }

        public static void OnUpdate()
        {
            if (!Config.EnableBlightWatch.Value) return;
            if (Plugin.IsForeignModLoaded("FFQoL")) return;

            if (Time.time < _nextPollTime) return;
            _nextPollTime = Time.time + 2f;

            // Snapshot keys — RefreshDiseaseAlert may evict on exception.
            var widgets = _cache.Keys.ToList();
            for (int i = 0; i < widgets.Count; i++)
            {
                RefreshDiseaseAlert(widgets[i]);
            }
        }

        /// <summary>
        /// If a widget shows up that we haven't cached (e.g. it spawned
        /// before BlightWatch's first run), pull it in lazily.
        /// </summary>
        private static void ScanAndRegisterWidgets()
        {
            var all = UnityEngine.Object.FindObjectsOfType<UISubWidgetCropFieldAlerts>();
            for (int i = 0; i < all.Length; i++)
            {
                var widget = all[i];
                if (_cache.ContainsKey(widget)) continue;
                CacheWidget(widget);
            }
        }

        private static void CacheWidget(UISubWidgetCropFieldAlerts widget)
        {
            var cropField    = _cropFieldField?.GetValue(widget) as Cropfield;
            var parent       = _diseaseParentField?.GetValue(widget) as GameObject;
            var sprite       = parent != null ? parent.GetComponent<DynamicSprite>() : null;
            _cache[widget] = new WidgetCache {
                CropField     = cropField,
                DiseaseParent = parent,
                DiseaseSprite = sprite,
            };
        }

        /// <summary>
        /// Compute "field is currently diseased" and write it to the widget's
        /// `hasLostCropsToDisease` flag + toggle the diseaseSprite visibility.
        /// </summary>
        internal static void RefreshDiseaseAlert(UISubWidgetCropFieldAlerts widget)
        {
            if (widget == null) return;
            if (!_cache.ContainsKey(widget)) ScanAndRegisterWidgets();
            if (!_cache.TryGetValue(widget, out var cached)) return;
            if (cached.DiseaseParent == null) return;

            bool diseased;
            try
            {
                bool hasActiveDisease = cached.CropField != null
                                        && (cached.CropField.GetDiseases()?.Count ?? 0) > 0;
                bool blackboardFlag   = cached.CropField != null
                                        && cached.CropField.widgetBlackboard != null
                                        && cached.CropField.widgetBlackboard.hasLostCropsToDisease;
                diseased = hasActiveDisease || blackboardFlag;
            }
            catch
            {
                // CropField was destroyed mid-frame — drop the entry, no alert.
                _cache.Remove(widget);
                return;
            }

            // Force the widget's flag to match our derived state. This is what
            // makes the alert "pinned" — without this, FF's own UpdateAlerts
            // pass would clear it after the first visible frame.
            _hasLostCropsToDiseaseField?.SetValue(widget, diseased);

            if (cached.DiseaseSprite != null)
            {
                cached.DiseaseSprite.objectIsDisabled = !diseased;
            }
            else
            {
                cached.DiseaseParent.SetActive(diseased);
            }
        }

        // ----- Patches -----

        [HarmonyPatch(typeof(UISubWidgetCropFieldAlerts), "Init")]
        internal static class InitPatch
        {
            private static void Postfix(UISubWidgetCropFieldAlerts __instance)
            {
                try
                {
                    if (!Config.EnableBlightWatch.Value) return;
                    CacheWidget(__instance);
                    RefreshDiseaseAlert(__instance);
                }
                catch (Exception ex) { Plugin.Log.Error($"[BlightWatch] Init: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(UISubWidgetCropFieldAlerts), "Release")]
        internal static class ReleasePatch
        {
            private static void Postfix(UISubWidgetCropFieldAlerts __instance)
            {
                try { _cache.Remove(__instance); }
                catch (Exception ex) { Plugin.Log.Error($"[BlightWatch] Release: {ex}"); }
            }
        }

        /// <summary>
        /// FF's own crop-field telemetry calls this when the disease state
        /// transitions. Hooking it gives us a fast path that doesn't wait
        /// for the next 2-second poll tick.
        /// </summary>
        [HarmonyPatch(typeof(UISubWidgetCropFieldAlerts), "OnHasLostCropsToDiseaseChanged")]
        internal static class ChangedPatch
        {
            private static void Postfix(UISubWidgetCropFieldAlerts __instance)
            {
                try
                {
                    if (!Config.EnableBlightWatch.Value) return;
                    RefreshDiseaseAlert(__instance);
                }
                catch (Exception ex) { Plugin.Log.Error($"[BlightWatch] OnChanged: {ex}"); }
            }
        }
    }
}
