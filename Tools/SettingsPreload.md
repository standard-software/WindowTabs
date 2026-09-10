# Settings preloading experiment

Branch dev/preload-settings-dialog starts from develop f3d29ee. Current source version ss_2026.09.08_next16_build8.

Build6 addresses enlarged check/radio glyphs, modal monitor transitions, workspace scrollbars and English language-confirmation buttons; see DpiControls.md.

The user confirmed build3's normal settings-change/reopen behavior. The subsequent authorized four-candidate Debug benchmark, including its shutdown failure and limitations, is recorded in SettingsFourWayResults.md. Earlier validation-limit notes below are historical.

## Behavior

- Construct all settings pages and create their native handles without Show/Activate during startup, after program/filter/manager services are registered and before notification plugins arm the watchdog.
- Preload holds neither the dialog gate nor its named mutex. A visible session acquires them; user Close hides the form and releases them. Real shutdown/disposal destroys it.
- Reuse the form while its structural key (language and dark mode) matches. Ordinary appearance, shortcut, behavior and custom-theme changes reload existing controls before Show, with write-back events suppressed. Language/dark-mode changes still rebuild because they affect captions and native control types.
- Reposition and rescale to the cursor monitor when opening. Preserve the existing DPI design/metric logic and eager page construction. Theme/native event handlers are attached once per constructed form.
- Refresh program nodes asynchronously without clearing the old list first. Refresh changed workspace data after Show without reconstructing its controls.
- Prevent reentrant show requests during preparation. Preload failure is logged; a later explicit request may retry.
- Carry over initial theme-event suppression and Appearance layout batching from the prior experiment; opening or preloading should not reapply the selected theme. Existing persistence of unmatched custom colors is retained.

## Review at the requested five-minute mark

Read CCXLog/ccxlog.md at about 02:08 JST, after the 02:03 request. Latest advice: Codex2 02:02:38, Claude2 02:02:41, Claude1 02:02:45.

- Codex2: implemented separate visible-session lifetime, native hidden preparation, current-monitor placement, data refresh, initialization fixes, and watchdog ordering. Add DEBUG preload elapsed time/private-bytes/handle deltas. Full startup/first-show/reopen/data-ready measurement is pending desktop-test permission.
- Claude2: audited retained state. Program data refreshes; workspace data reloads when its serialized section changes; presentation settings invalidate the whole cache. The cost of keeping all pages allocated is intentional for this user-requested experiment. Do not introduce idle eviction that would restore first-open construction latency. Memory impact still requires runtime measurement.
- Claude1: Release measurement and physical 100%/150% checks remain necessary before a performance/release claim. Do not substitute lazy first-open caching for requested startup preload. Prior experiments already measured all page constructors plus DPI phases; the assertion that only Appearance was measured is incorrect. JIT and ordering effects remain hypotheses.
- Release/push/history cleanup is outside this task. No merge, push, or timestamp rewrite.

## Validation limits

SettingsPreload.Tests.fsx exercises hidden native-handle creation, monitor preparation, focus and gate invariants on the actual SettingsDialogWindow, without displaying a window. It does not exercise the complete service cache, real user close/reopen, owned modal editors, or physical monitor transitions. Those tests and memory/Release performance comparison remain outstanding until desktop-test permission is granted. Do not report this experimental build as validated for release.

Build1 validation below is historical, not a measurement of build3. Build2 subsequently selected Programs on normal reopening; the user reported good responsiveness.

Debug and Release builds of next16_build1 completed successfully (Release retained the existing duplicate-resource merge warnings). The five non-displaying checks in SettingsPreload.Tests.fsx passed: hidden handle creation, hidden cursor-monitor preparation, unchanged gate during preparation and disposal, and unchanged foreground window. These results do not close the full-dialog validation gaps above.

## Latest review and build3

Read Claude1 09:22:55, Claude2 09:23:03 and Codex2 09:23:07 on September 11 after the requested delay.

