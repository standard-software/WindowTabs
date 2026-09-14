# Caption drag validation

## Candidate

- Implementation: `04ba098` (`dev/lock-window-position-A-caption-boundary`).
- Runtime label: `ss_2026.09.12_next1-A4_build2`.
- The branch includes `0b7995e`, the same-size border-move fallback.
- The separate `dev/lock-window-position-A-leak` diagnostics are not included.

## Physical desktop checks

On 2026-09-15, Windows Terminal, HmFiler, Chrome, VSCode and Excel were
tested on three monitors at 125%, 150% and 175%. Windows were positioned
away from display edges and the taskbar. The final runs recorded 85 + 15
successful gestures, with no failed recorded gestures. Skipped points were
not counted as passes; missing boundary and resize coverage was rechecked.

For every app/scale combination, checks covered the upper caption boundary
and the top, bottom, left and right resize borders. Some caption checks were
repeated at the boundary rather than farther inside the title bar, because
custom client controls occupied the originally selected point.

- A blocked caption gesture required no move/size-start event and no change
  in the sampled window rectangles.
- A resize required a move/size-start event and an actual size change.
- Resizing outward and allowing more processing time resolved the earlier
  inconclusive Terminal 150% and Chrome 175% observations. This does not
  establish the exact cause of those earlier inconclusive observations.
- Terminal and HmFiler exercised native-frame geometry. Chrome, VSCode and
  Excel provided regression coverage for custom frames; their title/client
  geometry excludes them from the new native-boundary correction.
- At these higher scales, a tested caption point can already return
  `HTCAPTION`. Passing there does not prove the `HTTOP` correction fired.
  The earlier 100% Terminal comparison specifically covered that path.

Raw observations are in the local temporary directory:

- `wt_dpi_drag_validation_20260915_114935.json` (85 gestures).
- `wt_dpi_drag_validation_20260915_115040.json` (15 gestures).

These files are local diagnostics, not repository dependencies. The terminal
summary is also recorded in `CCXLog/ccxlog.md`.

## Build and remaining release checks

A Release rebuild of this candidate succeeded using isolated output and
intermediate directories. ILRepack produced the merged `WindowTabs.exe`
(9,437,696 bytes). The first attempt using the ordinary intermediate
directory failed to write `Win32/obj/Release/Win32.csproj.FileListAbsolute.txt`
with access denied; ordinary-path Release rebuilding remains to be checked.
The running Debug executable was not replaced.

The project diff adds sources within their existing compile-folder groups
and preserves Debug `Optimize=true`.

All 11 `Tools/*.Tests.fsx` scripts completed with exit code 0 using Visual
Studio's .NET Framework F# Interactive and the current Debug dependencies.
This includes 89 caption-policy and 69 fallback checks; automated policy
checks do not replace the interactive release checks below.

The implementation is squash-integrated into develop as one feature, with
the candidate suffix removed (`ss_2026.09.12_next1`). Runtime implementation
is unchanged from `04ba098`; version notes and README describe the option.
Before release, finish interactive checks of lock OFF, double-click
maximize/restore, tab movement, detachment and menus.
Additional native-frame app checks may improve coverage, but no guarantee
for every window implementation follows from this sample. Do not silently
include the independent detach/snap feature or diagnostic branches.
