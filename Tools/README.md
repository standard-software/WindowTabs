# Tests

Run the git-tracked `*.Tests.fsx` files from PowerShell 7 or Windows PowerShell 5.1:

```powershell
pwsh -NoProfile -File Tools/Run-Tests.ps1
powershell -NoProfile -File Tools/Run-Tests.ps1 -Filter '*Order*' -Parallel 1 -TimeoutSec 300
```

In a git working tree, discovery uses `git ls-files`; untracked and ignored
files are not run. To include local development tests, pass `-IncludeUntracked`.
If git is unavailable or the working tree cannot be verified, discovery stops
with exit code 2. Only `-IncludeUntracked` enables scanning. Scanning never
descends into subdirectories of `Tools`. Both discovery methods skip `unite`, `bin`, `obj`, `packages`, `.git`,
and symbolic links or junctions.

Review side effects before running new tests. A first line of
`// TestRuntime: Manual` prevents execution, including with `-IncludeUntracked`.
These files are counted as manual tests, not passes. Declaration-like text in
anywhere in the file with invalid spelling, position, or value fails without
running. Use this marker for tests that access real settings, the registry, live desktop windows, or other
processes, and for helper files that are not standalone tests. The optional
exclusion table in the runner accepts repository-relative wildcard patterns.

Tests use `dotnet fsi` by default (requires the .NET SDK). A first line of
`// TestRuntime: Framework` selects Visual Studio's .NET Framework
`fsiAnyCpu.exe`, found through `vswhere` or standard installation locations.
Framework tests receive `WtProgram/bin/Debug` as an assembly search directory.
Tests referencing build assemblies require a matching Debug build; referencing
an executable as a library does not launch the application. Keep required
assembly references in each test. The runner supplies no test arguments.

The default is two concurrent tests and a five-minute timeout per test.
Termination and output collection each have bounded waits (up to five seconds
per step); cleanup continues after an individual stop failure.
`-Filter` matches a filename or repository-relative path using wildcards.
Logs and `results.json` go to a unique directory under `$env:TEMP`.
The summary separates passes, failures, exclusions, unavailable runtimes,
manual tests, and the number executed. Exit codes are 0 for a completed run
without failures, 1 for failures, and 2 if no test process ran (including
empty selection, manual-only selection, or unavailable runtimes only).
Exit code 0 means all executed tests passed; check the summary for
unavailable-runtime skips. A pass reflects the test's exit code; the runner does not infer assertion counts from output.

Use symbolic fixtures such as `title-1` and `app.exe`. Never copy real window
titles, document names, personal paths, user names, emails, handles, timestamps,
or trace data into tests or comments. Use English comments and messages;
localized strings may be inputs to localization tests. Keep automated tests
independent of real settings, the registry, running applications, and the
network. Review hidden UI code for Debug logging side effects too. Put temporary
files under the system temporary directory. Never commit logs, screenshots,
or test results.

Test scripts and the `Tools` folder are git-ignored by default, so local
drafts stay out of the repository. After reviewing a new test against the
rules above, track it explicitly with `git add -f <file>`.
