# Verifies that the files shipped by the installer stay in sync with the
# build output. Called from build_release.bat with the repo root as CWD:
#   -Phase pre  : after the WtProgram build — compare the file names in
#                 WtProgram\bin\Release\{Language,Settings} against the
#                 <File> entries in WtSetup\WtSetup.wxs (the MSI file list
#                 is static; unlisted files are omitted silently, which is
#                 how the ss_2026.07.16 MSI shipped without 6 languages)
#   -Phase post : after the MSI build — administrative-extract the MSI and
#                 compare its ACTUAL contents against the build output
param(
    [Parameter(Mandatory=$true)][ValidateSet('pre','post')] [string]$Phase,
    [string]$Wxs = 'WtSetup\WtSetup.wxs',
    [string]$Msi = 'exe\installer\WtSetup.msi'
)
$ErrorActionPreference = 'Stop'
$failed = $false

function Get-JsonNames([string]$dir) {
    if (Test-Path $dir) {
        @(Get-ChildItem $dir -Filter *.json | ForEach-Object { $_.Name } | Sort-Object)
    } else { @() }
}

if ($Phase -eq 'pre') {
    $wxsText = Get-Content $Wxs -Raw
    foreach ($folder in 'Language', 'Settings') {
        $disk = Get-JsonNames "WtProgram\bin\Release\$folder"
        $wxsNames = @([regex]::Matches($wxsText, [regex]::Escape("TargetDir)$folder\") + '([^"]+\.json)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        $missingInWxs = @($disk | Where-Object { $wxsNames -notcontains $_ })
        $missingOnDisk = @($wxsNames | Where-Object { $disk -notcontains $_ })
        if ($missingInWxs) {
            $failed = $true
            Write-Host "ERROR: $folder file(s) missing from WtSetup.wxs: $($missingInWxs -join ', ')"
            Write-Host "       Add a File entry for each to the ${folder}Files component."
        }
        if ($missingOnDisk) {
            $failed = $true
            Write-Host "ERROR: WtSetup.wxs lists $folder file(s) that are not in the build output: $($missingOnDisk -join ', ')"
        }
        if (-not $missingInWxs -and -not $missingOnDisk) {
            Write-Host "  ${folder}: $($disk.Count) file(s) in sync with WtSetup.wxs"
        }
    }
}
else {
    $extract = Join-Path $env:TEMP ("WtMsiVerify_" + [Guid]::NewGuid().ToString('N'))
    $msiFull = (Resolve-Path $Msi).Path
    $proc = Start-Process msiexec -ArgumentList "/a `"$msiFull`" /qn TARGETDIR=`"$extract`"" -Wait -PassThru
    if ($proc.ExitCode -ne 0) {
        Write-Host "ERROR: administrative extract of the MSI failed (msiexec exit $($proc.ExitCode))"
        exit 1
    }
    try {
        foreach ($folder in 'Language', 'Settings') {
            $disk = Get-JsonNames "WtProgram\bin\Release\$folder"
            $inMsi = Get-JsonNames (Join-Path $extract "WindowTabs\$folder")
            $missing = @($disk | Where-Object { $inMsi -notcontains $_ })
            if ($missing) {
                $failed = $true
                Write-Host "ERROR: the built MSI is missing $folder file(s): $($missing -join ', ')"
            } else {
                Write-Host "  ${folder}: all $($disk.Count) file(s) present in the MSI"
            }
        }
        foreach ($f in 'WindowTabs\WindowTabs.exe', 'WindowTabs\WindowTabs.exe.config', 'WindowTabs\version.md', 'WindowTabs\README.txt') {
            if (-not (Test-Path (Join-Path $extract $f))) {
                $failed = $true
                Write-Host "ERROR: the built MSI is missing $f"
            }
        }
        if (-not $failed) { Write-Host "  Main files present in the MSI" }
    }
    finally {
        Remove-Item -Recurse -Force $extract -ErrorAction SilentlyContinue
    }
}

if ($failed) { exit 1 }
exit 0
