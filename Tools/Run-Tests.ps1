[CmdletBinding()]
param(
    [string]$Filter = '*',
    [switch]$IncludeUntracked,
    [ValidateRange(1, 64)][int]$Parallel = 2,
    [ValidateRange(1, 86400)][int]$TimeoutSec = 300
)

# Optional repository-relative patterns: 'path/*.Tests.fsx' = 'Reason'
# Review side effects before changing exclusions or runtime markers.
$ExcludedTests = @{}

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# An explicit first-line marker avoids guessing from transitive references.
function Find-FrameworkFsi {
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } | Select-Object -Unique
    foreach ($root in $roots) {
        $vswhere = Join-Path $root 'Microsoft Visual Studio/Installer/vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $found = @(& $vswhere -products '*' -sort -find '**/fsiAnyCpu.exe')
            foreach ($candidate in $found) {
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
            }
        }
    }
    foreach ($root in $roots) {
        foreach ($version in @('18', '2022', '2019', '2017')) {
            foreach ($edition in @('Community', 'Professional', 'Enterprise', 'BuildTools')) {
                $candidate = Join-Path $root ("Microsoft Visual Studio/$version/$edition/Common7/IDE/CommonExtensions/Microsoft/FSharp/Tools/fsiAnyCpu.exe")
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
            }
        }
    }
    return $null
}
$dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { $null }
$frameworkFsi = Find-FrameworkFsi
$buildDirectory = Join-Path $repoRoot 'WtProgram/bin/Debug'
$logDirectory = Join-Path $env:TEMP ('WindowTabs-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$ignoredDirectories = @('unite', 'bin', 'obj', 'packages', '.git')

function Find-Tests([string]$Directory) {
    foreach ($entry in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($entry.LinkType -in @('SymbolicLink', 'Junction')) { continue }
        if ($entry.PSIsContainer) {
            if ($ignoredDirectories -notcontains $entry.Name -and $Directory -ne (Join-Path $repoRoot 'Tools')) { Find-Tests $entry.FullName }
        } elseif ($entry.Name -like '*.Tests.fsx') {
            $entry
        }
    }
}

# Accept the git listing separately so selection can be checked without changing git.
function Select-TrackedTests([string[]]$Paths) {
    foreach ($relative in $Paths | Sort-Object -Unique) {
        if (-not $relative -or $relative -notlike '*.Tests.fsx') { continue }
        $parts = $relative.Replace('\', '/').Split('/')
        if ([IO.Path]::IsPathRooted($relative) -or $parts -contains '..' -or
            @($parts | Where-Object { $ignoredDirectories -contains $_ }).Count -gt 0) { continue }
        $current = $repoRoot
        $linked = $false
        foreach ($part in $parts) {
            $current = Join-Path $current $part
            if (Test-Path -LiteralPath $current) {
                $entry = Get-Item -LiteralPath $current -Force
                if ($entry.LinkType -in @('SymbolicLink', 'Junction')) { $linked = $true; break }
            }
        }
        if (-not $linked -and (Test-Path -LiteralPath $current -PathType Leaf)) {
            Get-Item -LiteralPath $current -Force
        }
    }
}

function Read-GitOutput([string]$Executable, [string]$Arguments) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = '-C "' + $repoRoot + '" ' + $Arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            try { Stop-TestProcess $process } catch { }
            throw 'Git command timed out.'
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($output, $errors), 5000)) {
            throw 'Git output collection timed out.'
        }
        [pscustomobject]@{ ExitCode=$process.ExitCode; Output=$output.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

function Get-TestFiles {
    if ($IncludeUntracked) {
        Write-Host 'Discovery: working-tree tests (excluding nested Tools directories).'
        Find-Tests $repoRoot
        return
    }
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $git) { throw 'Git is unavailable. Use -IncludeUntracked explicitly to scan the working tree.' }
    $location = Read-GitOutput $git.Source 'rev-parse --is-inside-work-tree'
    if ($location.ExitCode -ne 0 -or $location.Output.Trim() -ne 'true') {
        throw 'Unable to verify the git working tree. Use -IncludeUntracked explicitly to scan it.'
    }
    $listing = Read-GitOutput $git.Source 'ls-files -z -- "*.Tests.fsx"'
    if ($listing.ExitCode -ne 0) { throw 'Unable to list tracked tests.' }
    Write-Host 'Discovery: git-tracked tests only.'
    Select-TrackedTests ($listing.Output.Split([char]0))
}

