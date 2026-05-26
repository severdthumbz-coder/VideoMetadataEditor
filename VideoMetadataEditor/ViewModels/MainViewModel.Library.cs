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
        if (ExtraLibraryFolders.Contains(folder)) return;
        if (string.Equals(folder, Settings.LibraryFolderPath, StringComparison.OrdinalIgnoreCase)) return;
        ExtraLibraryFolders.Add(folder);
        Settings.ExtraLibraryFolders.Add(folder);
        RaiseProperty(nameof(HasExtraFolders));
        _ = App.ConfigService.SaveAsync();
        StatusText = $"Extra folder added: {folder}";
    }

    private void ClearLibraryCache()
    {
        if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath))
            _libraryCacheService.Clear(Settings.LibraryFolderPath);
        else
            Services.LibraryCacheService.ClearAll();

        RaiseProperty(nameof(LibraryCacheInfo));
        StatusText = "Library cache cleared — next scan will rebuild it from disk.";
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

        // Stop the library watch service for the duration of the scan.
        // Without this, the watch service fires FileDetected for every file
        // in the folder (because LibraryEntries.Clear() makes them all look
        // "new"), adding all 2483 files via BuildSingleEntryAsync while the
        // scan simultaneously adds the same 2483 — doubling the result.
        bool watchWasActive = _libraryWatchService.IsActive;
        if (watchWasActive) _libraryWatchService.Stop();

        LibraryEntries.Clear();
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
            var entries = new List<Models.LibraryEntry>();
            foreach (var scanFolder in allFolders)
            {
                var folderEntries = await _libraryScanService.ScanFolderAsync(
                    scanFolder, Settings.LibraryRecursive, progress, ct);
                foreach (var e in folderEntries)
                    if (seen.Add(e.FilePath)) entries.Add(e);
            }

            foreach (var e in entries)
                LibraryEntries.Add(e);

            // ── Load cover art for entries missing it — background, UI-safe ────────
            // Only fires for cache-miss entries (from-disk reads). Cache hits already
            // have artwork. Processes in small batches with a yield between each so
            // the dispatcher thread is never flooded with InvokeAsync calls.
            var missingArt = entries.Where(e => e.CoverArt is not { Length: > 0 }).ToList();
            if (missingArt.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                  try
                  {
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    const int batchSize = 20;
                    for (int i = 0; i < missingArt.Count; i += batchSize)
                    {
                        if (ct.IsCancellationRequested) break;
                        var batch = missingArt.Skip(i).Take(batchSize).ToList();
                        foreach (var entry in batch)
                        {
                            if (ct.IsCancellationRequested) break;
                            try
                            {
                                var art = _metadataService.ReadArtworkOnly(entry.FilePath);
                                if (art is { Length: > 0 } && dispatcher != null)
                                    await dispatcher.InvokeAsync(
                                        () => entry.CoverArt = art,
                                        System.Windows.Threading.DispatcherPriority.Background, ct);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{System.IO.Path.GetFileNameWithoutExtension(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Name ?? "VM")}] {ex.GetType().Name}: {ex.Message}"); }
                        }
                        // Yield between batches — keeps dispatcher queue small
                        await Task.Delay(50, ct).ContinueWith(_ => { });
                    }
                  }
                  catch (OperationCanceledException) { }
                  catch (Exception ex) { Services.BackgroundTask.OnError?.Invoke("Bulk artwork load", ex); }
                }, ct);
            }

            // Build status line — include cache stats so user knows how many were fast reads
            var hits   = _libraryScanService.LastScanCacheHits;
            var misses = _libraryScanService.LastScanCacheMisses;
            LibraryScanStatus = hits > 0
                ? $"✓  {LibraryEntries.Count} file(s) — {hits} from cache, {misses} read from disk."
                : $"✓  {LibraryEntries.Count} file(s) scanned.";
            RaiseProperty(nameof(LibraryCacheInfo));
            LibraryScanProgress = 100;
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
        }
        catch (Exception ex)
        {
            LibraryScanStatus = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsLibraryScanning = false;

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
                    parts.Add($"{faststartTemps.Count} incomplete faststart temp file(s) " +
                              "(*.faststart.tmp.*) — leftovers from a Fix All Faststart that was " +
                              "interrupted. These are incomplete and safe to delete; your originals are intact");
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
    private void SyncLibraryEntry(string filePath, MovieMetadata meta)
    {
        var entry = LibraryEntries.FirstOrDefault(e =>
            e.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        if (entry == null) return;   // file not in library — nothing to update or cache

        // Update in-memory grid entry
        entry.Title        = meta.Title;
        entry.Year         = meta.Year;
        entry.Genre        = meta.Genre;
        entry.Director     = meta.Director;
        entry.Cast         = meta.Cast;
        entry.Description  = meta.Description;
        entry.ImdbId       = meta.ImdbId;
        entry.TmdbId       = meta.TmdbId;
        entry.ImdbRating   = meta.Rating;
        entry.MpaRating    = meta.MpaRating;
        if (meta.ArtworkBytes is { Length: > 0 })
            entry.CoverArt = meta.ArtworkBytes;

        // ── TV episode fields ──────────────────────────────────────────────────
        // These were missing before — caused the "untagged" warning to persist in
        // the TV tree after embedding, because the in-memory LibraryEntry still
        // had stale/empty IsEpisode, Season, Episode values. A full rescan cleared
        // it but the in-place sync after embed did not.
        entry.IsEpisode    = meta.IsEpisode;
        entry.ShowTitle    = meta.ShowTitle    ?? string.Empty;
        entry.Season       = meta.Season;
        entry.Episode      = meta.Episode;
        entry.EpisodeTitle = meta.EpisodeTitle ?? string.Empty;
        entry.AiredDate    = meta.AiredDate    ?? string.Empty;
        // Raise TV tree immediately so the untagged warning clears without rescan
        RaiseProperty(nameof(TvShowTree));

        // Refresh cache entry — file was just rewritten so mtime will change.
        // Use the updated LibraryEntry (not a stub) so the cache stays accurate.
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                _libraryCacheService.Put(filePath, info.Length, info.LastWriteTimeUtc, entry);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SyncLibraryEntry] {ex.GetType().Name}: {ex.Message}"); }
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
}

// ── Commands ──────────────────────────────────────────────────────────────────
