@echo off
setlocal enabledelayedexpansion

REM Extract version from Program.fs
for /f "usebackq tokens=3 delims=^" %%i in (`findstr /C:"let version = " WtProgram\Program.fs`) do (
    set VERSION_RAW=%%i
    REM Remove quotes and trim
    set VERSION=!VERSION_RAW:"=!
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

gh release create %TAG% ^
  "exe\installer\WtSetup.msi" ^
  "exe\zip\WindowTabs.zip" ^
  --title "%TITLE%" ^
  --notes "## Changes in %VERSION%

- Reorganize tab context menu structure
  - Rename \"Move tab\" → \"Move tab to another group\"
  - Rename \"Detach tab / Move window\" → \"Detach tab\"
  - Add \"Reposition tab group\" menu with display edge positioning options
- Update README.md documentation

## Download Options

- **WtSetup.msi** - Windows Installer package with automatic installation and uninstallation support
- **WindowTabs.zip** - Portable version that can be extracted and run from any location

See [version.md](https://github.com/standard-software/WindowTabs/blob/master/version.md) for complete version history."

echo.
echo Release created successfully!
pause
