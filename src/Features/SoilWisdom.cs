// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableCropRotationWarnings (bool, default false)
// EP changes: section renamed "Soil Wisdom"; pref key renamed EnableSoilWisdom.
//             Disease groups, seasonal thresholds, and validation rules unchanged
//             (see src/Common/CropRotationLogic.cs). Warning text rephrased
//             slightly to fit EP voice; mechanics identical.

using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using EssentialProvisions.Common;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Crop info window summary line + hover tooltip. When EnableSoilWisdom
    /// is on, every crop info window gets an extra row underneath the yield
    /// factors that says "✓ No rotation issues" or "⚠ N rotation issues —
    /// hover for details", with the full list shown on tooltip hover.
    /// </summary>
    internal static class SoilWisdom
    {
        private struct WindowCache
        {
            internal TMP_Text? Label;
            internal Cropfield? Field;
            internal List<string> CachedWarnings;
        }

        private static readonly Dictionary<UICropInfoWindow, WindowCache> _windows
            = new Dictionary<UICropInfoWindow, WindowCache>();
        private static readonly List<UICropInfoWindow> _refreshScratch = new List<UICropInfoWindow>();

        // Visual constants — match FFQoL's
        private const float FontSize = 14f;
        private const float RowHeight = 26f;
        private static readonly Color ColorOK      = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color ColorWarning = new Color(1f, 0.84f, 0f);

        private static float _nextPollTime;

        public static void Reset()
        {
            _windows.Clear();
            _nextPollTime = 0f;
        }

        public static void OnUpdate()
        {
            if (!Config.EnableSoilWisdom.Value) return;
            if (Plugin.IsForeignModLoaded("FFQoL")) return;

            if (Time.time < _nextPollTime) return;
            _nextPollTime = Time.time + 3f;

            _refreshScratch.Clear();
            _refreshScratch.AddRange(_windows.Keys);
            foreach (var window in _refreshScratch)
            {
                if (window == null) continue;
                if (!_windows.TryGetValue(window, out var cache)) continue;
                if (cache.Label == null) continue;
                UpdateAndCacheWarnings(window);
            }
        }

        // ----- Patches -----

        [HarmonyPatch(typeof(UICropInfoWindow), "LoadWindow")]
        internal static class LoadWindowPatch
        {
            private static void Postfix(UICropInfoWindow __instance, ICropInfoWindowDataHandler __0)
            {
                if (!Config.EnableSoilWisdom.Value) return;
                try
                {
                    var dataMb = __0 as MonoBehaviour;
                    var field  = dataMb != null ? dataMb.GetComponent<Cropfield>() : null;

                    TMP_Text? label;
                    if (_windows.TryGetValue(__instance, out var existing) && existing.Label != null)
                    {
                        label = existing.Label;
                    }
                    else
                    {
                        label = CreateRow(__instance);
                        if (label == null) return;
                    }

                    _windows[__instance] = new WindowCache {
                        Label          = label,
                        Field          = field,
                        CachedWarnings = new List<string>(),
                    };

                    // Wire the tooltip provider's pre-send hook to lazily
                    // populate rows from the cached warnings — avoids
                    // re-allocating the list every frame.
                    var tooltipProvider = label.GetComponent<GenericTooltipDataProvider>();
                    if (tooltipProvider != null)
                    {
                        var captured = __instance;
                        tooltipProvider.onPreSendProviderToReceiver = () =>
                        {
                            try
                            {
                                tooltipProvider.toolTipRowKeyNames.Clear();
                                tooltipProvider.toolTipRowValues.Clear();
                                if (_windows.TryGetValue(captured, out var c) && c.CachedWarnings != null)
                                {
                                    foreach (var s in c.CachedWarnings)
                                        tooltipProvider.toolTipRowKeyNames.Add(s);
                                }
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log.Warning($"[SoilWisdom] tooltip populate: {ex.Message}");
                            }
                        };
                    }

                    UpdateAndCacheWarnings(__instance);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[SoilWisdom] LoadWindow: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(UICropInfoWindow), "AddDragItem")]
        internal static class AddDragItemPatch
        {
            private static void Postfix(UICropInfoWindow __instance)
            {
                try
                {
                    if (Config.EnableSoilWisdom.Value) RefreshWindow(__instance);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[SoilWisdom] AddDragItem: {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(UICropInfoWindow), "ClearDragItem")]
        internal static class ClearDragItemPatch
        {
            private static void Postfix(UICropInfoWindow __instance)
            {
                try
                {
                    if (Config.EnableSoilWisdom.Value) RefreshWindow(__instance);
                }
                catch (Exception ex) { Plugin.Log.Warning($"[SoilWisdom] ClearDragItem: {ex.Message}"); }
            }
        }

        // ----- Helpers -----

        private static void RefreshWindow(UICropInfoWindow window)
        {
            if (window == null) return;
            if (!_windows.TryGetValue(window, out var cache)) return;
            if (cache.Label == null) return;
            UpdateAndCacheWarnings(window);
        }

        private static void UpdateAndCacheWarnings(UICropInfoWindow window)
        {
            if (!_windows.TryGetValue(window, out var cache)) return;
            var warnings = (cache.Field == null)
                ? new List<string>()
                : CropRotationLogic.Validate(cache.Field);
            cache.CachedWarnings = warnings;
            _windows[window] = cache;
            ApplySummary(cache.Label!, warnings.Count);
        }

        private static void ApplySummary(TMP_Text label, int warningCount)
        {
            var tooltip = label.GetComponent<CursorOverTooltip>();
            if (warningCount == 0)
            {
                label.text = "✓ No rotation issues";
                label.color = ColorOK;
                if (tooltip != null) tooltip.enabled = false;
            }
            else
            {
                label.text = $"⚠ {warningCount} rotation issue{(warningCount == 1 ? "" : "s")} — hover for details";
                label.color = ColorWarning;
                if (tooltip != null) tooltip.enabled = true;
            }
        }

        /// <summary>
        /// Build a fresh TMP row sibling-to the existing yield-factors row.
        /// Inherits font + sharedMaterial from yieldFactorsLabel so it
        /// renders in FF's medieval serif at the panel's native size.
        /// </summary>
        private static TMP_Text? CreateRow(UICropInfoWindow window)
        {
            var yieldLabel = window.yieldFactorsLabel;
            if (yieldLabel == null) return null;

            var yieldRow = ((TMP_Text)yieldLabel).transform.parent;
            if (yieldRow == null) return null;

            var rowsParent = yieldRow.parent;
            if (rowsParent == null) return null;

            var go = new GameObject("EP_SoilWisdomLabel");
            go.transform.SetParent(rowsParent, false);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight       = RowHeight;
            layout.preferredHeight = RowHeight;
            layout.flexibleHeight  = 0f;

            var rt = go.GetComponent<RectTransform>();
            var yieldRt = yieldRow.GetComponent<RectTransform>();
            if (rt != null && yieldRt != null)
            {
                rt.anchorMin = yieldRt.anchorMin;
                rt.anchorMax = yieldRt.anchorMax;
                rt.pivot     = yieldRt.pivot;
                rt.anchoredPosition = new Vector2(yieldRt.anchoredPosition.x, rt.anchoredPosition.y);
                rt.sizeDelta = new Vector2(yieldRt.sizeDelta.x, RowHeight);
            }

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.raycastTarget    = true;
            tmp.enableWordWrapping = false;
            tmp.overflowMode     = TextOverflowModes.Truncate;
            tmp.alignment        = TextAlignmentOptions.MidlineLeft;
            tmp.margin           = new Vector4(8f, 0f, 8f, 0f);
            tmp.fontSize         = FontSize;
            tmp.font             = ((TMP_Text)yieldLabel).font;
            tmp.fontSharedMaterial = ((TMP_Text)yieldLabel).fontSharedMaterial;

            // Place this row immediately after the yield-factors row.
            go.transform.SetSiblingIndex(yieldRow.GetSiblingIndex() + 1);

            // Add CursorOverTooltip + GenericTooltipDataProvider so hover wires
            // the warning rows. Best-effort — if the types are missing, the
            // summary text still renders, just no hover detail.
            try
            {
                go.AddComponent<CursorOverTooltip>();
                go.AddComponent<GenericTooltipDataProvider>();
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[SoilWisdom] tooltip wiring failed (text-only fallback): {ex.Message}");
            }

            return tmp;
        }
    }
}
