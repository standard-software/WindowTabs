# Settings regression tests

Run these four scripts on Windows with Visual Studio's **.NET Framework** F# Interactive, not `dotnet fsi`. Install Visual Studio with F# support. From the repository root, run the following in PowerShell:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vsPath) { throw 'Visual Studio with MSBuild is required.' }
$msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
$fsi = Join-Path $vsPath 'Common7\IDE\CommonExtensions\Microsoft\FSharp\Tools\fsi.exe'
if (-not (Test-Path -LiteralPath $fsi)) { throw 'Install F# support in Visual Studio.' }
& $msbuild .\WtProgram\WtProgram.fsproj /t:Build /p:Configuration=Debug /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
foreach ($test in 'AppDialog', 'SettingsPreload', 'SettingsRefresh', 'DpiControls') {
    & $fsi --exec ".\Tools\$test.Tests.fsx"
    if ($LASTEXITCODE -ne 0) { throw "$test tests failed." }
}
```

If the build reports locked output files, close WindowTabs and retry. No custom `OutputPath` is needed. Each script can also run independently using the same `fsi.exe` command after building. Separate processes isolate test services and static state.

The tests do not show windows, send desktop input, or modify user settings. DPI checks save two offscreen snapshots under `WtProgram/bin/Debug/TestResults`. They do not replace manual mixed-DPI or full dialog lifecycle testing.
