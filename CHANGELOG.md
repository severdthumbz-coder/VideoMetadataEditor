# Changelog

All notable changes to Video Metadata Editor are documented here.

## v1.4.0 Build 116 — Phase 2: DuplicatesViewModel fully decoupled + TV tree poster caching

- **TV tree poster delay (partial fix):** show posters in the TV tree were decoded from JPEG synchronously on the UI thread, and a fresh decode happened every time the tree was rebuilt (filter, sort, or switching into Tree view). Decoded posters are now cached by the identity of their source bytes (via a ConditionalWeakTable), so repeated rebuilds and re-entry into Tree view reuse the already-decoded frozen bitmap instead of re-decoding. Known remaining cost: the `TvShowTree` getter still rebuilds all nodes and re-scans season folders for missing episodes on each access — that heavier rebuild is a separate, larger performance fix (caching the built tree and rebuilding only on real data change) planned for a future build.
- **Phase 2 — DuplicatesViewModel fully decoupled from WPF.** Following the dialog migration in Build 115: its scan-progress `Dispatcher.InvokeAsync` now goes through `IUiDispatcher`, and the custom side-by-side compare window is no longer constructed in the ViewModel — the View supplies it via a `CompareDialogRequested` callback that returns the chosen delete path. DuplicatesViewModel now references WPF only through the cross-platform `ICommand` interface. No behaviour change.

---

## v1.4.0 Build 115 — Phase 2: DuplicatesViewModel migrated to dialog abstraction

Continues Phase 2. No behaviour change.

- `DuplicatesViewModel` now uses `IDialogService` for all its standard dialogs: both folder pickers (source/destination), all six message boxes (batch delete/move confirm, network permanent-delete confirm, partial-failure warning, single delete/move confirm, error, and the import merge/replace prompt), and the three file pickers (reference-file open, cache export save, cache import open). Same prompts, buttons, and behaviour.
- It now references WPF only via `ICommand`, a `Dispatcher` block (batched UI updates during scan), and the custom `DuplicateCompareDialog` window. Those last two are deliberately deferred: the dispatcher block will move to `IUiDispatcher`, and the compare window — being a custom View, not a standard dialog — will be extracted via an event to the code-behind, both as their own focused follow-ups.
- The real `WpfDialogService` is injected at construction; headless contexts fall back to the safe `NullDialogService`.

---

## v1.4.0 Build 114 — Phase 2: dialog abstraction (Health Check migrated) + Help cleanup

Continues Phase 2. No behaviour change.

- New `IDialogService` abstraction over message boxes and file/folder pickers (platform-neutral `DialogButtons`/`DialogIcon`/`DialogResult` enums), with a WPF implementation that stays in the app and a headless `NullDialogService` that auto-cancels and never auto-confirms a destructive prompt.
- `MediaHealthViewModel` migrated to it: the faststart confirm, fix-complete summary, remux complete/failed, ffmpeg-missing, and re-embed confirm dialogs all route through the abstraction with identical text and buttons. It now references WPF only via `ICommand` and one `Clipboard` call (the latter to be abstracted next).
- `DuplicatesViewModel` (which also has file/folder pickers) is the next migration target.
- Added CI tests for `NullDialogService`.
- Help tab: fixed the Revision History so it reads strictly newest-first (an out-of-order Build 105 entry was relocated to its correct position), and expanded the terse Build 110–113 entries to the same level of detail as older entries. Updated the Media Health Check workflow to document the semicolon Warning and the "Re-embed Semicolon Files" button.

---

## v1.4.0 Build 113 — Phase 2: command framework decoupled from WPF CommandManager

The keystone Phase 2 change. Behaviour is intended to be identical to before; this is a coupling change, not a behaviour change.

