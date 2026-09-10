# Four-way settings dialog benchmark

Measured September 11, 2026, using Debug builds at 100% DPI. The user's running app was restored to the main worktree executable, ss_2026.09.08_next16_build4. No merge or push was performed.

## Candidates

| Candidate | Branch | Measured commit | Source version |
|---|---|---|---|
| Existing optimization | dev/setting-dialog-speedup_1 | b2b649f | ss_2026.09.08_next13_build3 |
| Selected optimizations reverted | dev/setting-dialog-speedup_2 | 711b0c3 | ss_2026.09.08_next14_build3 |
| Construction/layout batching | dev/setting-dialog-layout-batching | 1e9ebfc | ss_2026.09.08_next15_build3 |
| Startup preload and retained controls | dev/preload-settings-dialog | a14e32a | ss_2026.09.08_next16_build4 |

The reverted candidate removes the shared parsed-settings/path/category lookup optimizations, not every historic dialog change. The preload candidate includes layout batching and reloading ordinary settings into retained controls.

## Method

- Order: 13, 14, 15, 16, then 16, 15, 14, 13. The final two processes were run after interruption/recovery described below.
- Each process opens six times: first open, three unchanged reopens, one reopen after a setting change, and one further reopen.
- The first request is scheduled two seconds after startup preparation. Each subsequent request follows close by approximately 750 ms. The UI timer waits for program data before closing, with a 30-second data timeout and a separate 180-second process timeout.
- After the fourth open, change snapTabHeightMargin through the settings service while the dialog is visible, then close it. This tests a persisted ordinary behavior change, not every possible setting or language/dark-mode reconstruction.
- Every process uses a fresh copy of the same baseline settings. The real settings file is not passed to benchmark processes. Baseline and binary backups are retained locally.
- All candidates are copied to, and launched from, the same main-worktree WtProgram/bin/Debug/WindowTabs.exe path. The main branch is switched to the matching candidate before each launch.
- All recorded samples report dpi=1.00 and eight program nodes. There is no mouse clicking or keyboard input in the harness.
- These are process-first opens, not disk-cache-cold starts. Application/OS caches are not flushed. Order is reversed once, not randomized; sample sizes are small.

## Timing definitions

- **Shown**: the existing manager show operation returned. This is not proof that pixels have appeared.
- **Painted**: after Show, Form.Update has processed pending paint work. It is a software-side paint boundary, not a physical-screen/compositor measurement.
- **Complete**: maximum of Painted, the post-show UI callback, and the program-tree data installation plus TreeView.Update boundary. This includes asynchronous program refresh, unlike Shown. On preload, workspace refresh runs in the queued callback before the completion callback. It does not imply that every hidden page has physically painted.
- **Startup**: main-entry instrumentation through service/plugin initialization and, for next16, preload. It excludes native/CLR work before main and the deliberate two-second settling interval.
- ready-before-close is only a harness waiting marker and is deliberately not used as completion latency.

## Mean latency, milliseconds

Each cell is **Painted / Complete**.

| Candidate | First open | Unchanged reopen | Reopen after setting change | Subsequent changed reopen |
|---|---:|---:|---:|---:|
| next13 | 1628.0 / 2036.5 | 1273.6 / 1556.0 | 1238.3 / 1482.5 | 1103.4 / 1464.5 |
| next14 | 1926.8 / 2361.3 | 1259.7 / 1650.0 | 1019.0 / 1454.5 | 1342.2 / 1736.8 |
| next15 | 1212.4 / 1490.9 | 660.1 / 903.7 | 646.7 / 847.7 | 632.0 / 923.3 |
| next16 | 177.1 / 239.6 | 82.7 / 349.0 | 81.6 / 386.6 | 78.2 / 404.5 |

Sample counts by column are 2 / 6 / 2 / 2 for next13, next14 and next16; 1 / 3 / 1 / 1 for next15 after excluding its unsuccessful process. Total: 42 samples from seven normally exited processes.

