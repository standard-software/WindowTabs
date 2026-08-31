@echo off
setlocal

echo ========================================
echo  WindowTabs Release Build
echo  Creating ZIP and MSI Installer
echo ========================================
echo.

:: ----------------------------------------
:: Clean previous outputs
:: ----------------------------------------
echo Cleaning previous outputs...
if exist exe\zip\WindowTabs.zip del exe\zip\WindowTabs.zip
if exist exe\zip\WindowTabs rmdir /s /q exe\zip\WindowTabs
if exist exe\installer\WtSetup.msi del exe\installer\WtSetup.msi
echo Done.
echo.

:: ----------------------------------------
:: Check MSBuild
:: ----------------------------------------
set MSBUILD="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist %MSBUILD% (
    echo ERROR: MSBuild not found at %MSBUILD%
    echo Please install Visual Studio 2026
    exit /b 1
)

:: ----------------------------------------
:: Clean OneDrive sync conflict files
:: ----------------------------------------
echo Cleaning OneDrive sync conflict files...
for %%f in (WtProgram\bin\Release\*-LAPTOP-*.dll WtProgram\bin\Release\*-LAPTOP-*.exe WtProgram\bin\Release\*-LAPTOP-*.pdb) do (
    if exist "%%f" (
        echo   Removing: %%f
        del "%%f"
    )
)
echo.

:: ----------------------------------------
:: Build WtProgram (Rebuild for clean ILRepack)
:: ----------------------------------------
echo [1/4] Building WtProgram...
%MSBUILD% WtProgram\WtProgram.fsproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
if errorlevel 1 (
    echo ERROR: WtProgram build failed
    exit /b 1
)
echo WtProgram build completed successfully.
echo.

:: ----------------------------------------
:: Verify ILRepack merge
:: ----------------------------------------
echo Verifying ILRepack merge...
for %%A in (WtProgram\bin\Release\WindowTabs.exe) do set EXE_SIZE=%%~zA
echo   WindowTabs.exe size: %EXE_SIZE% bytes
if %EXE_SIZE% LSS 5000000 (
    echo ERROR: WindowTabs.exe is too small [%EXE_SIZE% bytes].
    echo        ILRepack DLL merge likely failed.
    echo        Expected size is over 8MB when DLLs are properly merged.
    echo        Try running the build again. OneDrive file sync may have caused a lock.
    exit /b 1
)
echo   ILRepack merge verified successfully.

:: The merged exe must keep the Win32 manifest that declares per-monitor DPI
:: awareness. Losing it (e.g. an ILRepack change dropping Win32 resources)
:: would silently ship a blurry, OS-stretched build. findstr cannot be used
:: here: it misses matches inside binary files (verified), so search the raw
:: bytes with PowerShell instead.
powershell -NoProfile -Command "$b=[IO.File]::ReadAllBytes('WtProgram\bin\Release\WindowTabs.exe'); if([Text.Encoding]::ASCII.GetString($b).Contains('PerMonitorV2')){exit 0}else{exit 1}"
if errorlevel 1 (
    echo ERROR: PerMonitorV2 manifest not found in WindowTabs.exe.
    echo        The DPI-awareness manifest was lost during build or ILRepack.
    exit /b 1
)
echo   PerMonitorV2 manifest verified.
echo.

:: ----------------------------------------
:: Code signing (optional)
::
:: Unsigned binaries carry no publisher reputation, so SmartScreen warns on
:: them and Defender's machine-learning classifiers judge them on behaviour
:: alone - which is how an installer doing ordinary file maintenance came to
:: be reported as Trojan:Script/Wacatac.H!ml.
::
:: This step does nothing until a certificate exists. Set both variables to
:: turn it on, for example:
::
::   set WT_SIGN_TOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe
::   set WT_SIGN_ARGS=/fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Satoshi Yamamoto"
::
:: A timestamp is not optional: without /tr the signature stops verifying the
:: day the certificate expires, and every build already shipped goes back to
:: being unsigned.
::
:: The exe is signed here, before it is copied into either the ZIP or the MSI,
:: so both carry the same signed binary. The MSI is signed separately once it
:: has been built.
:: ----------------------------------------
if defined WT_SIGN_TOOL (
    echo Signing WindowTabs.exe...
    "%WT_SIGN_TOOL%" sign %WT_SIGN_ARGS% "WtProgram\bin\Release\WindowTabs.exe"
    if errorlevel 1 (
        echo ERROR: signing WindowTabs.exe failed
        exit /b 1
    )
    "%WT_SIGN_TOOL%" verify /pa "WtProgram\bin\Release\WindowTabs.exe"
    if errorlevel 1 (
        echo ERROR: the signature on WindowTabs.exe does not verify
        exit /b 1
    )
    echo   WindowTabs.exe signed and verified.
) else (
    echo Skipping code signing ^(WT_SIGN_TOOL is not set^).
)
echo.

:: ----------------------------------------
:: Verify installer file list (name-level)
:: (Every shipped Language/Settings *.json needs a <File> entry in
::  WtSetup.wxs; the MSI silently omits unlisted files - this caused the
::  ss_2026.07.16 MSI to ship without the 6 newly added languages.)
:: ----------------------------------------
echo Verifying installer file list against WtSetup.wxs...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File verify_release.ps1 -Phase pre
if errorlevel 1 (
    echo ERROR: WtSetup.wxs is out of sync with the build output. See above.
    exit /b 1
)
echo.

