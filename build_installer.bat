@echo off
setlocal

echo Building WindowTabs Installer...
echo.

:: Check if MSBuild exists
set MSBUILD="C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist %MSBUILD% (
    echo ERROR: MSBuild not found at %MSBUILD%
    echo Please install Visual Studio 2022 with WiX Toolset support
    exit /b 1
)

:: Build WtProgram first (Release configuration)
echo Building WtProgram...
%MSBUILD% WtProgram\WtProgram.fsproj /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
if errorlevel 1 (
    echo ERROR: WtProgram build failed
    exit /b 1
)
echo WtProgram build completed successfully.
echo.

:: Build WtSetup installer
echo Building WtSetup installer...
%MSBUILD% WtSetup\WtSetup.wixproj /p:Configuration=Release /p:Platform=x86 /v:minimal
if errorlevel 1 (
    echo ERROR: WtSetup build failed
    echo.
    echo Make sure WiX Toolset is installed:
    echo   1. Install WiX Toolset v3.11 or newer
    echo   2. Or restore NuGet packages: nuget restore WindowTabs.sln
    exit /b 1
)

:: Copy installer to exe\installer folder
echo.
echo Copying installer to exe\installer...
if not exist exe\installer (
    mkdir exe\installer
)
copy WtSetup\bin\Release\WtSetup.msi exe\installer\WtSetup.msi
if errorlevel 1 (
    echo WARNING: Failed to copy installer to exe\installer
) else (
    echo Installer copied successfully to exe\installer\WtSetup.msi
)

echo.
echo ========================================
echo Installer build completed successfully!
echo ========================================
echo.
echo Installer locations:
echo   1. WtSetup\bin\Release\WtSetup.msi
echo   2. exe\installer\WtSetup.msi
echo.
dir /B WtSetup\bin\Release\*.msi 2>nul
if errorlevel 1 (
    echo WARNING: MSI file not found in expected location
    echo Please check WtSetup\bin\Release\ directory
) else (
    echo.
    echo The installer is ready to use.
)

endlocal
