# Video Metadata Editor

A portable Windows desktop application for embedding metadata into video files.

![Build and Test](https://github.com/severdthumbz-coder/VideoMetadataEditor/actions/workflows/build-and-test.yml/badge.svg)

## What it does

- **Embed metadata** — title, year, director, genre, rating, cast, artwork, IMDB/TMDB IDs — into MP4, MKV, MOV, M4V, WMV, AVI, and WebM files using TagLib#
- **Find duplicates** — 5-stage detection (size, metadata, SHA-256 hash, alternate versions, audio fingerprint via Chromaprint)
- **Media health check** — detect and fix container issues: missing faststart, extension/container mismatch, broken headers, zero-byte files
- **Library management** — scan and browse your full video library with filtering, sorting, and TV episode gap detection
- **Integrations** — TMDB, OMDB, AniList (anime), Trakt.tv watch history, OpenSubtitles download, Kodi/Jellyfin NFO sidecar export

## Quick start

1. Download the latest release zip and extract it anywhere
2. Run `VideoMetadataEditor vX.Y.Z.exe` — no installer needed
3. Add your TMDB API key in **Settings → Metadata & APIs** (free at [themoviedb.org](https://www.themoviedb.org/settings/api))
4. Use **Add Folder** or drag files into the FILES panel
5. Select a file, search for metadata in the RETRIEVED DATA tab, click **Apply + Embed + Rename**

## Requirements

- Windows 10 or 11 (x64)
- .NET 8 Desktop Runtime (prompted on first launch if missing)

## Optional tools

These are not required but enable additional features:

| Tool | Feature | How to install |
|---|---|---|
| ffmpeg | Fix All Faststart, remux | Settings → External Tools → Download |
| fpcalc (Chromaprint) | Audio fingerprint duplicate detection | Duplicates tab → Install fpcalc |
| MKVToolNix (mkvpropedit) | Better MKV artwork for Plex/Jellyfin | Drop `mkvpropedit.exe` in the `native\` folder, or install normally |

## Write safety

Every metadata write uses a temp-file pattern — the original is never modified directly:

1. Original is copied to `.vme_tmp_GUID.ext`
2. Tags are written to the temp file
3. On success: original is replaced atomically
4. On failure: temp file is deleted, original is untouched
5. If the app crashes mid-write, the next launch detects and offers to recover or clean up the orphan

## Attribution

This product uses the TMDB API but is not endorsed or certified by TMDB.

- Metadata from [The Movie Database (TMDB)](https://www.themoviedb.org) — [Terms of Use](https://www.themoviedb.org/documentation/api/terms-of-use)
- Additional data from [OMDB API](https://www.omdbapi.com), [AniList](https://anilist.co), [Trakt.tv](https://trakt.tv), [OpenSubtitles](https://www.opensubtitles.com)
- Tag writing: [TagLib#](https://github.com/mono/taglib-sharp) (LGPL v2.1)
- Audio fingerprinting: [Chromaprint / fpcalc](https://acoustid.org/chromaprint) (LGPL v2.1)
- MKV artwork: [MKVToolNix](https://mkvtoolnix.download) (GPL v2)
- Media repair: [FFmpeg](https://ffmpeg.org) (LGPL v2.1 or later)

## License

See [LICENSE](LICENSE) for details.
