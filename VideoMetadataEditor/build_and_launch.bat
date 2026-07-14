@echo off
setlocal EnableDelayedExpansion
title Video Metadata Editor - Build

echo.
echo  ============================================================
echo   Video Metadata Editor v1.2.0 build 7 - Build and Package
echo  ============================================================
echo.

:: Check for .NET SDK
where dotnet >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  [ERROR] .NET SDK not found on PATH.
    echo.
    echo  Install .NET 8 SDK from:
    echo  https://dotnet.microsoft.com/download/dotnet/8.0
    echo.
    pause
    exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version 2^>nul') do set DOTNET_VER=%%v
echo  [OK] .NET SDK found: %DOTNET_VER%
echo.

:: Strip trailing backslash from the script directory
set "BAT_DIR=%~dp0"
if "%BAT_DIR:~-1%"=="\" set "BAT_DIR=%BAT_DIR:~0,-1%"

:: Find the .csproj - check same folder first, then VideoMetadataEditor subfolder
set "CSPROJ=%BAT_DIR%\VideoMetadataEditor.csproj"
set "PROJECT_DIR=%BAT_DIR%"

if not exist "%CSPROJ%" (
    set "CSPROJ=%BAT_DIR%\VideoMetadataEditor\VideoMetadataEditor.csproj"
    set "PROJECT_DIR=%BAT_DIR%\VideoMetadataEditor"
)

if not exist "%CSPROJ%" (
    echo  [ERROR] Project file not found. Searched:
    echo    %BAT_DIR%\VideoMetadataEditor.csproj
    echo    %BAT_DIR%\VideoMetadataEditor\VideoMetadataEditor.csproj
    echo.
    echo  Place this bat file next to the VideoMetadataEditor project folder.
    echo.
    pause
    exit /b 1
)

:: Strip trailing backslash from PROJECT_DIR to prevent double-backslash
if "%PROJECT_DIR:~-1%"=="\" set "PROJECT_DIR=%PROJECT_DIR:~0,-1%"

set "PUBLISH_DIR=%PROJECT_DIR%\publish"
set "EXE_PATH=%PUBLISH_DIR%\VideoMetadataEditor.exe"
set "CONFIG_FILE=%PUBLISH_DIR%\config.json"

echo  [OK] Project: %PROJECT_DIR%
echo.

:: [1/3] Restore
echo  [1/3] Restoring NuGet packages...
dotnet restore "%CSPROJ%" --nologo
if %ERRORLEVEL% neq 0 (
    echo.
    echo  [ERROR] Restore failed. Check your internet connection.
    echo.
    pause
    exit /b 1
)
echo  [OK] Packages restored.
echo.

:: [2/3] Publish
echo  [2/3] Building self-contained single EXE to:
echo   %PUBLISH_DIR%
echo.

dotnet publish "%CSPROJ%" --configuration Release --output "%PUBLISH_DIR%" --nologo

if %ERRORLEVEL% neq 0 (
    echo.
    echo  [ERROR] Build failed. See errors above.
    echo.
    pause
    exit /b 1
)

echo.
echo  [OK] Build succeeded.
echo.

if not exist "%EXE_PATH%" (
    echo  [WARNING] VideoMetadataEditor.exe not found.
) else (
    for %%F in ("%EXE_PATH%") do (
        set /a SIZE_MB=%%~zF / 1048576
        echo  [OK] VideoMetadataEditor.exe - !SIZE_MB! MB
    )
)

:: [3/3] Config
if not exist "%CONFIG_FILE%" (
    echo  [3/3] Creating default config.json...
    (
        echo {
        echo   "TmdbApiKey": "",
        echo   "OmdbApiKey": "",
        echo   "ImdbApiKey": "",
        echo   "IsDarkTheme": true,
        echo   "ShowSplashScreen": true,
        echo   "RenamePattern": "{Title} ({Year})",
        echo   "MaxConcurrentProcessing": 4,
        echo   "ArtworkMaxPx": 500,
        echo   "ArtworkJpegQuality": 85
        echo }
    ) > "%CONFIG_FILE%"
    echo  [OK] config.json created - add your API keys inside the app.
) else (
    echo  [3/3] config.json already exists - skipping.
)

:: Clean stray .pdb files
for %%F in ("%PUBLISH_DIR%\*.pdb") do del /Q "%%F" >nul 2>&1
echo  [OK] Publish folder is clean.

echo.
echo  ============================================================
echo   Build complete! Your portable app is ready.
echo  ============================================================
echo.
echo  Location:
echo   %PUBLISH_DIR%
echo.
echo  Contents:
echo   - VideoMetadataEditor.exe   (portable, no install needed)
echo   - config.json               (API keys and preferences)
echo.
echo  Copy the publish folder to any Windows 10/11 x64 PC.
echo  No .NET runtime installation required.
echo.

explorer "%PUBLISH_DIR%"

echo.
set /p LAUNCH= Launch VideoMetadataEditor.exe now? [Y/N]: 
if /i "!LAUNCH!"=="Y" (
    echo.
    echo  Launching...
    start "" "%EXE_PATH%"
)

echo.
echo  Done. Press any key to exit.
pause >nul
endlocal