- Implemented the shared stale-control/slow-reopen advice: reread existing controls rather than invalidating the whole dialog on ordinary settings changes. Initialization and refresh are not user edits; refresh does not persist themes or overwrite custom hotkeys.
- Claude1's key mismatch advice: retain the key captured before construction. Startup preload remains synchronous as explicitly requested; deferring it to idle could make the first request pay construction cost again.
- Codex2's resource advice: use a weak target for ProgramView's settings notification so it cannot retain disposed pages. The notification API still has no unsubscribe token; a small callback remains per structural rebuild. Full repeated-rebuild memory profiling remains outstanding.
- Appearance layout batching and initial-theme suppression are already included. No need to cherry-pick them again or merge/delete the comparison branches now.
- Release performance, physical mixed-DPI transitions and complete modal/shutdown lifecycle checks remain outstanding. Debug log request-to-show timings do not include asynchronous program-data completion; do not present them as a complete Release benchmark or claim a universal 30x gain.
- SettingsRefresh.Tests.fsx uses in-memory settings/program/filter services and real hidden controls. Ten checks cover no writes, retained control identities, externally changed behavior/appearance/mode values and repeated refresh. With all native handles created, a 20-refresh run averaged 15.12 ms; this is hidden-control synchronization, not dialog display latency. No user settings or desktop input are touched. All ten checks and the five hidden-window preparation checks passed against build3.
- Both non-displaying tests now reference SettingsRefreshValidation; build with `/p:OutputPath=bin\Debug\SettingsRefreshValidation\` before running them. Release/history cleanup is not part of this change.

## Review after the four-way benchmark

Read Claude1 09:49:50, Claude2 09:50:06 and Codex2 09:50:12 on September 11.

- Claude1: expanded SettingsRefresh.Tests.fsx to audit all eleven Behavior rows (seven checkboxes, three comboboxes, one three-option radio group plus its delay), all 22 editable appearance record fields, custom-theme addition/removal and saved colors, and all twelve shortcut fields across custom/Ctrl/Alt modes in both native and managed controls. The dark-mode toggle is structural and rebuilds; it is not a value-refresh field. Tests use in-memory settings and hidden native handles, with no desktop input.
- This audit exposed a separate invalid-value edge case: loading an out-of-range pinned width could leave its event-suppression flag set. Build5 uses try/finally for both pinned-width editor guards. The regression fails against the measured build4 binary and passes after the fix; all 170 checks pass. Refresh still does not save invalid external values, and the user's next valid edit emits its normal change event.
- Claude1/Codex2: startup cost, private bytes and handle counts are now alongside first/warm/changed/data-ready results in SettingsFourWayResults.md. Startup is explicitly main-entry to initialization-ready, not process-launch to all tab strips painted. That stricter desktop-wide startup boundary remains unmeasured.
- Codex2: the former 15-ms value-refresh result is not used as display latency. Painted markers are explicitly Update-return boundaries, not proof of physical screen presentation. All four candidates use the same paint/data markers and the same main executable path. The preload branch, embedded version, process path and watchdog were verified after restoration.
- Claude2: layout batching and initial-theme suppression already exist in the preload source. No additional cherry-pick is needed, and constructor SuspendLayout calls do not by themselves prove a speedup on later mixed-DPI movement. The three value-refresh methods are also implemented and now covered item by item. WtSetup is 26.09.08.16; the reported .15 was the temporary comparison branch, not the adopted branch.
- Claude2: retain the DEBUG benchmark and comparison branches for reproducibility as explicitly requested, including the next15 shutdown failure. Do not delete them, merge or release without a new instruction. Preload runs before watchdog installation; benchmark processes deliberately disable it, while the restored normal process arms it.
- No external Release benchmark was added: this request specified DEBUG instrumentation. A visible-window poll would still not measure complete rendering/data readiness. Release timing, physical mixed-DPI/modal lifecycle tests and repeated structural-rebuild memory profiling remain separate validation work, not proven by the current measurements.
- Version-history rewriting and release cleanup remain outside this task. The four-way timings apply to build4; build5 adds the exceptional-value guard fix and coverage, not a newly measured performance sample.
- Hardened the rerun script to reject branch/archive version mismatches before replacing the running app, and restore the original runtime rather than an obsolete benchmark archive.