The less strict Shown averages, for comparison:

| Candidate | First | Unchanged | Changed | Changed again |
|---|---:|---:|---:|---:|
| next13 | 1626.4 | 1273.3 | 1238.0 | 1103.1 |
| next14 | 1925.1 | 1259.4 | 1018.7 | 1341.8 |
| next15 | 1208.5 | 659.8 | 646.2 | 631.7 |
| next16 | 52.0 | 47.2 | 49.7 | 51.8 |

Preload's initial paint cost explains why its first Shown and Painted differ substantially. Do not describe the roughly 50-ms Shown result as full data completion.

## Startup tradeoff

| Candidate | Mean startup ms | Private bytes at startup, MB | Handles |
|---|---:|---:|---:|
| next13 | 876.2 | 27.20 | 323 |
| next14 | 897.4 | 28.12 | 319.5 |
| next15, successful process only | 892.8 | 27.33 | 323 |
| next16 | 2062.6 | 36.88 | 400.5 |

Compared with next13, next16 pays about 1.19 seconds, 9.68 MB and 78 handles at startup. In exchange, unchanged reopen Painted latency drops by approximately 93.5%, and Complete latency by approximately 77.6%. Memory figures are process private bytes at that boundary, not a leak test or isolated form allocation.

## Abnormal shutdown and exclusions

The second next15 process (PID 63400, raw log 06-next15.tsv) recorded all six program-data/paint completions and the final ready-before-close marker, but then exited with 0xc000041d. Windows Application events first reported 0xc0020001, followed by 0xc000041d, in KERNELBASE.dll. No useful managed stack was recorded.

The exact failing call is not known: the boundary is final dialog close/application shutdown, not necessarily the optimization itself. It could also involve the automated shutdown path. Do not claim this identifies a regression in layout batching, or that it cannot affect other candidates.

The script stopped and restored the preload app. This process's six measurements are preserved but excluded wholesale from the primary table. Remaining next14/next13 processes were completed using the original baseline copies. No other process in this experiment exited abnormally. In particular, both next16 processes completed normally.

## Conclusion and limits

The preload/reuse candidate best meets the requested low-latency first open and reopen behavior. Ordinary settings changes retain that advantage. The reverted candidate has no consistent advantage over the existing optimization. Batching helps, but does not approach retained-dialog latency.

This is a Debug, single-machine, 100%-DPI experiment. It does not establish Release latency, mixed-DPI correctness, every settings-change path, or long-running stability. The next15 shutdown failure needs a separate diagnosis if that candidate is pursued.

## Artifacts and reproduction

- SettingsFourWaySamples.csv contains the 42 included samples, including source version, scenario, timings, DPI and node counts.
- Local raw logs, failed-process data, Windows crash events, settings copies and binary backups: unite/four-way-13c3987e8733402188aeafcdf7ec13bd/. Settings copies and crash metadata are not committed.
- Shared DEBUG code: WtProgram/Shared/SettingsTiming.fs and WtProgram/ManagerViewService/SettingsBenchmark.fs.
- Build the four committed candidates with VS MSBuild, Configuration=Debug and OutputPath=bin/Debug/FourWay13/, FourWay14/, FourWay15/ and FourWay16/ respectively. Do not launch those archive paths directly.
- Run Tools/Run-FourWayBenchmark.ps1 from the main repository after obtaining desktop-test permission. It opens windows and replaces the active process, then restores the original named branch and backed-up runtime. It expects clean candidate branches and four archives whose versions match those branches. After later source changes, rebuild archives before another comparison; do not run the build4 archive against build5 sources. Reproducing the historical commit set should use an isolated reproduction workspace.
- Run Tools/Summarize-FourWayBenchmark.ps1 -ResultDirectory <local-results-folder>. Only processes entered in the successful-process manifest enter the primary summary.
- All four Debug builds succeeded. The next16 Release build also succeeded with existing duplicate-resource merge warnings; the two benchmark environment-key strings are absent from its executable. Release benchmarking was not run.
