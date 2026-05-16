// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableLaborerAlert (1 of 14 FFQoL prefs — the others land in Long Travels,
//                 Idle Hands, Blight Watch, Soil Wisdom, Service Bounds)
// EP changes: renamed pref to EnableLaborShortage; section renamed to "Labor Shortage" in panel.
//             Mirrors original behavior including the builders check (alert fires when
//             laborers OR builders are below recommended). Throttling matches original
//             (0.5s update, 2s setup retry).

using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Top-bar alert badge that fires when laborers/builders fall below the
    /// recommended count. The badge itself is a clone of the existing housing
    /// alert prefab — same sprite, same animation, same look as vanilla
    /// alerts. Parented as a sibling of the Professions button on the
    /// bottom-right bar, offset 55px upward.
    /// </summary>
    internal static class LaborShortage
    {
        // Cached state — populated lazily on first successful setup, cleared
        // by Reset() on scene transitions. Match FFQoL's exact set so that
        // the runtime behavior is verifiably equivalent.
        private static UIResourceAlert? _laborerAlert;
        private static RectTransform?    _badgeRt;
        private static Vector2           _buttonBasePosition;
        private static VillagerAutoSwapOccupationManager? _swapManager;
        private static FieldInfo?        _housingAlertField;
        private static float             _lastUpdateTime;
        private static float             _lastSetupRetryTime;

        public static void Reset()
        {
            _laborerAlert       = null;
            _badgeRt            = null;
            _buttonBasePosition = default;
            _swapManager        = null;
            _lastUpdateTime     = 0f;
            _lastSetupRetryTime = 0f;
        }

        /// <summary>
        /// Locate UITopBar.housingAlert (private instance field) once and
        /// cache the FieldInfo. Used by setup to find a template alert to clone.
        /// </summary>
        private static FieldInfo? HousingAlertField
        {
            get
            {
                if (_housingAlertField == null)
                {
                    _housingAlertField = typeof(UITopBar).GetField(
                        "housingAlert",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                }
                return _housingAlertField;
            }
        }

        // ----- UI traversal helpers (mirrors FFQoL) -----

        /// <summary>
        /// Find a Button anywhere under the given root whose onClick has a
        /// persistent listener calling the given method name. More robust
        /// than name-string matching when the button's GameObject name has
        /// drifted across game versions.
        /// </summary>
        private static Transform? FindButtonByMethod(Component root, string methodName)
        {
            if (root == null) return null;
            var buttons = root.GetComponentsInChildren<Button>(true);
            foreach (var btn in buttons)
            {
                int count = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < count; i++)
                {
                    if (btn.onClick.GetPersistentMethodName(i) == methodName)
                        return btn.transform;
                }
            }
            return null;
        }

        /// <summary>Fallback: case-insensitive name-fragment match.</summary>
        private static Transform? FindChildByNameFragment(Transform root, string fragment)
        {
            if (root == null) return null;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in transforms)
            {
                if (t.name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }
            return null;
        }

        /// <summary>
        /// One-shot setup: find a housing-alert template, clone it, attach to
        /// the Professions button area, configure rotation/scale, cache the
        /// UIResourceAlert reference and the swap manager. Called from the
        /// postfix when _laborerAlert is null, throttled by _lastSetupRetryTime
        /// so we don't hammer reflection lookups every frame.
        /// </summary>
        private static void TrySetupAlertOverlay(UITopBar topBar)
        {
            try
            {
                // Pull the existing housing alert as a template — same sprite,
                // same animation we want.
                var housingObj = HousingAlertField?.GetValue(topBar);
                var housingAlert = housingObj as UIResourceAlert;
                if (housingAlert == null) return;

                // Find the Professions button on the bottom-right bar (where we
                // want to anchor the badge). Try the method-name lookup first
                // (robust against GameObject renames), fall back to name fragment.
                var bottomRight = UnityEngine.Object.FindObjectOfType<UIBottomRightBar>();
                if (bottomRight == null) return;

                var profBtn = FindButtonByMethod(bottomRight, "ToggleProfessionsUI")
                              ?? FindChildByNameFragment(bottomRight.transform, "profession");
                if (profBtn == null)
                {
                    Plugin.Log.Warning("[LaborShortage] Could not find Professions button — alert badge not shown.");
                    return;
                }

                var parent = profBtn.parent ?? profBtn;
                var clone = UnityEngine.Object.Instantiate(housingAlert.gameObject, parent);
                clone.name = "EP_LaborShortageAlert";
                clone.transform.SetAsLastSibling();

                _badgeRt = clone.GetComponent<RectTransform>();
                var btnRt = profBtn.GetComponent<RectTransform>();
                if (_badgeRt != null && btnRt != null)
                {
                    _buttonBasePosition = btnRt.anchoredPosition;
                    _badgeRt.anchorMin = btnRt.anchorMin;
                    _badgeRt.anchorMax = btnRt.anchorMax;
                    _badgeRt.pivot = new Vector2(0.5f, 0.5f);
                    _badgeRt.anchoredPosition = _buttonBasePosition + new Vector2(0f, 55f);
                }

                // FFQoL flips the inner Image vertically — the housing alert
                // template assumes a different anchor; this corrects it for
                // the bottom-right placement.
                var image = clone.transform.Find("Image");
                if (image != null)
                {
                    image.localScale = new Vector3(1f, -1f, 1f);
                    var inner = image.Find("ResourceAlert");
                    if (inner != null) inner.localScale = new Vector3(1f, -1f, 1f);
                }

                clone.SetActive(true);
                _laborerAlert = clone.GetComponent<UIResourceAlert>();
                if (_laborerAlert == null)
                {
                    UnityEngine.Object.Destroy(clone);
                    return;
                }

                // Init the alert in a "hidden, no count" state so it's ready
                // for UpdateAlert calls from the postfix.
                _laborerAlert.Init(1, 0);
                _laborerAlert.hasResourceGained = true;

                _swapManager = UnityEngine.Object.FindObjectOfType<VillagerAutoSwapOccupationManager>();
                clone.SetActive(false);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[LaborShortage] Setup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Patches `UITopBar.UpdateResourceAlerts` (Postfix). Runs every frame
        /// the alerts loop runs, but throttled internally to 0.5s. Reads
        /// recommended-vs-available laborers/builders from ResourceManager
        /// and pushes the badge state via UIResourceAlert.UpdateAlert.
        /// </summary>
        [HarmonyPatch(typeof(UITopBar), "UpdateResourceAlerts")]
        internal static class Patch
        {
            private static void Postfix(UITopBar __instance)
            {
                try
                {
                    // Master toggle
                    if (!Config.EnableLaborShortage.Value)
                    {
                        // If we'd previously spawned the badge and the user
                        // turned the feature off, hide it (UpdateAlert(1u, 1, 0)
                        // = hidden state, matching FFQoL's disable path).
                        if (_laborerAlert != null) _laborerAlert.UpdateAlert(1u, 1, 0);
                        return;
                    }

                    // Foreign-mod kill switch — defer if FFQoL is loaded
                    if (Plugin.IsForeignModLoaded("FFQoL")) return;

                    // 0.5s throttle (FFQoL parity — alert state doesn't need
                    // to refresh more often than twice a second)
                    if (Time.time - _lastUpdateTime < 0.5f) return;
                    _lastUpdateTime = Time.time;

                    if (!GameManager.gameReadyToPlay) return;

                    // Lazy setup, retry every 2s if it failed previously
                    if (_laborerAlert == null)
                    {
                        if (Time.time - _lastSetupRetryTime < 2f) return;
                        _lastSetupRetryTime = Time.time;
                        TrySetupAlertOverlay(__instance);
                        if (_laborerAlert == null) return;
                    }

                    var gm = UnitySingleton<GameManager>.Instance;
                    var rm = gm?.resourceManager;
                    if (rm == null) return;

                    // Shortage criteria — mirrors FFQoL exactly:
                    //   laborer shortage = (available + min) < recommended
                    //   builder shortage = swapManager.maxBuildersDesired < recommended
                    bool laborerShort = rm.GetAvailableLaborers() + rm.GetMinLaborers() < rm.GetRecommendedLaborers();
                    bool builderShort = _swapManager != null
                                        && _swapManager.maxBuildersDesired < rm.GetRecommendedBuilders();

                    // UpdateAlert(visibility, ?, ?) — visibility 0 = show, 1 = hide
                    uint visibility = (laborerShort || builderShort) ? 0u : 1u;
                    _laborerAlert.UpdateAlert(visibility, 1, 0);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error($"[LaborShortage] {ex}");
                }
            }
        }
    }
}
