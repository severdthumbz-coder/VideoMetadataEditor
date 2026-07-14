<#
    publish-release.ps1
    ---------------------------------------------------------------
    Publishes a build you have ALREADY produced with build_and_launch
    to GitHub Releases, so the in-app update badge can point users to it.

    This does NOT build. Run your build script first.

    HOW TO RUN (pick one):
      Right way (from PowerShell, in this folder):
        .\publish-release.ps1
      If you get an execution-policy error, either:
        powershell -ExecutionPolicy Bypass -File .\publish-release.ps1
      or set it once for your user (permits local scripts you wrote):
        Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
    ---------------------------------------------------------------
#>

$ErrorActionPreference = 'Stop'

function Fail($msg) {
    Write-Host ""
    Write-Host "  [ERROR] $msg" -ForegroundColor Red
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host ""
Write-Host "  ============================================================"
Write-Host "   Video Metadata Editor - Publish GitHub Release"
Write-Host "  ============================================================"
Write-Host ""
Write-Host "  Publishes an already-built release to GitHub so the in-app"
Write-Host "  update badge can point users to it. Does NOT build."
Write-Host ""

# --- 0. Locate the project (script dir, then a VideoMetadataEditor subfolder) ---
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$Csproj = Join-Path $ScriptDir 'VideoMetadataEditor.csproj'
$ProjectDir = $ScriptDir
if (-not (Test-Path $Csproj)) {
    $Csproj = Join-Path $ScriptDir 'VideoMetadataEditor\VideoMetadataEditor.csproj'
    $ProjectDir = Join-Path $ScriptDir 'VideoMetadataEditor'
}
if (-not (Test-Path $Csproj)) {
    Fail "Project file not found. Looked in:`n    $(Join-Path $ScriptDir 'VideoMetadataEditor.csproj')`n    $Csproj"
}
$PublishDir = Join-Path $ProjectDir 'publish'

# --- 1. GitHub CLI present and authenticated? ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "GitHub CLI (gh) not found on PATH.`n  Install once:  winget install GitHub.cli`n  Then:          gh auth login"
}
$authOk = $false
try {
    $null = & gh auth status 2>&1
    if ($LASTEXITCODE -eq 0) { $authOk = $true }
} catch {
    $authOk = $false
}
if (-not $authOk) {
    Fail "GitHub CLI is not authenticated.`n  Run:  gh auth login"
}
Write-Host "  [OK] GitHub CLI found and authenticated."
Write-Host ""

# --- 2. Read FullVersion from the .csproj by PARSING IT AS XML ---
#     No text-scraping: find the PropertyGroup that actually defines FullVersion.
try {
    [xml]$proj = Get-Content -Raw -LiteralPath $Csproj
} catch {
    Fail "Could not read/parse the .csproj as XML:`n    $Csproj"
}

$AppVersion = $null
foreach ($pg in $proj.Project.PropertyGroup) {
    if ($pg.FullVersion) { $AppVersion = ([string]$pg.FullVersion).Trim(); break }
}
if ([string]::IsNullOrWhiteSpace($AppVersion)) {
    Fail "Could not find <FullVersion> in the .csproj:`n    $Csproj"
}
# Sanity-check it looks like a version (e.g. 1.4.0.131)
if ($AppVersion -notmatch '^\d+(\.\d+){1,3}$') {
    Fail "FullVersion '$AppVersion' does not look like a version number."
}
$Tag = "v$AppVersion"
Write-Host "  [OK] Version from project: $AppVersion   (tag: $Tag)"
Write-Host ""

# --- 3. Find the versioned EXE (AssemblyName = "VideoMetadataEditor v<ver>") ---
$ExePath = Join-Path $PublishDir "VideoMetadataEditor v$AppVersion.exe"
if (-not (Test-Path -LiteralPath $ExePath)) {
    Fail "Built EXE not found:`n    $ExePath`n`n  Did you build this version first?"
}
$SizeMB = [math]::Round((Get-Item -LiteralPath $ExePath).Length / 1MB, 1)
Write-Host "  [OK] Found EXE - $SizeMB MB"
Write-Host ""

# --- 4. Refuse to overwrite an existing release for this tag ---
#     "release not found" is the EXPECTED result for a new version, so this
#     check must not be fatal. gh writes that to stderr, which PowerShell
#     surfaces as a NativeCommandError under $ErrorActionPreference='Stop' -
#     so we neutralise stderr and read only the exit code.
$releaseExists = $false
try {
    $null = & gh release view $Tag 2>&1
    if ($LASTEXITCODE -eq 0) { $releaseExists = $true }
} catch {
    # gh exited non-zero (release doesn't exist) - that's fine, keep going.
    $releaseExists = $false
}
if ($releaseExists) {
    Fail "A release for $Tag already exists on GitHub.`n  Bump <FullVersion> and rebuild, or delete it first:`n    gh release delete $Tag"
}
Write-Host "  [OK] No existing release for $Tag - clear to publish."
Write-Host ""

# --- 5. Build release notes from the top section of CHANGELOG.md ---
$Changelog = Join-Path $ScriptDir 'CHANGELOG.md'
if (-not (Test-Path $Changelog)) { $Changelog = Join-Path $ProjectDir 'CHANGELOG.md' }

$NotesFile = Join-Path $env:TEMP "vme_release_notes_$AppVersion.md"
if (Test-Path $NotesFile) { Remove-Item $NotesFile -Force }

if (Test-Path $Changelog) {
    $lines = Get-Content -LiteralPath $Changelog
    $section = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $lines) {
        if ($line -match '^##\s') {
            if ($inSection) { break }   # reached the next section - stop
            $inSection = $true
        }
        if ($inSection) { $section.Add($line) }
    }
    if ($section.Count -gt 0) {
        $section -join "`r`n" | Set-Content -LiteralPath $NotesFile -Encoding UTF8
    }
}
if (-not (Test-Path $NotesFile)) {
    "Video Metadata Editor $AppVersion`r`n`r`nPortable single-EXE build. Download the .exe below." |
        Set-Content -LiteralPath $NotesFile -Encoding UTF8
}
Write-Host "  [OK] Release notes prepared from CHANGELOG."
Write-Host ""

# --- 6. Confirm, then publish ---
Write-Host "  About to publish:"
Write-Host "    Tag    : $Tag"
Write-Host "    Title  : $Tag"
Write-Host "    Asset  : $ExePath"
Write-Host ""
$confirm = Read-Host "  Publish this release to GitHub now? [Y/N]"
if ($confirm -notin @('Y','y')) {
    Write-Host ""
    Write-Host "  Cancelled. Nothing was published."
    Read-Host "Press Enter to exit"
    exit 0
}

Write-Host ""
Write-Host "  Publishing..."
# gh writes upload progress/notices to stderr; with $ErrorActionPreference='Stop'
# that would abort mid-publish even on success. Relax it for this call and judge
# the outcome by the exit code only.
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& gh release create $Tag $ExePath --title $Tag --notes-file $NotesFile 2>&1 | ForEach-Object { Write-Host "    $_" }
$ghExit = $LASTEXITCODE
$ErrorActionPreference = $prevEap

if ($ghExit -ne 0) {
    Fail "gh release create failed (exit $ghExit - see message above)."
}

Write-Host ""
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host "   Release $Tag published." -ForegroundColor Green
Write-Host "  ============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "  The in-app update badge will point users here once they"
Write-Host "  launch a build older than $AppVersion."
Write-Host ""
Read-Host "Press Enter to exit"
