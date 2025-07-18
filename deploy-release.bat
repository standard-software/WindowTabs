@echo off
setlocal

set SOURCE_DIR=WtProgram\bin\Release
set TARGET_DIR=exe\WindowTabs

echo Deploying WindowTabs (Release build) to %TARGET_DIR%...

if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"

:: Copy main executable
echo Copying WindowTabs.exe...
copy /Y "%SOURCE_DIR%\WindowTabs.exe" "%TARGET_DIR%\" 2>nul

:: Copy all necessary DLLs
echo Copying all DLLs...
copy /Y "%SOURCE_DIR%\FSharp.Core.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\FSharp.PowerPack.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\FSharp.PowerPack.Linq.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\FSharp.PowerPack.Metadata.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\FSharp.PowerPack.Parallel.Seq.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\Win32.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\Aga.Controls.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\Newtonsoft.Json.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\Interop.SHDocVw.dll" "%TARGET_DIR%\" 2>nul
copy /Y "%SOURCE_DIR%\Interop.Shell32.dll" "%TARGET_DIR%\" 2>nul

:: Copy Japanese resources
if not exist "%TARGET_DIR%\ja-JP" mkdir "%TARGET_DIR%\ja-JP"
copy /Y "%SOURCE_DIR%\ja-JP\WindowTabs.resources.dll" "%TARGET_DIR%\ja-JP\" 2>nul

:: Copy config file if exists
if exist "%SOURCE_DIR%\WindowTabs.exe.config" (
    copy /Y "%SOURCE_DIR%\WindowTabs.exe.config" "%TARGET_DIR%\" 2>nul
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
    echo Language resources:
    dir /B "%TARGET_DIR%\ja-JP\*.dll" 2>nul
) else (
    echo.
    echo ERROR: WindowTabs.exe not found in %SOURCE_DIR%
    echo Please run a Release build in Visual Studio first.
)

endlocal