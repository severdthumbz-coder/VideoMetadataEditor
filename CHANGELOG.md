# Changelog

All notable changes to Video Metadata Editor are documented here.

## v1.4.0 Build 100 — Write-failure dialog layout fix

- **Fix:** the "Write Failed" dialog packed five buttons (Open in fallback app, two Remux options, Copy, Close) into a fixed 720px non-resizable window using a horizontal StackPanel. The total button width exceeded the window, so the right-aligned row overflowed off the left edge and clipped the first button — e.g. "🔧 Open in TagScanner" showed as "in TagScanner". The window is now wider (880px) and resizable, and the buttons live in a WrapPanel so they reflow to a second row instead of clipping at any width.
- **Note on the underlying write failure:** for a file where verification reports the comment-token fields (IMDB/TMDB IDs, MPA, rating) were not committed while Title succeeds, the container cannot reliably store the embedded comment block — the genuine fix is Remux → .mp4 (fix + faststart), which rebuilds a clean container with the moov atom at the front so tags can be written. The remux + Replace Original flow then re-embeds the original metadata (with artwork, per Build 98).

---

## v1.4.0 Build 99 — Remux cache eviction on extension change

- **Fix:** after Remux → Replace Original, the library cache was evicted for the original path and the candidate path, but NOT for the actual final path when the container extension changed (e.g. original `.mkv` remuxed to `.mp4`, final name `Movie.mp4`). A stale cache entry under that final path could then be served on the next Scan Library, contributing to metadata appearing to revert after a remux. The final path is now also evicted.
- This was one mechanism (specific to remuxed files) within the broader family of write→cache→scan persistence issues addressed across Builds 92–98. It is not the root cause of reverts on files that were never remuxed — those were the `SyncLibraryEntry` early-return/in-memory mask (Build 92) and the first-save cache loss (Build 95).

---

## v1.4.0 Build 98 — Remux auto-embed and context-menu fixes

- **Remux → Replace Original → auto-embed:** the auto-embed that restores metadata into the freshly remuxed file now goes through the single source-of-truth projection (passing the loaded `VideoFile`), so the FILES panel reflects verified disk content and the disk-verified badge, instead of optimistically showing the intended metadata before it was confirmed on disk. Combined with the Build 97 verification handle fix, the embed that previously failed after a successful replace should now succeed (or give an honest diagnosis).
- **Artwork preserved on remux:** the original metadata captured before replacing is now read WITH artwork (was a fast/no-artwork read), so the remuxed file keeps its cover art on auto-embed rather than losing it.
- **Context-menu label fix:** the FILES-panel right-click item "Open in fallback app (e.g. TagScanner)" could render partially cut off; shortened to "Open in fallback app…" with the TagScanner/MediaInfo example moved into the tooltip.

---

## v1.4.0 Build 97 — Verification false-positive fix (file-handle ordering)

Follow-up to the Build 96 full-token verification. A user reported that a file which "wrote successfully" in older builds was now flagged as a failed write, and that even a remuxed copy failed — yet on rescan the file still showed its pre-write metadata (a genuine revert).

