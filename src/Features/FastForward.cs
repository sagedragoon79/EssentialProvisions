// Folded from Fast Frontier Speed Mod by Justin848 (v1.2.0)
// Original DLL: FastFrontier_FF.dll
// EP fold: section renamed "Fast Forward". Hardcoded speed array mirrors the
// original 15-entry table; configurable later if requested. Toggle works
// live mid-game — swaps the array dynamically via OnUpdate, doesn't require
// a save reload.
//
// Mechanism: three Harmony patches matching Fast Frontier exactly:
//   1. TimeManager.Awake (Postfix) — replace timeScales array on scene load
//   2. SettingsManager.get_maxGameSpeed (Prefix) — return TimeScales.Length-1
//      so the in-game speed UI accepts all the new tiers
//   3. TimeManager.UpdateTimeScale (Postfix) — warn at >20x (hunter/combat AI
//      breaks at experimental speeds; especially relevant with WotW installed)
//
// Plus an OnUpdate handler that swaps the array live when the master toggle
// changes mid-game — original Fast Frontier requires restart, EP doesn't.

using System;
using HarmonyLib;
using MelonLoader;

namespace EssentialProvisions.Features
{
    internal static class FastForward
    {
        // FF's default timeScales array (line 104272 in the decompiled source).
        private static readonly float[] VanillaTimeScales = { 0.5f, 1f, 2f, 3f };

        // Fast Frontier's exact 15-entry expansion.
        internal static readonly float[] ExpandedTimeScales =
            { 0.5f, 1f, 2f, 3f, 5f, 7f, 10f, 12f, 15f, 18f, 20f, 25f, 30f, 40f, 50f };

        private const float ExperimentalWarnThreshold = 20f;

        private static bool _wasEnabled;
        private static float _lastWarnedSpeed;

        public static void Reset()
        {
            _wasEnabled = false;
            _lastWarnedSpeed = 0f;
        }

        /// <summary>
        /// Live toggle support — if the master flips on/off after the scene's
        /// TimeManager.Awake already ran, swap the array dynamically so the
        /// player doesn't have to reload the save.
        /// </summary>
        public static void OnUpdate()
        {
            if (!GameManager.gameReadyToPlay) return;
            bool enabled = Config.EnableFastForward.Value
                && !Plugin.IsForeignModLoaded("FastFrontier", "Fast Frontier Speed Mod");
            if (enabled == _wasEnabled) return;
            _wasEnabled = enabled;

            var tm = UnitySingleton<GameManager>.Instance?.timeManager;
            if (tm == null) return;
            try
            {
                tm.timeScales = enabled ? ExpandedTimeScales : VanillaTimeScales;
                Plugin.Log.Msg($"[FastForward] Toggle {(enabled ? "ON" : "OFF")} — timeScales now [{string.Join(", ", tm.timeScales)}]");
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[FastForward] Live swap failed: {ex.Message}");
            }
        }

        [HarmonyPatch(typeof(TimeManager), "Awake")]
        internal static class TimeManagerAwakePatch
        {
            private static void Postfix(TimeManager __instance)
            {
                if (!Config.EnableFastForward.Value) return;
                if (Plugin.IsForeignModLoaded("FastFrontier", "Fast Frontier Speed Mod")) return;
                try
                {
                    __instance.timeScales = ExpandedTimeScales;
                    Plugin.Log.Msg($"[FastForward] TimeManager.Awake — installed expanded speeds [{string.Join(", ", ExpandedTimeScales)}]");
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[FastForward] Awake postfix: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(SettingsManager), "get_maxGameSpeed")]
        internal static class GetMaxGameSpeedPatch
        {
            private static bool Prefix(ref int __result)
            {
                if (!Config.EnableFastForward.Value) return true;
                if (Plugin.IsForeignModLoaded("FastFrontier", "Fast Frontier Speed Mod")) return true;
                try
                {
                    __result = ExpandedTimeScales.Length - 1;
                    return false; // skip vanilla
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[FastForward] get_maxGameSpeed: {ex.Message}");
                    return true; // fall back to vanilla
                }
            }
        }

        [HarmonyPatch(typeof(TimeManager), "UpdateTimeScale")]
        internal static class UpdateTimeScalePatch
        {
            private static void Postfix(TimeManager __instance)
            {
                if (!Config.EnableFastForward.Value) return;
                try
                {
                    float ts = __instance.GetTimeScale();
                    if (ts > ExperimentalWarnThreshold && ts != _lastWarnedSpeed)
                    {
                        _lastWarnedSpeed = ts;
                        Plugin.Log.Warning($"[FastForward] EXPERIMENTAL SPEED: {ts}x — hunters and combat AI may malfunction.");
                    }
                    else if (ts <= ExperimentalWarnThreshold)
                    {
                        _lastWarnedSpeed = 0f;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[FastForward] UpdateTimeScale: {ex.Message}");
                }
            }
        }
    }
}
