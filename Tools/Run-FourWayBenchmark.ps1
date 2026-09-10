param([string]$ExistingResultDirectory = '', [int]$StartIndex = 0)
$ErrorActionPreference = 'Stop'
$repo = (Get-Location).Path
if (@(git status --porcelain).Count -ne 0) { throw 'Commit or preserve local changes before running the benchmark.' }
$originalBranch = git branch --show-current
if (-not $originalBranch) { throw 'Start from a named branch so it can be restored.' }
$runtimeDir = Join-Path $repo 'WtProgram/bin/Debug'
$runtimeExe = Join-Path $runtimeDir 'WindowTabs.exe'
$liveSettings = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'WindowTabs/WindowTabsSettings.txt'
$resultDir = if ($ExistingResultDirectory) { (Resolve-Path -LiteralPath $ExistingResultDirectory).Path } else {
  Join-Path $repo ('unite/four-way-' + [Guid]::NewGuid().ToString('N'))
}
if (-not (Test-Path -LiteralPath $resultDir)) { New-Item -ItemType Directory -Path $resultDir | Out-Null }
$backupDir = Join-Path $resultDir ('original-binaries-' + [Guid]::NewGuid().ToString('N'))
if (-not (Test-Path -LiteralPath $backupDir)) {
  New-Item -ItemType Directory -Path $backupDir | Out-Null
  Get-ChildItem -LiteralPath $runtimeDir -File | Copy-Item -Destination $backupDir
}
$baseline = Join-Path $resultDir 'baseline-settings.txt'
if (-not (Test-Path -LiteralPath $baseline)) { Copy-Item -LiteralPath $liveSettings -Destination $baseline }
$initialHash = (Get-FileHash -LiteralPath $liveSettings -Algorithm SHA256).Hash
Add-Type -AssemblyName System.Windows.Forms
$anchor = [Windows.Forms.Cursor]::Position
$candidates = @{
  13 = 'dev/setting-dialog-speedup_1'
  14 = 'dev/setting-dialog-speedup_2'
  15 = 'dev/setting-dialog-layout-batching'
  16 = 'dev/preload-settings-dialog'
}
foreach ($number in $candidates.Keys) {
  $programSource = git show ($candidates[$number] + ':WtProgram/Program.fs')
  $expected = [regex]::Match(($programSource -join "`n"), 'let version = "([^"]+)"').Groups[1].Value
  $archiveExe = Join-Path $runtimeDir ("FourWay$number/WindowTabs.exe")
  $bytes = [IO.File]::ReadAllBytes($archiveExe)
  $matchesSource = $false
  foreach ($offset in 0,1) {
    if ([Text.Encoding]::Unicode.GetString($bytes,$offset,$bytes.Length-$offset).Contains($expected)) { $matchesSource = $true }
  }
  if (-not $expected -or -not $matchesSource) { throw "Rebuild FourWay$number from its current committed branch before testing." }
}
$manifest = if (Test-Path -LiteralPath (Join-Path $resultDir 'manifest.json')) {
  @(Get-Content -LiteralPath (Join-Path $resultDir 'manifest.json') -Raw | ConvertFrom-Json)
} else { @() }
$currentApp = $null
function Stop-ExactApp {
  Get-Process WindowTabs -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $runtimeExe } |
    ForEach-Object { Stop-Process -Id $_.Id; $_.WaitForExit() }
}
function Copy-Candidate([int]$number) {
  $source = Join-Path $runtimeDir ("FourWay" + $number)
  if (-not (Test-Path -LiteralPath (Join-Path $source 'WindowTabs.exe'))) { throw "Missing candidate $number" }
  Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $runtimeDir -Force
}
function Launch-App([string]$settings, [string]$log) {
  $info = [Diagnostics.ProcessStartInfo]::new($runtimeExe)
  $info.UseShellExecute = $false
  $info.WorkingDirectory = $runtimeDir
  $info.Environment.Remove('WINDOWTABS_BENCHMARK_SETTINGS') | Out-Null
  $info.Environment.Remove('WINDOWTABS_BENCHMARK_LOG') | Out-Null
  if ($settings) {
    $info.Environment['WINDOWTABS_BENCHMARK_SETTINGS'] = $settings
    $info.Environment['WINDOWTABS_BENCHMARK_LOG'] = $log
  }
  return [Diagnostics.Process]::Start($info)
}
Write-Output "RESULT_DIRECTORY=$resultDir"
try {
  Stop-ExactApp
  $sequence = @(13,14,15,16,16,15,14,13)
  for ($index = $StartIndex; $index -lt $sequence.Count; $index++) {
    $number = $sequence[$index]
    git switch $candidates[$number]
    if ($LASTEXITCODE -ne 0) { throw 'Branch switch failed' }
    Copy-Candidate $number
    $stem = ('{0:D2}-next{1}' -f ($index+1),$number)
    $settingsCopy = Join-Path $resultDir ($stem + '-settings.txt')
    $log = Join-Path $resultDir ($stem + '.tsv')
    if (Test-Path -LiteralPath $log) { throw "Refusing to overwrite existing results: $stem" }
    Copy-Item -LiteralPath $baseline -Destination $settingsCopy
    $started = Get-Date
    $currentApp = Launch-App $settingsCopy $log
    Write-Output "RUN $stem PID=$($currentApp.Id)"
    # An external timeout still works if the application's UI thread hangs.
    if (-not $currentApp.WaitForExit(180000)) { throw "Candidate timeout: $stem" }
    if ($currentApp.ExitCode -ne 0) { throw "Candidate failed: $stem exit=$($currentApp.ExitCode)" }
    if (-not (Test-Path -LiteralPath $log)) { throw "Missing log: $stem" }
    $rows = Import-Csv -LiteralPath $log -Delimiter "`t" -Header Run,Pid,Version,Stage,Milliseconds
    if (@($rows | Where-Object Stage -eq 'request').Count -ne 6) { throw "Incomplete six-run benchmark: $stem" }
    if ($rows.Stage -match 'timeout|benchmark-error|missing-form') { throw "Invalid benchmark run: $stem" }
    $manifest += [pscustomobject]@{
      Candidate=$number; Pass=([int][Math]::Floor($index/4)+1); Commit=(git rev-parse HEAD)
      Log=$log; Started=$started.ToString('o'); Seconds=((Get-Date)-$started).TotalSeconds
      CursorX=$anchor.X; CursorY=$anchor.Y; SettingsHash=$initialHash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultDir 'manifest.json') -Encoding utf8
    Write-Output "COMPLETE $stem"
    $currentApp = $null
  }
} finally {
  Stop-ExactApp
  git switch $originalBranch
  if ($LASTEXITCODE -ne 0) { throw 'Could not restore source branch' }
  # Restore the exact runtime present before this batch, not an older
  # benchmark archive that may no longer match the adopted branch.
  Get-ChildItem -LiteralPath $backupDir -File | Copy-Item -Destination $runtimeDir -Force
  $finalHash = (Get-FileHash -LiteralPath $liveSettings -Algorithm SHA256).Hash
  $restored = Launch-App '' ''
  Write-Output "RESTORED_PID=$($restored.Id)"
  Write-Output "LIVE_SETTINGS_UNCHANGED=$($initialHash -eq $finalHash)"
  Write-Output "RESULT_DIRECTORY=$resultDir"
}