- The `RelayCommand` / `AsyncRelayCommand` family no longer references WPF's `CommandManager` directly. CanExecute re-evaluation is routed through a new platform-neutral seam, `CommandRequery` (interface `ICommandRequeryProvider`).
- On WPF, the app installs `WpfCommandRequery` at startup (in `App.OnStartup`, before any ViewModel/command is created). It is backed by `CommandManager.RequerySuggested` / `InvalidateRequerySuggested()`, so the automatic enable/disable behaviour every button relies on is preserved exactly. The WPF reference now lives in one small provider class instead of being scattered through the command types.
- VM-level invalidate calls in `CopyMoveViewModel` (IsRunning) and `MetadataViewModel` (IsBusy) now go through `CommandRequery.Invalidate()` too.
- Result: the command classes and the cleaned child ViewModels reference WPF only through the cross-platform `ICommand` interface — a future Avalonia/MAUI host installs its own requery provider.
- Added CI tests for the `CommandRequery` seam (default safety, provider swap, handler routing).

What to verify after updating: command enable/disable behaves exactly as before — e.g. Search enables once you type a query, Embed/Apply enable when a file is selected, async buttons (Embed, Copy/Move, Health Check fixes) disable while running and re-enable when done, and the Health Check buttons enable based on their counts.

---

## v1.4.0 Build 112 — Phase 2: MetadataViewModel fully WPF-imaging-free + debounce cleanup

Continues Phase 2. No behaviour change.

- Removed a dead `BitmapSource ArtworkImage` property (and its `System.Windows.Media.Imaging` import) from `MetadataViewModel` — it was an unused leftover from the earlier extraction; the live artwork display binds to `MainViewModel.ArtworkImage` and to `RetrievedMetadata.ArtworkBytes` via the existing `BytesToImg` converter. `MetadataViewModel` now references WPF only through `ICommand`.
- Tightened the Build 111 rename-preview debounce to dispose the previous `CancellationTokenSource` when it's replaced, preventing accumulation of undisposed token sources during rapid typing.

Note: the remaining cross-platform blocker in the child ViewModels is not `ICommand` itself (shared across WPF/Avalonia) but `CommandManager.RequerySuggested` in the command classes, which is WPF-only. Replacing that auto-requery mechanism is a high-impact change affecting every command and will be done as its own carefully-tested build rather than folded into a sweep.

---

## v1.4.0 Build 111 — Phase 2: MetadataViewModel timer decoupling

Continues Phase 2. No behaviour change.

- `MetadataViewModel`'s rename-preview debounce no longer uses WPF's `DispatcherTimer`. It now uses a platform-agnostic `Task.Delay` + `CancellationTokenSource` debounce and marshals the result back to the UI thread via `IUiDispatcher`. Same 150ms debounce, same preview output.
- This removes the `DispatcherTimer` dependency from the ViewModel. The remaining WPF reference there (`BitmapSource ArtworkImage`) is scheduled for its own focused build, since changing the artwork property type touches the XAML `<Image>` binding and warrants isolated testing.

---

## v1.4.0 Build 110 — Phase 2: UI-dispatcher abstraction (cross-platform prep)

First step of Phase 2 (purifying the extracted ViewModels of WPF dependencies so they can move to a platform-agnostic Core project for Phase 3). No behaviour change.

- New `IUiDispatcher` abstraction (with a headless `NullUiDispatcher` default) decouples ViewModels from WPF's `System.Windows.Threading.Dispatcher`. The WPF-specific implementation (`WpfUiDispatcher`) is platform glue that stays in the app and is the only place referencing the WPF Dispatcher/CommandManager for this path.
- `SearchViewModel` no longer references `Application.Current.Dispatcher` or `CommandManager` directly — it calls `IUiDispatcher.InvalidateCommands()` instead. At runtime it receives a real `WpfUiDispatcher`; in headless contexts it falls back to the inline `NullUiDispatcher`. This is the proof-of-pattern that subsequent builds will apply to `MetadataViewModel` (DispatcherTimer) and the `MainViewModel` partials.
- Added CI tests for `NullUiDispatcher`. The `IUiDispatcher` abstraction is now compiled into the test project.

---

## v1.4.0 Build 109 — Re-embed semicolon-flagged files from Health Check

