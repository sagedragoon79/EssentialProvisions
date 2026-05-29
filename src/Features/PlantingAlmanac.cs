// Planting Almanac — crop-rotation templates in the crop field window.
//   - Dropdown of templates (curated disease-safe built-ins + the player's saved
//     rotations from UserData/EP_CropTemplates/*.json).
//   - Apply (cloned from the native Paste button): applies the selected template
//     to the open field via the game's own scheduler, then redraws the window
//     the same way vanilla Paste does (ResetWindow).
//   - Save (cloned from the native Copy button): captures the open field's
//     rotation, auto-names it from its crops, writes a shareable JSON.
//
// To look 100% native we CLONE the crop window's own controls — its
// TMP_Dropdown ("Harvest Selection Dropdown") and the CopyButton/PasteButton
// (52x52, IMG_Border_BuildingButtons01). If cloning ever fails (e.g. a future
// build renames them), we fall back to from-scratch controls so the feature
// still works. Engine lives in Common/CropTemplateStore.cs.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using EssentialProvisions.Common;

namespace EssentialProvisions.Features
{
    internal static class PlantingAlmanac
    {
        private sealed class WindowCache
        {
            internal Cropfield? Field;
            internal TMP_Text? Status;
            internal TMP_Dropdown? Dropdown;
            internal TMP_InputField? NameField;
            internal List<CropTemplate> Templates = new List<CropTemplate>();
        }

        private static readonly Dictionary<UICropInfoWindow, WindowCache> _windows
            = new Dictionary<UICropInfoWindow, WindowCache>();

        private static readonly Color ColTitle  = new Color(0.85f, 0.78f, 0.50f);
        private static readonly Color ColStatus = new Color(0.62f, 0.62f, 0.62f);
        private static readonly Color ColBtnBg  = new Color(0.17f, 0.15f, 0.11f, 0.85f);

        private static readonly Dictionary<string, Sprite?> _spriteCache = new Dictionary<string, Sprite?>();

        public static void Reset()
        {
            _windows.Clear();
            _spriteCache.Clear();
        }

        private static Sprite? FindSprite(string name)
        {
            if (_spriteCache.TryGetValue(name, out var cached)) return cached;
            Sprite? found = null;
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && s.name == name) { found = s; break; }
            _spriteCache[name] = found;
            return found;
        }

        // ----- Patches -----

