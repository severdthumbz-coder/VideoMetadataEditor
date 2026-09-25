# Changelog

All notable changes to Video Metadata Editor are documented here.

## v1.4.0 Build 153 — Onboarding, part 2b: contextual hints on every tab

Extends the Build 152 hint markers to the remaining tabs so the ⓘ helper is consistent across the whole app.

- **New ⓘ markers** on: Move/Copy (what the transfer does + subtitle sidecars), Settings (how the category nav works + auto-save), Library (what indexing does, that it's read-only), Duplicates (how detection works, nothing deleted without confirmation), and Health Check (read-only detection + lossless Stage 2 fixes).
- Same reusable hover pattern and the same master gate — all markers appear/disappear with Settings → Behaviour → "Show onboarding helpers".
- Layout only; no behaviour change.

Next in this arc: the first-run guided tour (part 3).

---

## v1.4.0 Build 152 — Onboarding, part 2: contextual hints (Raw vs Retrieved Data)

Second piece of the onboarding work: reusable ⓘ hint markers that explain a control on hover, starting with the most common point of confusion — the two data tabs.

- **ⓘ hint markers** appear next to the "Embedded Metadata" header on the Raw Data tab and above the search on the Retrieved Data tab. Hovering either one explains the distinction: Raw Data is what's written inside the file (and what you edit + embed), Retrieved Data is what TMDB/OMDB found online (search, pick a result, and it fills the Raw Data fields).
- **Reusable pattern:** the hint marker is a shared style, so future builds can drop the same ⓘ next to any other tricky control with one line.
- **Gated by the master toggle:** all hints hide when Settings → Behaviour → "Show onboarding helpers" is turned off, alongside the Legend and the coming first-run tour.

Next in this arc: the first-run guided tour (part 3).

---

## v1.4.0 Build 151 — Onboarding, part 1: icon & colour Legend

First piece of the progressive-disclosure onboarding work: a Legend that explains the app's icons and row colours, so newcomers aren't left guessing what a yellow row or a ♥ means.

- **❔ Legend button** in the top-right toolbar (next to the theme toggle) opens a popup flyout listing every row colour and icon with a plain-language meaning. Click away to dismiss.
- **Row colours explained:** green = written & renamed successfully this session, red = write failed, yellow/gold = newly watch-detected and not yet processed — the states that show up in the Files panel.
- **Icons & concepts:** 🔒/🔓 lock state, ♥ watched, ⚠ write error, ⬇ subtitle download, 🎬 launch external app, the Raw Data vs Retrieved Data distinction, and sidecar vs embedded tags.
- **Also in Help:** the same reference appears as a "Legend" section in the Help tab.
- **Master toggle:** new Settings → Behaviour → "Show onboarding helpers (legend, hints, first-run tour)" (default on). It governs the coming contextual hints and first-run tour; the Legend itself stays reachable from the toolbar either way.

Next in this arc: contextual hint markers (part 2), then the first-run guided tour (part 3).

---

## v1.4.0 Build 150 — Subtitles panel padding fix

Small visual fix following Build 149: the Subtitles panel content sat flush against the expander edges.

- The "Language:" label and dropdown are now inset from the left edge, and all panel content (results list, buttons, status text) is pulled off the right edge.
- The Download Selected / Launch Subtitle App button row is nudged in slightly so it aligns with the rest of the panel.
- No behaviour change — layout only.

---

## v1.4.0 Build 149 — Finish the OpenSubtitles arc: language picker, best-match sort, external-app hook

Completes the subtitle-download feature end to end and makes it discoverable — the panel was easy to skip over even though the download plumbing was already there.

- **Language dropdown.** The Retrieved Data → 🗒 Subtitles panel now has a "Language" dropdown of common languages (English, Spanish, French, Japanese, Portuguese-BR, and ~25 more). Picking one fills the language box; the box stays free-text, so raw codes like `pt-BR` or a multi-language list `en,fr` still work by hand.
- **Best match first.** Search results are now sorted by download count (then rating) instead of raw API order, and the top result is pre-selected — so for the common "just get me the English sub" case, one ⬇ Download grabs the best match.
- **Discoverable.** The Subtitles expander now opens by default and its header reads "🗒 Subtitles — download from OpenSubtitles", so the feature is visible rather than collapsed and unlabelled.
- **External subtitle app hook (optional).** New Settings → Metadata & APIs → 🗒 OpenSubtitles → **External Subtitle App** field: Browse to map any subtitle/dubbing tool (Subtitle Edit, or your own app). A **🎬 Launch Subtitle App** button then appears in two places — the Files-panel toolbar and the 🗒 Subtitles panel — and opens the currently-selected video in that app, passing the file path as an argument. Left blank, the button stays hidden/disabled. This is scaffolding for an external AI subtitle/dubbing workflow.
- No change to the download naming or transfer: subtitles still save as `<VideoBase>.<lang>[.forced][.hi].<ext>` and travel with the video via the Build 148 sidecar transfer.

---

## v1.4.0 Build 148 — Move/copy subtitle sidecars with their video

Move/Copy (Custom Fast engine) now also transfers subtitle sidecar files, so subtitles no longer get left behind in the source folder.

- **What travels:** any subtitle file sharing a video's base name — `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`/`.idx` (VobSub pair), `.sbv`. (`.lrc` is excluded — it's a lyrics format, not subtitles, and no media server pairs it.)
- **Renamed to match:** each subtitle is renamed to the video's *final* name while keeping its exact language/flag suffix — so `Show.S01E01.en.forced.srt` following `Show.S01E01.mkv` → `Show - S01E01 - Title - en.forced.srt` stays paired in Plex/Jellyfin. The suffix is preserved verbatim (no lossy re-parsing), so `.en`, `.forced`, `.hi`/`.sdh`, multi-token suffixes all survive.
- **Scope:** the Custom Fast engine only — which is the engine Smart Organisation uses (FastCopy, an external process, can't do per-file companion handling). The Smart Organisation description now notes that subtitles travel with each video.
- **Conflict handling:** follows the same File Conflict Resolution setting as the video (Skip skips an existing subtitle; other modes overwrite). Subtitle transfer is best-effort — a subtitle failure never fails the video transfer, and subtitles aren't counted as separate files in the result.
- **Toggle:** Settings → Behaviour → "Move/copy subtitle sidecars with their video" (default on).

---

## v1.4.0 Build 147 — Fix: auto-embed failed to match shows whose title has an apostrophe

Auto-embed couldn't confidently match shows like "Marvel's The Defenders" when the files dropped the apostrophe (`Marvels.The.Defenders`), leaving every episode queued for manual processing with "no confident match."

- **Cause:** the title-similarity normaliser replaced *all* punctuation — including apostrophes — with a space. So TMDB's "Marvel's The Defenders" became "marvel s the defenders" (splitting "marvel's" into two words "marvel" + "s"), while the file's "Marvels The Defenders" stayed one word "marvels". The word-overlap score dropped to ~0.4, below the 0.70 confidence threshold, so the match was rejected.
- **Fix:** apostrophes (straight and curly) and backticks are now *removed* rather than replaced with a space, so "Marvel's" collapses to "marvels" and matches a filename that omitted the apostrophe. The two titles now score 1.0 (identical) and match confidently. This only makes genuinely-same titles match; it can't cause false matches, since it merely reconciles the possessive/no-apostrophe spelling of the same word.

Applies to the auto-embed confidence match for watched TV files. Shows with apostrophes in their names now auto-embed instead of being queued for manual handling.

---

## v1.4.0 Build 146 — Auto-embedded TV episodes now get the series rating (part 2)

Auto-embedded TV episodes were embedding with rating 0.0 because TMDB gives most individual episodes no `vote_average` — the show's score (e.g. 8.2) lives on the *series* record, not the episode.

- **Fix:** `GetTvEpisodeAsync` now falls back to the series `vote_average` (and vote count) when the episode has no rating of its own. This needs **no extra API call** — the series record was already being fetched for the show title, genres, and content rating; the rating was simply never read from it. Episode-level ratings still win when present.
- Also populates `RatingVotes` to match whichever source (episode or series) supplied the rating; previously it was left at 0 for episodes.

Result: the single verified entry an auto-embedded episode produces now carries the show's rating instead of 0.0.

**Also:** the Settings → Behaviour cleanup toggle is renamed "Auto-clean stale **&amp; duplicate** Files-panel entries after rename," and its tooltip now notes it collapses duplicate rows too (keeping the verified one), not just stale ones — because that toggle now gates the build-145 duplicate-collapse fix as well. Turning it off brings back the duplicate/stale rows, so it's recommended ON.

---

## v1.4.0 Build 145 — Fix (part 3): the duplicate-collapse now runs on the path auto-embed actually uses

Builds 141–144 added stale-entry sweeping and same-file collapse logic, but the duplicate persisted — and this build explains why: all that logic was hooked onto `ApplyToFileAsync`, while the watch-folder auto-embed uses a *different* embed method (`EmbedTvEpisodeAsync`) that never called it. The reconciliation existed but never ran for auto-embedded files. (This was also why a failed Move could report "Could not find file" — a stale entry pointing at a pre-rename path was never cleaned.)

- **Fix:** `EmbedTvEpisodeAsync` — the method auto-embed and batch both use — now calls the duplicate-collapse and stale-entry sweep after its rename, both immediately and again on a short (~4s) delay. The delay matters because the phantom entry is created by an *asynchronous* re-detection (FileSystemWatcher event + the watch's file-settle delay) that can land seconds after the rename; the delayed pass catches that late-born duplicate. The collapse is idempotent and self-marshalling, so the extra pass is a safe no-op when there's nothing to do.
- Survivor rules unchanged: keep the undo-bearing entry, else the verified entry, else the most-complete; never remove an entry holding undo state.

This is the placement fix the earlier parts needed. Parts 1 (drop redundant search, 143) and 2 (collapse logic, 144) were correct in themselves; they just weren't wired into the auto-embed path until now.

---

## v1.4.0 Build 144 — Fix (part 2): collapse duplicate same-file panel entries after rename

Build 143 removed the redundant second *search*, but the logs showed the duplicate panel row persisted from a different cause: the auto-embed **rename** fires a FileSystemWatcher event for the new name, which re-detects the file and adds a second entry (unverified/yellow) beside the embedded+verified one (green). Winning that detection timing race up front proved unreliable, so this takes the deterministic approach: reconcile *after*.

- **Fix:** after a watched file is added and after a rename, the panel collapses entries that resolve to the same physical file (canonical path) down to one. Survivor precedence: an entry holding undo state first (so Undo Last Embed / Undo Batch can never be orphaned), then the disk-verified entry, then the most-complete one. Non-survivors are removed — but never one that holds undo state, so if two duplicates both carried undo state, both are kept rather than risk breaking undo.
- Because it runs after the entries exist, it's immune to the detection timing that defeated the earlier prevention attempts.
- Gated behind the same Settings → Behaviour toggle as the stale-entry sweep (default on).

This closes the duplicate-entry issue and, importantly, the risk that a duplicate row pointing at a renamed file could cause a later Move/Copy to act on the wrong location. The surviving entry is the verified one; a following change will enrich its TV rating (series-rating fallback) so it's also the most complete.

---

## v1.4.0 Build 143 — Fix (part 1): stop the redundant second search that caused duplicate watch entries

The real cause of the duplicate Files-panel row (traced from the logs): a watched file triggered **two independent TMDB searches** — the background pre-warm search (`ScheduleAutoSearch`) *and* the auto-embed's own search. Auto-embed embedded + verified + renamed the file (green entry) using its search; the separate pre-warm search finished later and wrote its result (e.g. a series-level rating) onto the entry after the rename, producing a divergent second row (yellow, unverified). The earlier build-142 canonical-path guard didn't help because this wasn't a path-comparison problem — it was two searches, two writes, one file.

- **Fix:** when auto-embed is enabled, the redundant pre-warm search is no longer scheduled for watched files — auto-embed already searches and embeds them. One flow, one entry. When auto-embed is *disabled*, the pre-warm search still runs (so manually-processed files have results ready), so nothing is lost for that workflow.
- Also halves the TMDB API calls for auto-embedded files.

This is part 1 of a two-part fix. Part 2 (a following build) makes the auto-embedded TV rating fall back to the series rating when the episode itself has none, so the single verified entry is also the richer one.

---

## v1.4.0 Build 142 — Fix: duplicate Files-panel entry for one file after watch-detect + auto-embed rename

A watched file could end up with two panel rows for the same file: one green (embedded + verified) and one yellow (unverified, sometimes carrying later-fetched metadata like a rating). Root cause: when the auto-embed renames the file, the rename's `File.Move` fires a FileSystemWatcher event for the new name *before* that name is registered as known, so the watch re-detects the file and adds a second entry. The panel's dedup guards compared raw path strings, which could also miss a duplicate when the two paths differed only in form (separators, casing, normalization).

- **Fix:** the watch's duplicate guards now compare by *canonical* full path (`Path.GetFullPath`) rather than raw string, so two forms of the same file are recognised as one — closing the duplicate whether it arises from the rename-event timing window or a path-form mismatch. The at-add-time re-check (which catches a concurrent add) is included, so both the path-form and timing variants are covered. The entry-lookup after add is canonicalised the same way.
- Build 141's stale-entry sweep is kept as a backstop for the separate case of an entry whose file is genuinely gone; the two fixes don't overlap or conflict.

Why this matters beyond cosmetics: a duplicate row pointing at a file another operation is about to move/rename could cause that operation to act on a file that isn't where the entry expects. Preventing the duplicate at creation removes that risk.

---

## v1.4.0 Build 141 — Fix: stale Files-panel entry left behind after watch-detect + auto-rename

When Watch Folder detected a freshly-downloaded file, it added a panel entry for that name; the file was then auto-renamed to its correct name, leaving the old entry (pointing at a path that no longer exists) sitting in the panel next to the correctly-renamed one. Clicking Refresh cleared it — because Refresh rebuilds the panel and skips paths that don't exist — but it shouldn't require a manual step.

- **Fix:** after a successful auto-rename, the panel now drops any leftover entry whose file no longer exists on disk — a lightweight, targeted version of the reconciliation the Refresh button already does (without the full clear-and-rebuild).
- **Safe for undo:** an entry is removed only when its file is gone *and* it holds no undo state (`UndoFilePath`/`UndoMetadata` both null). The live, renamed entry — whose file exists and carries the undo snapshot — is never touched, so Undo Last Embed / Undo Batch are unaffected. The collection mutation is marshalled to the UI thread.
- **Toggle:** Settings → Behaviour → "Auto-clean stale Files-panel entries after rename" (default on). Turn it off to keep the previous behaviour or if it ever misbehaves — no rebuild needed.

Cosmetic-only: the file was always correctly renamed and embedded; this just removes the confusing leftover row automatically.

---

## v1.4.0 Build 140 — Core write path now under automated CI test

The metadata write-and-read-back tests — the app's fundamental function — were skipped in CI because a hand-built minimal MP4 stub didn't reliably initialise TagLib#'s tag layer for writing. So the one thing the app most needs to get right had no automated verification; every write regression relied on being noticed manually.

- **Added a real MP4 fixture** (`VideoMetadataEditor.Tests/Assets/tiny.mp4`) — a 1-second 128×128 libx264 clip generated with ffmpeg and verified writable in the app before committing. It's copied next to the test assembly at build time.
- **Un-skipped the 8 `MetadataWriteTests`.** Each copies a fresh clean copy of the fixture into a temp dir, so every write starts from an untagged file, then verifies: title, all core fields (IDs/rating/MPA), TV-episode fields, rating stored as invariant decimal, watched-flag round-trip, overwrite-existing-tags, temp-file cleanup, and that a successful write doesn't destroy the file.
- Removed the obsolete base64 stub and skip machinery.

No application code changed — this is a test-only build. It closes the biggest hole in the safety net: the metadata write round-trip is now verified on every push.

---

## v1.4.0 Build 139 — Fix: build 138 didn't compile

Build 138's Watch Folder fix had a compile error: in the in-flight cleanup, `_inFlight.TryRemove(path, out _)` sat inside a `ContinueWith(_ => …)` whose lambda parameter was also named `_`, so the compiler bound the `out _` discard to the `Task` parameter instead of a fresh discard (CS1503). Changed to an explicit typed discard, `out byte _`. No logic change — this only makes build 138's fix actually compile. (CI caught it; 138 was never a working build.)

---

## v1.4.0 Build 138 — Fix: Watch Folder could permanently miss files that weren't ready on first detection

Watch Folder sometimes failed to pick up files that were in (or arrived in) a watched folder, and the only reliable workaround was to re-run Add Folder — which forces a fresh full load that ignores the watch's seen-set.

- **Root cause:** the watch marked a file as "known" the instant it was *detected*, before the app had actually loaded it. FileSystemWatcher fires as soon as a file appears — often before a large copy or download has finished writing it — so the load could fail (file still locked/incomplete). But the path was already recorded as known, so the periodic poll fallback skipped it forever. The file never appeared until a manual Add Folder bypassed the seen-set.
- **Fix:** the watch's "known" set is now populated only when a file is *successfully loaded* (the app already did this on the success path). Detection uses a separate short-lived in-flight guard purely to debounce duplicate dispatches; it clears after a few seconds, so a file that wasn't ready on first detection is retried on the next poll instead of being suppressed permanently. This is the retry behaviour you'd expect: a file still being copied in is picked up once it finishes.
- Net effect: files that arrive mid-copy, over slow/network paths, or in bulk (where FileSystemWatcher can drop events) are now reliably caught by the poll fallback rather than being written off after one failed attempt.

No change to detection speed for files that are ready immediately — those still load on the instant FileSystemWatcher event.

---

## v1.4.0 Build 137 — Fix: build 136 broke anime absolute-episode detection

Build 136 taught `ParseEpisode` to recognise a bare `E##` code (for episode-only TV filenames). That had an unintended side effect caught by CI: `ParseAbsoluteEpisode` (anime absolute numbering, for the `{AbsoluteEpisode}` token) used `EpisodeRegex.IsMatch` as a gate to skip season-relative codes — and now a bare `E153` matched that gate, so absolute detection stopped recognising `E153` as absolute episode 153.

- **Fix:** `ParseAbsoluteEpisode` now bails only when a *season-bearing* code is present (`S01E05`, `1x05`, `Season 1 Episode 5`), not on the bare `E##` alternative. The two readings coexist correctly: `E153` parses as a season-style episode for renaming *and* as absolute 153 for the `{AbsoluteEpisode}` token.
- Added regression tests locking in both directions (bare `E##` resolves as absolute; season-bearing codes do not).

No behaviour change for the build-136 fix itself — episode-only filenames still rename correctly as TV. This only restores anime absolute-episode detection that 136 inadvertently disabled.

---

## v1.4.0 Build 136 — Fix: episode-only filenames (E##) renamed as movies in batch

Batch Process renamed a whole TV series using the movie pattern when the files were named with a bare episode number and no season — e.g. `Monster.E05.The.Girl.of.Heidelberg` — even though single-file Apply + Embed + Rename handled them correctly.

- **Root cause:** `FilenameParser.ParseEpisode` only recognised `S01E05`, `1x05`, and `Season 1 Episode 5`. A bare `E05` (no `S##`) matched none of them, so the parser reported "no episode code." The batch's type-mismatch guard then saw metadata that said "episode" but a filename it thought had no episode code, concluded TMDB had mis-matched a movie to a series, and forced the movie rename pattern. The log showed this as repeated `⚠ Type mismatch: '…' has no episode code`.
- **Fix (parser):** `ParseEpisode` now also recognises a standalone `E##` token (season defaults to 1), guarded by word boundaries so it only matches `E` immediately followed by digits as its own token — `WALL-E`, `E.T.`, and `Escape` do not match. Verified against a range of movie filenames to avoid false positives; the existing type-mismatch guard remains a backstop (a movie that does contain a stray `E##` token and carries movie metadata is blocked from rename with a logged reason, never mangled).
- **Fix (consistency):** "Apply to Raw Data Tab" now also syncs the file's stored `RetrievedMetadata`, so a file searched-and-applied individually before a batch run carries the correct `IsEpisode` state. Previously it updated only `PendingMetadata`/`EmbeddedMetadata`, and the batch (which renames from `RetrievedMetadata ?? PendingMetadata`) could read a stale object.
- New parser tests cover episode-only codes, the standard codes still parsing, and a set of movie filenames that must not be misread as episodes.

Note: the TV Batch button was not broken — for a selection that is already fully TV-tagged it asks whether to skip already-tagged files, and answering Yes correctly leaves nothing to do.

---

## v1.4.0 Build 135 — Faster TV tree view

The Library TV tree (Show → Season → Episode) could take a noticeable moment to display or refresh, especially with many shows.

- **Root cause:** the `TvShowTree` is a computed property rebuilt on every change that raises it — filter, sort, watched-state toggle, tab switch, and *once per background artwork load* during a scan. Each rebuild ran `Directory.EnumerateFiles` against every season folder on disk (plus a regex per filename) to detect episodes present on disk but missing from the library. That disk walk, repeated on every raise, was the stall.
- **Fix:** the per-folder physical-episode scan is now cached. Disk is read once per folder and reused across tree rebuilds; the cache is invalidated only when files may actually have changed — after a library scan, and after an entry's on-disk metadata is written/verified. The "untagged episode on disk" detection is preserved exactly; it just no longer re-walks the disk on every UI refresh.

No visible change other than speed — the tree contents, missing-episode gaps, and untagged warnings are identical.

---

## v1.4.0 Build 134 — Minimise to system tray

The app can now collapse to the notification area and keep watching folders in the background — the natural companion to Watch Folder, which previously required leaving the window open.

- **Enable it** in Settings → Behaviour → "Minimise to system tray". Off by default; nothing changes unless you turn it on.
- **Closing or minimising hides to the tray** instead of quitting, with a one-time balloon so the window vanishing doesn't look like a crash. Double-click the tray icon to restore.
- **Tray menu:** Restore window, Rescan watched folder(s), Watching on/off, Start with Windows, Exit. Deliberately limited to actions that make sense with no window on screen — Duplicates, Health Check, and Copy/Move Selected are excluded because their whole point is inspecting results or acting on a UI selection; from a hidden window they'd either run invisibly or force the window open, defeating the purpose.
- **Exit from the tray is a real shutdown** — it runs the full teardown (event unsubscribe, cancel in-flight work, dispose the FileSystemWatchers, flush the library cache). Hiding to the tray deliberately skips all of that, because disposing the watchers is exactly what must *not* happen when the point is to keep watching.
- **Start with Windows** registers a per-user (HKCU) Run entry — no admin rights, no effect on other users. Because this is a portable EXE that can be moved, the entry stores the current executable path and is re-pointed automatically on startup if it has gone stale.
- **Implementation note:** the tray uses `System.Windows.Forms.NotifyIcon` via a `FrameworkReference` rather than `UseWindowsForms=true`. Setting that property would pull `System.Windows.Forms` into the implicit usings and make `Application`/`MessageBox` ambiguous against their WPF counterparts at 21 existing call sites. The FrameworkReference exposes the assembly for explicit, aliased use only — no collisions, no third-party dependency.

---

## v1.4.0 Build 133 — Fix: update check never ran

The update badge added in build 130 never appeared, even with a newer release published. Root cause: the check was chained onto the end of the startup library-scan lambda, so it only ran if that lambda reached its last line.

- **Decoupled from the library scan.** `CheckForUpdateAsync()` now fires on its own dispatcher callback rather than after `await ScanLibraryAsync()`. The scan lambda was effectively `async void` — if the scan threw, the exception was swallowed and every statement after it (including the update check) was silently skipped. The check is an independent concern and no longer depends on library state or scan success.
- **Startup scan failures are now caught and logged** instead of silently aborting the rest of startup.
- **The check is now traceable.** It previously failed silently in *every* case, which made "no update available" indistinguishable from "never ran" — the reason this bug went unnoticed. It now logs each outcome: skipped (disabled), checking, no newer release found, update available, or check failed with the error. All still non-fatal.

If the badge still doesn't appear, the Log tab will now say why.

---

## v1.4.0 Build 132 — Silence the last compiler warning

- **CS8601 in DuplicatesViewModel suppressed.** The `Groups` setter's `Set(ref _groups, value ?? ...)` drew a "possible null reference assignment" warning. The value is coalesced to an empty collection on the line itself, and `_groups` is null-guarded both before and after the assignment, so null and empty were always handled identically — the warning was cosmetic, never a runtime risk. Build 131 tried to clear it by coalescing explicitly; that didn't satisfy the compiler, whose nullable analysis can't see through the generic `Set<T>(ref T, T)` helper. It's now suppressed at the site with a `#pragma` documenting the reasoning. No behavior change; the build log is warning-clean.

---

## v1.4.0 Build 131 — Release tooling and warning cleanup

- **GitHub Actions bumped off deprecated Node 20.** The CI workflow's `actions/checkout` and `actions/setup-dotnet` were updated to v5 (Node 24), clearing GitHub's runner deprecation notice. (Workflow-only change, outside the app.)
- **CS8601 coalescing attempt.** The `Groups` setter was changed to coalesce null to an empty collection. This is correct and harmless, but did not clear the warning — see Build 132.
- **Release publishing moved to PowerShell.** `publish-release.ps1` replaces the batch publisher: it reads `<FullVersion>` by parsing the csproj as XML (rather than fragile text-scraping), checks `gh` auth, finds the versioned EXE, refuses to overwrite an existing release, builds notes from CHANGELOG.md, and confirms before publishing. `publish-release.bat` remains as a thin double-click launcher that invokes it with `-ExecutionPolicy Bypass`. (Tooling only, outside the app.)

---

## v1.4.0 Build 130 — Update check with title-bar badge

On startup, the app checks GitHub Releases for a newer version and surfaces an unobtrusive update badge if one exists.

- **Badge under the title.** When a newer release is found, a small "⬆ Update available: vX.Y.Z.N" badge appears beneath the version text. Clicking it opens the release page in the default browser. No automatic download — this is a portable single-EXE app, so the user chooses when to fetch the new binary.
- **Quiet and non-blocking.** The check runs on a background task after launch, never blocks startup, and fails silent on any error (offline, rate-limited, private repo, no release yet). Uses GitHub's unauthenticated `/releases/latest` endpoint (60 req/hour is ample for once-per-launch).
- **Opt-out.** Settings → Behaviour → "Check GitHub for updates on startup" (default on).
- **Pure, tested core.** `UpdateCheckService` splits the network fetch from the decision logic. `ParseTag` tolerates a leading `v` and whitespace; `Evaluate` treats only strictly-newer versions as updates and falls back to the releases page if a release lacks an `html_url`. New tests cover valid/invalid tags, newer/equal/older comparisons, malformed JSON, and missing fields.
- Replaces the previous behavior where an available update only flashed a transient status-bar message (easily missed and overwritten by the next action).

The badge stays dormant until a GitHub Release newer than the running build is published — so it's safe to ship before the first release exists.

---

## v1.4.0 Build 129 — Selection-aware Batch Edit + Help docs refreshed

- **Selection-aware Batch Edit Fields.** The dialog now greys out fields that don't apply to the current selection: Show title is disabled for an all-movie selection (it's TV-only), and Year is disabled for an all-episode selection (episodes use their aired date, which isn't batch-editable). A mixed selection leaves everything available; fields shared by both types (Genre, Cast, Director, MPA, Watched) are never greyed. The greying is computed in the ViewModel from the selection's content-type composition and applied in the dialog — the underlying `BatchFieldEditService` logic is unchanged.
- **Help tab brought up to date.** The Features list and Recommended Workflow had drifted — they predated the 120–128 arc. Both now document the rename grammar (conditional `< >` blocks, zero-padding, multi-episode ranges, `{AbsoluteEpisode}`), the NFO export overhaul (tvshow.nfo, multi-episode blocks, uniqueid/ratings/actor), NFO import, batch field editing, artwork sidecar export, the remux-to-fix suggestion on container-level write failures, and the multi-folder Watch Folder.
- **Fix: long help text no longer clips.** The shared body-text style (`BaseText`) did not set `TextWrapping`, so it defaulted to `NoWrap` — long lines in the Features list and Workflow ran off the right edge and were cut off even with the window maximised. `BaseText` now wraps; every body-text line across the app reflows to fit its panel. TextBlocks that already set `TextWrapping` inline are unaffected.
- **Fix: moved files no longer re-appear in the Files panel.** Build 128 widened the Watch Folder to every loaded file's folder. A side-effect: moving files *into* a watched folder caused the watch to detect them at their new path and re-add them as "new" arrivals — so a move looked like it hadn't removed the file. Move now seeds each successful destination path into the watch's known-set before removing the source entries, so the relocated files are recognised as already-known and aren't re-added. Affects both transfer engines (they share one post-transfer handler). Copy is unaffected (it never removed files).

---

## v1.4.0 Build 128 — Watch Folder monitors every folder your files came from

The Files-panel Watch Folder previously monitored only a single folder — `Settings.LastFolderPath`, the last one loaded. Files added via "Add Files", or loaded from several different folders, came from directories the watch never saw, so a new sibling dropped into one of those folders was never auto-detected.

- **Multi-folder watch.** `ApplyWatchFolderSetting` now derives the watch set from the distinct directories of all currently loaded files, and starts one watcher per folder through the existing `MultiWatchFolderService` (the same component the Library watch already uses). A new file appearing in *any* of those folders is detected.
- **Auto-extends on add.** Because the folder set is recomputed from the loaded files each time the watch is (re)applied — which already happens after Add Files and Add Folder — pulling in files from a new folder automatically widens the watch to include it.
- **Settings preserved.** The recursive toggle and poll interval are honored across all watched folders (`MultiWatchFolderService.Start` gained a `recursive` parameter, defaulting to true so the Library watch is unaffected). If Watch Folder is enabled before any files are loaded, it falls back to the last loaded folder as before.

No change to the detection mechanism itself (dual FileSystemWatcher + poll), the new-file highlight, or the auto-embed behavior — only the breadth of what's watched.

---

## v1.4.0 Build 127 — Suggest remux when a write fails at the container level

When a single-file metadata write (Apply + Embed + Rename) fails because the MP4 container can't be updated in place — a diagnosis category of `FormatUnsupported` or `Unknown`, as opposed to read-only / locked / permission / network-volume failures that a remux can't help — the app now offers a one-click fix.

- **Prompt → remux → auto-retry.** On a container-level failure for an MP4-family file (`.mp4`, `.m4v`, `.mov`), a dialog explains that a lossless remux often fixes it and offers to do it now. If accepted, the file is remuxed to a clean container (no re-encode, no quality loss), the clean copy replaces the original only after the remux succeeds, and the metadata write is retried once on the new file.
- **Reuses existing machinery.** The remux runs through the existing `FfmpegService.RemuxAsync` and `RemuxCommitService.ReplaceOriginal`; nothing new in the remux path itself. Requires ffmpeg (Settings → External Tools) — if it isn't installed, the app logs the suggestion and falls back to the normal write-error diagnostic.
- **Scope.** Applies to the single-file Apply + Embed + Rename path only. Batch operations are intentionally unaffected, since a per-file modal prompt shouldn't interrupt a batch run.
- The decision of whether to offer a remux is a pure, unit-tested `RemuxSuggestionService.ShouldSuggestRemux` (category is container-level AND extension is remuxable). New tests cover both triggering categories on MP4-family files, all five non-container categories (no suggestion), non-remuxable extensions (`.mkv`/`.avi`/`.webm`/none), and null/empty paths.

This is the inverse of the write self-test insight: when the encoded write and verify steps pass but a specific file still won't embed, the container is the suspect, and remux is the remedy — now surfaced automatically instead of relying on the user to discover it.

---

## v1.4.0 Build 126 — Fix: Raw Data action buttons no longer clip

The bottom action-button row on the Raw Data tab (Export NFO, Export Artwork, Import NFO, Apply + Embed + Rename, Rename Only, Use Retrieved, Lock/Unlock) was laid out as a single non-wrapping horizontal row. After Export Artwork (build 124) and Import NFO (build 125) were added, the row exceeded the available width and the rightmost buttons were clipped at the window edge, with no way to reveal them even when the window was maximised. The row is now a `WrapPanel`, so buttons reflow onto a second line when width is constrained instead of being cut off. Layout-only change; no behaviour difference.

---

## v1.4.0 Build 125 — NFO import

Adds reading on-disk `.nfo` files back into VME — the inverse of the build-122 exporter, and the bidirectional counterpart that brings VME in line with tools like TinyMediaManager.

- **New "📥 Import NFO" button** (Metadata tab action row). Reads the `.nfo` sidecar next to each selected video and loads its metadata into the editable fields **for review** — nothing is written to the video until you explicitly Apply + Embed.
- **Single + batch scope.** A single selected file loads straight into the editor; a multi-file selection populates each file's pending/retrieved metadata from its own sidecar.
- **Real-world tolerance** (`NfoImportService.ParseNfo`, pure and fully unit-tested):
  - IDs: modern `<uniqueid type="imdb">` **and** legacy flat `<imdbid>` / `<tmdbid>` / `<tvdbid>` / bare `<id>` (IMDb when `tt`-prefixed, else TMDB).
  - Ratings: modern `<ratings><rating><value>` (honouring `default="true"`) **and** legacy flat `<rating>`; votes with comma separators are normalised.
  - Multiple `<genre>` and `<actor><name>` elements collapse to VME's comma lists (actors de-duplicated, order preserved).
  - Jellyfin `<played>` maps to watched; `<plot>`/`<outline>`, `<aired>`/`<premiered>`, `<mpaa>` map to their fields.
  - Recognises `<movie>`, `<episodedetails>` (first block of a multi-episode file), and `<tvshow>` roots (a series id is stored as the series TMDB id). Tag casing and surrounding whitespace are tolerated.
  - Malformed XML or an unrecognised root returns null; such files are skipped and counted, never failing the run. Existing artwork on the file is preserved (NFO carries none).

New tests cover an export→import round-trip for movies and episodes, legacy flat-ID/rating formats, the bare-`<id>` heuristic, `default="true"` rating selection, multi-genre/actor joining and de-dup, the `<tvshow>` root, Jellyfin `<played>`, and a range of malformed/edge inputs.

---

## v1.4.0 Build 124 — Artwork sidecar export

Adds the ability to write poster artwork as sidecar image files next to the media, completing the on-disk sidecar story alongside NFO export. Plex, Jellyfin, Emby, and Kodi all read these.

- **New "🖼 Export Artwork" button** (Metadata tab action row; applies to all selected files). Writes the poster image to disk next to each video.
- **Two naming styles** (Settings → 🖼 Artwork Export):
  - **Kodi** (default) — `<video basename>-poster.jpg`; TV episodes use `-thumb.jpg`. Works even when multiple videos share a folder.
  - **Plex / Jellyfin** — a single `poster.jpg` in the video's folder.
- **Artwork source chain:** in-memory metadata → embedded tag (`ReadArtworkOnly`) → TMDB download (when a TMDB ID is present). Files with no obtainable image are skipped and counted, not failed.
- The path/naming logic lives in a pure, unit-tested `ArtworkSidecarService`; the write wrapper handles no-art and overwrite cases. New tests cover Kodi/Plex naming for movies and episodes, the show-folder poster path, and the write wrapper (no-art, empty-art, write-to-path, overwrite on/off).

Poster-only for this build; fanart/backdrop is planned for a later build (the metadata model carries no backdrop URL yet).

---

## v1.4.0 Build 123 — Batch field editing

Adds the ability to apply shared field values across many files at once — the last of the originally planned enhancements.

- **New "✎ Batch Edit Fields" button** in the top toolbar (enabled when one or more files are selected). Opens a checkbox dialog; only ticked fields are changed, everything else is left untouched.
- **Editable fields:** Genre, Cast, Director, MPA rating, Year, Show title, and Watched (set watched / set unwatched). Genre and Cast support **Replace** or **Append** — Append merges the new values into the existing list, de-duplicated case-insensitively, preserving order.
- **Safety by design:** per-file-unique fields (Title, Episode title, Season, Episode, plot, aired date, IMDb/TMDB/AniList IDs, rating/votes) are intentionally not batch-editable, so a single shared value can never overwrite data that must differ per file.
- **Reuses existing infrastructure:** each file is written through the normal `WriteMetadataDetailedAsync` path, existing artwork is preserved (a field-only edit never drops the poster), and the operation is captured as an Undo Batch snapshot — "↩ Undo Batch" reverts all changed files together. Batch editing does not rename files.

The merge logic lives in a pure, unit-tested `BatchFieldEditService` (`ApplyEdits`, `CombineCsv`); the dialog and write loop are the only UI/disk-touching parts. New tests cover opt-in field selection, single-value replaces, Replace vs Append (including case-insensitive de-dup and empty-existing/empty-replace edges), the watched tri-state, the HasAnyChange gate, and null-safety.

---

## v1.4.0 Build 122 — NFO export overhaul: tvshow.nfo, multi-episode blocks, stricter Kodi format

The `.nfo` sidecar exporter (`NfoExportService`) was reworked into pure, unit-tested XML builders (`BuildMovieNfo`, `BuildEpisodeNfo`, `BuildTvShowNfo`) and brought fully in line with the Kodi/XBMC NFO format — the common standard read by Plex's NFO Agent, Jellyfin, and Emby. The `📄 Export NFO` button, batch export, and library export are unchanged in how they're invoked; the output quality improved and gained new capabilities.

- **Optional `tvshow.nfo`.** A new Settings → 📄 NFO Export toggle ("Also write tvshow.nfo into each show folder", default **off**). When exporting episode NFOs, a series-level `<tvshow>` file is written once per distinct show folder, carrying the show title, plot, genres, premiered date, and series unique IDs.
- **Multi-episode files.** A file covering a range now emits one `<episodedetails>` block per episode in that range (the Plex/Kodi multi-episode convention), instead of a single block.
- **Format correctness.** XML declaration now matches the spec (`UTF-8`, `standalone="yes"`); `<originaltitle>` added for movies; `<uniqueid>` elements now flag the first ID `default="true"` (tmdb → imdb → tvdb precedence); `<ratings>` carries `<value>` and `<votes>`; `<actor>` now includes `<order>`. Genres and cast are split into individual `<genre>`/`<actor>` elements. Apostrophes are now escaped alongside the other XML entities. Empty fields are omitted rather than written as empty tags.
- **Flavour switch.** A `NfoFlavour` (Kodi/Plex/Jellyfin) selects minor per-target differences — currently, Jellyfin also receives `<played>` alongside `<watched>`. Default is Kodi (universal).

New unit tests assert well-formed XML, field mapping, genre/actor splitting, uniqueid default flagging, the ratings block, multi-episode block counts, XML escaping (including `&`, `<`, `>`, `"`, `'`), empty-field omission, and the watched/played flavour difference. The file-writing wrapper is exercised indirectly; its path helpers are tested directly.

---

## v1.4.0 Build 121 — {AbsoluteEpisode} rename token + version-tracked EXE filename

Adds anime absolute (series-wide) episode numbering to the rename engine, plus a fix so the published EXE filename can never drift from the build number again.

- **New `{AbsoluteEpisode}` token.** Renders the absolute episode number; honours pad specs (`{AbsoluteEpisode:000}`) and disappears inside a conditional `< >` block when unknown.
- **New Settings toggle — "Auto-detect absolute episode number"** in Settings → 🎌 AniList (default **off**). When enabled, `{AbsoluteEpisode}` is populated for TV episodes by a two-stage lookup:
  1. **Filename parse (primary, offline).** `FilenameParser.ParseAbsoluteEpisode` reads an absolute number from common fansub layouts — `[Group] Show - 153 [1080p]`, `Show - 153`, `Show E153` / `Ep153` / `Episode 153`, and trailing `Show 153 [BD]`. It never fires when a season/episode code (`S01E01` / `1x01`) is present (that number is season-relative), and rejects candidates that look like a year (1900–2099) or a resolution height (480/576/720/1080/1440/2160).
  2. **AniList relations-graph fallback (best-effort, network).** Only when the filename has no absolute number *and* the file is AniList-matched. `AniListApiService.ComputeAbsoluteEpisodeAsync` walks the `PREQUEL` relation chain backwards, summing each earlier entry's `episodes` count, and adds the in-season episode. On any uncertainty — a fork in the chain, a cycle, a missing episode count, or a network error — it returns null and the token resolves empty rather than emitting a confidently-wrong number. Runs only at rename time; live preview shows the filename-derived value.
- **`MovieMetadata.AniListId`** added (with Clone support) so the AniList ID is retained from search through to rename, enabling the fallback.
- **Fixed: EXE filename drift.** The project's `<AssemblyName>` was hardcoded to `v1.4.0.119`, so newer builds still published as that filename. The version is now defined once via a `<FullVersion>` property; `AssemblyName`, `AssemblyVersion`, and `FileVersion` all derive from it.

New unit tests cover absolute-number parsing (including the year/resolution/season-code guards) and `{AbsoluteEpisode}` token rendering, padding, conditional-block behaviour, and coexistence with multi-episode ranges. The AniList relations-graph walk is a live-network path and is intentionally not unit-tested.

---

## v1.4.0 Build 120 — Rename engine: conditional blocks, zero-pad control, multi-episode ranges

The filename builder (`FileRenameService.BuildFileName`) was rewritten from a flat `.Replace()` token chain into a small tokenizer. Three grammar additions, all opt-in or backward-compatible — every existing rename pattern produces byte-for-byte identical output.

- **Conditional blocks `< … >`.** An angle-bracketed segment is emitted only when every `{token}` inside it resolves non-empty; otherwise the whole segment (including its literal separators) is dropped. Example: `{ShowTitle}< - {EpisodeTitle}>` no longer leaves a dangling ` - ` for episodes with no title. A block with multiple tokens is dropped if *any* of them is empty. Single-level (no nesting — 80/20 by design). Square brackets `[ ]` stay literal, so the common `[{ImdbId}]` / `[{MPA}]` idiom is unaffected.
- **Zero-pad format control `{Token:00}`.** `{Season:00}`, `{Episode:000}` set the digit width. With no spec, Season/Episode keep the legacy width of 2, so old patterns are unchanged. Accepts both `00`-style (zero count) and `D2`/`d3`-style specs.
- **Multi-episode range detection.** New Settings → Rename Behaviour toggle **"Detect multi-episode ranges from source filename"** (default **off**). When enabled, renaming a TV episode inspects the *original* filename for a range — `S01E01-E03`, `S01E01-03`, `1x01-1x03`, consecutive `S01E01E02E03`, or bare `01_02` — and expands `{Episode}` to a range like `01-03` (respecting the pad width). The end episode must be greater than the start or the file is treated as single-episode. The range is read from the source filename only (no container field stores it); it does **not** rename already-named files and has no effect while the toggle is off.
- **Unknown tokens** (e.g. a typo'd `{Bogus}`) are now left intact in the output rather than silently dropped, making mistakes visible.

New unit tests cover range parsing (`FilenameParser.ParseEpisodeRange`), conditional blocks, pad widths, range expansion, unknown-token passthrough, and regression of the legacy movie/TV patterns. All pure logic — fully CI-testable under `NO_WPF`.

---

## v1.4.0 Build 119 — Write Diagnostic popup: Close button fix + lock during run

- **Fixed the Close button.** The per-step UI updates in the Full Write Diagnostic window used a blocking Dispatcher.Invoke, which saturated the UI thread while the 16 checks ran and left the Close click queued until the run finished — so it appeared dead. Step updates now use non-blocking Dispatcher.BeginInvoke, and the stray IsCancel flag was removed.
- **Buttons lock until the diagnostic completes.** Both Close and Export Log… now start disabled and are enabled together only when all checks finish. The title-bar X and Alt+F4 are blocked during the run as well, so the window can't be dismissed — and background work left orphaned — while checks are still running. The header notes the window stays open until complete, then switches to a completion message.

---

## v1.4.0 Build 118 — Memory & lifetime audit

A dedicated pass over event subscriptions, timers, disposables, and caches. Two genuine issues fixed; the rest of the audited areas were confirmed sound.

- **Fixed a session-growing leak in the FILES panel.** Each file added to the Files collection got a PropertyChanged handler via an anonymous lambda capturing the MainViewModel, and that handler was never removed when files left the collection. Every folder reload therefore left the previous files reachable and uncollectable. The handler is now a named method, detached when items are removed, and a new ClearFiles() detaches every handler before clearing (ObservableCollection.Clear raises a Reset with no OldItems, so per-item detach must be explicit). All Files.Clear() call sites route through ClearFiles().
- **Reduced rename-preview timer churn.** UpdateRenamePreview created a brand-new DispatcherTimer and Tick closure on every keystroke; it now reuses a single timer and a named handler.
- **Audited and confirmed sound (no change needed):** the API metadata cache is bounded (500-entry cap + lazy expiry eviction); the TV-tree poster cache uses a ConditionalWeakTable (self-collecting); FileSystemWatcher and CancellationTokenSource instances are disposed (watch services on window close, CTSs at their call sites); the per-file subscriptions inside the Duplicates group/file view models form a self-contained object island that GC reclaims together; and the duplicate hash cache is cleared after embeds and scans. The child-VM event wiring in the window shares the application lifetime, so it is collected as a unit at shutdown.

No behaviour change.

---

## v1.4.0 Build 117 — Phase 2 milestone: all child ViewModels WPF-free (except ICommand)

- New `IClipboardService` abstraction (WPF implementation + headless null default). `MediaHealthViewModel`'s "copy report to clipboard" now goes through it instead of calling `System.Windows.Clipboard` directly.
- **Milestone:** with this change, all five extracted child ViewModels — Search, Metadata, CopyMove, Duplicates, MediaHealth — now contain no direct WPF code references. Their only remaining WPF tie is the `System.Windows.Input.ICommand` interface, which is shared across WPF, Avalonia, and MAUI. Every Dispatcher, MessageBox, file/folder picker, clipboard, bitmap, and custom-window dependency has been routed through an abstraction (`IUiDispatcher`, `IDialogService`, `IClipboardService`, the command-requery seam) or a View-supplied callback.
- `MainViewModel` remains intentionally WPF-aware as the application coordinator (per the Phase 2 plan). The next decision is whether to also abstract its ~90 remaining WPF touchpoints or keep it as the platform shell and proceed to scoping Phase 3 (the shared Core project + a second UI head).
- Added a CI test for the null clipboard service. No behaviour change.

---

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