# Recognize declaration keys with up to two edits, including swapped letters.
function Test-RuntimeKey([string]$Key) {
    $keyText = ($Key -replace '[^a-zA-Z]', '').ToLowerInvariant()
    $expected = 'testruntime'
    if ([Math]::Abs($keyText.Length - $expected.Length) -gt 2) { return $false }
    $previous = @(0..$expected.Length)
    for ($i = 1; $i -le $keyText.Length; $i++) {
        $row = @($i) + @(0) * $expected.Length
        for ($j = 1; $j -le $expected.Length; $j++) {
            $cost = if ($keyText[$i - 1] -eq $expected[$j - 1]) { 0 } else { 1 }
            $row[$j] = [Math]::Min([Math]::Min($row[$j - 1] + 1, $previous[$j] + 1), $previous[$j - 1] + $cost)
        }
        $previous = $row
    }
    return $previous[$expected.Length] -le 2
}

# Inspect all lines; malformed or misplaced declarations never select a runtime.
function Get-TestRuntime([string[]]$Lines) {
    $declarations = @()
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $looksLikeDeclaration = ($Lines[$i] -replace '[^a-zA-Z]', '') -match '(?i)TestRuntime'
        if ($Lines[$i] -match '^\s*(?://|\(\*|#)?\s*([A-Za-z][A-Za-z_-]*)') {
            $looksLikeDeclaration = $looksLikeDeclaration -or (Test-RuntimeKey $Matches[1])
        }
        $looksLikeDeclaration = $looksLikeDeclaration -or ($Lines[$i] -match '^\s*//.*:\s*Manual\s*$')
        if ($looksLikeDeclaration) {
            $declarations += $i
        }
    }
    if ($declarations.Count -eq 0) { return 'DotNet' }
    if ($declarations.Count -eq 1 -and $declarations[0] -eq 0 -and $Lines[0] -ceq '// TestRuntime: Manual') { return 'Manual' }
    if ($declarations.Count -eq 1 -and $declarations[0] -eq 0 -and $Lines[0] -ceq '// TestRuntime: Framework') { return 'Framework' }
    throw 'Invalid TestRuntime declaration. Use an exact first-line Manual or Framework marker.'
}

function Receive-TestOutput($OutputTask, $ErrorTask, [int]$WaitMs = 5000) {
    $tasks = [Threading.Tasks.Task[]]@($OutputTask, $ErrorTask)
    if (-not [Threading.Tasks.Task]::WaitAll($tasks, $WaitMs)) { throw 'Output collection timed out.' }
    return $OutputTask.GetAwaiter().GetResult() + [Environment]::NewLine + $ErrorTask.GetAwaiter().GetResult()
}

function Stop-TestProcess($Process) {
    if ($Process.HasExited) { return }
    $stopInfo = New-Object Diagnostics.ProcessStartInfo
    $stopInfo.FileName = Join-Path $env:SystemRoot 'System32/taskkill.exe'
    $stopInfo.Arguments = '/PID ' + $Process.Id + ' /T /F'
    $stopInfo.UseShellExecute = $false
    $stopInfo.CreateNoWindow = $true
    $stopInfo.RedirectStandardOutput = $true
    $stopInfo.RedirectStandardError = $true
    $stop = [Diagnostics.Process]::Start($stopInfo)
    try {
        $stopOutput = $stop.StandardOutput.ReadToEndAsync()
        $stopError = $stop.StandardError.ReadToEndAsync()
        if (-not $stop.WaitForExit(5000)) {
            try { $stop.Kill() } catch { }
            throw 'Process termination command timed out.'
        }
        if (-not $Process.WaitForExit(5000)) { throw 'Unable to stop a timed-out test.' }
    } finally { $stop.Dispose() }
}

