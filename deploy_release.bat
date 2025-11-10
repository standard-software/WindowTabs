@echo off
setlocal

set SOURCE_DIR=WtProgram\bin\Release
set TARGET_DIR=exe\WindowTabs

echo Deploying WindowTabs (Release build) to %TARGET_DIR%...

if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"

:: Copy main executable
echo Copying WindowTabs.exe...
copy /Y "%SOURCE_DIR%\WindowTabs.exe" "%TARGET_DIR%\" 2>nul

:: All DLLs are now merged into WindowTabs.exe using ILRepack
:: No additional DLLs needed

:: Language resources are no longer needed (using code-based localization)

:: Copy config file if exists
if exist "%SOURCE_DIR%\WindowTabs.exe.config" (
    copy /Y "%SOURCE_DIR%\WindowTabs.exe.config" "%TARGET_DIR%\" 2>nul
)

:: Copy App.config as WindowTabs.exe.config if the above doesn't exist
if not exist "%TARGET_DIR%\WindowTabs.exe.config" (
    if exist "%SOURCE_DIR%\App.config" (
        copy /Y "%SOURCE_DIR%\App.config" "%TARGET_DIR%\WindowTabs.exe.config" 2>nul
    )
)

:: Copy version.md
if exist "version.md" (
    echo Copying version.md...
    copy /Y "version.md" "%TARGET_DIR%\" 2>nul
)

:: Check if WindowTabs.exe exists
if exist "%TARGET_DIR%\WindowTabs.exe" (
    echo.
    echo Deployment complete!
    echo.
    echo Files deployed to %TARGET_DIR%:
    echo ==============================
    dir /B "%TARGET_DIR%\*.exe" | findstr /V "ILMerge ILRepack"
    dir /B "%TARGET_DIR%\*.dll" | findstr /V "ILMerge ILRepack"

    echo.
    echo Creating WindowTabs.zip...
    powershell.exe -Command "Compress-Archive -Path '%TARGET_DIR%\*' -DestinationPath 'exe\WindowTabs.zip' -Force"
    if exist "exe\WindowTabs.zip" (
        echo WindowTabs.zip created successfully at exe\WindowTabs.zip
    ) else (
        echo ERROR: Failed to create WindowTabs.zip
    )
) else (
    echo.
    echo ERROR: WindowTabs.exe not found in %SOURCE_DIR%
    echo Please run a Release build in Visual Studio first.
)

endlocal