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

:: Remove temporary directory
rmdir /s /q "%OUTPUT_DIR%"
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
