@echo off
setlocal

echo Building WindowTabs Release and creating ZIP...
echo.

:: Clean previous output if exists
if exist exe\zip\WindowTabs.zip (
    echo Cleaning previous ZIP file...
    del exe\zip\WindowTabs.zip
    echo Previous ZIP file removed.
)
if exist exe\zip\WindowTabs (
    echo Cleaning previous output folder...
    rmdir /s /q exe\zip\WindowTabs
    echo Previous output folder removed.
)
if exist exe\zip\WindowTabs.zip (
    echo.
) else (
    if exist exe\zip\WindowTabs (
        echo.
    ) else (
        echo No previous output found.
    )
)
echo.

:: Check if MSBuild exists
set MSBUILD="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
if not exist %MSBUILD% (
    echo ERROR: MSBuild not found at %MSBUILD%
    echo Please install Visual Studio 2026
    exit /b 1
)

:: Build WtProgram (Release configuration, Any CPU)
echo Building WtProgram...
%MSBUILD% WtProgram\WtProgram.fsproj /p:Configuration=Release /p:Platform=AnyCPU /v:minimal
if errorlevel 1 (
    echo ERROR: WtProgram build failed
    exit /b 1
)
echo WtProgram build completed successfully.
echo.

:: Create output directory
set OUTPUT_DIR=exe\zip\WindowTabs
if exist "%OUTPUT_DIR%" (
    echo Cleaning existing output directory...
    rmdir /s /q "%OUTPUT_DIR%"
)
echo Creating output directory...
mkdir "%OUTPUT_DIR%"

:: Copy files
echo Copying files...
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

copy /Y "WtSetup\README.md" "%OUTPUT_DIR%\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy README.md
    exit /b 1
)

:: Copy Language folder
echo Copying Language folder...
mkdir "%OUTPUT_DIR%\Language"
xcopy /Y /E "WtProgram\bin\Release\Language\*" "%OUTPUT_DIR%\Language\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Language folder
    exit /b 1
)

echo Files copied successfully.
echo.

:: Create ZIP file
echo Creating ZIP file...
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

echo ZIP file created successfully.
echo.

:: Remove temporary directory
echo Removing temporary directory...
rmdir /s /q "%OUTPUT_DIR%"

echo.
echo ========================================
echo Build and ZIP creation completed!
echo ========================================
echo.
echo Output file:
echo   %ZIP_FILE%
echo.
dir "%ZIP_FILE%"

endlocal
