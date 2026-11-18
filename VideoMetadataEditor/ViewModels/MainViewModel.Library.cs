using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

// ┌─────────────────────────────────────────────────────────────────────────────┐
// │  MainViewModel.Library                                              
// │  Library — scan, cache, columns, tabs, TV picker, watch, export
// └─────────────────────────────────────────────────────────────────────────────┘

/// <remarks>
/// This is a partial class. The full MainViewModel is split across six files
/// for maintainability. All files compile to a single class — behaviour is
/// identical to a single-file implementation.
/// See: MainViewModel.cs (core), MainViewModel.FilesPanel.cs,
///      MainViewModel.CopyMove.cs, MainViewModel.Search.cs,
///      MainViewModel.Embed.cs, MainViewModel.Library.cs
/// </remarks>
public partial class MainViewModel
{
    private void InitLibraryColumns()
    {
        LibraryColumns.Clear();
        var saved = Settings.LibraryColumns;

        int idx = 0;
        foreach (var (id, header, binding, width) in _defaultColumns)
        {
            var saved_col = saved.FirstOrDefault(s => s.Id == id);
            // TV-only column IDs — hidden by default in movie/unified view
            // Must include all IDs from _tvColumns that are NOT in _movieColumns
            var tvOnlyIds = new HashSet<string>
                { "content", "show", "epcode", "eptitle", "aired", "season", "epnum" };
            LibraryColumns.Add(new Models.LibraryColumn
            {
                Id           = id,
                Header       = header,
                Binding      = binding,
                Width        = saved_col?.Width       ?? width,
                DisplayIndex = saved_col?.DisplayIndex ?? idx,
                // TV columns hidden by default; user can enable via ⚙ Columns
                IsVisible    = saved_col?.IsVisible    ?? !tvOnlyIds.Contains(id)
            });
            idx++;
        }
    }

    /// <summary>
    /// Called when a TV show result is selected — shows the picker and
    /// loads the series season list. Also auto-loads the episode that
    /// matches the file's current Season/Episode fields as a default selection.
    /// </summary>
    private async Task ShowTvPickerAsync(string seriesTmdbId, string showTitle)
    {
        // Cancel any previous in-flight picker for a different show
        _tvPickerCts?.Cancel();
        _tvPickerCts = new CancellationTokenSource();
        var cts = _tvPickerCts;

        _selectedSeriesTmdbId = seriesTmdbId;
        // Prefer the show title from EditingMetadata if it's been auto-detected
        TvPickerShowTitle = !string.IsNullOrWhiteSpace(EditingMetadata?.ShowTitle)
            ? EditingMetadata.ShowTitle
            : showTitle;
        TvSeasonNumbers.Clear();
        TvEpisodeItems.Clear();
        TvSelectedEpisode = null;
        IsTvPickerVisible = true;

        try
        {
        var (seasonCount, _, _) = await _apiService.GetTvSeriesInfoAsync(seriesTmdbId, TmdbKey, cts.Token);
        if (cts.IsCancellationRequested) return;
        if (seasonCount < 1) seasonCount = 1;

        for (int s = 1; s <= seasonCount; s++)
            TvSeasonNumbers.Add(s);

        // Default to the season from the file's embedded fields (or 1)
        int defaultSeason = EditingMetadata?.Season ?? 1;
        if (defaultSeason < 1 || defaultSeason > seasonCount) defaultSeason = 1;

        // Load episodes directly (await it) so they're ready before ShowTvPickerAsync returns.
        // Suppress the setter's fire-and-forget so we control timing precisely.
        _suppressSeasonAutoLoad = true;
        try
        {
            // Set backing field and raise PropertyChanged manually — bypasses equality guard
            // that would skip notification if defaultSeason == current value
            _tvSelectedSeason = defaultSeason;
            RaiseProperty(nameof(TvSelectedSeason));
        }
        finally { _suppressSeasonAutoLoad = false; }

        // Await episode load — picker is fully ready (episodes + default selection set)
        await LoadTvSeasonEpisodesAsync(defaultSeason);

        } // end try
        catch (OperationCanceledException) { IsTvPickerVisible = false; }
    }

    private async Task LoadTvSeasonEpisodesAsync(int season)
    {
        if (string.IsNullOrEmpty(_selectedSeriesTmdbId)) return;

        // Capture the series ID at the point of call — if it changes while loading,
        // discard results rather than showing episodes for the wrong show
        var seriesId = _selectedSeriesTmdbId;

        // Use a dedicated short-lived CTS for this load only.
        // Do NOT use _tvPickerCts — it gets cancelled by OnSelectedFileChanged
        // which kills in-flight episode loads when search results are clicked.
        using var loadCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        TvEpisodeItems.Clear();
        TvSelectedEpisode = null;

        var (episodes, err) = await _apiService.GetTvSeasonEpisodesAsync(
            seriesId, season, TmdbKey, loadCts.Token);

        // Discard if a different show was selected while loading
        if (_selectedSeriesTmdbId != seriesId) return;

        if (episodes.Count == 0 && err != null)
            Log( $"[{DateTime.Now:HH:mm:ss}] ⚠ Episodes load error S{season}: {err}");

        foreach (var ep in episodes)
            TvEpisodeItems.Add(ep);

        // Auto-select the episode matching the file's embedded episode number
        int defaultEp = EditingMetadata?.Episode ?? 1;
        TvSelectedEpisode = TvEpisodeItems.FirstOrDefault(e => e.EpisodeNumber == defaultEp)
                         ?? TvEpisodeItems.FirstOrDefault();
    }

    // ── Picker result flag — set to suppress auto-search overwriting manual load ──
    private bool _tvEpisodeManuallyLoaded;

