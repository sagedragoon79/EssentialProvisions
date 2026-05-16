// Folded from FFQoL by idontcare (v1.0.0, active)
// Original DLL: FFQoL_FF.dll
// EP fold curation: _research/folds/ffqol.md
// Original prefs: EnableCoverageArea (bool, default false)
// EP changes: section renamed "Service Bounds"; pref key renamed EnableServiceBounds.
//             Behaviour unchanged — six patches total: two getter postfixes hook
//             FF's existing selection-circle visibility/radius getters so wells +
//             strategically-upgraded buildings show their circle, plus OnSelected /
//             OnDeselected / PlaceableBuilding.Start / OnDestroy manage peer
//             circles (other same-type buildings, in white) and shelter highlights
//             within the radius.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class ServiceBounds
    {
        private static ResourceManager? _resourceManager;
        private static readonly List<GameObject> _highlighted = new List<GameObject>();
        private static readonly List<SelectionCircle> _activeCircles = new List<SelectionCircle>();

        // Reflective access to SelectionCircle.curColor — used to recolor peer
        // circles white so the selected building's circle remains distinct.
        private static readonly FieldInfo? _curColorField =
            typeof(SelectionCircle).GetField("curColor",
                BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Reset()
        {
            _resourceManager = null;
            _highlighted.Clear();
            _activeCircles.Clear();
        }

        private static ResourceManager? GetResourceManager()
        {
            if (_resourceManager == null)
                _resourceManager = UnityEngine.Object.FindObjectOfType<ResourceManager>();
            return _resourceManager;
        }

        /// <summary>
        /// Wells always show their radius; other buildings show only if they
        /// have a strategic-planning bonus (the FF upgrade mechanic). Same
        /// rule FFQoL uses — verified across markets / theaters / healer's
        /// houses / decoratives.
        /// </summary>
        private static float GetCoverageRadius(Building b)
        {
            if (b == null) return 0f;
            if (b is Well) return b.strategicPlanningRadius;
            if (b.strategicPlanningBonus != 0f && b.strategicPlanningRadius > 0f)
                return b.strategicPlanningRadius;
            return 0f;
        }

        /// <summary>
        /// Find buildings to draw peer-circles for: same C# type as `selected`
        /// AND matching `buildingDataRecordName` (so a basic Market doesn't
        /// pair with an upgraded variant). Wells short-circuit through
        /// `rm.wellsRO` for cheaper lookup.
        /// </summary>
        private static List<Building> GetSameTypeBuildings(Building selected, ResourceManager rm)
        {
            var result = new List<Building>();
            if (selected is Well)
            {
                var wells = rm.wellsRO;
                if (wells != null)
                {
                    foreach (var w in wells)
                        if (w != null) result.Add(w);
                }
                return result;
            }

            // Generic case: scan all buildings, filter by C# type + record name.
            // OnSelected is rare (player click), so the O(n) scan is free.
            var all = UnityEngine.Object.FindObjectsOfType<Building>();
            var selectedType   = selected.GetType();
            var selectedRecord = selected.buildingDataRecordName;
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                if (b.GetType() != selectedType) continue;
                if (b.buildingDataRecordName != selectedRecord) continue;
                result.Add(b);
            }
            return result;
        }

        private static void ShowCircles(Building selected, float selectedRadius)
        {
            HideCircles();
            var rm = GetResourceManager();
            if (rm == null) return;

            foreach (var peer in GetSameTypeBuildings(selected, rm))
            {
                bool isSelected = ReferenceEquals(peer, selected);
                float r = GetCoverageRadius(peer);
                if (r <= 0f) r = selectedRadius;

                var host = new GameObject("CircleHostName");
                host.transform.SetParent(peer.transform, false);
                var circle = host.AddComponent<SelectionCircle>();
                if (!isSelected)
                {
                    _curColorField?.SetValue(circle, Color.white);
                }
                circle.Init(peer.transform.position, r, "", 0f, null, null, null);
                circle.SetEnabled(true);
                _activeCircles.Add(circle);
            }
        }

        private static void ShowPlacementCircles(Building placing)
        {
            HideCircles();
            var rm = GetResourceManager();
            if (rm == null) return;

            float baseRadius = GetCoverageRadius(placing);
            if (baseRadius <= 0f) return;

            // During placement, ALL circles (including the in-progress one) are
            // drawn in white — the player isn't "selecting" any of them yet.
            foreach (var peer in GetSameTypeBuildings(placing, rm))
            {
                float r = GetCoverageRadius(peer);
                if (r <= 0f) r = baseRadius;

                var host = new GameObject("CircleHostName");
                host.transform.SetParent(peer.transform, false);
                var circle = host.AddComponent<SelectionCircle>();
                _curColorField?.SetValue(circle, Color.white);
                circle.Init(peer.transform.position, r, "", 0f, null, null, null);
                circle.SetEnabled(true);
                _activeCircles.Add(circle);
            }
        }

        private static void HideCircles()
        {
            foreach (var c in _activeCircles)
            {
                if (c == null) continue;
                c.SetEnabled(false);
                UnityEngine.Object.Destroy(((Component)c).gameObject);
            }
            _activeCircles.Clear();
        }

        /// <summary>
        /// CEHighlightState value 16 corresponds to the "mod overlay" highlight
        /// FF uses for things like raid threat indicators. Reusing it gives
        /// the highlighted shelters a recognizable visual treatment without
        /// us having to ship custom materials.
        /// </summary>
        private const CEHighlightState ModHighlight = (CEHighlightState)16;

        private static void ApplyHighlights(Building building, float radius)
        {
            var rm = GetResourceManager();
            if (rm == null) return;

            float r2 = radius * radius;
            Vector3 pos = building.transform.position;
            var shelters = rm.sheltersRO;
            if (shelters == null) return;

            foreach (var shelter in shelters)
            {
                if (shelter == null) continue;
                if ((pos - shelter.transform.position).sqrMagnitude > r2) continue;
                var h = shelter.GetComponent<CEHighlighter>();
                if (h == null) continue;
                h.SetHighlightState(ModHighlight);
                _highlighted.Add(shelter.gameObject);
            }
        }

        private static void ClearHighlights()
        {
            foreach (var go in _highlighted)
            {
                if (go == null) continue;
                var h = go.GetComponent<CEHighlighter>();
                if (h == null) continue;
                h.UnsetHighlightState(ModHighlight);
            }
            _highlighted.Clear();
        }

        // ----- Patches -----

        /// <summary>
        /// Hook FF's "should this building show its radius circle during
        /// placement?" getter — return true for any building with positive
        /// coverage radius. This single getter patch makes FF's own placement
        /// UI start showing the radius for wells / upgraded markets / etc.,
        /// without us having to render anything ourselves at this stage.
        /// </summary>
        [HarmonyPatch(typeof(Building), "showCircleOnPlaceable", MethodType.Getter)]
        internal static class ShowCircleOnPlaceablePatch
        {
            private static void Postfix(Building __instance, ref bool __result)
            {
                if (!Config.EnableServiceBounds.Value) return;
                if (Plugin.IsForeignModLoaded("FFQoL")) return;
                try
                {
                    if (!__result && GetCoverageRadius(__instance) > 0f)
                        __result = true;
                }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] ShowCirclePostfix: {ex}"); }
            }
        }

        /// <summary>
        /// Paired with the getter above — fills the radius value when FF asks
        /// for it during placement / selection UI.
        /// </summary>
        [HarmonyPatch(typeof(Building), "circleRadius", MethodType.Getter)]
        internal static class CircleRadiusPatch
        {
            private static void Postfix(Building __instance, ref float __result)
            {
                if (!Config.EnableServiceBounds.Value) return;
                if (Plugin.IsForeignModLoaded("FFQoL")) return;
                try
                {
                    if (__result == 0f)
                    {
                        float r = GetCoverageRadius(__instance);
                        if (r > 0f) __result = r;
                    }
                }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] CircleRadiusPostfix: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(PlaceableBuilding), "Start")]
        internal static class PlaceableStartPatch
        {
            private static void Postfix(PlaceableBuilding __instance)
            {
                if (!Config.EnableServiceBounds.Value) return;
                if (Plugin.IsForeignModLoaded("FFQoL")) return;
                try
                {
                    if (__instance != null && __instance.building != null)
                        ShowPlacementCircles(__instance.building);
                }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] PlaceableStart: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(PlaceableBuilding), "OnDestroy")]
        internal static class PlaceableDestroyPatch
        {
            // Not gated on the config — if circles exist (e.g. user toggled off
            // mid-placement), we still want to clean them up.
            private static void Prefix()
            {
                try { if (_activeCircles.Count > 0) HideCircles(); }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] PlaceableDestroy: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(Building), "OnSelected")]
        internal static class OnSelectedPatch
        {
            private static void Postfix(Building __instance)
            {
                if (!Config.EnableServiceBounds.Value) return;
                if (Plugin.IsForeignModLoaded("FFQoL")) return;
                try
                {
                    float r = GetCoverageRadius(__instance);
                    if (r <= 0f) return;
                    ClearHighlights();
                    ApplyHighlights(__instance, r);
                    ShowCircles(__instance, r);
                }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] OnSelected: {ex}"); }
            }
        }

        [HarmonyPatch(typeof(Building), "OnDeselected")]
        internal static class OnDeselectedPatch
        {
            // Not gated on the config — always clean up if state exists.
            private static void Prefix()
            {
                try
                {
                    if (_activeCircles.Count > 0 || _highlighted.Count > 0)
                    {
                        HideCircles();
                        ClearHighlights();
                    }
                }
                catch (Exception ex) { Plugin.Log.Error($"[ServiceBounds] OnDeselected: {ex}"); }
            }
        }
    }
}
