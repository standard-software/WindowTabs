@echo off
setlocal enabledelayedexpansion

REM Extract version from Program.fs using PowerShell
for /f "usebackq delims=" %%i in (`powershell -Command "(Select-String -Path 'WtProgram\Program.fs' -Pattern 'let version').Line.Split([char]34)[1]"`) do (
    set VERSION=%%i
)

if "%VERSION%"=="" (
    echo Error: Could not extract version from Program.fs
    pause
    exit /b 1
)

set TAG=%VERSION%
set TITLE=WindowTabs %VERSION%

echo Extracted version: %VERSION%
echo.

echo Creating GitHub Release: %TAG%
echo.

gh release create %TAG% "exe\installer\WtSetup.msi" "exe\zip\WindowTabs.zip" --title "%TITLE%" --generate-notes

echo.
echo Release created successfully!
pause