- New **"📝 Re-embed Semicolon Files (n)"** button on the Media Health Check panel, next to Fix All Issues. It loads every file flagged with the `SemicolonInComment` warning into the FILES panel and switches to that tab, ready for you to re-fetch metadata and Embed on the current (Build 105+) codec, which writes a clean, sanitised comment.
- The button is enabled only when at least one semicolon-flagged file is present and no scan/fix is running; its label shows the live count.
- Honesty note surfaced in the confirmation dialog: tags already lost to a pre-105 truncation cannot be recovered from the file itself — re-fetching from the metadata provider restores them. This is why the action loads files for a proper re-embed rather than silently rewriting the truncated on-disk comment (which would only sanitise the `;` without recovering dropped tokens).
- This complements "Fix All Issues", which intentionally excludes semicolon files because their remedy is a re-embed, not an ffmpeg remux.

---

## v1.4.0 Build 108 — CI build fix + semicolon check in Health Check

- **Fix:** Build 107 failed CI — `WriteSelfTest.cs` used `Path`/`FileInfo`/`Directory` which did not resolve in the WPF markup-compile pass (`CS0103`/`CS0246`). Switched those BCL IO calls to explicit aliases (`IOPath`, `IODir`, fully-qualified `System.IO.FileInfo`) matching the existing `SysFile` alias pattern, so resolution no longer depends on using-context.
- **Health Check now flags semicolons:** the Media Health Check runs a second pass that reads each file's embedded comment and flags any containing a `;` (new `SemicolonInComment` warning). These are files whose stored tags may have been truncated by a pre-Build-105 write; the suggested fix is to re-embed them on Build 105+. The flag is a Warning (never masks a more serious container error) and is excluded from the ffmpeg "Fix All" set since it is resolved by re-embedding, not remuxing.
- Tests added for `CheckEmbeddedComment` (flags a semicolon, ignores clean text, never masks an error).

---

## v1.4.0 Build 107 — Built-in Full Write Diagnostic tool

- Enabling **Settings → Write Error Diagnostics → "Enable full diagnostic dump"** now also reveals a **"🩺 Run Full Write Diagnostic on a File…"** button. It folds the entire v1–v16 investigation that found the Build 105 semicolon bug into a one-click tool: pick a file (defaults to your Move/Copy destination folder), and it runs all 16 write checks against a **safe copy** (the original is never modified), streaming a green check or red cross per step with details, finishing with a plain-language interpretation of where (if anywhere) the write breaks down, and an **Export Log…** button to save the full report wherever you like.
- New engine `WriteSelfTest` implements the 16 checks (basic round-trip, comment+description, full field set ±artwork, same-drive write, overwrite, length tolerance, the file's own artwork, atom/faststart layout, determinism, the file's real description, the semicolon-truncation probe, Encode sanitization, full encoded round-trip, atomic temp+replace, and end-to-end VerifyWritten). It is UI-free and unit-tested.
- Help tab: Revision History and the Recovery workflow document the new tool and when to use it.

---

## v1.4.0 Build 106 — Optional diagnostic dump + Help updates

- The full per-file write diagnostic that isolated the Build 105 semicolon bug is now retained as an **opt-in** tool instead of being removed. New setting: **Settings → Write Error Diagnostics → "Enable full diagnostic dump"** (default OFF). When enabled, every embed appends a detailed report to `%TEMP%\vme_artdump\<filename>.diag.txt` capturing, at each pipeline stage (pre-write, after temp copy, pre-Save, post-Save), the MP4 atom layout (ftyp/moov/mdat/udta/ilst + faststart verdict), TagLib# tag state, the intended comment with parsed tokens, and compressed-artwork validity. It has no effect on the write itself and is fully guarded.
- Help tab: Revision History updated (Builds 105/106), and the Recovery workflow now documents the recommended steps for a persistent write failure — remux → replace, re-embed on the current build, and finally the diagnostic dump.

---

## v1.4.0 Build 105 — ROOT CAUSE FIX: semicolon truncated the metadata comment

This is the fix for the long-running "metadata embeds then reverts on rescan" / "Write Failed" problem that affected only *some* files (e.g. "A Taste of Hunger (2021)", "Dr. STONE S02E04").

**Root cause:** TagLib# 2.3.0 truncates the MP4/Apple comment atom (©cmt) at the **first semicolon (`;`)** when writing. VME packs its structured metadata as `[VME:...]` tokens appended to the END of the comment, after the plot description. So any file whose description contained a semicolon had its entire token block silently cut off at write time — the IMDB/TMDB IDs, rating, MPA, and all TV episode fields were dropped, and the file kept its previous comment. On the next Library scan the file therefore appeared to "revert" to its pre-embed state. Files whose descriptions had no semicolon were unaffected, which is why it hit only some files.

