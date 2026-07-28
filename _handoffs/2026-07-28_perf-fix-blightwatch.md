# Perf fix: Blight Watch scene-scan freezes — READY TO SHIP as v1.5.1

**State: fix implemented + verified in-game. Debug build deployed to Mods 2026-07-28.
NOT committed, NOT tagged, NOT released. This session's job: ship it.**

## What was wrong
During a 500-pop lag hunt with a temporary profiling mod (FFPerfProbe, repo
`C:\Users\saged\source\repos\FFPerfProbe`), EP showed sub-second freezes:

- `UISubWidgetCropFieldAlerts.Init [EP]` / `.OnHasLostCropsToDiseaseChanged [EP]`
  — **387–432ms per call**, up to **38.7 ms/frame sustained** in one window;
  `UITopBar.UpdateResourceAlerts [EP+KC]` peaked at **778ms** (nested).
- The probe's FindObjectsOfType counter fingered the scan:
  `FindObjectsOfType[UISubWidgetCropFieldAlerts]` firing during those calls.

Root cause in `src/Features/BlightWatch.cs`: a hidden re-entrancy. FF's vanilla
`UISubWidgetCropFieldAlerts.Init` raises `OnHasLostCropsToDiseaseChanged`
MID-BODY — so EP's Changed postfix ran BEFORE the Init postfix had cached the
widget. The cache-miss path called `ScanAndRegisterWidgets()` — a full-scene
`FindObjectsOfType` costing 300–450ms on large towns — on EVERY widget init,
even though the widget instance was already in hand.

## The fix (already in source)
`src/Features/BlightWatch.cs`:
1. `RefreshDiseaseAlert`: cache-miss now calls `CacheWidget(widget)` directly —
   O(1), no scan. (Big explanatory comment left at the site.)
2. The full `ScanAndRegisterWidgets()` sweep still exists but runs exactly
   **once per map/enable** (new `_scannedOnce` flag, cleared in `Reset()`),
   from the 2-second poll in `OnUpdate` — its one legitimate job is picking up
   widgets that existed before the feature was enabled.

Behavior identical; only the redundant scans are gone.

## Verification (probe data, post-fix sessions)
Zero occurrences of `UISubWidgetCropFieldAlerts` or its FindObjectsOfType scans
in any window since the fix, across multiple sessions including active farming
(EfficientLabor moving 16+ farmers, SurplusSelling day ticks). EP.OnUpdate
steady at ~0.04 ms/frame.

## Also checked, NOT changed (fine as-is)
- `src/Common/WorkerAlertManager.cs` `TryLoadSprite()` —
  `Resources.FindObjectsOfTypeAll<Sprite>()` is properly one-shot
  (`_iconSprite`/`_spriteSearchFailed` guards). Re-runs once per map after
  scene unload nulls the sprite. Acceptable.
- `LaborShortage`'s `UpdateResourceAlerts` postfix — cheap itself; the 778ms
  spikes were the nested BlightWatch scans, now gone.

## Ship checklist
1. Bump version **1.5.0 → 1.5.1** in Plugin.cs (MelonInfo/Version const) AND
   `src/EssentialProvisions.csproj` (AssemblyVersion/FileVersion/Version).
2. Release build:
   `dotnet build src/EssentialProvisions.csproj -c Release -p:Platform=x64 -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Farthest Frontier"`
3. Stage Release DLL to `…\Farthest Frontier (Mono)\Mods\`.
4. Commit, tag `v1.5.1`, push, `gh release create v1.5.1 <DLL> --title
   "Essential Provisions v1.5.1" --notes …` (DLL attached per fleet convention).
5. **Workshop note:** Workshop never got 1.5.0 — upload jumps straight to
   1.5.1. Standing rule: EP publishes to Workshop BEFORE KC's next re-upload.
6. If shipping last of the three perf fixes (WotW/EP/RR): delete
   `FFPerfProbe.dll` from the Mods folder (temp diagnostic, adds overhead).

## Steam patch note (BBCode, copy-paste)
```
[h1]Essential Provisions v1.5.1[/h1]
Performance release.

[h2]Fixed[/h2]
[list]
[*][b]Sub-second freezes when crop-disease alerts appeared or refreshed in large towns.[/b] Blight Watch was rescanning the whole scene every time a crop-field alert widget initialized. It now caches widgets directly — same pinned disease alerts, none of the hitching.
[/list]
```
