@echo off
setlocal
title Video Metadata Editor - Publish GitHub Release

:: ---------------------------------------------------------------
::  Thin launcher: this .bat exists only so you can double-click to
::  publish, and so PowerShell's execution policy doesn't block the
::  script. ALL the real logic lives in publish-release.ps1 (parsing,
::  gh release, etc.) — PowerShell handles that far more reliably than
::  batch. Do not add logic here; edit publish-release.ps1 instead.
:: ---------------------------------------------------------------

set "PS1=%~dp0publish-release.ps1"

if not exist "%PS1%" (
    echo.
    echo  [ERROR] publish-release.ps1 not found next to this launcher:
    echo    %PS1%
    echo.
    echo  Keep publish-release.bat and publish-release.ps1 in the same folder.
    echo.
    pause
    exit /b 1
)

:: -ExecutionPolicy Bypass applies to THIS launch only (not system-wide),
:: so you never have to change your machine's PowerShell policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"

set "RC=%ERRORLEVEL%"
endlocal & exit /b %RC%
