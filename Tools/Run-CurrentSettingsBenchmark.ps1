# Run from the main worktree after committing and building DpiChoiceValidation.
# Opens settings automatically; obtain desktop-use permission before running.
$ErrorActionPreference = 'Stop'
if (@(git diff HEAD --name-only).Count) { throw 'Commit tracked changes first.' }
$runtime = (Resolve-Path 'WtProgram/bin/Debug').Path
$exe = Join-Path $runtime 'WindowTabs.exe'
$candidate = Join-Path $runtime 'DpiChoiceValidation'
$version = [regex]::Match((git show HEAD:WtProgram/Program.fs | Out-String), 'let version = "([^"]+)"').Groups[1].Value
$bytes = [IO.File]::ReadAllBytes((Join-Path $candidate 'WindowTabs.exe'))
if (-not $version -or -not (@(0,1 | Where-Object {
    [Text.Encoding]::Unicode.GetString($bytes,$_,$bytes.Length-$_).Contains($version)
}).Count)) { throw 'Candidate version does not match committed source.' }
$result = Join-Path (Get-Location) ('unite/current-settings-' + [Guid]::NewGuid().ToString('N'))
$backup = Join-Path $result 'original-binaries'
New-Item -ItemType Directory -Path $backup | Out-Null
Get-ChildItem -LiteralPath $runtime -File | Copy-Item -Destination $backup
$settings = Join-Path $env:APPDATA 'WindowTabs/WindowTabsSettings.txt'
$hash = (Get-FileHash -LiteralPath $settings).Hash
Copy-Item -LiteralPath $settings -Destination (Join-Path $result 'baseline.txt')
function Stop-Main {
    Get-Process WindowTabs -ErrorAction SilentlyContinue | Where-Object Path -eq $exe |
        ForEach-Object { Stop-Process -Id $_.Id; $_.WaitForExit() }
}
function Launch([string]$copy, [string]$log) {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.WorkingDirectory = $runtime
    $info.UseShellExecute = $false
    $info.Environment.Remove('WINDOWTABS_BENCHMARK_SETTINGS') | Out-Null
    $info.Environment.Remove('WINDOWTABS_BENCHMARK_LOG') | Out-Null
    if ($copy) {
        $info.Environment['WINDOWTABS_BENCHMARK_SETTINGS'] = $copy
        $info.Environment['WINDOWTABS_BENCHMARK_LOG'] = $log
    }
    [Diagnostics.Process]::Start($info)
}
$success = $false
$manifest = @()
try {
    Stop-Main
    Get-ChildItem -LiteralPath $candidate -File | Copy-Item -Destination $runtime -Force
    Write-Output "RESULT_DIRECTORY=$result"
    foreach ($pass in 1,2) {
        $copy = Join-Path $result "pass$pass-settings.txt"
        $log = Join-Path $result "pass$pass.tsv"
        Copy-Item -LiteralPath (Join-Path $result 'baseline.txt') -Destination $copy
        $app = Launch $copy $log
        Write-Output "PASS=$pass PID=$($app.Id)"
        if (-not $app.WaitForExit(55000)) { throw "Process timeout: $($app.Id)" }
        if ($app.ExitCode -ne 0) { throw "Abnormal exit: $($app.ExitCode)" }
        $rows = Import-Csv -LiteralPath $log -Delimiter "`t" -Header Run,Pid,Version,Stage,Ms
        if (@($rows | Where-Object Stage -eq 'request').Count -ne 6 -or
            ($rows.Stage -match 'timeout|benchmark-error|missing-form')) { throw 'Incomplete measurements.' }
        $manifest += [pscustomobject]@{ Pass=$pass; Pid=$app.Id; ExitCode=$app.ExitCode; Commit=(git rev-parse HEAD); Log=$log }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $result 'manifest.json') -Encoding utf8
        Write-Output "NORMAL_EXIT=$($app.Id)"
    }
    $success = $true
} finally {
    Stop-Main
    if (-not $success) { Get-ChildItem -LiteralPath $backup -File | Copy-Item -Destination $runtime -Force }
    $restored = Launch '' ''
    Write-Output "NORMAL_RUNTIME_PID=$($restored.Id)"
    Write-Output "LIVE_SETTINGS_UNCHANGED=$($hash -eq (Get-FileHash -LiteralPath $settings).Hash)"
}
& (Join-Path $PSScriptRoot 'Summarize-FourWayBenchmark.ps1') -ResultDirectory $result
