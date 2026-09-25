# End-to-end check that a window left in WindowTabs' parking spot, in no
# group, is brought back on screen. Runs the real WindowTabs.exe on the real
# desktop.
#
#   pwsh Tools\ParkedOrphan.E2E.ps1 -Exe WtProgram\bin\Debug\WindowTabs.exe -SettingsFile test.json
#
# -SettingsFile is copied and handed to WindowTabs through
# WINDOWTABS_BENCHMARK_SETTINGS, so the user's own settings are neither read
# nor written. That variable is honoured by a Debug build only; with a Release
# build, leave -SettingsFile out and WindowTabs runs on the real settings. For
# a run that touches nothing but the test windows, the file should have
# IsDisabled false, EnableTabbingByDefault false, IncludedPaths holding
# powershell.exe only, and no SavedTabGroupsForRestart.
#
# Three test windows are opened in a child powershell.exe:
#   probe   - on screen. WindowTabs must give it a tab, or the run proves
#             nothing (a disabled WindowTabs would "pass" the control and fail
#             the parked window for the wrong reason). Checked first.
#   parked  - top-left at the point hideOffScreen parks a window at (just past
#             the bottom right corner of every work area). WindowTabs never
#             grouped it, which is the state a tab is left in when it drops out
#             of its group while parked, or when WindowTabs is killed and
#             restarted. Expected: back on a monitor within -TimeoutSec.
#   control - off screen at (-6000,-6000), outside the parking region.
#             Expected: left alone.
#
# WindowTabs must not already be running. It is killed at the end, and any
# window it leaves in the parking region is moved back on screen, so the
# script does not itself strand anything.
#
# Exit code 0 when both expectations hold, 1 otherwise.
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $SettingsFile,
    [int] $StartupSec = 10,
    [int] $TimeoutSec = 30
)
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class E2E {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    public struct RECT { public int L, T, R, B; }
    public static IntPtr Find(string title) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => { var s = new StringBuilder(256); GetWindowText(h, s, 256);
            if (s.ToString() == title) { found = h; return false; } return true; }, IntPtr.Zero);
        return found;
    }
    public static List<IntPtr> Visible() {
        var o = new List<IntPtr>();
        EnumWindows((h, l) => { if (IsWindowVisible(h) && !IsIconic(h)) o.Add(h); return true; }, IntPtr.Zero);
        return o;
    }
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    // A visible window of process pid lying along the top edge of target:
    // the tab strip WindowTabs puts on a window it has grouped.
    public static bool HasStripOn(int pid, IntPtr target) {
        RECT t; if (!GetWindowRect(target, out t)) return false;
        bool found = false;
        EnumWindows((h, l) => { int p; GetWindowThreadProcessId(h, out p); RECT r;
            if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out r) && r.R > r.L && r.B > r.T &&
                Math.Min(r.R, t.R) > Math.Max(r.L, t.L) && r.B >= t.T - 60 && r.T <= t.T + 60) { found = true; return false; }
            return true; }, IntPtr.Zero);
        return found;
    }
    public static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }
}
'@

function Get-Rect([IntPtr] $h) { $r = New-Object E2E+RECT; [E2E]::GetWindowRect($h, [ref]$r) | Out-Null; $r }
function Test-OnScreen($r) {
    foreach ($s in [System.Windows.Forms.Screen]::AllScreens) {
        $b = $s.Bounds
        if ([Math]::Min($b.Right, $r.R) -gt [Math]::Max($b.Left, $r.L) -and
            [Math]::Min($b.Bottom, $r.B) -gt [Math]::Max($b.Top, $r.T)) { return $true }
    }
    $false
}

$work = [System.Windows.Forms.Screen]::AllScreens | ForEach-Object WorkingArea
$park = @{ X = ($work | Measure-Object Right -Maximum).Maximum + 100
           Y = ($work | Measure-Object Bottom -Maximum).Maximum + 100 }
$maxRight = ($work | Measure-Object Right -Maximum).Maximum
$maxBottom = ($work | Measure-Object Bottom -Maximum).Maximum

if (Get-Process WindowTabs -ErrorAction SilentlyContinue) { throw 'WindowTabs is already running; stop it first.' }