        /// <summary>
        /// While our name field is focused, suppress FF's HotkeyManager so typing
        /// doesn't move the camera (WASD) or fire window hotkeys. FF reads BOTH
        /// camera-move and all hotkeys through GetKeyComboDown, so blocking it here
        /// covers everything — without touching the input-state machine (which is
        /// what StartEnterText does, and that tears down the cropfield window).
        /// Driven purely off EventSystem focus, so there's no flag to get stuck.
        /// </summary>
        /// <summary>
        /// True only while a text field is genuinely focused (caret active), not
        /// merely the last-selected object — otherwise selection sticks after you
        /// click away and we'd keep swallowing hotkeys (which breaks Keep Clarity's
        /// building shortcuts).
        /// </summary>
        private static bool TextFieldFocused()
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            var input = go != null ? go.GetComponent<TMP_InputField>() : null;
            return input != null && input.isFocused;
        }

        // FF reads discrete hotkeys via GetKeyComboDown and continuous (held)
        // input — notably WASD camera panning — via GetKeyComboHeld. Suppress
        // BOTH while typing so neither leaks. No input-state push, so the
        // cropfield window stays open.
        [HarmonyPatch(typeof(global::HotkeyManager.HotkeyManager), "GetKeyComboDown")]
        internal static class HotkeyDownSuppressPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (TextFieldFocused()) { __result = false; return false; }
                return true;
            }
        }

        [HarmonyPatch(typeof(global::HotkeyManager.HotkeyManager), "GetKeyComboHeld")]
        internal static class HotkeyHeldSuppressPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (TextFieldFocused()) { __result = false; return false; }
                return true;
            }
        }

        [HarmonyPatch(typeof(UICropInfoWindow), "LoadWindow")]
        internal static class LoadWindowPatch
        {
            // Low priority → runs AFTER Soil Wisdom's postfix, so its rotation-status
            // label already exists and we can relocate it into our title row.
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(UICropInfoWindow __instance, ICropInfoWindowDataHandler __0)
            {
                if (!Config.EnablePlantingAlmanac.Value) return;
                try
                {
                    var dataMb = __0 as MonoBehaviour;
                    var field  = dataMb != null ? dataMb.GetComponent<Cropfield>() : null;
                    if (field == null) return;

                    // Reused window (pooled / refreshed): keep controls, update field.
                    if (_windows.TryGetValue(__instance, out var existing) && existing.Status != null)
                    {
                        existing.Field = field;
                        return;
                    }

                    BuildControls(__instance, field);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[PlantingAlmanac] LoadWindow: {ex.Message}");
                }
            }
        }

        // ----- UI build -----

        private static void BuildControls(UICropInfoWindow window, Cropfield field)
        {
            var yieldLabel = window.yieldFactorsLabel;
            if (yieldLabel == null) return;
            var yieldRow = ((TMP_Text)yieldLabel).transform.parent;
            if (yieldRow == null) return;
            var yieldFactorsArea = yieldRow.parent;          // YieldFactorsArea (narrow if it has its own layout)
            if (yieldFactorsArea == null) return;
            var windowContent = yieldFactorsArea.parent;     // WindowContent — full window width

            var font = ((TMP_Text)yieldLabel).font;
            var mat  = ((TMP_Text)yieldLabel).fontSharedMaterial;

            var cache = new WindowCache { Field = field, Templates = CropTemplateStore.All() };

            var container = new GameObject("EP_AlmanacControls");
            // Parent into WindowContent (full width) as a row right after the
            // Yield Factors section, not inside the narrow YieldFactorsArea.
            var host = windowContent != null ? windowContent : yieldFactorsArea;
            container.transform.SetParent(host, false);
            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 5f;
            vlg.padding = new RectOffset(12, 12, 6, 8);
            var cle = container.AddComponent<LayoutElement>();
            cle.minWidth = 300f;
            cle.flexibleWidth = 1f;
            cle.flexibleHeight = 0f;
            if (windowContent != null)
                container.transform.SetSiblingIndex(yieldFactorsArea.GetSiblingIndex() + 1);
            else
                container.transform.SetAsLastSibling();

            // Title row: "Planting Almanac" on the left, with Soil Wisdom's
            // rotation-status line relocated to its right (if Soil Wisdom is on).
            var titleRow = new GameObject("EP_AlmanacTitleRow");
            titleRow.transform.SetParent(container.transform, false);
            var titleHlg = titleRow.AddComponent<HorizontalLayoutGroup>();
            titleHlg.childControlWidth = true;
            titleHlg.childControlHeight = true;
            titleHlg.childForceExpandWidth = false;
            titleHlg.childForceExpandHeight = false;
            titleHlg.spacing = 14f;
            var titleRowLe = titleRow.AddComponent<LayoutElement>();
            titleRowLe.minHeight = 24f;
            titleRowLe.preferredHeight = 24f;

            var title = MakeLabel(titleRow.transform, "Planting Almanac", font, mat, 14f, ColTitle,
                TextAlignmentOptions.MidlineLeft, wrap: false, minHeight: 22f);
            AutoSize(title, 11f, 15f);
            var titleLe = title.GetComponent<LayoutElement>();
            if (titleLe != null) { titleLe.minWidth = 110f; titleLe.preferredWidth = 130f; }

            // Relocate Soil Wisdom's "✓ No rotation issues" label beside the title.
            // Same object, so Soil Wisdom keeps updating its text/tooltip; we just
            // move where it lives. (No-op if Soil Wisdom is disabled / not built.)
            var soilLabel = FindInWindow<TMP_Text>(window, "EP_SoilWisdomLabel");
            if (soilLabel != null)
            {
                soilLabel.transform.SetParent(titleRow.transform, false);
                // Soil Wisdom built this label's RectTransform for a different parent;
                // normalize it so the HorizontalLayoutGroup positions/sizes it cleanly.
                var lrt = soilLabel.rectTransform;
                lrt.anchorMin = new Vector2(0f, 0.5f);
                lrt.anchorMax = new Vector2(0f, 0.5f);
                lrt.pivot = new Vector2(0f, 0.5f);
                lrt.anchoredPosition = Vector2.zero;
                lrt.sizeDelta = new Vector2(0f, 22f);
                soilLabel.alignment = TextAlignmentOptions.MidlineLeft;
                soilLabel.enableWordWrapping = false;
                soilLabel.raycastTarget = true;
                var sle = soilLabel.GetComponent<LayoutElement>() ?? soilLabel.gameObject.AddComponent<LayoutElement>();
                sle.ignoreLayout = false;
                sle.flexibleWidth = 1f;     // fill the rest of the row, right of the title
                sle.minWidth = 140f;
                sle.minHeight = 22f;
                sle.preferredHeight = 22f;
            }

            // --- Dropdown (cloned from a native crop-window dropdown) ---
            var srcDropdown = FindInWindow<TMP_Dropdown>(window);
            if (srcDropdown != null)
            {
                var dd = UnityEngine.Object.Instantiate(srcDropdown, container.transform);
                dd.gameObject.name = "EP_AlmanacDropdown";
                dd.onValueChanged.RemoveAllListeners();
                dd.ClearOptions();
                dd.AddOptions(TemplateNames(cache.Templates));
                dd.value = 0;
                dd.RefreshShownValue();
                var ddle = dd.GetComponent<LayoutElement>() ?? dd.gameObject.AddComponent<LayoutElement>();
                ddle.minHeight = 44f;          // ~1.75x the native ~26px
                ddle.preferredHeight = 44f;
                ddle.flexibleWidth = 1f;       // span the full (now full-width) container
                cache.Dropdown = dd;
            }
            else
            {
                Plugin.Log.Warning("[PlantingAlmanac] No native dropdown found to clone — using per-template buttons.");
                foreach (var t in cache.Templates)
                {
                    var captured = t;
                    MakeButton(container.transform, t.Name, font, mat, () => ApplyTemplate(window, captured));
                }
            }

            // --- Name field (typed name for Save; native text entry) ---
            cache.NameField = MakeInputField(container.transform, "Name for Save (optional)", font, mat);

            // --- Apply / Save / Delete buttons (cloned from native Paste / Copy / Clear) ---
            var btnRow = new GameObject("EP_AlmanacButtonRow");
            btnRow.transform.SetParent(container.transform, false);
            var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.spacing = 8f;
            var brle = btnRow.AddComponent<LayoutElement>();
            brle.minHeight = 55f;
            brle.preferredHeight = 55f;

            MakeActionButton(window, btnRow.transform, FindButtonByName(window, "PasteButton"),
                "Apply", "Apply Template", font, mat, () => ApplySelected(window));
            MakeActionButton(window, btnRow.transform, FindButtonByName(window, "CopyButton"),
                "Save", "Save as Template", font, mat, () => SaveCurrent(window));
            MakeActionButton(window, btnRow.transform, FindButtonByName(window, "ClearSelectedCrop"),
                "Delete", "Delete Template", font, mat, () => DeleteSelected(window));

            var status = MakeLabel(container.transform,
                "Select a rotation and Apply. Save stores this field's rotation.",
                font, mat, 11f, ColStatus, TextAlignmentOptions.TopLeft, wrap: true, minHeight: 28f);

            cache.Status = status;
            _windows[window] = cache;
        }

        // ----- Actions -----

        private static void ApplySelected(UICropInfoWindow window)
        {
            if (!_windows.TryGetValue(window, out var cache)) return;
            int idx = cache.Dropdown != null ? cache.Dropdown.value : 0;
            if (idx < 0 || idx >= cache.Templates.Count) return;
            ApplyTemplate(window, cache.Templates[idx]);
        }

        private static void ApplyTemplate(UICropInfoWindow window, CropTemplate template)
        {
            try
            {
                if (!_windows.TryGetValue(window, out var cache) || cache.Field == null) return;

                var result = CropTemplateStore.Apply(template, cache.Field);
                Plugin.Log.Msg($"[PlantingAlmanac] {result.Summary(template.Name)}");
                var shortMsg = result.ShortSummary();

                RefreshWindow(window);   // redraw schedule live (vanilla Paste path)
                SetStatus(window, cache, shortMsg);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] Apply: {ex.Message}");
            }
        }

        private static void SaveCurrent(UICropInfoWindow window)
        {
            try
            {
                if (!_windows.TryGetValue(window, out var cache) || cache.Field == null) return;

                var captured = CropTemplateStore.Capture(cache.Field, "");
                if (captured.Years.Count == 0)
                {
                    SetStatus(window, cache, "Nothing to save — this field has no rotation yet.");
                    return;
                }

                // Use the typed name if the player entered one; else auto-name from crops.
                string typed = cache.NameField != null ? (cache.NameField.text ?? "").Trim() : "";
                captured.Name = typed.Length > 0 ? typed : CropTemplateStore.ContentName(captured);

                string savedName = CropTemplateStore.SaveUserTemplate(captured);
                if (cache.NameField != null) cache.NameField.text = "";
                Plugin.Log.Msg($"[PlantingAlmanac] Saved rotation \"{savedName}\".");

                // Re-scan templates and select the freshly-saved one.
                cache.Templates = CropTemplateStore.All();
                if (cache.Dropdown != null)
                {
                    cache.Dropdown.ClearOptions();
                    cache.Dropdown.AddOptions(TemplateNames(cache.Templates));
                    int idx = cache.Templates.FindIndex(t => !t.BuiltIn && t.Name == savedName);
                    cache.Dropdown.value = idx >= 0 ? idx : 0;
                    cache.Dropdown.RefreshShownValue();
                }
                SetStatus(window, cache, $"Saved \"{savedName}\".");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] Save: {ex.Message}");
            }
        }

        private static void DeleteSelected(UICropInfoWindow window)
        {
            try
            {
                if (!_windows.TryGetValue(window, out var cache)) return;
                int idx = cache.Dropdown != null ? cache.Dropdown.value : -1;
                if (idx < 0 || idx >= cache.Templates.Count) return;

                var target = cache.Templates[idx];
                if (target.BuiltIn)
                {
                    SetStatus(window, cache, "Built-in presets can't be deleted.");
                    return;
                }

                bool removed = CropTemplateStore.DeleteUserTemplate(target.Name);
                Plugin.Log.Msg($"[PlantingAlmanac] Delete \"{target.Name}\" → {(removed ? "removed" : "not found")}.");

                cache.Templates = CropTemplateStore.All();
                if (cache.Dropdown != null)
                {
                    cache.Dropdown.ClearOptions();
                    cache.Dropdown.AddOptions(TemplateNames(cache.Templates));
                    cache.Dropdown.value = 0;
                    cache.Dropdown.RefreshShownValue();
                }
                SetStatus(window, cache, removed ? $"Deleted \"{target.Name}\"." : "Template file not found.");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] Delete: {ex.Message}");
            }
        }

        private static void SetStatus(UICropInfoWindow window, WindowCache cache, string msg)
        {
            // RefreshWindow may have rebuilt our controls — prefer the live cache.
            if (_windows.TryGetValue(window, out var after) && after.Status != null)
                after.Status.text = msg;
            else if (cache.Status != null)
                cache.Status.text = msg;
        }

        // ----- Cloning helpers -----

        private static T? FindInWindow<T>(UICropInfoWindow w, string? name = null) where T : Component
        {
            foreach (var c in ((Component)w).GetComponentsInChildren<T>(true))
            {
                if (c == null) continue;
                if (name == null || c.gameObject.name == name) return c;
            }
            return null;
        }

        private static GameObject? FindButtonByName(UICropInfoWindow w, string name)
        {
            var b = FindInWindow<Button>(w, name);
            return b != null ? b.gameObject : null;
        }

        /// <summary>Clone a native button (Copy/Paste) and rewire it; fall back to a
        /// text button if the source isn't found.</summary>
        private static void MakeActionButton(UICropInfoWindow window, Transform parent,
            GameObject? srcButton, string label, string tooltip, TMP_FontAsset font, Material mat, UnityAction onClick)
        {
            if (srcButton != null)
            {
                var clone = UnityEngine.Object.Instantiate(srcButton, parent);
                clone.name = "EP_Almanac" + label;
                var btn = clone.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.RemoveAllListeners();
                    btn.onClick.AddListener(onClick);
                    btn.interactable = true;   // vanilla Paste clones in as disabled (empty copy buffer)
                }
                RewriteTooltip(clone, tooltip);
                var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
                le.minWidth = 50f; le.preferredWidth = 50f;   // +25% over the prior 40
                le.minHeight = 50f; le.preferredHeight = 50f;
                return;
            }

            // Fallback: text button with the bordered sprite.
            MakeButton(parent, label, font, mat, onClick);
        }

        /// <summary>Replace a cloned control's tooltip rows so it stops showing the
        /// vanilla "Copy"/"Paste" text. Same pre-send override pattern as the
        /// worker-alert tooltips.</summary>
        private static void RewriteTooltip(GameObject go, string text)
        {
            try
            {
                var provider = go.GetComponentInChildren<GenericTooltipDataProvider>(true);
                if (provider == null) return;
                provider.toolTipRowKeyNames.Clear();
                provider.toolTipRowValues.Clear();
                provider.AddKeyValue(text, "");
                provider.onPreSendProviderToReceiver = () =>
                {
                    try
                    {
                        provider.toolTipRowKeyNames.Clear();
                        provider.toolTipRowValues.Clear();
                        provider.AddKeyValue(text, "");
                    }
                    catch { /* tolerate */ }
                };
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] tooltip rewrite: {ex.Message}");
            }
        }

        // ----- Window refresh (vanilla Paste path) -----

        private static MethodInfo? _resetWindowMethod;

        private static void RefreshWindow(UICropInfoWindow window)
        {
            try
            {
                if (_resetWindowMethod == null)
                    _resetWindowMethod = typeof(UICropInfoWindow).GetMethod(
                        "ResetWindow", BindingFlags.Instance | BindingFlags.NonPublic);
                _resetWindowMethod?.Invoke(window, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[PlantingAlmanac] window refresh failed: {ex.Message}");
            }
        }

        // ----- Misc helpers -----

        private static List<string> TemplateNames(List<CropTemplate> templates)
        {
            var names = new List<string>(templates.Count);
            foreach (var t in templates) names.Add(t.Name);
            return names;
        }

        private static void AutoSize(TMP_Text t, float min, float max)
        {
            t.enableAutoSizing = true;
            t.fontSizeMin = min;
            t.fontSizeMax = max;
        }

        /// <summary>Build a native-styled TMP_InputField. Focusing it calls
        /// InputManager.StartEnterText so game hotkeys don't fire while typing
        /// (the same flow pets/temple renaming use); blur calls StopEnterText.</summary>
        private static TMP_InputField MakeInputField(Transform parent, string placeholder,
            TMP_FontAsset font, Material mat)
        {
            var go = new GameObject("EP_NameField");
            go.transform.SetParent(parent, false);
            var bg = go.AddComponent<Image>();
            var sprite = FindSprite("IMG_BorderSimpleThickLight01B") ?? FindSprite("BTN_BorderFocus01_UP");
            if (sprite != null) { bg.sprite = sprite; bg.type = Image.Type.Sliced; bg.color = new Color(0.12f, 0.12f, 0.12f, 0.85f); }
            else bg.color = ColBtnBg;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 26f; le.preferredHeight = 26f; le.flexibleWidth = 1f;

            var input = go.AddComponent<TMP_InputField>();

            var textArea = new GameObject("Text Area");
            textArea.transform.SetParent(go.transform, false);
            var taRT = textArea.AddComponent<RectTransform>();
            Stretch(taRT, 8f, 4f);
            textArea.AddComponent<RectMask2D>();

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(textArea.transform, false);
            var textTmp = textGo.AddComponent<TextMeshProUGUI>();
            textTmp.font = font; textTmp.fontSharedMaterial = mat; textTmp.fontSize = 13f;
            textTmp.color = Color.white; textTmp.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(textTmp.rectTransform, 0f, 0f);

            var phGo = new GameObject("Placeholder");
            phGo.transform.SetParent(textArea.transform, false);
            var phTmp = phGo.AddComponent<TextMeshProUGUI>();
            phTmp.font = font; phTmp.fontSharedMaterial = mat; phTmp.fontSize = 13f;
            phTmp.color = new Color(0.6f, 0.6f, 0.6f, 0.8f); phTmp.fontStyle = FontStyles.Italic;
            phTmp.alignment = TextAlignmentOptions.MidlineLeft; phTmp.text = placeholder;
            Stretch(phTmp.rectTransform, 0f, 0f);

            input.textViewport = taRT;
            input.textComponent = textTmp;
            input.placeholder = phTmp;
            input.targetGraphic = bg;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.characterLimit = 60;
            input.text = "";

            // Hotkey/camera suppression while typing is handled by the
            // HotkeyManager.GetKeyComboDown patch below (which keys off this
            // field being the focused object) — NOT via InputManager.StartEnterText,
            // whose input-state push tears down the cropfield window.
            return input;
        }

        private static void Stretch(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }

        private static TMP_Text MakeLabel(Transform parent, string text, TMP_FontAsset font,
            Material mat, float fontSize, Color color, TextAlignmentOptions align, bool wrap, float minHeight)
        {
            var go = new GameObject("EP_AlmanacLabel");
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = minHeight;
            le.flexibleHeight = 0f;
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.font = font;
            tmp.fontSharedMaterial = mat;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = wrap;
            tmp.overflowMode = TextOverflowModes.Truncate;
            tmp.raycastTarget = false;
            return tmp;
        }

        private static Button MakeButton(Transform parent, string label, TMP_FontAsset font,
            Material mat, UnityAction onClick)
        {
            var go = new GameObject("EP_AlmanacBtn");
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            var sprite = FindSprite("BTN_BorderFocus01_UP");
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; img.color = Color.white; }
            else img.color = ColBtnBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 60f; le.minHeight = 24f; le.preferredHeight = 24f;

            var txtGo = new GameObject("Label");
            txtGo.transform.SetParent(go.transform, false);
            var t = txtGo.AddComponent<TextMeshProUGUI>();
            t.text = label;
            t.font = font;
            t.fontSharedMaterial = mat;
            t.color = Color.white;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.raycastTarget = false;
            AutoSize(t, 8f, 13f);
            var trt = txtGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(4f, 2f);
            trt.offsetMax = new Vector2(-4f, -2f);

            if (onClick != null) btn.onClick.AddListener(onClick);
            return btn;
        }
    }
}
