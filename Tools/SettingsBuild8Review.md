# Build8 follow-up review and measurements

Source: 2a11595, dev/preload-settings-dialog, ss_2026.09.08_next16_build8.

## Changes and checks

- Theme save and edit dialogs now use DpiAwareDialog and InitializeDpi, matching the existing modal monitor-transition implementation. Input validation, overwrite, rename and delete semantics are unchanged. Actual monitor transitions of both dialogs were confirmed working by the user (CCXLog, September 11, 12:19).
- Header display/hit testing in build7 was accepted by the user.
- Debug and Release builds passed. The common non-displaying DPI suite passed all 53 checks; it does not exercise theme save/edit UI workflows.
- Shutdown boundaries are logged only in DEBUG: saving settings, removing grouped windows, disposing cached settings, returning from the message loop, and disposing plugins. The cached-disposal marker was absent from the Release binary.

## Current-version benchmark

Runner: Tools/Run-CurrentSettingsBenchmark.ps1. It uses the main executable path, committed-version validation and copied settings. Two fresh processes, six opens each. Original settings SHA256 was unchanged, and the normal build8 runtime was restored afterward.

Raw artifacts: unite/current-settings-5a3051af1e4346be83e6bebcbbde569b (manifest.json, pass1.tsv, pass2.tsv, samples.csv, summary.csv).

Debug, 175% DPI, nine program nodes. Mean elapsed milliseconds from settings request:

| Scenario | N | Show returned | Paint boundary | Data/paint completion boundary |
| :-- | --: | --: | --: | --: |
| First | 2 | 192.8 | 444.9 | 677.6 |
| Warm | 6 | 87.8 | 133.6 | 348.1 |
| After setting change | 2 | 98.2 | 139.1 | 222.2 |
| Subsequent reopen | 2 | 74.2 | 118.1 | 220.4 |

Startup-ready: 2735.8 / 2873.6 ms. These are software boundaries, not physical screen presentation times. The previous four-way comparison used build4 at 100% with eight nodes; do not infer a regression or improvement by directly comparing these different conditions. No Release timing measurement was performed.

### Follow-up at 100% DPI

Same build8 executable, nine program nodes, two fresh processes and twelve opens. All twelve samples report dpi=1.00 and programs-ready:nodes=9. The same runner used copied settings and the main executable path; the live settings hash was unchanged and normal build8 operation was restored.

Raw artifacts: unite/current-settings-a6c369288ba64204806838f9f7d72bc2 (manifest.json, pass1.tsv, pass2.tsv, samples.csv, summary.csv). The manifest records c9401c7, a documentation-only successor to the build8 source commit.

| Scenario | N | Show returned | Paint boundary | Data/paint completion boundary |
| :-- | --: | --: | --: | --: |
| First | 2 | 65.7 | 181.7 | 235.5 |
| Warm | 6 | 79.6 | 111.9 | 382.8 |
| After setting change | 2 | 90.4 | 121.4 | 385.3 |
| Subsequent reopen | 2 | 87.3 | 119.7 | 382.2 |

Startup-ready: 2414.7 / 2067.5 ms. Unlike the historical build4 comparison, build8's 100% and 175% runs have the same version and node count. They were still separate, small measurement batches with potentially different machine load; do not attribute every difference to DPI. In particular, the build4/build8 warm-paint difference is not established as measurement noise or as a DPI-fix regression.

## Shutdown results and limits

PIDs 65664 and 72272 exited with code zero after all six measurements each. settings_dialog_trace.log records all shutdown boundaries for both: cached disposal completed at 11:44:58.048 / 11:45:14.441 and plugin disposal completed at 11:44:58.051 / 11:45:14.446.

The earlier next15 native failure remains unresolved, not attributed exclusively to that candidate. Two successful exits do not prove absence of intermittent failure. This test calls the normal shutdown service; it does not exercise the restart menu's delayed successor-launch command or its confirmation UI.

The subsequent 100% batch also completed normally (PIDs 58280 and 53916, exit code zero). This adds successful observations, not proof that the earlier intermittent failure is fixed. Menu-driven restart and long-duration resource trends remain unverified, distinct from an identified unfixed defect.

## Other review advice

- Keep startup preloading synchronous for now: the requested priority is a ready-to-show first dialog, and the user explicitly accepts startup cost. With idle scheduling, a first request arriving before preload would still pay construction cost; this is a timing tradeoff, not a demonstrated data race.
- Scaling already restores captured design metrics in both directions; the header's explicit 100% exception was the defect. A broad redesign is not justified solely by a count of reported defects.
- Appearance layout batching and one-time full dark-theme application are already present in this branch (AppearanceView construction SuspendLayout/ResumeLayout and DesktopManagerForm.prepareHidden's prepared guard). No additional transplant is needed.
- Retain comparison branches for reproducibility during acceptance. Deletion, merging, release, and timestamp rewriting are deferred to their own requested workflow; nothing was pushed.
- Do not estimate an intermittent crash rate from one failed comparison process. Retain the failure record and DEBUG diagnostics.

## Acceptance decision and latest advice

- The user agreed to retain build8 without further optimization. No DPI-dependent skipping or other optimization was implemented after that decision.
- Additional Release measurements and a 175% develop comparison are optional if publishing specific performance claims, not prerequisites imposed on this completed speedup task. A visible-window timestamp is not equivalent to completed painting or data refresh.
- Do not describe the roughly 41-76 ms Show-return measurements as complete visual presentation. Use the defined boundaries and version/DPI-specific tables above.
- The current setup source is Version="26.09.08.16", not the .15 value mentioned in one review. No correction is needed there.
- Theme monitor transitions and header behavior are user-confirmed. Keep the unresolved historical crash, menu-restart check and long-duration checks separately documented; do not reopen confirmed items as missing implementation.