function Close-TestJobs($Jobs) {
    $failures = 0
    foreach ($job in $Jobs) {
        try { Stop-TestProcess $job.Process } catch {
            $failures++
            Write-Warning ('Unable to stop test: ' + $job.Test.File)
        } finally {
            try { $job.Process.Dispose() } catch { $failures++ }
        }
    }
    return $failures
}

$pending = New-Object 'Collections.Generic.Queue[object]'
$active = New-Object 'Collections.Generic.List[object]'
$results = New-Object 'Collections.Generic.List[object]'
$cleanupPending = New-Object 'Collections.Generic.List[object]'
$executed = 0
try { $testFiles = @(Get-TestFiles | Sort-Object FullName) } catch {
    Write-Output 'Test discovery failed. Git must be available and identify a working tree; use -IncludeUntracked explicitly to scan. Exit code 2.'
    exit 2
}
foreach ($file in $testFiles) {
    $relative = $file.FullName.Substring($repoRoot.Length + 1).Replace('\', '/')
    if ($file.Name -notlike $Filter -and $relative -notlike $Filter) { continue }
    $exclusion = @($ExcludedTests.Keys | Where-Object { $relative -like $_ } | Sort-Object | Select-Object -First 1)
    if ($exclusion.Count -gt 0) {
        Write-Output ('SKIP {0}: {1}' -f $relative, $ExcludedTests[$exclusion[0]])
        $results.Add([pscustomobject]@{ File=$relative; Status='Skipped'; ExitCode=$null; Seconds=0; Reason=$ExcludedTests[$exclusion[0]]; Log=$null })
    } else {
        try { $runtime = Get-TestRuntime @(Get-Content -LiteralPath $file.FullName) } catch {
            Write-Output ('INVALID MARKER {0}: test not executed.' -f $relative)
            $results.Add([pscustomobject]@{ File=$relative; Status='InvalidMarker'; ExitCode=1; Seconds=0; Reason='Invalid TestRuntime declaration.'; Log=$null })
            continue
        }
        if ($runtime -eq 'Manual') {
            Write-Output ('MANUAL {0}: not executed.' -f $relative)
            $results.Add([pscustomobject]@{ File=$relative; Status='Manual'; ExitCode=$null; Seconds=0; Reason='Manual test marker.'; Log=$null })
            continue
        }
        $executable = if ($runtime -eq 'Framework') { $frameworkFsi } else { $dotnet }
        if (-not $executable) {
            Write-Output ('RUNTIME UNAVAILABLE SKIP {0}: {1}' -f $relative, $runtime)
            $results.Add([pscustomobject]@{ File=$relative; Status='RuntimeUnavailable'; Runtime=$runtime; ExitCode=$null; Seconds=0; Reason='Required runtime was not found.'; Log=$null })
            continue
        }
        $pending.Enqueue([pscustomobject]@{ File=$relative; FullName=$file.FullName; Runtime=$runtime; Executable=$executable })
    }
}

$cleanupFailures = 0
try {
    while ($pending.Count -gt 0 -or $active.Count -gt 0) {
        while ($pending.Count -gt 0 -and $active.Count -lt $Parallel) {
            $test = $pending.Dequeue()
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $test.Executable
            $info.Arguments = if ($test.Runtime -eq 'Framework') {
                '--nologo --utf8output --preferreduilang:en-US --lib:"' + $buildDirectory + '" --exec "' + $test.FullName + '"'
            } else { 'fsi --preferreduilang:en-US --exec "' + $test.FullName + '"' }
            $info.WorkingDirectory = $repoRoot
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardOutputEncoding = $utf8
            $info.StandardErrorEncoding = $utf8
            $watch = [Diagnostics.Stopwatch]::StartNew()
            try {
                $process = [Diagnostics.Process]::Start($info)
                $executed++
                $active.Add([pscustomobject]@{
                    Test=$test; Process=$process; Watch=$watch
                    Output=$process.StandardOutput.ReadToEndAsync()
                    Error=$process.StandardError.ReadToEndAsync()
                })
            } catch {
                $watch.Stop()
                $results.Add([pscustomobject]@{ File=$test.File; Status='StartError'; ExitCode=1; Seconds=$watch.Elapsed.TotalSeconds; Reason='Unable to start FSI.'; Log=$null })
                Write-Output ('FAIL {0}: unable to start FSI' -f $test.File)
            }
        }
        foreach ($job in @($active.ToArray())) {
            $timedOut = -not $job.Process.HasExited -and $job.Watch.Elapsed.TotalSeconds -ge $TimeoutSec
            $stopFailed = $false
            if ($timedOut) {
                try { Stop-TestProcess $job.Process } catch { $stopFailed = $true }
            }
            if (-not $stopFailed -and -not $job.Process.HasExited) { continue }
            $code = if ($job.Process.HasExited) { $job.Process.ExitCode } else { 1 }
            $status = if ($stopFailed) { 'StopError' } elseif ($timedOut) { 'Timeout' } elseif ($code -eq 0) { 'Passed' } else { 'Failed' }
            try { $output = Receive-TestOutput $job.Output $job.Error } catch {
                $output = 'Output collection failed or timed out.'
                if ($status -ne 'StopError' -and $status -ne 'Timeout') { $status = 'OutputError' }
            }
            $job.Watch.Stop()
            if ($status -eq 'Failed' -and $output -match 'error FS[0-9]+') { $status = 'CompileError' }
            $logName = $job.Test.File.Replace('/', '_') + '.log'
            [IO.File]::WriteAllText((Join-Path $logDirectory $logName), $output, $utf8)
            $seconds = [Math]::Round($job.Watch.Elapsed.TotalSeconds, 3)
            $results.Add([pscustomobject]@{ File=$job.Test.File; Runtime=$job.Test.Runtime; Status=$status; ExitCode=$code; Seconds=$seconds; Reason=''; Log=$logName })
            Write-Output ('{0} {1} ({2:N2}s)' -f $status.ToUpperInvariant(), $job.Test.File, $seconds)
            if ($stopFailed) { $cleanupPending.Add($job) } else { $job.Process.Dispose() }
            $active.Remove($job) | Out-Null
        }
        if ($active.Count -gt 0) { Start-Sleep -Milliseconds 100 }
    }
} finally {
    $cleanupFailures = Close-TestJobs @($active.ToArray() + $cleanupPending.ToArray())
}
$passed = @($results | Where-Object { $_.Status -eq 'Passed' }).Count
$skipped = @($results | Where-Object { $_.Status -eq 'Skipped' }).Count
$unavailable = @($results | Where-Object { $_.Status -eq 'RuntimeUnavailable' }).Count
$manual = @($results | Where-Object { $_.Status -eq 'Manual' }).Count
$failed = $results.Count - $passed - $skipped - $unavailable - $manual
[IO.File]::WriteAllText((Join-Path $logDirectory 'results.json'), (ConvertTo-Json -InputObject @($results.ToArray()) -Depth 4), $utf8)
Write-Output ('Total: {0}; passed: {1}; failed: {2}; skipped: {3}; runtime unavailable: {4}; manual: {5}; executed: {6}' -f $results.Count, $passed, $failed, $skipped, $unavailable, $manual, $executed)
Write-Output ('Logs: ' + $logDirectory)
if ($executed -eq 0) { Write-Output 'No tests were executed (0). Exit code 2.'; exit 2 }
if ($failed -gt 0 -or $cleanupFailures -gt 0) { exit 1 }
exit 0