$stamp = Get-Date -Format 'HHmmss'
$parkedTitle = "WT-E2E parked $stamp"
$controlTitle = "WT-E2E control $stamp"
$probeTitle = "WT-E2E probe $stamp"
$child = @"
Add-Type -AssemblyName System.Windows.Forms
function New-TestForm(`$t, `$x, `$y) {
    `$f = New-Object System.Windows.Forms.Form
    `$f.Text = `$t; `$f.StartPosition = 'Manual'
    `$f.Location = New-Object System.Drawing.Point(`$x, `$y)
    `$f.Size = New-Object System.Drawing.Size(640, 400)
    `$f.Show(); `$f
}
# Started with -WindowStyle Hidden, the process's first ShowWindow is turned
# into SW_HIDE. Spend it on a form nobody needs.
`$z = New-TestForm 'WT-E2E absorb' 300 300; `$z.Hide()
`$p = New-TestForm '$probeTitle' 300 300
`$a = New-TestForm '$parkedTitle' $($park.X) $($park.Y)
`$b = New-TestForm '$controlTitle' -6000 -6000
# A foreground change is what makes WindowTabs look at new windows; without
# one it may not look again for a long time.
`$t = New-Object System.Windows.Forms.Timer
`$t.Interval = 1500
`$t.add_Tick({ `$t.Stop(); `$p.Activate() })
`$t.Start()
[System.Windows.Forms.Application]::Run(`$a)
"@
$enc = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))

$wt = $null; $forms = $null
try {
    Write-Host "park point ($($park.X),$($park.Y)); starting $Exe"
    if ($SettingsFile) {
        $settingsCopy = Join-Path ([IO.Path]::GetTempPath()) "wt-e2e-settings-$stamp.json"
        Copy-Item $SettingsFile $settingsCopy
        $env:WINDOWTABS_BENCHMARK_SETTINGS = $settingsCopy
        Write-Host "settings: $settingsCopy"
    }
    $wt = Start-Process -FilePath $Exe -PassThru
    Remove-Item Env:WINDOWTABS_BENCHMARK_SETTINGS -ErrorAction SilentlyContinue
    Start-Sleep -Seconds $StartupSec
    if ($wt.HasExited) { throw "WindowTabs exited during startup (code $($wt.ExitCode))" }

    $forms = Start-Process powershell.exe -ArgumentList '-NoProfile', '-EncodedCommand', $enc -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $hp = [E2E]::Find($parkedTitle); $hc = [E2E]::Find($controlTitle); $hb = [E2E]::Find($probeTitle) }
    until (($hp -ne [IntPtr]::Zero -and $hc -ne [IntPtr]::Zero -and $hb -ne [IntPtr]::Zero) -or (Get-Date) -gt $deadline)
    if ($hp -eq [IntPtr]::Zero -or $hc -eq [IntPtr]::Zero -or $hb -eq [IntPtr]::Zero) { throw 'test windows did not appear' }
    $r = Get-Rect $hp
    Write-Host "parked window created at ($($r.L),$($r.T))"
    $deadline = (Get-Date).AddSeconds(25)
    do { Start-Sleep -Milliseconds 250 } until ([E2E]::HasStripOn($wt.Id, $hb) -or (Get-Date) -gt $deadline)
    if (-not [E2E]::HasStripOn($wt.Id, $hb)) {
        throw 'PRECONDITION: WindowTabs gave the on-screen probe no tab strip - disabled, or powershell.exe not tabbed. The run proves nothing.'
    }
    Write-Host 'precondition: WindowTabs is tabbing (probe got a tab strip)'

    $sw = [Diagnostics.Stopwatch]::StartNew(); $back = $false
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Milliseconds 500
        if (Test-OnScreen (Get-Rect $hp)) { $back = $true; break }
    }
    $rp = Get-Rect $hp; $rc = Get-Rect $hc
    $controlLeft = -not (Test-OnScreen $rc)
    "parked:  {0} after {1:N1}s, now ({2},{3})" -f ($(if ($back) { 'BACK ON SCREEN' } else { 'STILL PARKED' })), $sw.Elapsed.TotalSeconds, $rp.L, $rp.T
    "control: {0}, now ({1},{2})" -f ($(if ($controlLeft) { 'left alone' } else { 'MOVED' })), $rc.L, $rc.T
    if ($back -and $controlLeft) { 'RESULT: PASS'; $code = 0 } else { 'RESULT: FAIL'; $code = 1 }
}
finally {
    if ($forms -and -not $forms.HasExited) { Stop-Process -Id $forms.Id -Force }
    if ($wt -and -not $wt.HasExited) { Stop-Process -Id $wt.Id -Force; Start-Sleep -Seconds 1 }
    if ($settingsCopy) { Remove-Item $settingsCopy -ErrorAction SilentlyContinue }
    # Undo what a killed WindowTabs leaves behind: anything in the parking region comes home.
    $homeArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    foreach ($h in [E2E]::Visible()) {
        $r = Get-Rect $h
        if ($r.L -ge $maxRight -and $r.T -ge $maxBottom -and -not (Test-OnScreen $r)) {
            [E2E]::SetWindowPos($h, [IntPtr]::Zero, $homeArea.Left + 80, $homeArea.Top + 60, 0, 0, 0x0001 -bor 0x0004) | Out-Null
            Write-Host "cleanup: moved '$([E2E]::Title($h))' back from ($($r.L),$($r.T))"
        }
    }
}
exit $code
