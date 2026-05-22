# native/ — Audio Fingerprinting Support

## fpcalc.exe

fpcalc.exe is the official Chromaprint audio fingerprinting tool. It is used
by the ♪ Deep Scan feature in the Duplicate File Scanner to identify files
that contain the same audio content at different quality levels or in different
containers — catches duplicates that size+metadata scanning misses.

## Three ways to get it:

### 1. Auto-install (recommended)
Click the "⬇ Install fpcalc" button in the Duplicates tab. The app downloads
the latest release directly from GitHub and installs it here automatically.
Internet connection required. Click "⬆ Check Update" at any time to update.

### 2. Manual install
Download from: https://github.com/acoustid/chromaprint/releases/latest
  → chromaprint-fpcalc-X.X.X-windows-x86_64.zip → extract fpcalc.exe
Place fpcalc.exe in this native/ folder alongside VideoMetadataEditor.exe.
The app detects it immediately on next launch — no rebuild needed.

### 3. Embed at build time (developers only)
Place fpcalc.exe in the Native/ subfolder of the SOURCE project before
running build_and_launch.bat. It will be embedded as an assembly resource
and auto-extracted to this folder on first run.

## Without fpcalc.exe

The app works normally. Only the ♪ Deep Scan button is unavailable.
All other duplicate detection (Stages 1-4) continues to work.
