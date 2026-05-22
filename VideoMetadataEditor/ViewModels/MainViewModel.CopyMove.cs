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
// │  MainViewModel.CopyMove                                              
// │  Move / Copy — engine selection, execution, Smart Organise, progress
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
    private async Task ExecuteCopyMoveAsync(CopyMode mode)
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0 || string.IsNullOrWhiteSpace(CopyDestination)) return;

        var verb    = mode == CopyMode.Copy ? "Copying" : "Moving";
        var sources = selected.Select(f => f.FilePath).ToList();

        // ── Smart organise: build per-file destination map ─────────────────
        Dictionary<string, string>? destMap = null;
        var skippedUntagged = new List<VideoFile>();
        if (Settings.SmartOrganiseEnabled)
        {
            destMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected)
            {
                var meta  = file.EmbeddedMetadata;
                var dest  = Services.FileCopyService.ResolveDestination(
                    CopyDestination,
                    meta.IsEpisode,
                    meta.ShowTitle,
                    meta.Year,
                    meta.Season,
                    smartOrganise:       true,
                    moviesFolderName:    Settings.MoviesFolderName,
                    tvFolderName:        Settings.TvShowsFolderName,
                    createSeasonFolders: Settings.CreateSeasonSubfolders,
                    untaggedHandling:    Settings.UntaggedFileHandling,
                    unsortedFolderName:  Settings.UnsortedFolderName,
                    title:               meta.Title,
                    filePath:            file.FilePath);

                if (string.IsNullOrEmpty(dest))
                {
                    // "Skip" mode — untagged file excluded from transfer
                    skippedUntagged.Add(file);
                    continue;
                }
                destMap[file.FilePath] = dest;
            }

            // Remove skipped files from the source list
            if (skippedUntagged.Count > 0)
            {
                foreach (var s in skippedUntagged)
                {
                    selected.Remove(s);
                    sources.Remove(s.FilePath);
                }
                ConsoleLog.Insert(0,
                    $"[{DateTime.Now:HH:mm:ss}] ⏭ Skipped {skippedUntagged.Count} untagged file(s) " +
                    "— no embedded metadata and filename doesn't match TV episode pattern. " +
                    "Tag them first, or change Settings → Untagged file handling.");

                // List up to 10 skipped filenames so the user can see exactly which ones were affected
                int shown = Math.Min(10, skippedUntagged.Count);
                for (int i = 0; i < shown; i++)
                {
                    ConsoleLog.Insert(0,
                        $"[{DateTime.Now:HH:mm:ss}]   ⤷ skipped: {System.IO.Path.GetFileName(skippedUntagged[i].FilePath)}");
                }
                if (skippedUntagged.Count > shown)
                {
                    ConsoleLog.Insert(0,
                        $"[{DateTime.Now:HH:mm:ss}]   ⤷ … and {skippedUntagged.Count - shown} more (full list in app logs)");
                }

                if (selected.Count == 0)
                {
                    StatusText = $"All {skippedUntagged.Count} file(s) were untagged — nothing to {verb.ToLower()}.";
                    return;
                }
            }
        }

        // ── Disk space analysis ────────────────────────────────────────────────
        var analysis = Services.FileCopyService.AnalyseDiskSpace(sources, CopyDestination);
        if (!analysis.HasSufficientSpace)
        {
            StatusText = $"⚠ Insufficient space — {analysis.Message}";
            ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] ABORTED: {analysis.Message}");
            DiskSpaceStatus = analysis.Message;
            return;
        }

        DiskSpaceStatus = analysis.Message;

        IsBusy = true;
        ProgressValue = 0;
        StatusText = $"{verb} {selected.Count} file(s)  ·  {analysis.RequiredDisplay} needed  ·  {analysis.AvailableDisplay} free";

        // Pass sources so Auto mode can pick FastCopy for large files
        var engine = Services.FileCopyService.ResolvedEngine(Settings.PreferredCopyEngine, sources);

        try
        {
            if (engine is CopyEngine.FastCopy)
            {
                StatusText = $"{verb} {selected.Count} file(s) via {engine}…";
                ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Launching {engine} for {selected.Count} file(s)");

                // Parse ConflictMode for external engines
                var extConflict = ConflictMode switch
                {
                    "Skip"             => Services.ConflictMode.Skip,
                    "OverwriteIfNewer" => Services.ConflictMode.OverwriteIfNewer,
                    "Rename"           => Services.ConflictMode.Rename,
                    _                  => Services.ConflictMode.Overwrite
                };

                // Real-time progress from destination folder monitoring
                var extProgress = new System.Progress<(long bytesDone, long bytesTotal, int filesDone,
                    int filesTotal, string current, int pct)>(p =>
                {
                    ProgressValue = p.pct;
                    StatusText    = p.bytesTotal > 0
                        ? $"{verb} via {engine} — {p.filesDone}/{p.filesTotal} files · " +
                          $"{Services.FileCopyService.FormatBytes(p.bytesDone)} / {Services.FileCopyService.FormatBytes(p.bytesTotal)}" +
                          (string.IsNullOrWhiteSpace(p.current) ? "" : $"  ·  {p.current}")
                        : $"{verb} via {engine}… {p.filesDone}/{p.filesTotal} files";
                });

                var extResult = await Services.FileCopyService.RunExternalAsync(
                    sources, CopyDestination, mode, engine, extConflict, extProgress);

                ProgressValue = extResult.Success ? 100 : 0;
                bool ok = extResult.Success;

                // Build clear engine-specific result message
                string engineName = "FastCopy";
                string opLabel    = mode == Services.CopyMode.Copy ? "Copy" : "Move";

                if (ok)
                {
                    var details = new System.Text.StringBuilder();
                    details.Append($"{extResult.FilesCopied} file(s)");
                    if (extResult.BytesCopied > 0)
                        details.Append($" ({Services.FileCopyService.FormatBytes(extResult.BytesCopied)})");
                    if (extResult.FilesSkipped > 0)
                        details.Append($"  ·  {extResult.FilesSkipped} skipped");
                    if (extResult.FilesFailed  > 0)
                        details.Append($"  ·  {extResult.FilesFailed} failed");
                    StatusText = $"✓  {engineName} — {opLabel} Operation Successful  ·  {details}";
                }
                else
                {
                    var reason = extResult.ErrorMessage ?? "check the transfer window for details";
                    StatusText = $"✗  {engineName} — {opLabel} Operation Failed  ·  {reason}";
                }

                ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {StatusText}");

                await Task.Delay(600);

                // For Move: remove transferred files that no longer exist at source
                if (mode == CopyMode.Move)
                {
                    int removed = 0;
                    foreach (var vf in selected.ToList())
                    {
                        if (!File.Exists(vf.FilePath))
                        {
                            Files.Remove(vf);
                            removed++;
                        }
                    }
                    // If all files have been moved out, stop the watcher so it
                    // doesn't keep monitoring an empty folder
                    if (Settings.WatchFolderEnabled &&
                        !Files.Any(f => !f.IsSeparator))
                        _watchFolderService.Stop();

                    if (removed > 0)
                        StatusText = $"✓  {engineName} — {opLabel} Operation Successful  ·  {removed} file(s) moved, removed from list.";
                }
            }
            else
            {
                // ── Built-in multi-threaded engine ─────────────────────────────
                int workerCount = CopyDestination.StartsWith(@"\\")
                    ? Math.Min(Environment.ProcessorCount, 2)
                    : Math.Min(Environment.ProcessorCount, 4);

                int maxRetries  = Settings.CopyMaxRetries;
                int retryDelay  = Settings.CopyRetryDelayMs;

                StatusText = $"{verb} {selected.Count} file(s)  ·  {workerCount} thread(s)  ·  up to {maxRetries} retries";
                ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] Built-in engine  ·  {workerCount} threads  ·  retry ×{maxRetries} @ {retryDelay}ms");

                // Speed tracking
                long lastBytes    = 0;
                var  speedTick    = DateTime.UtcNow;

                var batchProgress = new System.Progress<(int filesDone, int total, long bytesDone, long bytesTotal, string currentFile)>(p =>
                {
                    // Byte-accurate progress bar (0-99 while running, 100 on completion)
                    ProgressValue = p.bytesTotal > 0
                        ? (int)Math.Min(99, (double)p.bytesDone / p.bytesTotal * 100)
                        : (int)Math.Min(99, (double)p.filesDone / Math.Max(p.total, 1) * 100);

                    // Rolling speed: recalculate every 500 ms
                    var now = DateTime.UtcNow;
                    double elapsedSec = (now - speedTick).TotalSeconds;
                    string speedStr = string.Empty;
                    if (elapsedSec >= 0.5 && p.bytesDone > lastBytes)
                    {
                        double bytesPerSec = (p.bytesDone - lastBytes) / elapsedSec;
                        speedStr   = $"  ·  {Services.FileCopyService.FormatBytes((long)bytesPerSec)}/s";
                        lastBytes  = p.bytesDone;
                        speedTick  = now;
                    }

                    // "Copying... 94.2% (8/13) · 9.43 GB / 14.31 GB · 423 MB/s · movie.mp4"
                    double pct     = p.bytesTotal > 0 ? (double)p.bytesDone / p.bytesTotal * 100 : 0;
                    string current = string.IsNullOrWhiteSpace(p.currentFile) ? string.Empty : $"  ·  {p.currentFile}";

                    StatusText = $"{verb}…  {pct:F1}%  ({p.filesDone}/{p.total})  ·  " +
                                 $"{Services.FileCopyService.FormatBytes(p.bytesDone)} / " +
                                 $"{Services.FileCopyService.FormatBytes(p.bytesTotal)}" +
                                 speedStr + current;
                });

                // Parse ConflictMode from the string setting
                var conflictMode = ConflictMode switch
                {
                    "Overwrite"        => Services.ConflictMode.Overwrite,
                    "OverwriteIfNewer" => Services.ConflictMode.OverwriteIfNewer,
                    "Rename"           => Services.ConflictMode.Rename,
                    _                  => Services.ConflictMode.Skip
                };

                List<Services.FileCopyResult> results;
                if (destMap != null)
                {
                    // Smart Organise: pass the full source list to BuiltInBatchCopyAsync
                    // with a per-file destination resolver. This keeps full Level-1 parallelism
                    // (all files copied concurrently) instead of the old sequential loop.
                    // BuiltInBatchCopyAsync resolves each file's destination from the map.
                    results = await Services.FileCopyService.BuiltInBatchCopyAsync(
                        sources, CopyDestination, mode,
                        conflictMode: conflictMode,
                        maxRetries:   maxRetries,
                        retryDelayMs: retryDelay,
                        progress:     batchProgress,
                        destMap:      destMap);
                }
                else
                {
                    results = await Services.FileCopyService.BuiltInBatchCopyAsync(
                        sources, CopyDestination, mode,
                        conflictMode: conflictMode,
                        maxRetries:   maxRetries,
                        retryDelayMs: retryDelay,
                        progress:     batchProgress);
                }

                // ── Process results ────────────────────────────────────────────
                int succeeded = results.Count(r => r.Success);
                int failed    = results.Count(r => !r.Success);
                int verified  = results.Count(r => r.Verified);

                foreach (var r in results)
                {
                    if (r.Success)
                        ConsoleLog.Insert(0,
                            $"[{DateTime.Now:HH:mm:ss}] ✓ {Path.GetFileName(r.SourcePath)} " +
                            $"({Services.FileCopyService.FormatBytes(r.BytesCopied)})" +
                            (r.Verified ? " · size verified" : ""));
                    else
                        ConsoleLog.Insert(0,
                            $"[{DateTime.Now:HH:mm:ss}] ✕ {Path.GetFileName(r.SourcePath)} — {r.Error}");
                }

                ProgressValue = 100;
                string cfOpLabel = mode == Services.CopyMode.Copy ? "Copy" : "Move";
                long totalCopied = results.Where(r => r.Success).Sum(r => r.BytesCopied);
                StatusText = failed == 0
                    ? $"✓  Custom Fast — {cfOpLabel} Operation Successful  ·  {succeeded} file(s) ({Services.FileCopyService.FormatBytes(totalCopied)})  ·  {verified} size-verified"
                    : $"⚠  Custom Fast — {cfOpLabel} Operation Finished  ·  {succeeded} succeeded, {failed} failed  ·  see Processing Log";

                // Fire banner event
                var transferArgs = new TransferEventArgs
                {
                    IsMove      = mode == Services.CopyMode.Move,
                    Succeeded   = succeeded,
                    Failed      = failed,
                    Verified    = verified,
                    BytesMoved  = totalCopied,
                    Destination = CopyDestination ?? string.Empty
                };
                if (mode == Services.CopyMode.Move)
                    MoveCompleted?.Invoke(this, transferArgs);
                else
                    CopyCompleted?.Invoke(this, transferArgs);

                await Task.Delay(800);

                var successfulResults = results.Where(r => r.Success).ToList();
                await RefreshAfterTransferAsync(selected, mode, successfulResults);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"{verb} error: {ex.Message}";
            ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}");
        }
        finally
        {
            EndOperation();
            RaiseProperty(nameof(CanCopyMove));
        }
    }

    /// <summary>
    /// After a transfer completes, clears the file list and rebuilds it from the
    /// last selected folder so it reflects the current on-disk state.
    /// For moves the destination folder is used; for copies the original folder.
    /// </summary>
    private async Task RefreshAfterTransferAsync(
        List<VideoFile> transferred,
        CopyMode mode,
        List<Services.FileCopyResult>? results = null)
    {
        await Task.Yield();

        if (mode == CopyMode.Copy)
        {
            // Copy — source files are still exactly where they were.
            // Nothing to update in the file list.
            StatusText = $"Copy complete — {transferred.Count} file(s) copied.";
            return;
        }

        // Move — remove successfully moved files from the panel.
        // Strategy: trust the engine's Success report first; fall back to
        // physical existence check for any file the engine didn't report on.
        // Never clear the full list — files not involved in this operation stay.
        var successfulSourcePaths = new HashSet<string>(
            results?.Where(r => r.Success).Select(r => r.SourcePath)
                    ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        int removedCount = 0;
        foreach (var vf in transferred.ToList())
        {
            bool reportedSuccess = successfulSourcePaths.Contains(vf.FilePath);
            bool fileGone        = !File.Exists(vf.FilePath);

            if (reportedSuccess || fileGone)
            {
                Files.Remove(vf);
                removedCount++;
            }
        }

        // If the currently selected file was removed, clear the editing panel
        if (SelectedFile == null || !Files.Contains(SelectedFile))
        {
            SelectedFile = Files.FirstOrDefault();
        }

        int remaining = Files.Count(f => !f.IsSeparator);
        StatusText = removedCount > 0
            ? $"✓ Move complete — {removedCount} file(s) moved and removed from list.  {remaining} file(s) remaining."
            : $"Move complete — no files were removed from the list (check Processing Log for errors).";
    }

    private void OnSelectedFileChanged()
    {
        if (SelectedFile == null)
        {
            ClearEditing();
            return;
        }

        // Show original filename (read-only, always the on-disk name before any rename)
        OriginalFileName = SelectedFile.FileName;
        RaiseProperty(nameof(SelectedFileIsReadOnly));

        // Load technical media info for the info panel (async to avoid blocking UI)
        IsPreviewPlaying = false;
        MediaInfo = null; // clear stale info immediately
        _ = Task.Run(() => Services.MediaInfoService.Read(SelectedFile.FilePath))
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        System.Windows.Application.Current.Dispatcher.Invoke(() => MediaInfo = t.Result);
                });

        // Load pending (cached) metadata into editing fields
        CopyMetadataTo(SelectedFile.PendingMetadata, EditingMetadata);

        // ── Content-type ground-truth reset ───────────────────────────────────
        // CopyMetadataTo copies PendingMetadata which may carry a stale IsEpisode
        // from a previous manual toggle. Always override from EmbeddedMetadata
        // (the on-disk source of truth), then auto-detect from filename if needed.
        EditingMetadata.IsEpisode    = SelectedFile.EmbeddedMetadata.IsEpisode;
        EditingMetadata.ShowTitle    = SelectedFile.EmbeddedMetadata.ShowTitle;
        EditingMetadata.Season       = SelectedFile.EmbeddedMetadata.Season;
        EditingMetadata.Episode      = SelectedFile.EmbeddedMetadata.Episode;
        EditingMetadata.EpisodeTitle = SelectedFile.EmbeddedMetadata.EpisodeTitle;
        EditingMetadata.AiredDate    = SelectedFile.EmbeddedMetadata.AiredDate;
        EditingMetadata.TvdbId       = SelectedFile.EmbeddedMetadata.TvdbId;
        EditingMetadata.TmdbSeriesId = SelectedFile.EmbeddedMetadata.TmdbSeriesId;

        // Auto-detect from filename if not yet embedded
        if (!EditingMetadata.IsEpisode)
        {
            var epDetect = Services.FilenameParser.ParseEpisode(
                System.IO.Path.GetFileNameWithoutExtension(SelectedFile.FilePath));
            if (epDetect.HasValue)
            {
                EditingMetadata.IsEpisode = true;
                EditingMetadata.ShowTitle = epDetect.Value.showTitle;
                EditingMetadata.Season    = epDetect.Value.season;
                EditingMetadata.Episode   = epDetect.Value.episode;
            }
        }

        // Notify toggle bindings immediately
        RaiseProperty(nameof(IsEpisodeMode));
        RaiseProperty(nameof(IsMovieMode));

        // Always enforce the clean parsed title + year — the pending/embedded Title
        // may still contain raw filename noise if the file was just added.
        if (!string.IsNullOrWhiteSpace(SelectedFile.ParsedTitle))
            EditingMetadata.Title = SelectedFile.ParsedTitle;
        if (!string.IsNullOrWhiteSpace(SelectedFile.ParsedYear))
            EditingMetadata.Year = SelectedFile.ParsedYear;

        // Format capabilities
        FormatSupportsArtwork  = SelectedFile.SupportsArtwork;
        FormatSupportsFullTags = SelectedFile.SupportsFullTags;
        FormatWarning          = SelectedFile.FormatWarning;

        // Artwork — load lazily: artwork was skipped during folder scan to save time.
        // If EmbeddedMetadata has no artwork yet, read it now in the background.
        if (EditingMetadata.ArtworkBytes is not { Length: > 0 }
            && SelectedFile.EmbeddedMetadata.ArtworkBytes is not { Length: > 0 })
        {
            var filePath = SelectedFile.FilePath;
            _ = Task.Run(() => _metadataService.ReadArtworkOnly(filePath))
                .ContinueWith(t =>
                {
                    if (!t.IsCompletedSuccessfully || t.Result == null) return;
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        // Only apply if this file is still selected
                        if (SelectedFile?.FilePath.Equals(filePath,
                            StringComparison.OrdinalIgnoreCase) == true)
                        {
                            SelectedFile.EmbeddedMetadata.ArtworkBytes = t.Result;
                            SelectedFile.PendingMetadata.ArtworkBytes   = t.Result;
                            EditingMetadata.ArtworkBytes = t.Result;
                            UpdateArtworkDisplay(t.Result);
                        }
                    });
                });
        }
        UpdateArtworkDisplay(EditingMetadata.ArtworkBytes);

        // Cancel any in-flight ShowTvPickerAsync — only if it belongs to a different file
        // (guards against same-file property-chain re-triggers killing an active picker)
        _tvPickerCts?.Cancel();
        _tvPickerCts = null;
        _selectedSeriesTmdbId = string.Empty;  // clear so picker knows it's fresh

        // Reset TV picker — must never bleed across file switches
        IsTvPickerVisible = false;
        TvSeasonNumbers.Clear();
        TvEpisodeItems.Clear();
        TvSelectedEpisode = null;
        _selectedSeriesTmdbId = string.Empty;
        _tvEpisodeManuallyLoaded = false;

        // Restore retrieved metadata from file cache
        var cachedMeta = SelectedFile.RetrievedMetadata;
        RetrievedMetadata = cachedMeta;

        // If file has a cached episode result, ensure Step 2 panel shows immediately
        // (IsTvPickerVisible was just set to false above, HasTvEpisodeResult re-evaluates here)
        // No extra raise needed — RetrievedMetadata setter already raises HasTvEpisodeResult.

        // The search key is ONLY parsed title + year — no other filename tokens
        var queryTitle = SelectedFile.ParsedTitle;
        var queryYear  = SelectedFile.ParsedYear;

        // Fall back to embedded title/year only if parse found nothing
        if (string.IsNullOrWhiteSpace(queryTitle)) queryTitle = EditingMetadata.Title;
        if (string.IsNullOrWhiteSpace(queryYear))  queryYear  = EditingMetadata.Year;

        // In TV mode, use ShowTitle directly — no year appended (shows span years)
        if (EditingMetadata.IsEpisode && !string.IsNullOrWhiteSpace(EditingMetadata.ShowTitle))
        {
            queryTitle = EditingMetadata.ShowTitle;
            queryYear  = string.Empty;  // year irrelevant for show search
        }

        // Only auto-populate SearchQuery when switching to a NEW file.
        // If the user already typed a custom search term for THIS file, preserve it.
        // This prevents losing "Dr. STONE" when the user re-clicks the same file
        // or clicks the file after typing a manual search term.
        var autoQuery = FilenameParser.BuildSearchQuery(queryTitle, queryYear);
        if (string.IsNullOrWhiteSpace(SearchQuery)
            || _lastAutoQueryFile != SelectedFile.FilePath)
        {
            SearchQuery = autoQuery;
            _lastAutoQueryFile = SelectedFile.FilePath;

            // Auto-detect TV mode from filename episode code so TV shows search
            // the correct TMDB endpoint without the user manually toggling.
            // Only override when the file has no embedded IsEpisode state yet.
            if (!EditingMetadata.IsEpisode)
            {
                var epDetected = Services.FilenameParser.ParseEpisode(
                    System.IO.Path.GetFileNameWithoutExtension(SelectedFile.FileName));
                if (epDetected.HasValue)
                {
                    // File has S##E## — switch to TV mode and use the clean show title
                    IsEpisodeMode = true;
                    SearchQuery   = epDetected.Value.showTitle;
                }
            }
        }

        // Auto-populate the ID Lookup field from embedded IDs — so the search
        // in Retrieved Data tab will immediately use the correct ID if present
        var embeddedImdb = EditingMetadata.ImdbId?.Trim();
        var embeddedTmdb = EditingMetadata.TmdbId?.Trim();
        if (!string.IsNullOrWhiteSpace(embeddedTmdb))
            IdLookup = embeddedTmdb;
        else if (!string.IsNullOrWhiteSpace(embeddedImdb))
            IdLookup = embeddedImdb;
        else
            IdLookup = string.Empty;

        // Show parsed breakdown in Raw Data tab banner
        ParsedFileInfo = !string.IsNullOrWhiteSpace(queryTitle)
            ? $"Searching with — Title: \"{queryTitle}\"  ·  Year: {(string.IsNullOrWhiteSpace(queryYear) ? "not detected" : queryYear)}"
            : null;

        // Status bar
        if (!string.IsNullOrWhiteSpace(queryTitle))
        {
            var yr = string.IsNullOrWhiteSpace(queryYear) ? "year unknown" : queryYear;
            StatusText = $"Detected: \"{queryTitle}\" ({yr}) — auto-search ready";
        }

        // Auto-trigger search for this file if not already retrieved
        if (!string.IsNullOrWhiteSpace(queryTitle) && SelectedFile.RetrievedMetadata == null)
            ScheduleAutoSearch(SelectedFile, queryTitle, queryYear);

        UpdateRenamePreview();
    }

    /// <summary>
    /// Background auto-search: tries TMDB then OMDB using parsed title+year,
    /// then falls back to ID-based lookup if needed.
    /// Pass calledFromBatch=true to skip IsBusy management (batch controls it).
    /// </summary>
}