:: ----------------------------------------
:: Create ZIP
:: ----------------------------------------
echo [2/4] Creating ZIP...

set OUTPUT_DIR=exe\zip\WindowTabs
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"

:: Copy files for ZIP
copy /Y "WtProgram\bin\Release\WindowTabs.exe" "%OUTPUT_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy WindowTabs.exe
    exit /b 1
)
copy /Y "WtProgram\bin\Release\WindowTabs.exe.config" "%OUTPUT_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy WindowTabs.exe.config
    exit /b 1
)
copy /Y "version.md" "%OUTPUT_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy version.md
    exit /b 1
)
copy /Y "WtSetup\README.txt" "%OUTPUT_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy README.txt
    exit /b 1
)

:: Copy Language folder
mkdir "%OUTPUT_DIR%\Language"
xcopy /Y /E "WtProgram\bin\Release\Language\*" "%OUTPUT_DIR%\Language\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Language folder
    exit /b 1
)

:: Copy Settings folder
mkdir "%OUTPUT_DIR%\Settings"
xcopy /Y /E "WtProgram\bin\Release\Settings\*" "%OUTPUT_DIR%\Settings\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Settings folder
    exit /b 1
)

:: Compress to ZIP
set ZIP_FILE=exe\zip\WindowTabs.zip
if exist "%ZIP_FILE%" del "%ZIP_FILE%"

pushd exe\zip\WindowTabs
powershell.exe -Command "Compress-Archive -Path '*' -DestinationPath '..\WindowTabs.zip' -Force"
set COMPRESS_ERROR=%errorlevel%
popd

if %COMPRESS_ERROR% neq 0 (
    echo ERROR: Failed to create ZIP file
    exit /b 1
)
if not exist "%ZIP_FILE%" (
    echo ERROR: ZIP file not created
    exit /b 1
)

:: Remove temporary directory (Dropbox / OneDrive sync may hold a file
:: handle on the freshly created folder, so retry once after 5 seconds.)
rmdir /s /q "%OUTPUT_DIR%" 2>nul
if exist "%OUTPUT_DIR%" (
    echo   Temporary folder still present, waiting 5s for sync to release locks...
    timeout /t 5 /nobreak >nul
    rmdir /s /q "%OUTPUT_DIR%" 2>nul
)
if exist "%OUTPUT_DIR%" (
    echo   WARNING: Could not remove temporary folder "%OUTPUT_DIR%".
    echo            ZIP is already created; delete the folder manually later.
)
echo ZIP created successfully.
echo.

:: ----------------------------------------
:: Build MSI Installer
:: ----------------------------------------
echo [3/4] Building MSI Installer...
:: BuildProjectReferences=false prevents WtSetup from rebuilding WtProgram
:: (already built in step 1), avoiding file lock conflicts
%MSBUILD% WtSetup\WtSetup.wixproj /p:Configuration=Release /p:Platform=x86 /p:BuildProjectReferences=false /v:minimal
if errorlevel 1 (
    echo ERROR: WtSetup build failed
    echo.
    echo Make sure WiX Toolset is installed:
    echo   1. Install WiX Toolset v3.11 or newer
    echo   2. Or restore NuGet packages: nuget restore WindowTabs.sln
    exit /b 1
)

:: Copy MSI to exe\installer
if not exist exe\installer mkdir exe\installer
copy /Y WtSetup\bin\Release\WtSetup.msi exe\installer\WtSetup.msi >nul
if errorlevel 1 (
    echo WARNING: Failed to copy installer to exe\installer
) else (
    echo MSI Installer created successfully.
)
echo.

:: ----------------------------------------
:: Verify MSI contents (extract and compare against build output)
:: ----------------------------------------
echo Verifying MSI contents...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File verify_release.ps1 -Phase post
if errorlevel 1 (
    echo ERROR: the built MSI does not contain the expected files. See above.
    exit /b 1
)
echo.

:: Sign the MSI itself. Signing the executable inside it is not enough: the
:: file a user downloads and double-clicks is the MSI, and that is what
:: SmartScreen and the AV engines judge. Signed after the content check, so
:: that nothing modifies the package afterwards.
if defined WT_SIGN_TOOL (
    echo Signing WtSetup.msi...
    "%WT_SIGN_TOOL%" sign %WT_SIGN_ARGS% "exe\installer\WtSetup.msi"
    if errorlevel 1 (
        echo ERROR: signing WtSetup.msi failed
        exit /b 1
    )
    "%WT_SIGN_TOOL%" verify /pa "exe\installer\WtSetup.msi"
    if errorlevel 1 (
        echo ERROR: the signature on WtSetup.msi does not verify
        exit /b 1
    )
    echo   WtSetup.msi signed and verified.
    echo.
)

:: ----------------------------------------
:: Summary
:: ----------------------------------------
echo [4/4] Done!
echo.
echo ========================================
echo  Release Build Completed!
echo ========================================
echo.
echo Output files:
echo   ZIP: %ZIP_FILE%
echo   MSI: exe\installer\WtSetup.msi
echo.
dir exe\zip\WindowTabs.zip exe\installer\WtSetup.msi 2>nul

endlocal
