@echo off
setlocal EnableDelayedExpansion
title Video Metadata Editor - Publish GitHub Release

echo.
echo  ============================================================
echo   Video Metadata Editor - Publish GitHub Release
echo  ============================================================
echo.
echo  This publishes a build you have ALREADY produced with
echo  build_and_launch.bat to GitHub Releases, so the in-app
echo  update badge can point users to it.
echo.
echo  It does NOT build. Run build_and_launch.bat first.
echo.

:: ----------------------------------------------------------------
:: 0. Locate the project (same discovery logic as the build script)
:: ----------------------------------------------------------------
set "BAT_DIR=%~dp0"
if "%BAT_DIR:~-1%"=="\" set "BAT_DIR=%BAT_DIR:~0,-1%"

set "CSPROJ=%BAT_DIR%\VideoMetadataEditor.csproj"
set "PROJECT_DIR=%BAT_DIR%"
if not exist "%CSPROJ%" (
    set "CSPROJ=%BAT_DIR%\VideoMetadataEditor\VideoMetadataEditor.csproj"
    set "PROJECT_DIR=%BAT_DIR%\VideoMetadataEditor"
)
if not exist "%CSPROJ%" (
    echo  [ERROR] Project file not found next to this script.
    echo.
    pause
    exit /b 1
)
if "%PROJECT_DIR:~-1%"=="\" set "PROJECT_DIR=%PROJECT_DIR:~0,-1%"
set "PUBLISH_DIR=%PROJECT_DIR%\publish"

:: ----------------------------------------------------------------
:: 1. Check GitHub CLI is installed and authenticated
:: ----------------------------------------------------------------
where gh >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  [ERROR] GitHub CLI ^(gh^) not found on PATH.
    echo.
    echo  Install it once with:   winget install GitHub.cli
    echo  Then authenticate with: gh auth login
    echo.
    pause
    exit /b 1
)

gh auth status >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo  [ERROR] GitHub CLI is not authenticated.
    echo.
    echo  Run:  gh auth login
    echo.
    pause
    exit /b 1
)
echo  [OK] GitHub CLI found and authenticated.
echo.

:: ----------------------------------------------------------------
:: 2. Extract FullVersion from the .csproj  (single source of truth)
:: ----------------------------------------------------------------
set "APP_VERSION="
for /f "tokens=2 delims=<>" %%v in ('findstr /i "<FullVersion>" "%CSPROJ%"') do set "APP_VERSION=%%v"

if "!APP_VERSION!"=="" (
    echo  [ERROR] Could not read ^<FullVersion^> from the .csproj.
    echo.
    pause
    exit /b 1
)
set "TAG=v!APP_VERSION!"
echo  [OK] Version from project: !APP_VERSION!   ^(tag: !TAG!^)
echo.

:: ----------------------------------------------------------------
:: 3. Find the versioned EXE  (AssemblyName = "VideoMetadataEditor v<ver>")
:: ----------------------------------------------------------------
set "EXE_PATH=%PUBLISH_DIR%\VideoMetadataEditor v!APP_VERSION!.exe"

if not exist "!EXE_PATH!" (
    echo  [ERROR] Built EXE not found:
    echo    !EXE_PATH!
    echo.
    echo  Did you run build_and_launch.bat for this version first?
    echo.
    pause
    exit /b 1
)
for %%F in ("!EXE_PATH!") do (
    set /a SIZE_MB=%%~zF / 1048576
    echo  [OK] Found EXE - !SIZE_MB! MB
)
echo.

:: ----------------------------------------------------------------
:: 4. Refuse to overwrite an existing release for this tag
:: ----------------------------------------------------------------
gh release view "!TAG!" >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo  [ERROR] A release for !TAG! already exists on GitHub.
    echo.
    echo  Either bump ^<FullVersion^> and rebuild, or delete the old
    echo  release first with:  gh release delete !TAG!
    echo.
    pause
    exit /b 1
)

:: ----------------------------------------------------------------
:: 5. Build release notes from CHANGELOG.md (top section only)
::    Falls back to a generic note if the changelog isn't found.
:: ----------------------------------------------------------------
set "CHANGELOG=%BAT_DIR%\CHANGELOG.md"
if not exist "%CHANGELOG%" set "CHANGELOG=%PROJECT_DIR%\CHANGELOG.md"
set "NOTES_FILE=%TEMP%\vme_release_notes_!APP_VERSION!.md"
if exist "!NOTES_FILE!" del /q "!NOTES_FILE!"

if exist "%CHANGELOG%" (
    :: Copy lines from the first "## " heading up to (not including) the next "## "
    set "IN_SECTION="
    for /f "usebackq delims=" %%L in ("%CHANGELOG%") do (
        set "LINE=%%L"
        echo !LINE! | findstr /b /c:"## " >nul
        if !ERRORLEVEL! equ 0 (
            if defined IN_SECTION (
                goto :notes_done
            ) else (
                set "IN_SECTION=1"
            )
        )
        if defined IN_SECTION echo !LINE!>>"!NOTES_FILE!"
    )
    :notes_done
)

if not exist "!NOTES_FILE!" (
    echo Video Metadata Editor !APP_VERSION!>"!NOTES_FILE!"
    echo.>>"!NOTES_FILE!"
    echo Portable single-EXE build. Download the .exe below.>>"!NOTES_FILE!"
)

echo  [OK] Release notes prepared from CHANGELOG.
echo.

:: ----------------------------------------------------------------
:: 6. Confirm, then publish
:: ----------------------------------------------------------------
echo  About to publish:
echo    Tag    : !TAG!
echo    Title  : !TAG!
echo    Asset  : !EXE_PATH!
echo.
set /p CONFIRM= Publish this release to GitHub now? [Y/N]: 
if /i not "!CONFIRM!"=="Y" (
    echo.
    echo  Cancelled. Nothing was published.
    echo.
    pause
    exit /b 0
)

echo.
echo  Publishing...
gh release create "!TAG!" "!EXE_PATH!" --title "!TAG!" --notes-file "!NOTES_FILE!"

if %ERRORLEVEL% neq 0 (
    echo.
    echo  [ERROR] gh release create failed. See the message above.
    echo.
    pause
    exit /b 1
)

echo.
echo  ============================================================
echo   Release !TAG! published.
echo  ============================================================
echo.
echo  The in-app update badge will now point users here once they
echo  launch a build older than !APP_VERSION!.
echo.
pause >nul
endlocal