    private async Task PickTvEpisodeAsync()
    {
        // Snapshot all picker state at entry — immune to any property-change side-effects
        var seriesId   = _selectedSeriesTmdbId;
        var season     = TvSelectedSeason;
        var epItem     = TvSelectedEpisode;
        var epNum      = epItem?.EpisodeNumber ?? 1;
        var showTitle  = TvPickerShowTitle;
        var targetFile = SelectedFile;

        if (string.IsNullOrEmpty(seriesId))
        {
            StatusText = "TV load failed — series ID missing. Please select the show again.";
            Log( $"[{DateTime.Now:HH:mm:ss}] PickTvEpisode: seriesId empty");
            return;
        }
        if (epItem == null)
        {
            StatusText = "TV load failed — no episode selected. Choose an episode first.";
            Log( $"[{DateTime.Now:HH:mm:ss}] PickTvEpisode: no episode selected");
            return;
        }

        StatusText = $"Loading {showTitle} S{season:D2}E{epNum:D2}…";
        Log( $"[{DateTime.Now:HH:mm:ss}] TV: fetching {showTitle} S{season}E{epNum}");

        try
        {
            var (meta, err) = await _apiService.GetTvEpisodeAsync(seriesId, season, epNum, TmdbKey);

            if (meta == null && !string.IsNullOrWhiteSpace(OmdbKey))
            {
                Log( $"[{DateTime.Now:HH:mm:ss}] TMDB miss ({err}) — trying OMDB");
                (meta, _) = await _apiService.GetOmdbEpisodeAsync(showTitle, season, epNum, OmdbKey);
            }

            if (meta != null)
            {
                meta.TmdbSeriesId = seriesId;
                meta.IsEpisode    = true;
                if (string.IsNullOrWhiteSpace(meta.MpaRating)) meta.MpaRating = "NR";

                // ── TWO-STEP FLOW (same as movies, confirmed working) ─────────────
                // Step 1 (this method): fetch → populate RetrievedMetadata → close picker
                //   → detail panel appears with poster + "Apply to Raw Data Tab" button
                // Step 2 (ApplyRetrievedCommand): copies RetrievedMetadata to EditingMetadata
                //   → switches to Raw Data tab — exactly like the movie flow

                // Persist on the file object so it survives re-selection
                if (targetFile != null) targetFile.RetrievedMetadata = meta;

                // Set RetrievedMetadata — this triggers the detail panel to appear
                RetrievedMetadata = meta;
                _tvEpisodeManuallyLoaded = true;

                // Close the picker — detail panel is now visible with poster art
                // and the standard "Apply to Raw Data Tab" button
                IsTvPickerVisible = false;

                StatusText = $"✓ {meta.ShowTitle} S{meta.Season:D2}E{meta.Episode:D2} loaded — click Apply to Raw Data Tab";
                Log( $"[{DateTime.Now:HH:mm:ss}] ✓ Episode ready: {meta.ShowTitle} S{meta.Season:D2}E{meta.Episode:D2}");
            }
            else
            {
                StatusText = $"S{season:D2}E{epNum:D2} not found on TMDB or OMDB.";
                Log( $"[{DateTime.Now:HH:mm:ss}] Episode not found");
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Load error: {ex.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] PickTvEpisode error: {ex}");
        }
    }


    /// <summary>
    /// Info line shown in Settings tab — how many entries are in the current cache.
    /// </summary>
    public string LibraryCacheInfo =>
        _libraryCacheService.HasCache
            ? $"{_libraryCacheService.CachedEntryCount:N0} entries cached  ·  subsequent scans skip TagLib# for unchanged files"
            : "No cache yet — run a library scan to build it";

    private void AddExtraLibraryFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Add extra library folder"
        };
        if (dlg.ShowDialog() != true) return;
        var folder = dlg.FolderName;
        // Build 168: compare normalised paths against the SAVED list too, not just the chips
        // on screen — a folder on a drive that was offline at startup has no chip, and
        // re-adding it used to save a duplicate.
        var key = NormalizeFolder(folder);
        if (key == NormalizeFolder(Settings.LibraryFolderPath))
        {
            StatusText = "That folder is already the main library folder.";
            return;
        }
        if (Settings.ExtraLibraryFolders.Any(f => NormalizeFolder(f) == key))
        {
            if (!ExtraLibraryFolders.Any(f => NormalizeFolder(f) == key) && Directory.Exists(folder))
            {
                // Saved but not shown (drive was offline at startup) — just show it again.
                ExtraLibraryFolders.Add(Settings.ExtraLibraryFolders.First(f => NormalizeFolder(f) == key));
                RaiseProperty(nameof(HasExtraFolders));
            }
            StatusText = $"{folder} is already in the library.";
            return;
        }
        ExtraLibraryFolders.Add(folder);
        Settings.ExtraLibraryFolders.Add(folder);
        RaiseProperty(nameof(HasExtraFolders));
        _ = App.ConfigService.SaveAsync();
        StatusText = $"Extra folder added: {folder}";
    }

    private static string NormalizeFolder(string? path) => Services.LibraryFolderList.Normalize(path);

    /// <summary>Posters first, then audio/subtitle tracks (build 175).</summary>
    private async Task LoadArtworkThenTracksAsync(List<Models.LibraryEntry> entries, HashSet<string> freshPaths,
                                                  string? activeTabPath, bool pruneStaleThumbs, CancellationToken ct)
    {
        await LoadLibraryArtworkAsync(entries, freshPaths, activeTabPath, pruneStaleThumbs, ct);
        if (ct.IsCancellationRequested) return;
        try { await LoadLibraryTracksAsync(entries, activeTabPath, ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.LogService.Error("Library", "Track pass failed", ex); }
    }

    /// <summary>
    /// Fills in Library artwork (grid covers and TV show posters) in priority order:
    ///   1. one episode per show / every movie in the active tab
    ///   2. the rest of the active tab
    ///   3. one episode per show elsewhere
    ///   4. everything else
    /// Each entry is served from the thumbnail cache when possible; only cache misses open
    /// the video. Entries that already carry full artwork (fresh reads) are shrunk to a
    /// thumbnail too, which also cuts memory use. UI updates are applied in batches.
    /// </summary>
    private async Task LoadLibraryArtworkAsync(
        List<Models.LibraryEntry> entries, HashSet<string> freshPaths,
        string? activeTabPath, bool pruneStaleThumbs, CancellationToken ct)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || entries.Count == 0) return;

            static string ShowKey(Models.LibraryEntry e) =>
                string.IsNullOrWhiteSpace(e.ShowTitle) ? e.Title : e.ShowTitle;

            var firstEpisodes = new HashSet<Models.LibraryEntry>(
                entries.Where(e => e.IsEpisode)
                       .GroupBy(ShowKey)
                       .Select(g => g.OrderBy(e => e.Season ?? 0).ThenBy(e => e.Episode ?? 0).First()));

            bool InActiveTab(Models.LibraryEntry e) =>
                !string.IsNullOrEmpty(activeTabPath) &&
                e.FilePath.StartsWith(activeTabPath, StringComparison.OrdinalIgnoreCase);

            int Priority(Models.LibraryEntry e)
            {
                bool lead = !e.IsEpisode || firstEpisodes.Contains(e);
                if (InActiveTab(e)) return lead ? 0 : 1;
                return firstEpisodes.Contains(e) ? 2 : 3;
            }

            var ordered = entries.OrderBy(Priority).ToList();

            await Task.Run(async () =>
            {
                var pending = new List<(Models.LibraryEntry entry, byte[]? art)>();

                async Task Flush()
                {
                    if (pending.Count == 0) return;
                    var batch = pending.ToList();
                    pending.Clear();
                    await dispatcher.InvokeAsync(() =>
                    {
                        foreach (var (entry, art) in batch)
                        {
                            if (art is { Length: > 0 }) entry.CoverArt = art;
                            entry.ArtworkChecked = true;   // build 172: answer known (art or none)
                            OnEpisodeArtLoaded(entry);
                        }
                        RaiseLibraryAttentionCounts();
                    }, System.Windows.Threading.DispatcherPriority.Background, ct);
                }

                // Pass 1 — no video opened: cached thumbs, or shrink art we already hold.
                var needRead = new List<Models.LibraryEntry>();
                foreach (var e in ordered)
                {
                    if (ct.IsCancellationRequested) return;
                    byte[]? thumb;
                    if (e.CoverArt is { Length: > 0 } full)
                    {
                        // Already showing art. Only a fresh read carries full-size art that
                        // still needs shrinking and caching; anything else is done.
                        if (!freshPaths.Contains(e.FilePath))
                        {
                            if (!e.ArtworkChecked) pending.Add((e, null));   // has art — mark checked
                            if (pending.Count >= 50) await Flush();
                            continue;
                        }
                        thumb = Services.ArtworkThumbCache.Store(e.FilePath, full);
                    }
                    else if (Services.ArtworkThumbCache.TryGet(e.FilePath, out var cached))
                        thumb = cached;
                    else { needRead.Add(e); continue; }

                    // Add even when there's no artwork, so the row is marked as checked.
                    pending.Add((e, thumb is { Length: > 0 } && !ReferenceEquals(thumb, e.CoverArt) ? thumb : null));
                    if (pending.Count >= 50) await Flush();
                }
                await Flush();

                // Pass 2 — cache misses: open the video once, then cache the thumbnail.
                foreach (var e in needRead)
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        var art = _metadataService.ReadArtworkOnly(e.FilePath);
                        var thumb = Services.ArtworkThumbCache.Store(e.FilePath, art);
                        pending.Add((e, thumb));   // null = checked, no artwork
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Artwork] {ex.GetType().Name}: {ex.Message}"); }

                    // Small batches here so posters pop in steadily while the disk works.
                    if (pending.Count >= 10) await Flush();
                }
                await Flush();

                // Build 172: posters are all known now — re-apply the Show filter once.
                await dispatcher.InvokeAsync(RefreshLibraryAttention);

                // Pass 3 (build 171) — remove thumbnails no current file uses: left behind
                // by re-embeds (new modified time = new key), renames, moves and deletions.
                if (pruneStaleThumbs && !ct.IsCancellationRequested)
                {
                    var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in entries)
                    {
                        if (ct.IsCancellationRequested) return;
                        var k = Services.ThumbCacheKeys.KeyForFile(e.FilePath);
                        if (k != null) liveKeys.Add(k);
                    }
                    // Safety: an empty key set would mean "delete everything" — never do that.
                    if (liveKeys.Count == 0) return;

                    var (removed, bytes) = Services.ArtworkThumbCache.Prune(liveKeys);
                    if (removed > 0)
                        await dispatcher.InvokeAsync(() =>
                            Log($"[{DateTime.Now:HH:mm:ss}] 🧹 Removed {removed} outdated poster thumbnail(s) " +
                                $"({bytes / 1024.0:N0} KB) — from re-embedded, renamed or deleted files."));
                }
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.BackgroundTask.OnError?.Invoke("Bulk artwork load", ex); }
    }

    private void ClearLibraryCache()
    {
        // Build 176: every library folder's cache (the primary and the extra folders).
        _libraryCacheService.ClearEverything();
        foreach (var e in LibraryEntries) e.Tracks = null;   // re-read tracks on the next scan too

        var thumbs = Services.ArtworkThumbCache.Clear();   // build 170: posters rebuild too
        RaiseProperty(nameof(LibraryCacheInfo));
        StatusText = $"Library cache cleared ({thumbs} cached poster thumbnail(s) too) — next scan will rebuild it from disk.";
        Log( $"[{DateTime.Now:HH:mm:ss}] 🗑 Library cache cleared.");
    }

    /// <summary>
    /// Persists the current LibraryColumns state.
    /// When activeTabIsTv=true saves to Settings.TvColumns;
    /// when false saves to Settings.MovieColumns.
    /// Always saves to Settings.LibraryColumns (unified fallback).
    /// </summary>
    public void SaveLibraryColumnSettings()
    {
        // Save ALL columns to the unified list (general fallback)
        Settings.LibraryColumns = LibraryColumns.Select(c => new Models.LibraryColumnSettings
        {
            Id = c.Id, IsVisible = c.IsVisible, DisplayIndex = c.DisplayIndex, Width = c.Width
        }).ToList();

        // Save ONLY the columns relevant to this tab type into the type-specific bucket.
        // Storing all 19 columns (mixed movie+TV) causes restoration issues — each bucket
        // should contain only the columns that belong to that view.
        var relevantIds = (ActiveTabIsTv ? _tvColumns : _movieColumns)
            .Select(d => d.Id)
            .ToHashSet();

        // Get the actual DataGrid display order from the DataGrid column objects
        // via SyncLibraryColumnVisibility — use DisplayIndex as set by the DataGrid
        var typeSnapshot = LibraryColumns
            .Where(c => relevantIds.Contains(c.Id))
            .Select(c => new Models.LibraryColumnSettings
            {
                Id           = c.Id,
                IsVisible    = c.IsVisible,
                DisplayIndex = c.DisplayIndex,
                Width        = c.Width
            })
            .ToList();

        if (ActiveTabIsTv)
            Settings.TvColumns = typeSnapshot;
        else
            Settings.MovieColumns = typeSnapshot;

        _ = App.ConfigService.SaveAsync();
    }

    /// <summary>
    /// Saves layout for the current tab type and shows a status bar confirmation.
    /// Called by the Save Column Layout button in the Library tab.
    /// </summary>
    public void SaveLibraryColumnLayoutWithConfirmation()
    {
        SaveLibraryColumnSettings();

        var tabType    = ActiveTabIsTv ? "TV Shows" : "Movies";
        var visCount   = LibraryColumns.Count(c => c.IsVisible);
        var totalCount = LibraryColumns.Count;

        // Explicit synchronous save so status confirms actual disk write
        App.ConfigService.Save();

        StatusText = $"✓ {tabType} column layout saved ({visCount}/{totalCount} columns visible) — restored on next launch.";
        Log(
            $"[{DateTime.Now:HH:mm:ss}] 💾 {tabType} column layout saved " +
            $"({visCount} visible, {totalCount - visCount} hidden).");
    }

    public string LibraryFolderDisplay => Settings.LibraryFolderPath ?? string.Empty;

    public bool HasExtraFolders => ExtraLibraryFolders.Count > 0;

    public string AllLibraryFoldersTooltip
    {
        get
        {
            var folders = new List<string>();
            if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath))
                folders.Add($"Primary: {Settings.LibraryFolderPath}");
            foreach (var f in ExtraLibraryFolders)
                folders.Add($"Extra:   {f}");
            return folders.Count == 0
                ? "No library folder set"
                : string.Join("\n", folders);
        }
    }

    public void BrowseLibraryFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder to scan for movie library",
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath) &&
            Directory.Exists(Settings.LibraryFolderPath))
            dlg.InitialDirectory = Settings.LibraryFolderPath;

        if (dlg.ShowDialog() != true) return;

        Settings.LibraryFolderPath = dlg.FolderName;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(CanScanLibrary));
        RaiseProperty(nameof(LibraryFolderDisplay));

        // Restart library watch on new folder if enabled
        if (Settings.LibraryWatchEnabled)
            ApplyLibraryWatchSetting();
    }

    private CancellationTokenSource? _libraryCts;

    private async Task ScanLibraryAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.LibraryFolderPath)) return;

        _libraryCts?.Cancel();
        _libraryCts = new CancellationTokenSource();
        var ct = _libraryCts.Token;

        IsLibraryScanning = true;

        // Build 175: timed in the activity log (start, cache hits, finish).
        using var scanOp = Services.LogService.Begin("Library", "Library scan",
            "Folders:" + Environment.NewLine + string.Join(Environment.NewLine,
                new[] { Settings.LibraryFolderPath }.Concat(ExtraLibraryFolders)
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => "  " + f + (Directory.Exists(f) ? "" : "   (not reachable)"))));

        // Stop the library watch service for the duration of the scan.
        // Without this, the watch service fires FileDetected for every file
        // in the folder (because LibraryEntries.Clear() makes them all look
        // "new"), adding all 2483 files via BuildSingleEntryAsync while the
        // scan simultaneously adds the same 2483 — doubling the result.
        bool watchWasActive = _libraryWatchService.IsActive;
        if (watchWasActive) _libraryWatchService.Stop();

        // ── In-place update instead of clear-and-rebuild ──────────────────────
        // Previously: LibraryEntries.Clear() + scan + re-add all entries.
        // Problem: Clear() destroys all in-memory updates from SyncLibraryEntry
        //          (metadata embedded since last scan), forcing a cache lookup that
        //          could return stale data or an old disk read.
        // Solution: update LibraryEntries in place —
        //   • Files already in the collection that haven't changed on disk → keep as-is
        //     (preserves SyncLibraryEntry updates without any cache involvement)
        //   • Files whose mtime changed → re-read from TagLib# and update the entry
        //   • New files not yet in the collection → add
        //   • Files removed from disk → remove from collection
        // This means embedded metadata is ALWAYS reflected — the in-memory LibraryEntry
        // is the source of truth for the current session.

        LibraryScanProgress = 0;
        LibraryScanStatus   = "Starting scan…";

        var progress = new System.Progress<(int done, int total, string current)>(p =>
        {
            LibraryScanProgress = p.total > 0 ? (int)(p.done * 100.0 / p.total) : 0;
            LibraryScanStatus   = p.total > 0
                ? $"Scanning {p.done}/{p.total}  ·  {p.current}"
                : p.current;
        });

        try
        {
            // Collect all folders: primary + extras
            var allFolders = new List<string> { Settings.LibraryFolderPath };
            allFolders.AddRange(ExtraLibraryFolders.Where(f =>
                !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));

            var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanned = new List<Models.LibraryEntry>();
            int hits = 0, misses = 0;   // build 176: totals across ALL folders (was: last folder only)
            foreach (var scanFolder in allFolders)
            {
                var folderEntries = await _libraryScanService.ScanFolderAsync(
                    scanFolder, Settings.LibraryRecursive, progress, ct);
                hits   += _libraryScanService.LastScanCacheHits;
                misses += _libraryScanService.LastScanCacheMisses;
                Services.LogService.Debug("Library",
                    $"Scanned {scanFolder}: {folderEntries.Count} file(s) — {_libraryScanService.LastScanCacheHits} from cache, " +
                    $"{_libraryScanService.LastScanCacheMisses} read from disk");
                foreach (var e in folderEntries)
                    if (seen.Add(e.FilePath)) scanned.Add(e);
            }

            // ── In-place merge: update LibraryEntries without clearing ─────────
            // Build a lookup of what we have in memory right now.
            var existing = LibraryEntries
                .ToDictionary(e => e.FilePath, e => e, StringComparer.OrdinalIgnoreCase);

            // Build a lookup of what the scan found.
            var scannedMap = scanned
                .ToDictionary(e => e.FilePath, e => e, StringComparer.OrdinalIgnoreCase);

            // 1. Update or add entries.
            //    For files already in LibraryEntries: the in-memory entry already has
            //    any SyncLibraryEntry updates — KEEP it, just update technical fields
            //    (size, codec, duration) from the scan result so they stay fresh.
            //    For new files: add the scan result directly.
            var toAdd = new List<Models.LibraryEntry>();
            foreach (var se in scanned)
            {
                if (existing.TryGetValue(se.FilePath, out var current))
                {
                    // File already in collection.
                    // Always update technical fields — these don't come from embed.
                    current.Duration      = se.Duration;
                    current.VideoCodec    = se.VideoCodec;
                    current.AudioCodec    = se.AudioCodec;
                    current.Resolution    = se.Resolution;
                    current.Format        = se.Format;
                    current.FileSizeBytes = se.FileSizeBytes;
                    current.Subtitles     = se.Subtitles;
                    // Build 175: a fresh read means the file changed — read its tracks again.
                    // A cache hit carries the tracks read earlier (if any).
                    if (se.IsFreshRead)          current.Tracks = null;
                    else if (se.Tracks != null)  current.Tracks = se.Tracks;

                    // Only overwrite metadata if the scan did a FRESH TagLib# read
                    // (cache miss or MarkAsEmbedded forced read). Cache hits may be
                    // stale — the in-memory entry from SyncLibraryEntry is more current.
                    if (se.IsFreshRead)
                    {
                        current.Title         = se.Title;
                        current.Year          = se.Year;
                        current.Genre         = se.Genre;
                        current.Director      = se.Director;
                        current.Cast          = se.Cast;
                        current.Description   = se.Description;
                        current.ImdbId        = se.ImdbId;
                        current.TmdbId        = se.TmdbId;
                        current.ImdbRating    = se.ImdbRating;
                        current.MpaRating     = se.MpaRating;
                        current.IsEpisode     = se.IsEpisode;
                        current.ShowTitle     = se.ShowTitle;
                        current.Season        = se.Season;
                        current.Episode       = se.Episode;
                        current.EpisodeTitle  = se.EpisodeTitle;
                        current.AiredDate     = se.AiredDate;
                        current.TmdbSeriesId  = se.TmdbSeriesId;
                        current.IsWatched     = se.IsWatched || current.IsWatched;
                        if (se.CoverArt is { Length: > 0 })
                            current.CoverArt  = se.CoverArt;
                    }
                }
                else
                {
                    toAdd.Add(se);
                }
            }

            // 2. Remove entries for files no longer on disk.
            var toRemove = LibraryEntries
                .Where(e => !scannedMap.ContainsKey(e.FilePath))
                .ToList();
            foreach (var e in toRemove) LibraryEntries.Remove(e);

            // 3. Add new entries.
            foreach (var e in toAdd) LibraryEntries.Add(e);

            // ── Cover art — background, UI-safe (build 170) ─────────────────────────
            // Thumbnails now come from a persistent cache (thumbs\ next to the EXE), so
            // a cached library no longer re-opens every video for its cover. Order: the
            // tab you're looking at first, and one episode per TV show early so show
            // posters appear quickly. See LoadLibraryArtworkAsync.
            // Target the objects actually in LibraryEntries — on a rescan, existing rows are
            // updated from the scan results rather than replaced, so artwork set on a scan
            // result never reached the row on screen.
            var activeTabPath = Settings.LibraryTabbedMode ? _selectedLibraryTab?.FolderPath : null;
            var live = LibraryEntries.ToDictionary(e => e.FilePath, e => e, StringComparer.OrdinalIgnoreCase);
            var artTargets = scanned
                .Select(se => live.TryGetValue(se.FilePath, out var cur) ? cur : se)
                .ToList();
            var freshPaths = new HashSet<string>(
                scanned.Where(se => se.IsFreshRead).Select(se => se.FilePath), StringComparer.OrdinalIgnoreCase);
            // Build 171: tidy the thumbnail cache afterwards, but only when the scan covered
            // the whole library. If a library drive is offline, its files are missing from
            // this scan and their thumbnails must not be treated as stale.
            bool wholeLibraryScanned =
                Directory.Exists(Settings.LibraryFolderPath) &&
                Settings.ExtraLibraryFolders.All(f => string.IsNullOrWhiteSpace(f) || Directory.Exists(f));
            // Build 175: then the audio/subtitle tracks — after the posters, so the two
            // passes don't compete for the same disk.
            _ = LoadArtworkThenTracksAsync(artTargets, freshPaths, activeTabPath, wholeLibraryScanned, ct);

            // Build status line — include cache stats so user knows how many were fast reads
            LibraryScanStatus = hits > 0
                ? $"✓  {LibraryEntries.Count} file(s) — {hits} from cache, {misses} read from disk."
                : $"✓  {LibraryEntries.Count} file(s) scanned.";
            RaiseProperty(nameof(LibraryCacheInfo));
            LibraryScanProgress = 100;
            // Folder contents were just re-read — drop the TvShowTree folder-scan cache
            // so the tree reflects any files added/removed since the last scan.
            InvalidatePhysicalEpisodeCache();
            // Rebuild per-folder tabs for tabbed display
            RebuildLibraryTabs();

            // Prune stale cache entries — files renamed/moved/deleted outside VME
            // leave ghost entries that inflate the cache and cause stale library rows.
            // SaveAsync evicts any path that's no longer in the live scan results.
            var livePaths = LibraryEntries.Select(e => e.FilePath).ToList();
            _ = _libraryCacheService.SaveAsync(livePaths);

            // Check for incomplete operations in the library folder
            CheckForRecovery(new[] { Settings.LibraryFolderPath });

            // Re-seed library watch with all scanned paths so it only fires for new arrivals,
            // then restart it (was stopped at top of scan to prevent double-add).
            if (Settings.LibraryWatchEnabled && watchWasActive)
            {
                var existingPaths = LibraryEntries.Select(e => e.FilePath).ToList();
                var allWatchFolders = new List<string>();
                if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath))
                    allWatchFolders.Add(Settings.LibraryFolderPath);
                allWatchFolders.AddRange(ExtraLibraryFolders.Where(f =>
                    !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));
                _libraryWatchService.Start(allWatchFolders, existingPaths,
                    Settings.LibraryWatchPollMinutes);
            }
        }
        catch (OperationCanceledException)
        {
            LibraryScanStatus = "Scan cancelled.";
            scanOp.Cancel();
        }
        catch (Exception ex)
        {
            LibraryScanStatus = $"Scan error: {ex.Message}";
            scanOp.Fail(ex);
        }
        finally
        {
            IsLibraryScanning = false;
            scanOp.Outcome ??= LibraryScanStatus.TrimStart('✓', ' ');

            // Always restart watch if it was running before the scan — even on cancel/error.
            // Leaving it stopped means new files would be missed indefinitely.
            if (Settings.LibraryWatchEnabled && watchWasActive && !_libraryWatchService.IsActive)
            {
                var existingPaths = LibraryEntries.Select(e => e.FilePath).ToList();
                var allWatchFolders2 = new List<string>();
                if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath))
                    allWatchFolders2.Add(Settings.LibraryFolderPath);
                allWatchFolders2.AddRange(ExtraLibraryFolders.Where(f =>
                    !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));
                _libraryWatchService.Start(allWatchFolders2, existingPaths,
                    Settings.LibraryWatchPollMinutes);
            }
        }
    }

    /// <summary>
    /// Rebuilds LibraryTabs from the current LibraryEntries — one tab per folder.
    /// Called after every scan. Thread-safe: entries are grouped by their folder paths.
    /// </summary>
    public void RebuildLibraryTabs()
    {
        // Snapshot inputs before going async — these are UI-thread collections
        var primaryFolder = Settings.LibraryFolderPath ?? string.Empty;
        var extraFolders  = ExtraLibraryFolders.ToList();
        var entriesSnap   = LibraryEntries.ToList();

        // Build tabs on a background thread (avoids blocking UI during large libraries)
        Services.BackgroundTask.Run(() =>
        {
            var allFolders = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryFolder))
                allFolders.Add(primaryFolder);
            allFolders.AddRange(extraFolders);
            allFolders = allFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var newTabs = new List<Models.LibraryTab>();
            foreach (var folder in allFolders)
            {
                var tab = new Models.LibraryTab(folder);
                foreach (var e in entriesSnap.Where(e =>
                    e.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                    tab.Entries.Add(e);
                // Restore user-set TV/Movie override for this folder
                if (Settings.TabTypeOverrides.TryGetValue(folder, out var tvOverride))
                    tab.IsTvOverride = tvOverride;

                if (tab.Entries.Count > 0 || allFolders.Count == 1)
                    newTabs.Add(tab);
            }

            // Apply to UI collections on the dispatcher thread
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LibraryTabs.Clear();
                foreach (var t in newTabs) LibraryTabs.Add(t);
                SelectedLibraryTab = LibraryTabs.FirstOrDefault();
                RaiseProperty(nameof(HasMultipleTabs));
                // Force the view to show entries for the newly selected tab
                LibraryView?.Refresh();
                RaiseProperty(nameof(LibraryFilterCount));
                // Also refresh TvShowTree in case tree view is active
                RaiseProperty(nameof(TvShowTree));
            });
        }, "Library tab rebuild");
    }

    private async Task ExportLibraryCsvAsync()
    {
        if (!LibraryEntries.Any()) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export Library as CSV",
            Filter     = "CSV Files|*.csv",
            FileName   = $"Movie Library {DateTime.Now:yyyy-MM-dd}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var visibleCols = LibraryColumns
            .Where(c => c.IsVisible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        await Task.Run(() =>
        {
            using var sw = new System.IO.StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            // Header
            sw.WriteLine(string.Join(",", visibleCols.Select(c => $"\"{c.Header}\"")));
            // Rows
            foreach (var entry in LibraryEntries)
            {
                var values = visibleCols.Select(c =>
                {
                    var prop = typeof(Models.LibraryEntry).GetProperty(c.Binding);
                    var val  = prop?.GetValue(entry)?.ToString() ?? string.Empty;
                    return $"\"{val.Replace("\"", "\"\"")}\"";
                });
                sw.WriteLine(string.Join(",", values));
            }
        });

        StatusText = $"Library exported: {Path.GetFileName(dlg.FileName)}";
        Log( $"[{DateTime.Now:HH:mm:ss}] Library CSV exported → {dlg.FileName}");
    }

    private async Task ExportLibraryXlsxAsync()
    {
        if (!LibraryEntries.Any()) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title    = "Export Library as Excel",
            Filter   = "Excel Files|*.xlsx",
            FileName = $"Movie Library {DateTime.Now:yyyy-MM-dd}.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        var ct = BeginOperation();
        IsBusy    = true;
        StatusText = "Exporting XLSX…";
        try
        {
            var visibleCols = LibraryColumns
                .Where(c => c.IsVisible)
                .OrderBy(c => c.DisplayIndex)
                .ToList();

            var entries = LibraryEntries.ToList();
            await Task.Run(() =>
                Services.XlsxWriterService.Write(dlg.FileName, entries, visibleCols), ct);

            StatusText = $"Exported {entries.Count} row(s) → {System.IO.Path.GetFileName(dlg.FileName)}";
            Log( $"[{DateTime.Now:HH:mm:ss}] XLSX export → {dlg.FileName}");
        }
        catch (Exception ex)
        {
            StatusText = $"XLSX export error: {ex.Message}";
        }
        finally { EndOperation(resetProgress: false); }
    }

    private async Task LibraryRowDoubleClickAsync(Models.LibraryEntry? entry)
    {
        if (entry == null) return;

        // Ask user what to do
        var result = System.Windows.MessageBox.Show(
            $"Load \"{entry.Title}\" into the Files panel?\n\n" +
            "Yes = load file and run auto-search\n" +
            "No  = cancel",
            "Load File from Library",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        // Add to Files list if not already there — insert separator if mixing sources
        bool alreadyIn = Files.Any(f => !f.IsSeparator &&
            f.FilePath.Equals(entry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (!alreadyIn)
        {
            if (Files.Any(f => !f.IsSeparator) && !(Files.LastOrDefault()?.IsSeparator == true))
                Files.Add(new VideoFile { FilePath = string.Empty, IsSeparator = true, FileSizeBytes = 0 });
            TryAddFile(entry.FilePath);
        }

        // Select it
        var vf = Files.FirstOrDefault(f =>
            f.FilePath.Equals(entry.FilePath, StringComparison.OrdinalIgnoreCase));
        if (vf != null)
        {
            SelectedFile = vf;
            if (!string.IsNullOrWhiteSpace(vf.EmbeddedMetadata.Title))
                SearchQuery = vf.EmbeddedMetadata.Title;
        }

        // Switch to RAW DATA tab — set index AND fire event so code-behind also forces it
        SelectedTabIndex = 0;
        RequestSwitchToRawData?.Invoke(this, EventArgs.Empty);

        StatusText = $"Loaded from Library: {entry.Title}";
        Log( $"[{DateTime.Now:HH:mm:ss}] Library → Files panel: {entry.FileName}");
        // Recovery check removed from load-from-library — CheckForRecovery would find
        // .vme_tmp_ files that are actively being written, triggering false positives.
        // Recovery only runs at startup (TriggerStartupRecovery) and on explicit scans.
    }

    public event EventHandler? RequestSwitchToRawData;

    /// <summary>
    /// Loads all selected Library entries into the Files panel without prompting.
    /// Called from the "Load Selected to Files Panel" button.
    /// </summary>
    private void LoadLibrarySelection(System.Collections.IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        // If there are already non-separator files in the panel, insert a separator row
        // so the user can visually distinguish files from different sources
        bool needSeparator = Files.Any(f => !f.IsSeparator);

        int added = 0;
        VideoFile? firstAdded = null;

        foreach (var item in selectedItems.OfType<Models.LibraryEntry>())
        {
            if (Files.Any(f => !f.IsSeparator &&
                f.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Insert separator before the first new file from this batch
            if (needSeparator && added == 0)
            {
                Files.Add(new VideoFile
                {
                    FilePath      = string.Empty,
                    IsSeparator   = true,
                    FileSizeBytes = 0
                });
            }

            TryAddFile(item.FilePath);
            if (firstAdded == null)
                firstAdded = Files.LastOrDefault(f => !f.IsSeparator &&
                    f.FilePath.Equals(item.FilePath, StringComparison.OrdinalIgnoreCase));
            added++;
        }

        if (added == 0)
        {
            StatusText = "All selected files are already in the Files panel.";
            return;
        }

        // Select the first newly added file and switch to RAW DATA
        if (firstAdded != null) SelectedFile = firstAdded;
        SelectedTabIndex = 0;
        RequestSwitchToRawData?.Invoke(this, EventArgs.Empty);

        StatusText = $"Loaded {added} file(s) from Library into Files panel.";
        Log( $"[{DateTime.Now:HH:mm:ss}] Library → Files panel: {added} file(s) loaded.");

        // Register the newly-loaded Files panel paths with the FILES watch service
        // so the poll timer doesn't treat them as new arrivals
        if (_watchFolderService.IsActive)
        {
            foreach (var f in Files.Where(x => !x.IsSeparator))
                _watchFolderService.AddKnownPath(f.FilePath);
        }

        // Check for incomplete operations in the folders of the loaded files
        var foldersToCheck = selectedItems.OfType<Models.LibraryEntry>()
            .Select(e => Path.GetDirectoryName(e.FilePath) ?? string.Empty)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (foldersToCheck.Count > 0)
            CheckForRecovery(foldersToCheck);
    }

    /// <summary>
    /// Scans all known video folders for .vme_tmp_* and .vme_bak_* files left by crashed sessions
    /// and deletes them. Runs once on startup on a background thread.
    /// </summary>
    private async Task CleanOrphanedTempFilesAsync()
    {
        try
        {
            var policy = Settings.OrphanTempFilePolicy;

            var foldersToCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Last-used Files panel folder
            var lastFolder = Settings.LastFolderPath;
            if (!string.IsNullOrWhiteSpace(lastFolder) && Directory.Exists(lastFolder))
                foldersToCheck.Add(lastFolder);

            // Library folder — also a write target, must be checked at startup
            var libFolder = Settings.LibraryFolderPath;
            if (!string.IsNullOrWhiteSpace(libFolder) && Directory.Exists(libFolder))
                foldersToCheck.Add(libFolder);

            // Extra library folders — snapshot on UI thread before background use
            List<string> extraSnapShot;
            try { extraSnapShot = ExtraLibraryFolders.ToList(); }
            catch { extraSnapShot = new List<string>(); }
            foreach (var extra in extraSnapShot.Where(Directory.Exists))
                foldersToCheck.Add(extra);

            if (foldersToCheck.Count == 0) return;

            // Scan for orphans
            var candidates = await Task.Run(() =>
                Services.RecoveryService.Scan(foldersToCheck));

            if (candidates.Count == 0) return;

            Log(
                $"[{DateTime.Now:HH:mm:ss}] Startup: {candidates.Count} orphan temp file(s) found.");

            if (policy == "AutoDelete")
            {
                // Auto-delete silently on startup
                int deleted = 0;
                foreach (var c in candidates)
                {
                    var (ok, _) = Services.RecoveryService.Delete(c);
                    if (ok) deleted++;
                }
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] Startup: auto-deleted {deleted} orphan file(s).");
            }
            else if (policy == "Ask")
            {
                // Defer to RecoveryCandidatesFound — will open the dialog
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                    await dispatcher.InvokeAsync(() =>
                        RecoveryCandidatesFound?.Invoke(this, candidates));
            }
            // "Ignore" — do nothing

            // ── Remux candidate orphan sweep ──────────────────────────────────
            // Find un-committed *.remux.* files left by a crash/close between remux
            // and Replace/Restore. Offer to clean them up.
            await SweepRemuxOrphansAsync(foldersToCheck);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{System.IO.Path.GetFileNameWithoutExtension(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Name ?? "VM")}] {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Finds un-committed *.remux.* candidates across known folders and, if any
    /// exist, asks the user whether to discard them (keeping originals) — the safety
    /// net for the non-destructive remux workflow.
    /// </summary>
    private async Task SweepRemuxOrphansAsync(HashSet<string> folders)
    {
        try
        {
            var recursive = Settings.AddFolderRecursive;
            var orphans = await Task.Run(() =>
            {
                var found = new List<string>();
                foreach (var f in folders)
                {
                    found.AddRange(Services.RemuxCommitService.FindOrphans(f, recursive));
                    // Also sweep interrupted faststart temp files (*.faststart.tmp.*)
                    found.AddRange(Services.FfmpegService.FindFaststartOrphans(f, recursive));
                }
                return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });

            if (orphans.Count == 0) return;

            // Build 168: a faststart temp VIDEO whose original no longer exists is not a
            // leftover — it's the only copy of that video (an older faststart fix deleted
            // the original and then failed to rename the temp into place). Never offer to
            // delete those; offer to restore them instead.
            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".m4v", ".mov", ".mkv" };
            var stranded = orphans
                .Where(p => videoExts.Contains(System.IO.Path.GetExtension(p)))
                .Select(p => (temp: p, original: Services.WorkingFileFilter.FaststartOriginalFor(p)))
                .Where(x => x.original != null && !System.IO.File.Exists(x.original))
                .Select(x => (x.temp, original: x.original!))
                .ToList();
            if (stranded.Count > 0)
            {
                var strandedSet = new HashSet<string>(stranded.Select(x => x.temp), StringComparer.OrdinalIgnoreCase);
                orphans = orphans.Where(p => !strandedSet.Contains(p)).ToList();
                await OfferRestoreStrandedAsync(stranded);
            }
            if (orphans.Count == 0) return;

            // Separate the two kinds for clearer messaging
            var faststartTemps = orphans
                .Where(p => System.IO.Path.GetFileNameWithoutExtension(p)
                    .Contains(Services.FfmpegService.FaststartTempMarker, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var remuxCandidates = orphans.Except(faststartTemps, StringComparer.OrdinalIgnoreCase).ToList();

            Log(
                $"[{DateTime.Now:HH:mm:ss}] Startup: {remuxCandidates.Count} remux + " +
                $"{faststartTemps.Count} faststart-temp orphan(s) found.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            await dispatcher.InvokeAsync(() =>
            {
                var preview = string.Join("\n",
                    orphans.Take(8).Select(p => "  • " + System.IO.Path.GetFileName(p)));
                if (orphans.Count > 8) preview += $"\n  …and {orphans.Count - 8} more";

                // Build a description tailored to what was found
                var parts = new List<string>();
                if (faststartTemps.Count > 0)
                    parts.Add($"{faststartTemps.Count} faststart temp file(s) (*.faststart.tmp.*) — " +
                              "leftovers from an interrupted Fix Faststart, or .nfo files written for them. " +
                              "Each one's original video is still present, so these are safe to delete " +
                              "(they go to the Recycle Bin)");
                if (remuxCandidates.Count > 0)
                    parts.Add($"{remuxCandidates.Count} un-committed remux copy(ies) (*.remux.*) — " +
                              "clean copies never committed via Replace/Restore. Originals are intact");

                var res = System.Windows.MessageBox.Show(
                    "Found leftover files from a previous session:\n\n" +
                    preview + "\n\n" +
                    string.Join("\n\n", parts) + "\n\n" +
                    "Clean these up now? (Choose No to keep them for review.)",
                    "Leftover Files From Interrupted Operation",
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

                if (res == System.Windows.MessageBoxResult.Yes)
                {
                    int deleted = 0;
                    foreach (var p in orphans)
                    {
                        try
                        {
                            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p,
                                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            deleted++;
                        }
                        catch { /* skip locked */ }
                    }
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}] Startup: discarded {deleted} leftover file(s).");
                }
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RemuxSweep] {ex.Message}"); }
    }

    /// <summary>
    /// Build 168: offers to rename stranded faststart temps back to their original names.
    /// Only renames when the original name is still free; never deletes anything.
    /// </summary>
    private async Task OfferRestoreStrandedAsync(List<(string temp, string original)> stranded)
    {
        Log($"[{DateTime.Now:HH:mm:ss}] ⚠ Startup: {stranded.Count} video(s) exist only as a faststart temp file " +
            "(original missing): " + string.Join(", ", stranded.Select(x => System.IO.Path.GetFileName(x.temp))));

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        await dispatcher.InvokeAsync(() =>
        {
            var preview = string.Join("\n", stranded.Take(8).Select(x =>
                $"  • {System.IO.Path.GetFileName(x.temp)}\n      → {System.IO.Path.GetFileName(x.original)}"));
            if (stranded.Count > 8) preview += $"\n  …and {stranded.Count - 8} more";

            var res = System.Windows.MessageBox.Show(
                $"{stranded.Count} video(s) exist only as a faststart temp file — the original is missing.\n\n" +
                preview + "\n\n" +
                "An earlier Fix Faststart removed the original and then couldn't rename the fixed copy " +
                "into place. The temp file is the complete, fixed video.\n\n" +
                "Rename them back to their original names now?\n" +
                "(Choose No to leave them as they are. VME will ask again next launch.)",
                "Restore Videos From Interrupted Faststart Fix",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

            if (res != System.Windows.MessageBoxResult.Yes) return;

            int restored = 0;
            foreach (var (temp, original) in stranded)
            {
                try
                {
                    if (System.IO.File.Exists(original)) continue;   // never overwrite
                    System.IO.File.Move(temp, original);
                    restored++;
                    Log($"[{DateTime.Now:HH:mm:ss}] ✓ Restored {System.IO.Path.GetFileName(original)}");
                }
                catch (Exception ex)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] ✗ Couldn't restore {System.IO.Path.GetFileName(original)}: {ex.Message}");
                }
            }
            StatusText = $"Restored {restored} of {stranded.Count} video(s) from faststart temp files.";
        });
    }

    /// <summary>
    /// Called by MainWindow.OnLoaded — runs startup orphan recovery AFTER the window
    /// has subscribed to RecoveryCandidatesFound, eliminating the startup race condition.
    /// </summary>
    public void TriggerStartupRecovery()
    {
        Services.BackgroundTask.Run(CleanOrphanedTempFilesAsync, "Startup recovery");
    }

    /// <summary>
    /// Scans the given folders for orphan VME temp/backup files and fires
    /// RecoveryCandidatesFound if any are detected. Call after every folder/library load.
    /// </summary>
    public void CheckForRecovery(IEnumerable<string> folders)
    {
        var policy = Settings.OrphanTempFilePolicy;
        if (policy == "Ignore") return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        Services.BackgroundTask.Run(() =>
        {
            var candidates = Services.RecoveryService.Scan(folders);
            if (candidates.Count == 0) return;

            // ALL ObservableCollection mutations MUST be on the UI thread
            dispatcher?.InvokeAsync(() =>
            {
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠ Recovery: {candidates.Count} orphan file(s) found " +
                    $"(policy: {policy}).");

                if (policy == "AutoDelete")
                {
                    // Fire-and-forget the delete on a new background task,
                    // then dispatch the result back to UI thread
                    _ = Task.Run(() =>
                    {
                        int deleted = 0;
                        foreach (var c in candidates)
                        {
                            var (ok, _) = Services.RecoveryService.Delete(c);
                            if (ok) deleted++;
                        }
                        return deleted;
                    }).ContinueWith(t =>
                    {
                        int deleted = t.Result;
                        if (deleted > 0)
                        {
                            StatusText = $"Auto-deleted {deleted} orphan temp file(s).";
                            Log(
                                $"[{DateTime.Now:HH:mm:ss}] Auto-deleted {deleted} orphan temp file(s).");
                        }
                    }, System.Threading.CancellationToken.None,
                       System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
                       System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                }
                else
                {
                    // "Ask" (default) — show RecoveryDialog
                    RecoveryCandidatesFound?.Invoke(this, candidates);
                }
            });
        }, "Orphan recovery scan");
    }

    /// <summary>
    /// Removes any .vme_tmp_* / .vme_bak_* rows from the Files panel.
    /// Called after every successful embed/batch so Watch Folder pickups don't linger.
    /// </summary>
    private void EvictTempFileRows()
    {
        var toRemove = Files
            .Where(f => IsVmeTempPath(f.FilePath))
            .ToList();

        foreach (var f in toRemove)
        {
            Files.Remove(f);
            Log( $"[{DateTime.Now:HH:mm:ss}] Evicted temp row: {Path.GetFileName(f.FilePath)}");
        }
    }

    /// <summary>
    /// Returns true if the path looks like a VME temp/backup file that should
    /// never appear in the Files panel or be picked up by Watch Folder.
    /// </summary>
    private static bool IsVmeTempPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".vme_tmp_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".vme_bak_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Updates the corresponding LibraryEntry when a file is successfully embedded.</summary>
    /// <summary>
    /// THE single source-of-truth projection for a file's metadata after any write.
    ///
    /// A file's metadata lives in up to four places — disk tags, the FILES-panel
    /// <see cref="VideoFile.EmbeddedMetadata"/>, the <see cref="LibraryEntry"/> grid row,
    /// and the persistent cache. Disk is the only authority. Every embed/write path
    /// MUST funnel through this one method so all representations are projected from a
    /// single fresh disk read in a single place. Hand-updating any representation at a
    /// call site is what caused metadata to silently revert after a scan/restart.
    /// </summary>
    /// <param name="lookupPath">The file's path BEFORE any rename (used to find the existing grid/cache entry).</param>
    /// <param name="currentPath">The file's path AFTER any rename (the real on-disk path now).</param>
    /// <param name="fallback">In-memory metadata used only if the disk re-read fails.</param>
    /// <param name="filesPanelFile">Optional FILES-panel VideoFile to refresh from disk too.</param>
    private void SyncLibraryEntry(
        string lookupPath, string currentPath, MovieMetadata fallback,
        VideoFile? filesPanelFile = null)
    {
        // ── 1. Authoritative re-read from disk ─────────────────────────────────
        // The cache freshness key is (path, size, mtime). Re-reading the file we just
        // wrote guarantees every representation holds exactly what is physically
        // embedded, pinned to the correct post-write stat.
        MovieMetadata disk;
        long diskSize;
        DateTime diskMtime;
        try
        {
            var info = new FileInfo(currentPath);
            if (!info.Exists) return;   // file vanished (e.g. failed rename) — nothing to sync
            disk      = _metadataService.ReadMetadata(currentPath);  // includes artwork
            diskSize  = info.Length;
            diskMtime = info.LastWriteTimeUtc;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncLibraryEntry] disk re-read failed: {ex.Message}");
            disk = fallback; diskSize = 0; diskMtime = DateTime.UtcNow;
        }

        // Carry over filename-derived title/year fallbacks the scan would apply,
        // so an embed that left Title empty still shows a sensible value.
        var (parsedTitle, parsedYear) = FilenameParser.Parse(
            Path.GetFileNameWithoutExtension(currentPath));
        var resolvedTitle = !string.IsNullOrWhiteSpace(disk.Title) ? disk.Title
                          : !string.IsNullOrWhiteSpace(parsedTitle) ? parsedTitle
                          : Path.GetFileNameWithoutExtension(currentPath);
        var resolvedYear  = !string.IsNullOrWhiteSpace(disk.Year) ? disk.Year
                          : !string.IsNullOrWhiteSpace(parsedYear) ? parsedYear
                          : string.Empty;

        // ── Single field-mapping helper — used for BOTH the grid entry and the    ──
        // ── cache entry so the two can never drift apart again.                   ──
        void ApplyDiskTo(Models.LibraryEntry target, bool preserveWatched)
        {
            target.FilePath      = currentPath;
            target.FileSizeBytes = diskSize > 0 ? diskSize : target.FileSizeBytes;
            target.Title         = resolvedTitle;
            target.Year          = resolvedYear;
            target.Genre         = disk.Genre;
            target.Director      = disk.Director;
            target.Cast          = disk.Cast;
            target.Description   = disk.Description;
            target.ImdbId        = disk.ImdbId;
            target.TmdbId        = disk.TmdbId;
            target.ImdbRating    = disk.Rating;
            target.MpaRating     = disk.MpaRating;
            target.IsWatched     = preserveWatched ? (disk.IsWatched || target.IsWatched) : disk.IsWatched;
            if (disk.ArtworkBytes is { Length: > 0 })
                target.CoverArt  = disk.ArtworkBytes;
            target.IsEpisode     = disk.IsEpisode;
            target.ShowTitle     = disk.ShowTitle    ?? string.Empty;
            target.Season        = disk.Season;
            target.Episode       = disk.Episode;
            target.EpisodeTitle  = disk.EpisodeTitle ?? string.Empty;
            target.AiredDate     = disk.AiredDate    ?? string.Empty;
            target.TmdbSeriesId  = disk.TmdbSeriesId ?? string.Empty;
        }

        // ── 2. Project into the FILES-panel VideoFile (if supplied) ────────────
        // Previously only the single-file embed path did this; batch paths left the
        // panel object holding the pre-write snapshot. Now every path can refresh it.
        if (filesPanelFile != null)
        {
            try
            {
                filesPanelFile.EmbeddedMetadata = disk;

                // "What's actually on disk" signal: compare what we intended to write
                // (fallback) against what we just read back from disk. A clean match
                // means the panel reflects verified disk content, not a snapshot that
                // could silently revert. diskSize == 0 means the re-read failed, so we
                // can't claim verification.
                if (diskSize > 0)
                {
                    var intended = Services.MetadataService.BuildComment(
                        fallback.Description, fallback.ImdbId, fallback.TmdbId,
                        fallback.Rating, fallback.MpaRating, fallback.IsWatched,
                        fallback.IsEpisode, fallback.ShowTitle, fallback.Season,
                        fallback.Episode, fallback.EpisodeTitle, fallback.AiredDate,
                        string.Empty, fallback.TmdbSeriesId);
                    var onDisk = Services.MetadataService.BuildComment(
                        disk.Description, disk.ImdbId, disk.TmdbId,
                        disk.Rating, disk.MpaRating, disk.IsWatched,
                        disk.IsEpisode, disk.ShowTitle, disk.Season,
                        disk.Episode, disk.EpisodeTitle, disk.AiredDate,
                        string.Empty, disk.TmdbSeriesId);
                    var missing = Services.VmeCommentCodec.VerifyWritten(intended, onDisk);
                    filesPanelFile.DiskVerified = missing.Count == 0;
                }
                else
                {
                    filesPanelFile.DiskVerified = false;
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SyncLibraryEntry] panel refresh failed: {ex.Message}"); }
        }

        // ── 3. Project into the grid entry (if the file is in the library) ─────
        var entry = LibraryEntries.FirstOrDefault(e =>
            e.FilePath.Equals(lookupPath, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            ApplyDiskTo(entry, preserveWatched: true);
            entry.ArtworkChecked = true;   // build 172: just read from disk

            // Re-check the Show filter so a fixed title drops off "Missing poster" /
            // "Untagged" straight away, and cache the new poster thumbnail.
            if (disk.ArtworkBytes is { Length: > 0 })
                _ = Task.Run(() => Services.ArtworkThumbCache.Store(currentPath, disk.ArtworkBytes));
            var attentionDispatcher = System.Windows.Application.Current?.Dispatcher;
            if (attentionDispatcher != null && !attentionDispatcher.CheckAccess())
                attentionDispatcher.InvokeAsync(RefreshLibraryAttention);
            else
                RefreshLibraryAttention();

            // A file's on-disk metadata just changed, so the physical-episode scan
            // for its folder may be stale — drop the cache so the tree re-reads.
            InvalidatePhysicalEpisodeCache();

            // Raise TV tree so the untagged warning clears without a rescan. May be
            // called from a background batch task — marshal to the UI dispatcher.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.InvokeAsync(() => RaiseProperty(nameof(TvShowTree)));
            else
                RaiseProperty(nameof(TvShowTree));
        }

        // ── 4. Project into the persistent cache (ALWAYS, even if not in grid) ──
        // Files added via Add Files/Add Folder that were never scanned into the
        // library still must update the cache, or the next scan/restart reverts.
        if (diskSize > 0)
        {
            try
            {
                Models.LibraryEntry cacheEntry;
                if (entry != null) { cacheEntry = entry; }
                else { cacheEntry = new Models.LibraryEntry(); ApplyDiskTo(cacheEntry, preserveWatched: false); }

                // On rename, drop the stale old-path cache entry so a future scan
                // can't resurrect pre-rename metadata.
                if (!lookupPath.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                    _libraryCacheService.Evict(lookupPath);

                _libraryCacheService.Put(currentPath, diskSize, diskMtime, cacheEntry);

                var livePaths = LibraryEntries.Select(e => e.FilePath).ToList();
                if (!livePaths.Contains(currentPath, StringComparer.OrdinalIgnoreCase))
                    livePaths.Add(currentPath);
                _ = _libraryCacheService.SaveAsync(livePaths);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SyncLibraryEntry] cache update failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void RaiseProperty([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Exports NFO sidecar files for every entry currently in the Library.
    /// Each .nfo is saved alongside the video file (same folder, same base name).
    /// </summary>
    private async Task ExportLibraryNfoAsync()
    {
        var entries = LibraryEntries.ToList();
        if (entries.Count == 0) return;

        var answer = System.Windows.MessageBox.Show(
            $"Export NFO sidecar files for all {entries.Count} library entries?\n\n" +
            "Each .nfo file will be saved alongside its video file and can be used " +
            "by Kodi, Jellyfin, and Plex.",
            "Export Library NFO",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Yes);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        IsBusy = true;
        StatusText = $"Exporting NFO for {entries.Count} files…";

        // Build (filePath, metadata) pairs from LibraryEntries
        var pairs = entries
            .Where(e => File.Exists(e.FilePath))
            .Select(e => (filePath: e.FilePath, meta: e.ToMovieMetadata()))
            .ToList<(string filePath, Models.MovieMetadata meta)>();

        var progress = new System.Progress<(int done, int total)>(p =>
            LibraryScanStatus = $"Exporting NFO… {p.done}/{p.total}");

        var result = await Services.NfoExportService.ExportAsync(
            pairs, progress, default, writeTvShowNfo: Settings.WriteTvShowNfo);
        int ok     = result.ok;
        int failed = result.failed;

        EndOperation();
        IsBusy = false;
        StatusText = failed == 0
            ? $"✓ Exported {ok} NFO file(s)."
            : $"Exported {ok} NFO file(s), {failed} failed.";
        Log($"[{DateTime.Now:HH:mm:ss}] Library NFO export: {ok} ok, {failed} failed.");
    }
}

// ── Commands ──────────────────────────────────────────────────────────────────
