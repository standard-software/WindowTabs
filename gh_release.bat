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
REM The release is created against the tip of the REMOTE main: gh release
REM create makes the tag there, whatever is checked out here. Run from main,
REM and only after it has been pushed - otherwise the tag lands on the
REM previous release's commit (which is what happened with ss_2026.09.04).
for /f "usebackq delims=" %%i in (`git rev-parse --abbrev-ref HEAD`) do set BRANCH=%%i
if not "%BRANCH%"=="main" (
    echo Error: run this from main. Current branch: %BRANCH%
    pause
    exit /b 1
)
git fetch origin main >nul 2>&1
for /f "usebackq delims=" %%i in (`git rev-parse main`) do set LOCAL_SHA=%%i
for /f "usebackq delims=" %%i in (`git rev-parse origin/main`) do set REMOTE_SHA=%%i
if not "%LOCAL_SHA%"=="%REMOTE_SHA%" (
    echo Error: local main is %LOCAL_SHA:~0,7% but origin/main is %REMOTE_SHA:~0,7%.
    echo        Push first - git push origin main - then run this again.
    pause
    exit /b 1
)
set TITLE=WindowTabs version %VERSION%
set NOTES=For details, see [version.md](https://github.com/standard-software/WindowTabs/blob/main/version.md)

echo Extracted version: %VERSION%
echo.

echo Creating GitHub Release: %TAG%
echo.

gh release create %TAG% "exe\installer\WtSetup.msi" "exe\zip\WindowTabs.zip" --title "%TITLE%" --notes "%NOTES%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Release failed! Please check authentication with: gh auth login
) else (
    echo.
    echo Release created successfully!
)
pause