- **Root cause of the false failures:** the post-write verification re-opened the file with `File.Create` (a TagLib# open) while the *write* handle was still open inside the same `using` scope. Reading back tags through a second handle while the first hadn't released could return a partially-flushed/locked view, so verification reported phantom "fields not committed" failures on files that actually wrote correctly — including freshly remuxed ones.
- **Fix:** the tag-write now runs in its own `using` block that fully releases the file handle *before* verification re-opens it. Verification reads a settled file.
- **Description no longer hard-verified:** the free-text Description field is excluded from the pass/fail verification. Some containers truncate or normalise long comment atoms, which would fail an otherwise-correct write. Structured tokens (rating, IDs, MPA, watched, all TV fields) are still strictly verified — those are what matter for data integrity.
- **Note on genuine reverts:** when a file's metadata truly does not survive (rescan shows pre-write values), the write path now exhausts its temp-copy and in-place retries and surfaces a diagnosis that recommends a faststart remux. The remux uses lossless `-c copy -movflags +faststart`, which relocates the MP4 moov atom to the front so tags can be written — and with this build's handle fix, the remuxed file is verified correctly instead of false-failing.

---

## v1.4.0 Build 96 — Phase 2: Self-verification and recoverability

Builds on the Phase 1 single-projection foundation. The goal: a silent partial-write or revert should be impossible to miss.

### Full-token post-write verification
- After every embed, the write path re-reads the file and now verifies the **complete VME token set** (rating, IMDB/TMDB IDs, MPA rating, watched flag, and all TV episode fields), not just the Title. A container that silently dropped, say, the episode tokens previously passed the Title-only check and reverted on the next scan; now the write reports failure with the exact list of fields that did not commit.
- Verification is only enforced for formats that claim full-tag support (MP4/M4V/MKV/MOV). WebM/WMV/AVI, which already surface a capability warning, keep the Title-only check so the stricter verification doesn't produce false failures on formats that genuinely can't store the token block.
- The comparison logic lives in a new pure `VmeCommentCodec.VerifyWritten` method — case-insensitive, whitespace-trimmed, and it only checks fields that were actually set (no false positives for blank fields).

### "What's actually on disk" indicator
- The FILES panel now shows a per-file verification badge: green "✓ verified on disk" when the post-embed disk re-read matched the intended write, amber "⚠ not verified" when it did not. This makes the difference between "verified disk content" and "an in-memory snapshot that might revert" visible rather than hidden. Backed by a new `VideoFile.DiskVerified` state set during the single-projection sync.

### Tests
- New `VerifyWritten` unit tests (6) cover identical match, dropped rating, dropped episode fields (the Dr. Stone silent-revert scenario), unset-field false-positive guard, case/whitespace insensitivity, and the watched-false case. Pure logic — run unskipped in CI.

---

## v1.4.0 Build 95 — First-save cache persistence bug (caught by Phase 1 tests)

The Phase 1 integration tests did their job: they failed on first CI run and exposed a real, long-standing production bug in the cache persistence layer.

- **Bug:** `LibraryCacheService.SaveAsync` used `File.Replace(tmp, target, null)`, which throws `FileNotFoundException` when the target cache file does not yet exist — i.e. on the **first save for any library folder** (fresh install, or first scan of a newly added folder). The exception was swallowed by a silent `catch { }`, so the entire first cache write was lost and nothing persisted until some later save happened to run against an existing file. This compounded the "metadata reverts after restart" symptom on fresh setups.
- **Fix:** use atomic `File.Replace` only when the target already exists; otherwise `File.Move(..., overwrite: true)` to create it. The save `catch` now logs the failure to debug output and cleans up any orphaned `.tmp` file instead of swallowing silently — a fully silent catch is how this stayed hidden.
- All 5 `LibraryCachePersistenceTests` now pass (movie round-trip, TV-episode fields, stale invalidation, rename eviction, ghost pruning).

---

## v1.4.0 Build 94 — CI fix for Phase 1 tests

- Fix: the new `LibraryCachePersistenceTests` failed to compile in CI (`CS0246: LibraryEntry could not be found`). The test project compiles a hand-picked subset of source files rather than referencing the WPF main project, and `LibraryModels.cs` / `LibraryCacheService.cs` / `SubtitleDetector.cs` were not in that list.
- Added those three files to the test project's compile list.
- Guarded the WPF-only members of `LibraryModels.cs` under `#if !NO_WPF`: the `BitmapSource CoverSource` property and its `DecodeCoverArt` helper on `LibraryEntry` (the `CoverArt` byte storage the cache uses stays available unconditionally), and the entire `LibraryTab` class (a UI-only view-model using `CollectionViewSource`/`ViewModelBase`, irrelevant to cache logic). No behaviour change in the main WPF build, which always compiles these.

---

## v1.4.0 Build 93 — Phase 1: Foundation stabilization

This build pays down the architectural debt behind the recurring library state bugs, rather than adding features. No user-facing feature changes; the goal is that the revert class of bug cannot recur.

### Removed dead parallel state
- Deleted `LibraryViewModel` entirely. It was a half-wired shell holding its own `Entries`, `Tabs`, `View`, `Columns` and command set that the UI never bound to — the actual library state and commands live on `MainViewModel` (`LibraryEntries`, `LibraryView`, `LibraryTabs`, `TvShowTree`, `ScanLibraryCommand`, etc.). Two parallel "library entry" collections were a standing source of "which collection am I updating" confusion. Its only live effect, a column-visibility callback on tab change, is already handled by the real tab-selection path — verified before removal, so behaviour is unchanged.

### Single source-of-truth projection for metadata
- A file's metadata can live in four places: disk tags, the FILES-panel `VideoFile.EmbeddedMetadata`, the `LibraryEntry` grid row, and the persistent cache. Disk is the only authority. `SyncLibraryEntry` is now the one method every embed path funnels through; it does a single fresh disk read and projects that into all representations in one place.
- It now also refreshes the FILES-panel `VideoFile.EmbeddedMetadata` (via an optional parameter). Previously only the single-file path did this; the batch and TV-batch paths left the panel object holding a pre-write snapshot — the same revert class one layer up.
- Grid-entry and cache-entry field mapping is now a single shared local helper, so the two can no longer drift apart.
- All three embed paths (single-file, batch, TV batch) pass their `VideoFile` so the panel, grid, and cache update together.

### Tests — integration coverage for the bug class that kept recurring
- New `LibraryCachePersistenceTests` (5 tests) exercise the embed → Put → SaveAsync → (new instance) Load → TryGet lifecycle — the close/reopen cycle where metadata used to revert. These run unskipped in CI because the cache is pure logic (no TagLib# container needed). Coverage: movie round-trip, TV-episode fields round-trip (guards the "Dr. Stone S02E04 untagged" symptom), stale-file invalidation, rename old-path eviction, and ghost pruning.
- Total: 80 test methods across 10 test files.

---

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
