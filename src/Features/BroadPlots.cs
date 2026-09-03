// Original EP feature (not a fold) — requested by players 2026-07-28.
//
// Vanilla pens in the two "drag out a rectangle" buildings — the Graveyard and the
// Crop Field — from three directions at once. All three checks live on
// ResizeablePlaceable (the shared base for PlaceableGraveyard, PlaceableCropField,
// and FlattenPlaceable):
//
//   IsUnderMinSize()      width < minDimensions.x  || height < minDimensions.y
//   IsOverMaxSize()       width > maxDimensions.x  || height > maxDimensions.y
//   IsOverMaxGridCount()  base returns false; PlaceableCropField overrides it
//
// UpdatePlacementValidity() runs those three and force-invalidates the whole grid on
// any hit, which is why an under-min drag paints red no matter how good the ground is.
// The min is why a 4x10 field is refused: BOTH dimensions must clear the minimum, so a
// long thin rectangle is illegal even when its area is ample.
//
// WHERE THE NUMBERS COME FROM — two different places, so this needs two mechanisms:
//   Graveyard  min/max are the placeable's own serialized minDimensions/maxDimensions
//              (ResizeablePlaceable's code defaults are 3x3 / 10x10; the prefab
//              overrides them). Per-instance data — cannot be fixed centrally.
//   Crop field PlaceableCropField.Start() COPIES AgricultureManager.minFieldDimensions
//              / maxFieldDimensions over those fields, and the 288-cell ceiling is
//              AgricultureManager.maxGridCount, consulted in two places:
//              PlaceableCropField.IsOverMaxGridCount() (blocks the expand drag) and
//              Cropfield.CheckGridCount() (greys out the Expand button entirely, once
//              within one minimum-size field of the ceiling).
//
// SO: crops are handled by rewriting the three AgricultureManager values once per map
// (cache original -> overwrite -> restore on toggle-off, the RabiesTreatable pattern).
// That one change reaches the placement drag, the expand drag, the Expand button, AND
// the size readout in the placement panel, which colours the width/height red against
// those same fields — patching the predicates alone would leave that readout lying.
// Graveyards get Harmony postfixes on the two ResizeablePlaceable predicates, gated to
// PlaceableGraveyard so FlattenPlaceable keeps vanilla behaviour. The predicates are
// also gated to PlaceableCropField as a belt-and-braces guard, in case a placeable was
// instantiated before the field override landed and copied stale limits in Start().
//
// The cell ceiling stays a real (configurable) number rather than infinity: it is the
// one genuinely unbounded resource here, and a 5,000-square field is a performance
// question, not a balance one.

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EssentialProvisions.Features
{
    /// <summary>
    /// Lifts the size limits on Graveyards and Crop Fields: no 10x10 graveyard cap, no
    /// 12x12 / 12x24 field cap, no "must be at least N wide in both directions" rule
    /// (so 4x10 and other long thin plots are legal), and a configurable ceiling on
    /// total crop-field cells in place of vanilla's fixed 288.
    /// </summary>
    internal static class BroadPlots
    {
        // Effectively "no limit" for the drag rectangle. Large enough that no playable
        // map can reach it, small enough to stay well clear of numeric edge cases.
        private static readonly Vector2 UnboundedMax = new Vector2(4096f, 4096f);
        private static readonly Vector2 UnboundedMin = new Vector2(1f, 1f);

        private static FieldInfo? _minDimsField;
        private static FieldInfo? _maxDimsField;
        private static FieldInfo? _maxGridCountField;
        private static bool _reflectionResolved;

        // Applied state, so we only touch the manager when something actually changes
        // and can hand the originals back verbatim on toggle-off.
        private static AgricultureManager? _appliedTo;
        private static Vector2 _origMinDims;
        private static Vector2 _origMaxDims;
        private static int _origMaxGridCount;
        private static int _appliedCellCap;

        public static void Reset()
        {
            // Scene changed — the old AgricultureManager is gone; drop the handle
            // without trying to restore through a dead reference.
            _appliedTo = null;
        }

        private static bool ResolveReflection()
        {
            if (_reflectionResolved)
                return _minDimsField != null && _maxDimsField != null && _maxGridCountField != null;
            _reflectionResolved = true;

            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var t = typeof(AgricultureManager);
            _minDimsField      = t.GetField("_minFieldDimensions", Flags);
            _maxDimsField      = t.GetField("_maxFieldDimensions", Flags);
            _maxGridCountField = t.GetField("_maxGridCount", Flags);

            bool ok = _minDimsField != null && _maxDimsField != null && _maxGridCountField != null;
            if (!ok)
                Plugin.Log.Warning("[BroadPlots] AgricultureManager size fields not found — crop-field limits left at vanilla.");
            return ok;
        }

        public static void OnUpdate()
        {
            try
            {
                if (!GameManager.gameReadyToPlay) return;
                var gm = UnitySingleton<GameManager>.Instance;
                if (gm == null) return;
                var am = gm.agricultureManager;
                if (am == null) return;

                bool wantOn  = Config.EnableBroadPlots.Value;
                int  wantCap = Config.BroadPlotsMaxFieldCells.Value;

                if (wantOn)
                {
                    // Apply on first sight of this manager, or re-apply if the slider moved.
                    if (!ReferenceEquals(_appliedTo, am)) Apply(am, wantCap);
                    else if (_appliedCellCap != wantCap) UpdateCellCap(am, wantCap);
                }
                else if (ReferenceEquals(_appliedTo, am))
                {
                    Restore(am);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[BroadPlots] {ex.Message}");
            }
        }

        private static void Apply(AgricultureManager am, int cellCap)
        {
            if (!ResolveReflection()) return;

            _origMinDims      = (Vector2)_minDimsField!.GetValue(am);
            _origMaxDims      = (Vector2)_maxDimsField!.GetValue(am);
            _origMaxGridCount = (int)_maxGridCountField!.GetValue(am);

            _minDimsField.SetValue(am, UnboundedMin);
            _maxDimsField.SetValue(am, UnboundedMax);
            _maxGridCountField.SetValue(am, cellCap);

            _appliedTo = am;
            _appliedCellCap = cellCap;
            Plugin.Log.Msg(
                $"[BroadPlots] Crop-field limits lifted — min {_origMinDims.x}x{_origMinDims.y} -> 1x1, " +
                $"max {_origMaxDims.x}x{_origMaxDims.y} -> unbounded, cells {_origMaxGridCount} -> {cellCap}. " +
                "Graveyard size caps lifted too.");
        }

        private static void UpdateCellCap(AgricultureManager am, int cellCap)
        {
            if (_maxGridCountField == null) return;
            _maxGridCountField.SetValue(am, cellCap);
            _appliedCellCap = cellCap;
            Plugin.Log.Msg($"[BroadPlots] Crop-field cell ceiling now {cellCap}.");
        }

        private static void Restore(AgricultureManager am)
        {
            if (_minDimsField == null || _maxDimsField == null || _maxGridCountField == null)
            {
                _appliedTo = null;
                return;
            }
            _minDimsField.SetValue(am, _origMinDims);
            _maxDimsField.SetValue(am, _origMaxDims);
            _maxGridCountField.SetValue(am, _origMaxGridCount);
            _appliedTo = null;
            Plugin.Log.Msg("[BroadPlots] Vanilla crop-field size limits restored.");
        }

        /// <summary>
        /// True for the two placeables this feature covers. FlattenPlaceable shares the
        /// same base class and is deliberately left on vanilla behaviour.
        /// </summary>
        private static bool IsCovered(ResizeablePlaceable p) =>
            p is PlaceableGraveyard || p is PlaceableCropField;

        [HarmonyPatch(typeof(ResizeablePlaceable), "IsUnderMinSize")]
        internal static class MinSizePatch
        {
            private static void Postfix(ResizeablePlaceable __instance, ref bool __result)
            {
                try
                {
                    if (!__result) return;                       // already legal — nothing to lift
                    if (!Config.EnableBroadPlots.Value) return;
                    if (!IsCovered(__instance)) return;
                    __result = false;
                }
                catch (Exception ex) { Plugin.Log.Warning($"[BroadPlots] {ex.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ResizeablePlaceable), "IsOverMaxSize")]
        internal static class MaxSizePatch
        {
            private static void Postfix(ResizeablePlaceable __instance, ref bool __result)
            {
                try
                {
                    if (!__result) return;
                    if (!Config.EnableBroadPlots.Value) return;
                    if (!IsCovered(__instance)) return;
                    __result = false;
                }
                catch (Exception ex) { Plugin.Log.Warning($"[BroadPlots] {ex.Message}"); }
            }
        }
    }
}
