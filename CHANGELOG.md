# Changelog

All notable changes to Video Metadata Editor are documented here.

## v1.4.0 Build 92 — Library persistence root-cause fix

### Library metadata no longer reverts after scan / restart (the persistent bug)
- **Root cause:** `SyncLibraryEntry` trusted the in-memory metadata object as the cache's source of truth and returned early (`if (entry == null) return;`) before updating the cache whenever a file was not yet in `LibraryEntries` (e.g. added via Add Files/Add Folder rather than a library scan). The persistent cache kept stale or absent data, so the next scan or app restart reverted the grid to the pre-embed values. The in-memory `_recentlyEmbedded` forced-read set masked this within a session but is wiped on restart — which is why "close and reopen" reverted.
- **Fix:** `SyncLibraryEntry` now re-reads the file from disk after every embed and writes that authoritative result into both the grid entry and the persistent cache. The cache is updated **unconditionally** — even when the file is not yet in `LibraryEntries`. On rename it evicts the stale old-path cache entry so a future scan can't resurrect pre-rename metadata.
- All three embed paths (single-file, batch, TV batch) now call `SyncLibraryEntry`; previously only the single-file path did.
- FILES panel `EmbeddedMetadata` is refreshed from disk after a single-file embed so the panel reflects exactly what was persisted.
- TV-tree refresh from `SyncLibraryEntry` is marshalled to the UI dispatcher, since batch embeds call it from background tasks.

### TV tree — tagged episodes no longer shown as "untagged"
- Dr. Stone S02E04 and similar correctly-tagged episodes were appearing as "(untagged — embed metadata)" placeholders. This was a downstream symptom of the revert bug (a reverted episode lost `IsEpisode` and was re-surfaced by the folder gap-scanner), plus the gap-scanner not excluding episode numbers already represented by a real `LibraryEntry`.
- **Fix:** `InsertMissingEpisodes` now receives the set of already-tagged episode numbers and only flags a gap slot as "untagged" when a physical file exists **and** no tagged entry already covers that episode.

---

## v1.4.0 Build 91 — Library in-place merge + binding/stability fixes

- Library in-place merge — `LibraryEntries.Clear()` removed; entries updated/added/removed selectively to preserve in-memory embed updates
- `MarkAsEmbedded` forces a fresh TagLib# read for embedded files on next scan
- Binding fixes: `SubtitleSearchResults`, `InvBool`, `BoolToWatchedLabel`, `WriteStatus.Untouched`
- Stability: `CanScanLibrary` checks `!IsBusy`; `ConcurrentDictionary` cache index; Trakt token-refresh semaphore; static `HttpClient` singleton
- New: subtitle panel, Undo Batch button, Save Layout column sync, Export All NFO, watch progress badge, auto update check

---

## v1.4.0 Build 90 — Quality pass and pre-release fixes

### Functional improvements
- Fingerprint cache import now supports **Merge vs Replace** with no restart required
- Quick Scan now honours excluded patterns (previously only respected in Deep Scan)
- Cross-stage duplicate group deduplication — same file pair caught by multiple stages appears only once
- TMDB popularity score used as tiebreaker in search ranking
- Standard tag fallback reads IMDB ID, rating, MPA from Apple tag atoms (files tagged by external tools)
- Health Check: **Fix Extension** renames mislabelled files instantly (no remux, no re-encode)
- TV batch logs library gaps for skipped files (no S##E## code)
- Watch folder completely rewritten: silent seed pass prevents startup flood; FSW buffer-overflow recovery does immediate poll instead of waiting

### Thread safety
- `ConsoleLog.Insert` routed through dispatcher-aware `Log()` helper across all 122 call sites
- Fixes `NotSupportedException` on CollectionView from background threads (Trakt startup, library scan)

### Infrastructure
- Library cache auto-prunes ghost entries on every scan
- Fix All Faststart uses `DriveCapabilityService` for worker count (HDD: 1–2 workers, NVMe: 12–16)
- `VmeCommentCodec` extracted as pure-logic class, testable without TagLib
- Language dropdown now shows only languages with actual translations (removed 0-key entries)
- TMDB attribution links added to splash screen and Help → Credits section
- Batch search throttled to 3 concurrent slots + 350ms gap to stay within TMDB rate limits
- Recycle Bin delete handles network paths (confirms permanent delete, falls back gracefully)
- Settings tab redesigned with ListBox navigation (8 categories, replaces 23-GroupBox scroll wall)
- Recommended Workflow redesigned as interactive ComboBox selector (9 topics, replaces 43-step list)
- Help tab: 7 previously undocumented features added to workflow steps

### Tests
- `MetadataWriteTests` (8 tests) — real MP4/MKV file write + read-back round-trips
- `MetadataRoundTripTests` (10 tests) — VME comment codec encode/decode
- Total: 74 test methods across 9 test files

### CI
- GitHub Actions updated to `checkout@v4.2.2` / `setup-dotnet@v4.3.1`

---

## v1.4.0 Build 89 — Media Health Check + Deep Scan polish

- Media Health Check: detect and fix MP4 no-faststart, extension/container mismatch, zero-byte files, unreadable headers
- Fix All Faststart with parallel ffmpeg execution and IsFixing banner
- Deep Scan audio fingerprint: buffer overflow recovery, FSW restart with immediate poll
- Remux commit workflow (Replace Original / Restore Original)
- Startup orphan sweep for `.faststart.tmp.*` and `.remux.*` files
- Library cache statistics in scan status line

---

## v1.4.0 Build 77–88 — Library and Duplicates

- Library tab: multi-folder support, tabbed display, TV show tree with gap detection
- Library watch service (MultiWatchFolderService)
- Library cache with drive-aware parallel scanning
- Duplicate detection stage 4: alternate version detection
- Chromaprint audio fingerprint deep scan with persistent cache
- FastCopy integration for bulk duplicate moves
- XLSX library export
- Smart Organisation routing for Move/Copy

---

## v1.4.0 Build 46–76 — Integrations and Reliability

- Trakt.tv watch history sync with DPAPI token storage
- OpenSubtitles subtitle download
- NFO export for Kodi/Jellyfin/Plex
- AniList anime search provider
- MKVToolNix mkvpropedit integration for proper Matroska cover.jpg
- Write failure diagnosis (read-only, process lock, network, permissions)
- Recovery dialog for crash orphans
- Dark/Light theme with DynamicResource throughout

---

## v1.0.0 Build 1 — Initial Release

- Full metadata read/write via TagLib#
- TMDB + OMDB API integration
- Batch folder processing
- Dark/Light theme
- Portable single-EXE with config.json in app directory