This was isolated with a sequence of standalone diagnostics: synthetic comment text of any length wrote fine, but the real descriptions failed at a fixed point; character-class elimination then pinned it to the semicolon, and the truncation position exactly matched the read-back length on both test files.

**Fix:** `VmeCommentCodec.Encode` now sanitizes semicolons (replacing `;` with `,`, which reads naturally in prose) in the description and in free-text token values (show/episode titles) before building the comment, so the token block can never be truncated away. Decoding is unaffected.

**Tests:** added regression tests using the exact real "A Taste of Hunger" and "Dr. STONE" descriptions, asserting the output contains no `;` and that all tokens still decode. Also a direct `Sanitize` test.

Diagnostic instrumentation from Builds 101–104 has been removed now that the cause is found; the artwork-free write fallback (Build 102) and full-token post-write verification (Build 96) remain as defense-in-depth.

---

## v1.4.0 Build 104 — Comprehensive write diagnostic dump

- Expands the Build 103 dump into a full per-file snapshot written to `%TEMP%\vme_artdump\<name>.diag.txt`, captured in ONE embed attempt and firing for every file (with or without artwork, movies and TV episodes alike). Intended to confirm whether the same root cause affects both "A Taste of Hunger" and "Dr. STONE S02E04".
- Each report records, at four pipeline stages (pre-write original, temp-after-copy, pre-Save with tags set, post-Save after handle release): the MP4 atom layout (ftyp/moov/mdat/udta/ilst offsets and a faststart/moov-at-end verdict), TagLib# tag state (tagTypes, Title, Comment length+preview, Description length, picture count/size, Apple comment length), the exact intended comment with parsed tokens, the metadata object, and compressed-artwork JPEG validity. The post-Save stage reveals definitively whether Save() committed or silently left the old comment.
- Diagnostic only — no change to write behaviour.

---

## v1.4.0 Build 103 — Artwork-output dump for diagnosis

- Seven standalone diagnostics have now cleared the file, container, A: drive, File.Replace, in-place write, comment length (100–1000 chars all round-trip), and pre-existing-tag overwrite. The only remaining unreproduced variable is the actual output of `CompressArtwork` (System.Drawing/GDI re-encode), which cannot be produced outside the running app.
- This build dumps the exact compressed artwork bytes and comment string to `%TEMP%\vme_artdump\` during a write, so the real GDI output can be loaded into the standalone diagnostic and tested directly. Diagnostic only — no behaviour change to the write.

---

## v1.4.0 Build 102 — Artwork-free write fallback + fuller diagnostic

- Standalone diagnostics cleared the file, container, artwork bytes, field ordering, the A: drive, File.Replace, and in-place writes — every operation works in isolation. The one app step never reproducible standalone is `CompressArtwork` (System.Drawing re-encode) feeding `tag.Pictures`, so the prime remaining suspect is the picture atom disturbing the comment atom on this specific container.
- **Automatic artwork-free retry:** when a write fails post-write verification and artwork was being embedded, the app now retries once on a fresh temp copy WITHOUT artwork. If that succeeds, the text metadata (IDs, rating, MPA, TV fields) is preserved and the file's existing cover art is left untouched — turning a total failure into a partial success.
- **Fuller diagnostic:** the verification-failure message now reports the verify file's TagTypes, the intended vs read-back comment lengths, and a readback preview, so the remaining unknown is fully visible.

---

## v1.4.0 Build 101 — Write-verification diagnostic instrumentation

- Standalone testing proved TagLib# writes and reads the VME comment tokens correctly on the reported problem file in every configuration (with/without real 67KB artwork, short and 737-char comments) — so the container, file, artwork, and TagLib# are not the cause. The remaining possibility is an asymmetry inside the app's own write/verify comparison.
- This build adds diagnostic output to the post-write verification failure path: it logs (and appends to the error dialog) the intended IMDB token versus the read-back IMDB token, so a verification mismatch can be pinpointed exactly rather than inferred. No behaviour change to the write itself.

---

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
