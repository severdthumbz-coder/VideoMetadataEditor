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
// │  MainViewModel.Embed                                              
// │  Embed / Metadata — apply retrieved, write tags, rename, batch, NFO
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
    private void ApplyRetrievedToEditing()
    {
        if (RetrievedMetadata == null) return;
        CopyMetadataTo(RetrievedMetadata, EditingMetadata);
        // Block auto-search debounce from overwriting what the user just applied
        _tvEpisodeManuallyLoaded = RetrievedMetadata.IsEpisode;

        if (SelectedFile != null)
        {
            // Update PendingMetadata so data survives re-selection and is ready to embed
            CopyMetadataTo(RetrievedMetadata, SelectedFile.PendingMetadata);

            // Update EmbeddedMetadata TV fields so the ground-truth reset in
            // OnSelectedFileChanged doesn't clobber IsEpisode back to false
            // before the user has had a chance to embed. Only TV fields are updated
            // here — the actual on-disk tags are unchanged until explicit embed.
            if (RetrievedMetadata.IsEpisode)
            {
                SelectedFile.EmbeddedMetadata.IsEpisode    = true;
                SelectedFile.EmbeddedMetadata.ShowTitle    = RetrievedMetadata.ShowTitle;
                SelectedFile.EmbeddedMetadata.Season       = RetrievedMetadata.Season;
                SelectedFile.EmbeddedMetadata.Episode      = RetrievedMetadata.Episode;
                SelectedFile.EmbeddedMetadata.EpisodeTitle = RetrievedMetadata.EpisodeTitle;
                SelectedFile.EmbeddedMetadata.AiredDate    = RetrievedMetadata.AiredDate;
                SelectedFile.EmbeddedMetadata.TmdbSeriesId = RetrievedMetadata.TmdbSeriesId;
                // Also patch IDs so ground-truth reset doesn't lose them
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.TmdbId))
                    SelectedFile.EmbeddedMetadata.TmdbId = RetrievedMetadata.TmdbId;
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.ImdbId))
                    SelectedFile.EmbeddedMetadata.ImdbId = RetrievedMetadata.ImdbId;
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.TvdbId))
                    SelectedFile.EmbeddedMetadata.TvdbId = RetrievedMetadata.TvdbId;
            }
        }

        RaiseProperty(nameof(IsEpisodeMode));
        RaiseProperty(nameof(IsMovieMode));
        UpdateArtworkDisplay(EditingMetadata.ArtworkBytes);
        UpdateRenamePreview();
    }

    // ── Apply / Embed ─────────────────────────────────────────────────────────

    private async Task ApplyToFileAsync()
    {
        if (SelectedFile == null) return;

        // ── CRITICAL: snapshot all mutable state BEFORE the first await ──────
        var targetFile      = SelectedFile;
        var metadataToWrite = EditingMetadata.Clone();
        var ct              = BeginOperation();   // cancellable via Cancel button

        IsBusy = true;
        targetFile.IsProcessing = true;
        bool ok = false;
        bool wasRenamed = false;

        try
        {
            var progress = new System.Progress<string>(msg =>
            {
                Log( $"[{DateTime.Now:HH:mm:ss}] {msg}");
                StatusText = msg;
            });

            // ── Step 1: Stop media preview ────────────────────────────────────
            MediaPlayerStopRequested?.Invoke(this, EventArgs.Empty);
            await Task.Delay(120);

            // ── Step 2: Clear read-only attribute if set ─────────────────────
            if (targetFile.IsReadOnly)
            {
                Log( $"[{DateTime.Now:HH:mm:ss}] Read-only detected — attempting auto-clear…");
                bool cleared = await Services.FileLockService.TryClearReadOnlyAsync(targetFile.FilePath);
                if (!cleared)
                {
                    StatusText = $"🔒 Cannot clear read-only — check NTFS permissions.";
                    Log( $"[{DateTime.Now:HH:mm:ss}] BLOCKED (read-only): {targetFile.FileName}");
                    ReadOnlyFileBlocked?.Invoke(this, targetFile);
                    return;
                }
                RaiseProperty(nameof(SelectedFileIsReadOnly));
                Log( $"[{DateTime.Now:HH:mm:ss}] Read-only cleared automatically.");
            }

            // ── Step 3: Full writability diagnosis ────────────────────────────
            var report = await Services.FileLockService.DiagnoseAsync(targetFile.FilePath);
            if (!report.IsWritable)
            {
                if (report.Issue == Services.WritabilityIssue.LockedByThisApp)
                {
                    StatusText = "⏯ Waiting for preview player to release file…";
                    Log( $"[{DateTime.Now:HH:mm:ss}] Waiting for player release: {targetFile.FileName}");
                    bool released = await Services.FileLockService.WaitForWritableAsync(
                        targetFile.FilePath, maxWaitMs: 4000);
                    if (!released)
                    {
                        StatusText = $"⏯ Preview player still holds the file — stop playback and retry.";
                        Log( $"[{DateTime.Now:HH:mm:ss}] BLOCKED (player lock): {targetFile.FileName}");
                        ReadOnlyFileBlocked?.Invoke(this, targetFile);
                        return;
                    }
                }
                else
                {
                    var friendly = Services.FileLockService.FriendlyError(report);
                    StatusText = friendly;
                    Log( $"[{DateTime.Now:HH:mm:ss}] BLOCKED: {report.Message}");
                    foreach (var p in report.LockingProcesses)
                        Log( $"[{DateTime.Now:HH:mm:ss}]   Locking process: {p.Name} (PID {p.Pid})");
                    ReadOnlyFileBlocked?.Invoke(this, targetFile);
                    return;
                }
            }

            // ── Save undo snapshot BEFORE writing ────────────────────────────
            targetFile.UndoFilePath = targetFile.FilePath;
            targetFile.UndoMetadata = targetFile.EmbeddedMetadata.Clone();
            RaiseProperty(nameof(UndoLastEmbedCommand));

            CopyMetadataTo(metadataToWrite, targetFile.PendingMetadata);

            var currentPath = targetFile.FilePath;
            var writeResult = await _metadataService.WriteMetadataDetailedAsync(
                currentPath, metadataToWrite, Settings, progress, ct: ct);
            ok = writeResult.Success;

            if (ok)
            {
                targetFile.WriteStatus = Models.WriteStatus.Success;
                var oldName = Path.GetFileName(currentPath);
                var res1 = MediaInfo?.ResolutionDisplay ?? string.Empty;
                var fmt1 = targetFile.Extension ?? string.Empty;
                bool renamed = false; string newPath = currentPath; string? err = null;
                // Always rename when the user explicitly clicked "Apply + Embed + Rename"
                // AutoRenameEnabled only controls AUTOMATIC rename in Batch/Watch operations
                (renamed, newPath, err) = await SafeRenameAsync(targetFile, metadataToWrite, ct);
                if (!renamed && !string.IsNullOrWhiteSpace(err))
                    Log( $"[{DateTime.Now:HH:mm:ss}] Rename skipped: {err}");

                if (renamed && File.Exists(newPath))
                {
                    targetFile.FilePath = newPath;
                    // Only update OriginalFileName in UI if this is still the selected file
                    if (SelectedFile == targetFile)
                        OriginalFileName = targetFile.FileName;

                    var newName = Path.GetFileName(newPath);
                    wasRenamed = !oldName.Equals(newName, StringComparison.OrdinalIgnoreCase);
                    if (wasRenamed)
                    {
                        // Update watch service knownPaths so the old name is forgotten
                        // and the new name is registered — prevents re-detection of old name
                        _watchFolderService.AddKnownPath(newPath);
                        Log( $"[{DateTime.Now:HH:mm:ss}] Renamed: {oldName} → {newName}");
                        RenameSucceeded?.Invoke(this, new RenameEventArgs(oldName, newName));
                    }
                }
                else if (!renamed && !string.IsNullOrWhiteSpace(err))
                {
                    Log( $"[{DateTime.Now:HH:mm:ss}] Rename error: {err}");
                    RenameFailed?.Invoke(this, new RenameEventArgs(oldName, "", err));
                }

                EmbedSucceeded?.Invoke(this, new EmbedEventArgs(targetFile.FileName, wasRenamed));
                // Clear the yellow "new file" highlight after successful embed
                targetFile.IsNewFile = false;
                // Sync the Library entry if it exists
                SyncLibraryEntry(targetFile.FilePath, metadataToWrite);
                // Remove any .vme_* temp rows the Watch Folder may have caught
                EvictTempFileRows();
                // Invalidate duplicate detector hash cache for the modified file
                Services.DuplicateDetectorService.ClearHashCache();
            }
            else
            {
                targetFile.WriteStatus = Models.WriteStatus.Failed;
                if (writeResult.Diagnosis != null)
                {
                    var diag   = writeResult.Diagnosis;
                    var detail = BuildDiagnosticMessage(targetFile.FileName, diag);
                    targetFile.WriteErrorDetail = detail;
                    Log( $"[{DateTime.Now:HH:mm:ss}] WRITE FAILED — {targetFile.FileName}");
                    Log( $"[{DateTime.Now:HH:mm:ss}]   {diag.Reasons.FirstOrDefault()}");
                    if (Settings.VerboseWriteErrors)
                        WriteFailedDetailed?.Invoke(this, (targetFile, detail));
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            Log( $"[{DateTime.Now:HH:mm:ss}] Apply cancelled by user.");
        }
        catch (Exception ex)
        {
            Log( $"[{DateTime.Now:HH:mm:ss}] Apply error: {ex.Message}");
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            targetFile.IsProcessing = false;
            if (targetFile.WriteStatus == Models.WriteStatus.Untouched)
                targetFile.WriteStatus = ok ? Models.WriteStatus.Success : Models.WriteStatus.Failed;
            targetFile.StatusMessage = ok ? (wasRenamed ? "Renamed" : "Embedded") : (ct.IsCancellationRequested ? "Cancelled" : "Error");
            EndOperation(resetProgress: false);
        }
    }

    /// <summary>
    /// Renames the currently selected single file using the current rename pattern
    /// WITHOUT embedding any metadata. Tags are not touched.
    /// </summary>
    private async Task RenameCurrentFileAsync()
    {
        if (SelectedFile == null) return;

        var targetFile = SelectedFile;
        var meta       = EditingMetadata.Clone();
        var ct         = BeginOperation();
        IsBusy         = true;
        StatusText     = "Renaming…";

        var resolution = MediaInfo?.ResolutionDisplay ?? string.Empty;
        var fmt        = targetFile.Extension ?? string.Empty;

        var pattern = meta.IsEpisode ? Settings.TvRenamePattern : Settings.RenamePattern;
        var (success, newPath, error) = await _renameService.RenameFileAsync(
            targetFile.FilePath, pattern, meta, resolution, fmt, ct);

        if (success)
        {
            var oldName = Path.GetFileName(targetFile.FilePath);
            var newName = Path.GetFileName(newPath);
            targetFile.FilePath = newPath;
            StatusText = $"✓ Renamed: {oldName} → {newName}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Renamed: {oldName} → {newName}");
            RenameSucceeded?.Invoke(this, new RenameEventArgs(targetFile.FilePath, newPath));

            // Refresh the display name shown in the Files panel
            RaiseProperty(nameof(OriginalFileName));
        }
        else
        {
            StatusText = $"Rename failed: {error}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Rename failed: {error}");
        }

        EndOperation(resetProgress: false);
    }

    /// <summary>
    /// Renames all selected files using the current rename pattern
    /// WITHOUT embedding any metadata. Tags are not touched.
    /// </summary>
    private async Task BatchRenameOnlyAsync()
    {
        var targets = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (targets.Count == 0) return;

        var ct = BeginOperation();
        IsBusy     = true;
        StatusText = $"Renaming {targets.Count} file(s)…";
        ProgressValue = 0;

        int done = 0, succeeded = 0, failed = 0;
        foreach (var file in targets)
        {
            ct.ThrowIfCancellationRequested();
            var meta = file.EmbeddedMetadata;

            // Fall back to parsing title/year from filename if no embedded metadata
            if (string.IsNullOrWhiteSpace(meta.Title))
            {
                var (parsedTitle, parsedYear) = FilenameParser.Parse(file.FileNameNoExt);
                meta = meta.Clone();
                if (string.IsNullOrWhiteSpace(meta.Title)) meta.Title = parsedTitle;
                if (string.IsNullOrWhiteSpace(meta.Year))  meta.Year  = parsedYear;
            }

            // Snapshot original path so rename can be undone per-file
            file.UndoFilePath = file.FilePath;
            file.UndoMetadata = file.EmbeddedMetadata.Clone();

            var (success, newPath, error) = await SafeRenameAsync(
                file, meta, ct, isFromBatch: true);

            if (success)
            {
                file.FilePath = newPath;
                succeeded++;
            }
            else
            {
                // Rename failed — clear snapshot so undo isn't offered falsely
                file.UndoFilePath = null;
                file.UndoMetadata = null;
                file.StatusMessage = $"Rename failed: {error}";
                file.HasError      = true;
                failed++;
            }

            done++;
            ProgressValue = (int)(done * 100.0 / targets.Count);
            StatusText = $"Renaming {done}/{targets.Count}…";
        }

        IsBusy     = false;
        ProgressValue = 100;
        StatusText = failed == 0
            ? $"✓ Renamed {succeeded} file(s)."
            : $"Renamed {succeeded} file(s) — {failed} failed.";
        Log( $"[{DateTime.Now:HH:mm:ss}] Batch rename: {succeeded} succeeded, {failed} failed.");
        RaiseProperty(nameof(UndoLastEmbedCommand));
        EndOperation(resetProgress: false);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SAFE RENAME HELPER  (Build 80 — prevents wrong-pattern rename)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps RenameFileAsync with type-mismatch detection.
    ///
    /// Problem: BatchProcess uses meta.IsEpisode to choose the rename pattern.
    /// If TMDB returns a Series result for a movie file (ambiguous title match),
    /// the movie gets renamed with S##E## tokens from the TV pattern — breaking it.
    ///
    /// Detection: if the metadata says IsEpisode=true but FilenameParser finds
    /// no S##E## code in the original filename, the file is almost certainly a movie
    /// being misidentified. We check ConfirmTypeMismatchRename and either:
    ///   - Skip the rename and log a warning (safe default)
    ///   - Proceed if user has disabled the check
    /// </summary>
    private async Task<(bool renamed, string newPath, string? err)> SafeRenameAsync(
        VideoFile vf,
        MovieMetadata meta,
        CancellationToken ct,
        bool isFromBatch = false)
    {
        // AutoRenameEnabled only blocks AUTOMATIC renames (batch/watch folder)
        // Never blocks explicit button clicks (isFromBatch=false)
        if (!Settings.AutoRenameEnabled && isFromBatch)
            return (false, vf.FilePath, "Auto-rename is disabled in Settings.");

        var pattern   = meta.IsEpisode ? Settings.TvRenamePattern : Settings.RenamePattern;
        var res       = string.Empty;
        var fmt       = vf.Extension ?? string.Empty;

        // ── Type mismatch check ────────────────────────────────────────────────
        if (Settings.ConfirmTypeMismatchRename && isFromBatch)
        {
            // 1. TMDB says episode but filename has no S##E## → likely a movie
            bool metaSaysEpisode  = meta.IsEpisode;
            bool fileHasEpCode    = FilenameParser.ParseEpisode(
                Path.GetFileNameWithoutExtension(vf.FileName)).HasValue;
            bool fileHasMovieYear = !string.IsNullOrWhiteSpace(
                FilenameParser.Parse(Path.GetFileNameWithoutExtension(vf.FileName)).year);

            if (metaSaysEpisode && !fileHasEpCode)
            {
                // TMDB returned a series for what looks like a movie file
                // Override IsEpisode to false — use the movie pattern instead
                var corrected = meta.Clone();
                corrected.IsEpisode = false;
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠ Type mismatch: '{vf.FileName}' has no episode code " +
                    $"but TMDB returned a series result ('{meta.ShowTitle ?? meta.Title}'). " +
                    $"Using movie pattern instead. Check the metadata manually.");
                return await _renameService.RenameFileAsync(
                    vf.FilePath, Settings.RenamePattern, corrected, res, fmt, ct);
            }

            if (!metaSaysEpisode && fileHasEpCode)
            {
                // TMDB returned a movie for what looks like a TV episode file.
                // BLOCK the rename entirely — do not apply movie pattern to episode files.
                // The file keeps its original name; the user should use 📺 TV Batch instead.
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⛔ Rename blocked: '{vf.FileName}' has an episode " +
                    $"code (S##E##) but TMDB returned a movie result ('{meta.Title}'). " +
                    $"Metadata embedded but file NOT renamed. Use 📺 TV Batch for episode files.");
                // Return success=false to block rename (metadata was already embedded above)
                return (false, vf.FilePath,
                    $"Rename blocked: episode file cannot use movie rename pattern. " +
                    $"Use 📺 TV Batch for correct episode naming.");
            }
        }

        return await _renameService.RenameFileAsync(vf.FilePath, pattern, meta, res, fmt, ct);
    }

    private async Task BatchProcessAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;
        var ct = BeginOperation();
        IsBusy = true;
        int done = 0, successCount = 0, failCount = 0, renamedCount = 0;

        var progress = new System.Progress<string>(msg =>
        {
            Log( $"[{DateTime.Now:HH:mm:ss}] {msg}");
            StatusText = msg;
        });

        // ── Phase 1: Simultaneously auto-search all files missing metadata ─────
        var needsSearch = selected.Where(vf => vf.RetrievedMetadata == null
                                            && !string.IsNullOrWhiteSpace(vf.ParsedTitle)).ToList();

        if (needsSearch.Any())
        {
            StatusText = $"Auto-searching metadata for {needsSearch.Count} file(s)…";
            ((IProgress<string>)progress).Report($"Starting simultaneous metadata search for {needsSearch.Count} file(s)…");

            int searchDone = 0;
            // TMDB rate limit: 40 requests / 10 seconds per API key.
            // 3 concurrent slots × ~350ms minimum gap = ~8.5 req/s — well inside the
            // limit. The 350ms gap is enforced by a short Task.Delay before each
            // semaphore release so slots don't immediately re-fire.
            var searchSemaphore = new SemaphoreSlim(3);
            var searchTasks = needsSearch.Select(async vf =>
            {
                await searchSemaphore.WaitAsync(ct);
                try
                {
                    vf.StatusMessage = "Searching…";
                    // Detect TV files from filename episode code — routes to correct endpoint
                    // even for files with no embedded metadata (raw torrent files)
                    var epInfo = Services.FilenameParser.ParseEpisode(
                        Path.GetFileNameWithoutExtension(vf.FileName));

                    string batchTitle;
                    if (epInfo.HasValue)
                    {
                        // TV episode — set a clean show title and mark IsEpisode
                        // via a temporary local flag rather than mutating EmbeddedMetadata
                        // (mutation would corrupt state if the search finds no result)
                        batchTitle = epInfo.Value.showTitle;
                        // Temporarily set IsEpisode so AutoSearchAsync routes to TV endpoint.
                        // This is safe: ApplyToFileAsync overwrites EmbeddedMetadata on success,
                        // and AutoSearchAsync overwrites it with retrieved metadata on success.
                        if (!vf.EmbeddedMetadata.IsEpisode)
                        {
                            vf.EmbeddedMetadata.IsEpisode = true;
                            vf.EmbeddedMetadata.Season    = epInfo.Value.season;
                            vf.EmbeddedMetadata.Episode   = epInfo.Value.episode;
                        }
                    }
                    else
                    {
                        batchTitle = vf.EmbeddedMetadata.IsEpisode && !string.IsNullOrWhiteSpace(vf.EmbeddedMetadata.ShowTitle)
                            ? vf.EmbeddedMetadata.ShowTitle
                            : vf.ParsedTitle;
                    }
                    await AutoSearchAsync(vf, batchTitle, epInfo.HasValue ? string.Empty : vf.ParsedYear, calledFromBatch: true, ct: ct);
                    vf.StatusMessage = vf.RetrievedMetadata != null ? "Found" : "Not found";
                }
                finally
                {
                    // Minimum gap between requests per slot — prevents TMDB 429
                    await Task.Delay(350, ct).ConfigureAwait(false);
                    searchSemaphore.Release();
                    Interlocked.Increment(ref searchDone);
                    // Phase 1 uses 0-50% of the progress bar
                    ProgressValue = (int)((double)searchDone / needsSearch.Count * 50);
                    StatusText = $"Searching metadata… {searchDone} / {needsSearch.Count}";
                }
            });

            await Task.WhenAll(searchTasks);
            ((IProgress<string>)progress).Report($"Metadata search complete — {needsSearch.Count(vf => vf.RetrievedMetadata != null)} matched.");
        }

        // ── Phase 2: Embed + rename all selected files concurrently ───────────
        // Resolve write worker count: Auto = tuned to drive + CPU, Manual = user setting
        int writeWorkers;
        if (Settings.AutoConcurrentProcessing)
        {
            var samplePath = selected.First().FilePath;
            writeWorkers   = Services.WriteThreadService.RecommendedWorkers(samplePath);
            Log(
                $"[{DateTime.Now:HH:mm:ss}] Auto concurrency: {Services.WriteThreadService.Describe(samplePath)}");
        }
        else
        {
            writeWorkers = Math.Max(1, Settings.MaxConcurrentProcessing);
            Log(
                $"[{DateTime.Now:HH:mm:ss}] Manual concurrency: {writeWorkers} worker(s)");
        }

        StatusText = $"Processing {selected.Count} file(s) — {writeWorkers} concurrent worker(s)…";
        var processSemaphore = new SemaphoreSlim(writeWorkers);

        var tasks = selected.Select(async vf =>
        {
            await processSemaphore.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) { vf.StatusMessage = "Cancelled"; return; }
                vf.IsProcessing = true;
                // Use retrieved metadata if available; fall back to pending (edited) metadata
                var meta = vf.RetrievedMetadata ?? vf.PendingMetadata;

                // Ensure artwork is populated for batch files that were never selected
                // (lazy load skips unselected files; WriteMetadataDetailedAsync also does
                //  this internally, but syncing here keeps the UI thumbnail correct too)
                if (meta.ArtworkBytes is not { Length: > 0 }
                    && vf.EmbeddedMetadata.ArtworkBytes is not { Length: > 0 })
                {
                    var existingArt = await Task.Run(() =>
                        _metadataService.ReadArtworkOnly(vf.FilePath), ct);
                    if (existingArt is { Length: > 0 })
                    {
                        vf.EmbeddedMetadata.ArtworkBytes = existingArt;
                        vf.PendingMetadata.ArtworkBytes  = existingArt;
                        if (meta == vf.PendingMetadata) meta.ArtworkBytes = existingArt;
                    }
                }
                else if (meta.ArtworkBytes is not { Length: > 0 }
                         && vf.EmbeddedMetadata.ArtworkBytes is { Length: > 0 })
                {
                    // Use already-loaded embedded artwork
                    meta.ArtworkBytes = vf.EmbeddedMetadata.ArtworkBytes;
                }

                if (ct.IsCancellationRequested) { vf.StatusMessage = "Cancelled"; return; }
                var writeResult = await _metadataService.WriteMetadataDetailedAsync(
                    vf.FilePath, meta, Settings, progress, ct: ct);
                var ok = writeResult.Success;

                if (ok)
                {
                    // ── Snapshot BEFORE rename so undo can restore both path and tags ──
                    vf.UndoFilePath = vf.FilePath;
                    vf.UndoMetadata = vf.EmbeddedMetadata.Clone();

                    vf.WriteStatus = Models.WriteStatus.Success;
                    vf.IsNewFile   = false; // clear yellow highlight on successful embed
                    var oldName = Path.GetFileName(vf.FilePath);
                    var (renamed, newPath, err) = await SafeRenameAsync(
                        vf, meta, ct, isFromBatch: true);

                    if (renamed && File.Exists(newPath))
                    {
                        vf.FilePath = newPath;
                        // Register renamed path so Watch Folder poll doesn't re-detect it
                        _watchFolderService.AddKnownPath(newPath);
                        if (!Path.GetFileName(newPath).Equals(oldName, StringComparison.OrdinalIgnoreCase))
                            Interlocked.Increment(ref renamedCount);
                    }

                    Interlocked.Increment(ref successCount);
                }
                else
                {
                    vf.WriteStatus = Models.WriteStatus.Failed;
                    if (writeResult.Diagnosis != null)
                    {
                        vf.WriteErrorDetail = BuildDiagnosticMessage(vf.FileName, writeResult.Diagnosis);
                        Log( $"[{DateTime.Now:HH:mm:ss}] WRITE FAILED — {vf.FileName}: {writeResult.Diagnosis.Reasons.FirstOrDefault()}");
                    }
                    Interlocked.Increment(ref failCount);
                }

                vf.StatusMessage = ok ? "Done" : "Error";
            }
            finally
            {
                vf.IsProcessing = false;
                processSemaphore.Release();
                Interlocked.Increment(ref done);
                ProgressValue = (int)((double)done / selected.Count * 100);
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { /* individual tasks already handle cancellation */ }
        finally
        {
            var cancelled = ct.IsCancellationRequested;
            StatusText = cancelled
                ? $"Batch cancelled — {successCount} succeeded, {failCount} failed, {renamedCount} renamed."
                : $"Batch complete — {successCount} succeeded, {failCount} failed, {renamedCount} renamed.";
            EndOperation();
        }

        RaiseProperty(nameof(HasAnyFailure));
        BatchCompleted?.Invoke(this, new BatchEventArgs(successCount, failCount, renamedCount));

        // Evict any .vme_tmp_* or .vme_bak_* files that the Watch Folder may have
        // picked up during the write cycle — they no longer exist on disk after
        // a successful atomic replace.
        EvictTempFileRows();
    }

    // ── Artwork ───────────────────────────────────────────────────────────────

    private void CopyArtworkToClipboard()
    {
        var bytes = EditingMetadata?.ArtworkBytes;
        if (bytes is not { Length: > 0 }) return;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            System.Windows.Clipboard.SetImage(bmp);
            StatusText = "✓ Cover art copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Clipboard copy failed: {ex.Message}";
        }
    }

    public void PickArtwork()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        SetArtworkFromFile(dlg.FileName);
    }

    public void SetArtworkFromFile(string filePath)
    {
        try
        {
            var bytes = System.IO.File.ReadAllBytes(filePath);
            EditingMetadata.ArtworkBytes = bytes;
            UpdateArtworkDisplay(bytes);
        }
        catch (Exception ex)
        {
            StatusText = $"Artwork error: {ex.Message}";
        }
    }

    public void SetArtworkFromBytes(byte[] bytes)
    {
        EditingMetadata.ArtworkBytes = bytes;
        UpdateArtworkDisplay(bytes);
    }

    private void UpdateArtworkDisplay(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            ArtworkImage = null;
            return;
        }
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new System.IO.MemoryStream(bytes);
            img.CacheOption  = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            ArtworkImage = img;
        }
        catch { ArtworkImage = null; }
    }

    // ── Theme ─────────────────────────────────────────────────────────────────

    // ── Write failure diagnostic message builder ──────────────────────────────

    private static string BuildDiagnosticMessage(
        string fileName, Services.WriteDiagnosis diag)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Could not write metadata to: {fileName}");
        sb.AppendLine();
        sb.AppendLine("─── Why it failed ───");
        foreach (var r in diag.Reasons)
            sb.AppendLine($"  • {r}");
        sb.AppendLine();
        sb.AppendLine("─── What you can do ───");
        foreach (var o in diag.Options)
            sb.AppendLine($"  • {o}");
        sb.AppendLine();
        sb.AppendLine($"Diagnostic category: {diag.Category}");
        return sb.ToString();
    }

    private void ToggleTheme()
    {
        App.ToggleTheme();
        IsDark = App.IsDarkTheme;
    }

    // ── Fallback application (Settings → Write Error Diagnostics) ────────────

    /// <summary>
    /// Display name of the selected fallback application — file name without extension,
    /// or empty string when none is set. Shown in buttons and labels throughout the UI.
    /// </summary>
    // ── Move/Copy saved-settings proxy properties ────────────────────────────
    // Settings.LastCopyDestination and Settings.LastConflictMode are nested
    // sub-properties — WPF cannot observe changes on them directly.
    // These proxies raise PropertyChanged so the Settings tab refreshes
    // immediately when values are saved or cleared.

    public string SavedCopyDestination
    {
        get => Settings.LastCopyDestination;
        set { Settings.LastCopyDestination = value; RaiseProperty(); }
    }

    public string SavedConflictMode
    {
        get => Settings.LastConflictMode;
        set { Settings.LastConflictMode = value; RaiseProperty(); }
    }

    // ── Smart Move/Copy proxy properties ─────────────────────────────────────
    public bool SmartOrganiseEnabled
    {
        get => Settings.SmartOrganiseEnabled;
        set
        {
            Settings.SmartOrganiseEnabled = value;
            RaiseProperty();
            RaiseProperty(nameof(FastCopyAllowed));
            RaiseProperty(nameof(AutoEngineAllowed));
            // If smart organise is enabled, force engine to Built-in
            if (value && Settings.PreferredCopyEngine != "Built-in")
            {
                PreferredCopyEngine = "Built-in";   // proxy setter raises PropertyChanged + saves
            }
            _ = App.ConfigService.SaveAsync();
        }
    }

    /// <summary>
    /// FastCopy is only available when it is installed AND Smart Organisation is off.
    /// Smart Organisation requires per-file routing which FastCopy cannot support.
    /// </summary>
    /// <summary>
    /// Proxy for Settings.PreferredCopyEngine so XAML bindings refresh
    /// when SmartOrganiseEnabled forces the engine to Built-in.
    /// </summary>
    public string PreferredCopyEngine
    {
        get => Settings.PreferredCopyEngine;
        set { Settings.PreferredCopyEngine = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    public bool FastCopyAllowed  => FastCopyDetected && !Settings.SmartOrganiseEnabled;

    /// <summary>Preview path shown in the Smart Organisation banner — no double backslash.</summary>
    public string SmartMoviesPreviewPath
    {
        get
        {
            var dest   = (CopyDestination ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var folder = string.IsNullOrWhiteSpace(Settings.MoviesFolderName)
                ? "" : Settings.MoviesFolderName;
            return string.IsNullOrWhiteSpace(folder)
                ? dest + System.IO.Path.DirectorySeparatorChar
                : System.IO.Path.Combine(dest, folder) + System.IO.Path.DirectorySeparatorChar;
        }
    }

    public string SmartTvPreviewPath
    {
        get
        {
            var dest   = (CopyDestination ?? "").TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var folder = string.IsNullOrWhiteSpace(Settings.TvShowsFolderName)
                ? "TV Shows" : Settings.TvShowsFolderName;
            // No year in TV show folder — prevents fragmentation when seasons
            // have different years or year is missing from some files.
            return System.IO.Path.Combine(dest, folder) + @"\{Show}\Season ##";
        }
    }

    /// <summary>
    /// Auto mode is only useful when FastCopy is available and Smart Organisation is off.
    /// </summary>
    public bool AutoEngineAllowed => FastCopyDetected && !Settings.SmartOrganiseEnabled;
    public string MoviesFolderName
    {
        get => Settings.MoviesFolderName;
        set
        {
            Settings.MoviesFolderName = string.IsNullOrWhiteSpace(value) ? "Movies" : value.Trim();
            RaiseProperty();
            RaiseProperty(nameof(SmartMoviesPreviewPath));
            _ = App.ConfigService.SaveAsync();
        }
    }
    public bool LibraryTabbedMode
    {
        get => Settings.LibraryTabbedMode;
        set
        {
            Settings.LibraryTabbedMode = value;
            RaiseProperty();
            if (value)
            {
                // Enabling: build/rebuild tabs from current entries
                if (LibraryEntries.Any()) RebuildLibraryTabs();
            }
            else
            {
                // Disabling: collapse tab bar regardless of folder count.
                // HasMultipleTabs now = TabbedMode && Count>=1, so just
                // raising HasMultipleTabs is enough to hide the tab bar.
                // Keep LibraryTabs populated so re-enabling is instant.
                _selectedLibraryTab = null;
                RaiseProperty(nameof(SelectedLibraryTab));
            }
            RaiseProperty(nameof(HasMultipleTabs));
            LibraryView?.Refresh();
            _ = App.ConfigService.SaveAsync();
        }
    }

    public bool LibraryTvTreeView
    {
        get => Settings.LibraryTvTreeView;
        set
        {
            Settings.LibraryTvTreeView = value;
            RaiseProperty();
            RaiseProperty(nameof(LibraryShowDataGrid));
            RaiseProperty(nameof(LibraryShowTreeView));
            _ = App.ConfigService.SaveAsync();
        }
    }
    public bool LibraryShowDataGrid => !Settings.LibraryTvTreeView || !ActiveTabIsTv;
    public bool LibraryShowTreeView  =>  Settings.LibraryTvTreeView &&  ActiveTabIsTv;

    // ── TV Tree filter and sort ────────────────────────────────────────────────
    private string _tvTreeFilter = string.Empty;
    public string TvTreeFilter
    {
        get => _tvTreeFilter;
        set { Set(ref _tvTreeFilter, value); RaiseProperty(nameof(TvShowTree)); }
    }

    private string _tvTreeSort = "AZ";  // AZ | EpisodeCount | RecentlyAdded
    public string TvTreeSort
    {
        get => _tvTreeSort;
        set { Set(ref _tvTreeSort, value); RaiseProperty(nameof(TvShowTree)); }
    }

    public System.Collections.ObjectModel.ObservableCollection<VideoMetadataEditor.Models.TvShowNode> TvShowTree
    {
        get
        {
            // Tab filter
            IEnumerable<Models.LibraryEntry> source = LibraryEntries;
            if (Settings.LibraryTabbedMode && _selectedLibraryTab != null)
            {
                var tabPath = _selectedLibraryTab.FolderPath;
                source = LibraryEntries.Where(e =>
                    e.FilePath.StartsWith(tabPath, StringComparison.OrdinalIgnoreCase));
            }

            var episodes = source.Where(e => e.IsEpisode).ToList();

            // Build nodes
            var grouped = episodes
                .GroupBy(e => string.IsNullOrWhiteSpace(e.ShowTitle) ? e.Title : e.ShowTitle);

            // Search filter
            if (!string.IsNullOrWhiteSpace(_tvTreeFilter))
            {
                var f = _tvTreeFilter.ToLowerInvariant();
                grouped = grouped.Where(g => g.Key.ToLowerInvariant().Contains(f));
            }

            var nodes = grouped.Select(sg =>
            {
                var seasons = sg.GroupBy(e => e.Season ?? 0).OrderBy(s => s.Key)
                    .Select(ss =>
                    {
                        var orderedEps  = ss.OrderBy(e => e.Episode ?? 0).ToList();
                        // Scan folder for files with S##E## codes that aren't in LibraryEntries
                        // (files with wrong/missing metadata show as "untagged" not "missing")
                        var knownPhysical = new HashSet<int>();
                        var seasonFolders = orderedEps
                            .Select(e => Path.GetDirectoryName(e.FilePath))
                            .Where(d => d != null).Distinct();
                        var seasonNum = orderedEps[0].Season ?? 0;
                        foreach (var dir in seasonFolders)
                        {
                            if (string.IsNullOrWhiteSpace(dir)) continue;
                            foreach (var f in Directory.EnumerateFiles(dir))
                            {
                                var baseName = Path.GetFileNameWithoutExtension(f);
                                var epM = System.Text.RegularExpressions.Regex.Match(baseName,
                                    $@"(?i)[Ss]{seasonNum:D2}[Ee](\d{{1,3}})");
                                if (epM.Success && int.TryParse(epM.Groups[1].Value, out var epNum))
                                    knownPhysical.Add(epNum);
                            }
                        }
                        var withGaps = InsertMissingEpisodes(orderedEps, knownPhysical);
                        return new VideoMetadataEditor.Models.TvSeasonNode
                        {
                            SeasonNumber = ss.Key,
                            SeasonLabel  = ss.Key == 0 ? "Specials" : $"Season {ss.Key:D2}",
                            Episodes     = withGaps
                        };
                    }).ToList();

                return new VideoMetadataEditor.Models.TvShowNode
                {
                    ShowTitle = sg.Key,
                    CoverArt  = sg.FirstOrDefault(e => e.CoverArt != null)?.CoverArt,
                    Seasons   = seasons
                };
            }).ToList();

            // Sort
            nodes = _tvTreeSort switch
            {
                "EpisodeCount"  => nodes.OrderByDescending(n => n.EpisodeCount).ToList(),
                "RecentlyAdded" => nodes.OrderByDescending(n =>
                    n.Seasons.SelectMany(s => s.Episodes)
                        .Where(e => !e.IsMissingEpisode)
                        .Max(e => (DateTime?)e.DownloadDate) ?? DateTime.MinValue).ToList(),
                _ => nodes.OrderBy(n => n.ShowTitle).ToList()
            };

            return new(nodes);
        }
    }

    /// <summary>
    /// Inserts placeholder LibraryEntry rows for missing episode numbers so
    /// gaps are visible in the tree (e.g. E01, E02, [E03 missing], E04).
    /// </summary>
    /// <summary>
    /// Fills sequence gaps with placeholder "missing" entries.
    /// Also accepts a set of knownPhysicalEpisodes — episode numbers parsed from
    /// physical files in the same folder that exist but have wrong/missing metadata.
    /// Those are shown as "⚠ exists but untagged" rather than purely missing.
    /// </summary>
    private static List<Models.LibraryEntry> InsertMissingEpisodes(
        List<Models.LibraryEntry> ordered,
        HashSet<int>? knownPhysicalEpisodes = null)
    {
        if (ordered.Count < 2) return ordered;

        var result = new List<Models.LibraryEntry>();
        int prev = ordered[0].Episode ?? 0;
        result.Add(ordered[0]);

        for (int i = 1; i < ordered.Count; i++)
        {
            int cur = ordered[i].Episode ?? 0;
            if (cur - prev > 10)
            {
                // Large gap — show a single summary placeholder instead of many rows
                result.Add(new Models.LibraryEntry
                {
                    IsEpisode        = true,
                    ShowTitle        = ordered[0].ShowTitle,
                    Season           = ordered[0].Season,
                    Episode          = prev + 1,
                    EpisodeTitle     = $"({cur - prev - 1} episode(s) missing)",
                    IsMissingEpisode = true,
                    FilePath         = string.Empty
                });
            }
            for (int ep = prev + 1; ep < cur && cur - prev <= 10; ep++)
            {
                // Check if a physical file exists with this episode number
                bool physicalExists = knownPhysicalEpisodes?.Contains(ep) == true;
                result.Add(new Models.LibraryEntry
                {
                    IsEpisode        = true,
                    ShowTitle        = ordered[0].ShowTitle,
                    Season           = ordered[0].Season,
                    Episode          = ep,
                    // Distinguish "file exists but untagged" from "file truly missing"
                    EpisodeTitle     = physicalExists ? "(untagged — embed metadata)" : "(missing)",
                    IsMissingEpisode = true,
                    FilePath         = string.Empty
                });
            }
            result.Add(ordered[i]);
            prev = cur;
        }
        return result;
    }

    // ── Smart File Rename Pattern ─────────────────────────────────────────────
    /// <summary>Movie rename pattern — used when IsEpisode is false.</summary>
    public string MovieRenamePattern
    {
        get => Settings.RenamePattern;
        set { Settings.RenamePattern = value; RaiseProperty(); UpdateRenamePreview(); _ = App.ConfigService.SaveAsync(); }
    }

    /// <summary>TV show rename pattern — used when IsEpisode is true.</summary>
    public string TvRenamePattern
    {
        get => Settings.TvRenamePattern;
        set { Settings.TvRenamePattern = value; RaiseProperty(); UpdateRenamePreview(); _ = App.ConfigService.SaveAsync(); }
    }

    /// <summary>Active pattern based on current file type.</summary>
    public string ActiveRenamePattern => IsEpisodeMode ? Settings.TvRenamePattern : Settings.RenamePattern;

    public ObservableCollection<string> MovieRenamePresets => new(new[]
    {
        "{Title} ({Year})",
        "{Title} ({Year}) [{MPA}]",
        "{Title} ({Year}) [{Resolution}]",
        "{Title} ({Year}) [{Resolution}] [{MPA}]",
        "{Title} - {Director} ({Year})",
        "{Title} ({Year}) {Rating}",
    });

    public ObservableCollection<string> TvRenamePresets => new(new[]
    {
        "{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}",
        "{ShowTitle} S{Season}E{Episode}",
        "{ShowTitle} - S{Season}E{Episode}",
        "{ShowTitle} - {Season}x{Episode} - {EpisodeTitle}",
        "{ShowTitle} Season {Season} Episode {Episode} - {EpisodeTitle}",
    });

    public string TvShowsFolderName
    {
        get => Settings.TvShowsFolderName;
        set
        {
            Settings.TvShowsFolderName = string.IsNullOrWhiteSpace(value) ? "TV Shows" : value.Trim();
            RaiseProperty();
            RaiseProperty(nameof(SmartTvPreviewPath));
            _ = App.ConfigService.SaveAsync();
        }
    }
    public bool CreateSeasonSubfolders
    {
        get => Settings.CreateSeasonSubfolders;
        set { Settings.CreateSeasonSubfolders = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    // ── Untagged file handling proxies ───────────────────────────────────────
    // These were previously bound as {Binding UntaggedFileHandling} which resolved
    // to nothing on MainViewModel and silently lost the user's selection.
    public string UntaggedFileHandling
    {
        get => Settings.UntaggedFileHandling;
        set
        {
            if (Settings.UntaggedFileHandling == value) return;
            Settings.UntaggedFileHandling = value;
            RaiseProperty();
            _ = App.ConfigService.SaveAsync();
        }
    }

    public string UnsortedFolderName
    {
        get => Settings.UnsortedFolderName;
        set
        {
            if (Settings.UnsortedFolderName == value) return;
            Settings.UnsortedFolderName = value;
            RaiseProperty();
            _ = App.ConfigService.SaveAsync();
        }
    }

    public string DuplicateExcludedPatterns
    {
        get => Settings.DuplicateExcludedPatterns;
        set
        {
            if (Settings.DuplicateExcludedPatterns == value) return;
            Settings.DuplicateExcludedPatterns = value;
            RaiseProperty();
            _ = App.ConfigService.SaveAsync();
        }
    }

    /// <summary>
    /// Proxy for Settings.FallbackAppPath so WPF binding refreshes
    /// when BrowseFallbackApp() writes the new path.
    /// </summary>
    public string FallbackAppPath
    {
        get => Settings.FallbackAppPath;
        set { Settings.FallbackAppPath = value; RaiseProperty(); RaiseProperty(nameof(FallbackAppName)); RaiseProperty(nameof(HasFallbackApp)); }
    }

    public string FallbackAppName =>
        string.IsNullOrWhiteSpace(Settings.FallbackAppPath)
            ? string.Empty
            : System.IO.Path.GetFileNameWithoutExtension(Settings.FallbackAppPath);

    /// <summary>True when a valid fallback application has been configured.</summary>
    public bool HasFallbackApp =>
        !string.IsNullOrWhiteSpace(Settings.FallbackAppPath)
        && System.IO.File.Exists(Settings.FallbackAppPath);

    private void BrowseFallbackApp()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select fallback application (e.g. TagScanner, MediaInfo Lite)",
            Filter = "Executables and Files (*.exe;*.xlsx;*.bat)|*.exe;*.xlsx;*.bat|All Files|*.*"
        };

        if (!string.IsNullOrWhiteSpace(Settings.FallbackAppPath))
        {
            var dir = System.IO.Path.GetDirectoryName(Settings.FallbackAppPath);
            if (dir != null && System.IO.Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }

        if (dlg.ShowDialog() != true) return;

        FallbackAppPath = dlg.FileName;   // proxy setter raises all bindings
        _ = App.ConfigService.SaveAsync();
    }

    private void ClearFallbackApp()
    {
        FallbackAppPath = string.Empty;   // proxy setter raises all bindings
        _ = App.ConfigService.SaveAsync();
    }

    private void SaveCopySettings()
    {
        Settings.LastCopyDestination = CopyDestination;
        Settings.LastConflictMode    = ConflictMode;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(SavedCopyDestination));
        RaiseProperty(nameof(SavedConflictMode));
        RaiseProperty(nameof(ClearSavedCopyDestCommand));
        StatusText = $"✓ Move/Copy settings saved — destination: {(string.IsNullOrWhiteSpace(CopyDestination) ? "(none)" : CopyDestination)}, conflict mode: {ConflictMode}";
        Log( $"[{DateTime.Now:HH:mm:ss}] 💾 Move/Copy settings saved.");
    }

    private void ClearSavedCopyDest()
    {
        Settings.LastCopyDestination = string.Empty;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(SavedCopyDestination));
        RaiseProperty(nameof(ClearSavedCopyDestCommand));
        StatusText = "Move/Copy saved destination cleared.";
    }

    /// <summary>
    /// Launches the configured fallback application with the given file path as its argument.
    /// Handles both EXE (launched directly) and non-EXE files (opened via shell association —
    /// e.g. an .xlsx report template opens in Excel).
    /// </summary>
    public void LaunchFallbackApp(string filePath)
    {
        if (!HasFallbackApp) return;
        try
        {
            var app = Settings.FallbackAppPath;
            var ext = System.IO.Path.GetExtension(app).ToLowerInvariant();

            var psi = ext == ".exe"
                ? new System.Diagnostics.ProcessStartInfo(app, "\"" + filePath + "\"")
                    { UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(app)
                    { UseShellExecute = true };

            System.Diagnostics.Process.Start(psi);
            Log(
                $"[{DateTime.Now:HH:mm:ss}] 🔧 Opened in {FallbackAppName}: {System.IO.Path.GetFileName(filePath)}");
        }
        catch (Exception ex)
        {
            StatusText = $"Could not launch {FallbackAppName}: {ex.Message}";
            Log(
                $"[{DateTime.Now:HH:mm:ss}] ✕ Fallback app launch failed: {ex.Message}");
        }
    }

    // ── Rename Preview ────────────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _renamePreviewDebounce;

    private void UpdateRenamePreview()
    {
        _renamePreviewDebounce?.Stop();
        _renamePreviewDebounce = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(150) };
        _renamePreviewDebounce.Tick += (_, _) =>
        {
            _renamePreviewDebounce.Stop();
            var ext        = SelectedFile?.Extension?.ToLowerInvariant() ?? "mp4";
            var resolution = MediaInfo?.ResolutionDisplay ?? string.Empty;
            var fmt        = SelectedFile?.Extension ?? string.Empty;
            var pattern    = IsEpisodeMode ? Settings.TvRenamePattern : Settings.RenamePattern;
            RenamePreview  = _renameService.Preview(pattern, EditingMetadata, "." + ext, resolution, fmt);
        };
        _renamePreviewDebounce.Start();
    }

    // ── Key Validation ────────────────────────────────────────────────────────

    private async Task ValidateKeysAsync()
    {
        IsBusy = true;
        StatusText = "Validating API keys…";

        // Reset flash state
        TmdbKeyValid = null;
        OmdbKeyValid = null;

        var tmdb = await _apiService.ValidateTmdbKeyAsync(TmdbKey);
        var omdb = await _apiService.ValidateOmdbKeyAsync(OmdbKey);

        // Flash results
        TmdbKeyValid = tmdb;
        OmdbKeyValid = omdb;

        // Lock fields that passed and persist state for next launch
        if (tmdb)
        {
            TmdbKeyLocked = true;
            Settings.TmdbKeyValidated = true;
        }
        if (omdb)
        {
            OmdbKeyLocked = true;
            Settings.OmdbKeyValidated = true;
        }

        // Save valid keys immediately
        _ = App.ConfigService.SaveAsync();

        StatusText = $"TMDB: {(tmdb ? "✓ Valid" : "✗ Invalid")}  ·  OMDB: {(omdb ? "✓ Valid" : "✗ Invalid")}";

        // Auto-clear flash indicators after 4 seconds
        // Use CancellationToken.None so key display doesn't vanish on cancel
        try { await Task.Delay(4000); } catch (OperationCanceledException) { /* ok */ }
        TmdbKeyValid = null;
        OmdbKeyValid = null;

        EndOperation(resetProgress: false);
    }

    // ── Watch Folder (delegated to WatchFolderService) ──────────────────────

    public void ApplyWatchFolderSetting()
    {
        _watchFolderService.Stop();
        if (!Settings.WatchFolderEnabled || string.IsNullOrWhiteSpace(Settings.LastFolderPath))
            return;
        var existingPaths = Files.Where(f => !f.IsSeparator).Select(f => f.FilePath);
        _watchFolderService.Start(Settings.LastFolderPath, existingPaths,
            Settings.WatchFolderPollMinutes, Settings.WatchFolderRecursive);

        // Scan the watched folder for orphans from a previous crashed session
        CheckForRecovery(new[] { Settings.LastFolderPath });
    }

    /// <summary>
    /// Starts or restarts the library folder watcher based on current settings.
    /// Called when LibraryWatchEnabled or LibraryWatchPollMinutes changes,
    /// when the library folder path changes, or on startup.
    /// </summary>
    public void ApplyLibraryWatchSetting()
    {
        _libraryWatchService.Stop();
        if (!Settings.LibraryWatchEnabled
            || string.IsNullOrWhiteSpace(Settings.LibraryFolderPath)
            || !Directory.Exists(Settings.LibraryFolderPath))
            return;

        // Watch ALL library folders: primary + all extra folders
        var allFolders = new List<string>();
        if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath))
            allFolders.Add(Settings.LibraryFolderPath);
        allFolders.AddRange(ExtraLibraryFolders.Where(f =>
            !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));

        var existing = LibraryEntries.Select(e => e.FilePath);
        _libraryWatchService.Start(allFolders, existing,
            Settings.LibraryWatchPollMinutes);

        var folderList = string.Join(", ", allFolders.Select(System.IO.Path.GetFileName));
        Log(
            $"[{DateTime.Now:HH:mm:ss}] 📚 Library watch started: {folderList} ({allFolders.Count} folder(s))");
    }

    /// <summary>Cleanly releases all unmanaged resources on app exit.</summary>
    public void DisposeWatchResources()
    {
        _watchFolderService.Stop();
        _libraryWatchService.Stop();
        _libraryCts?.Cancel();
        _libraryCts?.Dispose();
        _libraryCts = null;
        _renamePreviewDebounce?.Stop();
        _renamePreviewDebounce = null;
    }

    // Watch folder events handled by WatchFolderService

    private void TryAddWatchedFile(string path)
    {
        // When Watch Folder detects a VME temp/backup file, run recovery check
        // instead of adding it to the FILES panel
        if (IsVmeTempPath(path))
        {
            // Only scan for orphans when NOT actively writing.
            // During an active embed the .vme_tmp_ file is intentional — it is
            // the in-progress write. Scanning now would show a spurious
            // "Incomplete Operation" popup for a file that is still being written.
            if (!IsBusy)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir))
                    CheckForRecovery(new[] { dir });
            }
            return;
        }
        if (Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            return;

        // Wait briefly for the file to finish writing
        TryAddFile(path, isNew: true);
        var justAdded = Files.FirstOrDefault(f => !f.IsSeparator &&
            f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (justAdded != null)
        {
            justAdded.IsNewFile = true;
            _watchFolderService.AddKnownPath(path);
            StatusText = $"Watch folder: {justAdded.FileName} added.";
        }
    }

    public void TryAddFile(string path, bool isNew = false)
    {
        // Silently ignore VME temp/backup files
        if (IsVmeTempPath(path)) return;
        if (!MetadataService.IsSupported(path)) return;
        if (Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

        // Offload the TagLib# read to a background thread so the UI thread is not blocked.
        // For Watch Folder files (isNew=true) this is especially important — FSW can fire
        // for multiple files simultaneously.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        _ = Task.Run(() =>
        {
            try
            {
                var format = MetadataService.DetectFormat(path);
                var (supportsArt, supportsFullTags, warning) = MetadataService.GetFormatCapabilities(format);
                var info     = new FileInfo(path);
                var embedded = _metadataService.ReadMetadataFast(path);
                var (parsedTitle, parsedYear) = FilenameParser.Parse(
                    Path.GetFileNameWithoutExtension(path));

                if (!string.IsNullOrWhiteSpace(parsedTitle))
                    embedded.Title = parsedTitle;
                else if (string.IsNullOrWhiteSpace(embedded.Title))
                    embedded.Title = Path.GetFileNameWithoutExtension(path);
                if (!string.IsNullOrWhiteSpace(parsedYear))
                    embedded.Year = parsedYear;

                var vf = new VideoFile
                {
                    FilePath         = path,
                    Format           = format,
                    FileSizeBytes    = info.Length,
                    SupportsArtwork  = supportsArt,
                    SupportsFullTags = supportsFullTags,
                    FormatWarning    = warning,
                    EmbeddedMetadata = embedded,
                    ParsedTitle      = parsedTitle,
                    ParsedYear       = parsedYear,
                    IsNewFile        = isNew
                };
                vf.PendingMetadata = vf.EmbeddedMetadata.Clone();

                dispatcher?.InvokeAsync(() =>
                {
                    // Re-check after async gap — another task may have added it
                    if (Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        return;
                    Files.Add(vf);
                    _watchFolderService.AddKnownPath(path);
                    StatusText = $"{Files.Count(f => !f.IsSeparator)} file(s) loaded.";

                    // Auto-embed if enabled and this file was detected by the watch folder
                    if (isNew && Settings.AutoEmbedEnabled && !IsBusy)
                        _ = AutoEmbedFileAsync(vf);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { /* skip unreadable file */ }
        });
    }

    // ── Library Tab ───────────────────────────────────────────────────────────

    // ── Movie columns (shown in Movies tab / default view) ───────────────────────
    private static readonly List<(string Id, string Header, string Binding, double Width)> _movieColumns = new()
    {
        ("cover",    "Cover",      "CoverArt",            60),
        ("title",    "Title",      "Title",               200),
        ("year",     "Year",       "Year",                55),
        ("genre",    "Genre",      "Genre",               120),
        ("director", "Director",   "Director",            130),
        ("cast",     "Cast",       "Cast",                180),
        ("rating",   "Rating",     "RatingDisplay",       60),
        ("mpa",      "MPA",        "MpaRating",           55),
        ("duration", "Duration",   "Duration",            75),
        ("size",     "Size",       "FileSizeDisplay",     75),
        ("format",   "Format",     "Format",              55),
        ("res",      "Resolution", "Resolution",          90),
        ("date",     "Added",      "DownloadDateDisplay", 90),
        ("imdb",     "IMDB ID",    "ImdbId",              90),
        ("subs",     "Subtitles",  "SubtitleSummary",     130),
        ("desc",     "Synopsis",   "Description",         220),
    };

    // ── TV columns (shown in TV Shows tab) ────────────────────────────────────
    // Order: Cover, Show Title, Season, Episode, Episode Title, Aired,
    //        Cast, Genre, Rating, MPA, Duration, Size, Format, Added, IMDB ID, Synopsis
    private static readonly List<(string Id, string Header, string Binding, double Width)> _tvColumns = new()
    {
        ("cover",    "Cover",          "CoverArt",            60),
        ("show",     "Show Title",     "ShowTitle",           180),
        ("season",   "Season",         "SeasonDisplay",       60),
        ("epnum",    "Episode",        "EpisodeDisplay",      65),
        ("eptitle",  "Episode Title",  "EpisodeTitle",        200),
        ("aired",    "Aired",          "AiredDate",           90),
        ("cast",     "Cast",           "Cast",                200),
        ("genre",    "Genre",          "Genre",               120),
        ("rating",   "Rating",         "RatingDisplay",       60),
        ("mpa",      "MPA",            "MpaRating",           55),
        ("duration", "Duration",       "Duration",            75),
        ("size",     "Size",           "FileSizeDisplay",     75),
        ("format",   "Format",         "Format",              55),
        ("date",     "Added",          "DownloadDateDisplay", 90),
        ("imdb",     "IMDB ID",        "ImdbId",              90),
        ("subs",     "Subtitles",      "SubtitleSummary",     130),
        ("desc",     "Synopsis",       "Description",         220),
    };

    // Combined for column chooser (all unique ids)
    private static readonly List<(string Id, string Header, string Binding, double Width)> _defaultColumns =
        _movieColumns
        .Concat(_tvColumns.Where(t => !_movieColumns.Any(m => m.Id == t.Id)))
        .ToList();

    // ══════════════════════════════════════════════════════════════════════════
    // SERIES-LEVEL TV BATCH PROCESSING  (Build 76)
    // ══════════════════════════════════════════════════════════════════════════
    //
    // Flow:
    //  1. For each selected file: FilenameParser.ParseEpisode() → showTitle, season, ep
    //  2. Group files by normalised show title
    //  3. Per show group: SearchTvAsync → pick best match → resolve TMDB series ID
    //  4. Per file in group: GetTvEpisodeAsync(seriesId, season, ep)
    //       → apply metadata → embed + rename
    //  5. Files with no parseable episode code are skipped with a log entry
    //  6. Confidence gate: only auto-apply if the search result title is a
    //     ≥80% substring match to the parsed show name (prevents wrong-show embeds)

    private async Task BatchTvAsync()
    {
        var selected = Files
            .Where(f => f.IsSelected && !f.IsSeparator)
            .ToList();

        if (selected.Count == 0) return;

        IsBusy        = true;
        ProgressValue = 0;
        StatusText    = $"📺 TV Batch — parsing {selected.Count} file(s)…";

        var ct = BeginOperation();

        try
        {
            // ── Step 1: Parse episode codes from filenames ────────────────────
            var parseable = new List<(VideoFile file, string showTitle, int season, int episode)>();
            var skipped   = new List<VideoFile>();

            foreach (var file in selected)
            {
                var stem   = System.IO.Path.GetFileNameWithoutExtension(file.FileName);
                var parsed = FilenameParser.ParseEpisode(stem);
                if (parsed.HasValue)
                    parseable.Add((file, parsed.Value.showTitle, parsed.Value.season, parsed.Value.episode));
                else
                    skipped.Add(file);
            }

            if (skipped.Count > 0)
            {
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠ TV Batch — {skipped.Count} file(s) skipped: " +
                    $"no S##E## code found. ({string.Join(", ", skipped.Take(3).Select(f => f.FileName))}" +
                    (skipped.Count > 3 ? $"… +{skipped.Count - 3} more" : "") + ")");

                // Cross-reference skipped files with library gap data to give
                // the user actionable information about which episodes are missing.
                var libraryGaps = GetLibraryGapsForFiles(parseable
                    .Select(p => p.showTitle).Distinct().ToList());
                if (libraryGaps.Count > 0)
                {
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}] 💡 Known library gaps that could not be auto-matched: " +
                        string.Join(", ", libraryGaps.Take(8)) +
                        (libraryGaps.Count > 8 ? $" (+{libraryGaps.Count - 8} more)" : "") +
                        " — rename files to S##E## format to enable auto-embedding.");
                }
            }

            if (parseable.Count == 0)
            {
                StatusText = "⚠ TV Batch — no files with S##E## episode codes found. " +
                             "Rename files to include S01E01 format first.";
                return;
            }

            // ── Step 2: Group by normalised show title ────────────────────────
            var groups = parseable
                .GroupBy(p => NormaliseShowTitle(p.showTitle),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            Log(
                $"[{DateTime.Now:HH:mm:ss}] 📺 TV Batch — " +
                $"{parseable.Count} file(s), {groups.Count} show(s): " +
                string.Join(", ", groups.Select(g => g.Key)));

            int totalFiles = parseable.Count;
            int doneFiles  = 0;
            int embedded   = 0;
            int failed     = 0;

            // ── Step 3: Resolve each show → TMDB series ID ───────────────────
            foreach (var group in groups)
            {
                if (ct.IsCancellationRequested) break;

                var showName = group.First().showTitle;
                StatusText = $"📺 TV Batch — searching '{showName}'…";

                var (tvResults, tvErr) = await _apiService.SearchTvAsync(
                    showName, TmdbKey, ct);

                if (tvResults == null || tvResults.Count == 0)
                {
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}] ✗ '{showName}' — no TMDB results" +
                        (tvErr != null ? $": {tvErr}" : ""));
                    failed += group.Count();
                    doneFiles += group.Count();
                    continue;
                }

                // Pick best result: highest confidence match to show name
                var best = PickBestTvResult(tvResults, showName);
                if (best == null)
                {
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}] ✗ '{showName}' — no confident match " +
                        $"(best: '{tvResults[0].Title}')");
                    failed += group.Count();
                    doneFiles += group.Count();
                    continue;
                }

                var seriesId = best.TmdbId.ToString();
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ✓ '{showName}' → '{best.Title}' (TMDB {seriesId})");

                // ── Step 4: Embed each episode in the group ───────────────────
                foreach (var (file, _, season, episode) in group)
                {
                    if (ct.IsCancellationRequested) break;

                    StatusText = $"📺 TV Batch — {doneFiles + 1}/{totalFiles}  " +
                                 $"S{season:D2}E{episode:D2} · {file.FileName}";

                    try
                    {
                        var (meta, epErr) = await _apiService.GetTvEpisodeAsync(
                            seriesId, season, episode, TmdbKey, ct);

                        if (meta == null)
                        {
                            // OMDB fallback
                            (meta, _) = await _apiService.GetOmdbEpisodeAsync(
                                best.Title, season, episode, OmdbKey, ct);
                        }

                        if (meta == null)
                        {
                            Log(
                                $"[{DateTime.Now:HH:mm:ss}] ✗ S{season:D2}E{episode:D2} — " +
                                $"no metadata: {epErr ?? "unknown"}");
                            failed++;
                        }
                        else
                        {
                            meta.IsEpisode    = true;
                            meta.TmdbSeriesId = seriesId;
                            if (string.IsNullOrWhiteSpace(meta.MpaRating))
                                meta.MpaRating = "NR";

                            // Apply and embed using the single-file path
                            await EmbedTvEpisodeAsync(file, meta, ct);
                            embedded++;
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log(
                            $"[{DateTime.Now:HH:mm:ss}] ✗ {file.FileName}: {ex.Message}");
                        failed++;
                    }
                    finally
                    {
                        doneFiles++;
                        ProgressValue = totalFiles > 0
                            ? (int)((double)doneFiles / totalFiles * 100)
                            : 0;
                    }
                }
            }

            // ── Step 5: Summary ───────────────────────────────────────────────
            ProgressValue = 100;
            var summary = $"📺 TV Batch complete — {embedded} embedded" +
                          (failed  > 0 ? $", {failed} failed"  : "") +
                          (skipped.Count > 0 ? $", {skipped.Count} skipped (no episode code)" : "");
            StatusText = summary;
            Log( $"[{DateTime.Now:HH:mm:ss}] {summary}");

            BatchCompleted?.Invoke(this, new BatchEventArgs(embedded, failed, 0));
        }
        catch (OperationCanceledException)
        {
            StatusText    = "📺 TV Batch cancelled.";
            ProgressValue = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"📺 TV Batch error: {ex.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] TV BATCH ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            EndOperation();
        }
    }

    /// <summary>
    /// Writes a single TV episode's metadata to a file without changing SelectedFile.
    /// Used by BatchTvAsync so the main editing panel is undisturbed during batch.
    /// </summary>
    private async Task EmbedTvEpisodeAsync(
        VideoFile file, MovieMetadata meta, CancellationToken ct)
    {
        if (file.IsReadOnly)
        {
            bool cleared = await Services.FileLockService.TryClearReadOnlyAsync(file.FilePath);
            if (!cleared)
            {
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] 🔒 Skipped (read-only): {file.FileName}");
                return;
            }
        }

        // Snapshot BEFORE write so TV Batch and Watch Folder auto-embed support undo
        file.UndoFilePath = file.FilePath;
        file.UndoMetadata = file.EmbeddedMetadata.Clone();

        var writeResult = await _metadataService.WriteMetadataDetailedAsync(
            file.FilePath, meta, Settings,
            progress: null, ct: ct);

        if (!writeResult.Success)
        {
            // Write failed — clear snapshot so undo button stays greyed out
            file.UndoFilePath = null;
            file.UndoMetadata = null;
            Log(
                $"[{DateTime.Now:HH:mm:ss}] ✗ Embed failed: {file.FileName} — {string.Join("; ", writeResult.AttemptErrors)}");
            file.WriteStatus = WriteStatus.Failed;
            return;
        }

        // Write succeeded — snapshot captured above is valid

        file.WriteStatus = WriteStatus.Success;

        // Select pattern based on content type — movies use movie pattern, episodes use TV pattern
        var pattern = meta.IsEpisode
            ? Settings.TvRenamePattern
            : Settings.RenamePattern;

        if (!string.IsNullOrWhiteSpace(pattern))
        {
            try
            {
                var ext        = file.Extension ?? "mp4";
                var resolution = string.Empty;
                var fmt        = ext;
                var (renamed, newPath, renErr) = await SafeRenameAsync(
                    file, meta, ct, isFromBatch: true);
                if (renamed)
                {
                    file.FilePath = newPath;
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}] ↗ Renamed: {System.IO.Path.GetFileName(newPath)}");
                }
            }
            catch (Exception ex)
            {
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠ Rename skipped: {ex.Message}");
            }
        }

        Log(
            $"[{DateTime.Now:HH:mm:ss}] ✓ {meta.ShowTitle} " +
            $"S{meta.Season:D2}E{meta.Episode:D2} — {meta.EpisodeTitle}");
    }

    /// <summary>
    /// Picks the best TV search result for a given show name.
    /// Returns null if no result reaches the 70% confidence threshold.
    /// </summary>
    private static SearchResult? PickBestTvResult(
        IReadOnlyList<SearchResult> results, string query)
    {
        if (results.Count == 0) return null;

        var qNorm = NormaliseShowTitle(query);

        // Exact match first
        var exact = results.FirstOrDefault(r =>
            string.Equals(NormaliseShowTitle(r.Title), qNorm, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Substring / prefix match with score threshold
        var scored = results
            .Select(r => (result: r, score: TitleSimilarity(NormaliseShowTitle(r.Title), qNorm)))
            .Where(x => x.score >= 0.70)
            .OrderByDescending(x => x.score)
            .FirstOrDefault();

        return scored.result;
    }

    private static string NormaliseShowTitle(string title)
    {
        // Remove common noise: year in brackets, "the", punctuation normalisation
        var t = title.ToLowerInvariant();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*\(\d{4}\)\s*$", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[^a-z0-9\s]", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s{2,}", " ").Trim();
        return t;
    }

    /// <summary>
    /// Returns 0.0–1.0 similarity between two normalised title strings.
    /// Uses longest-common-subsequence ratio as a fast proxy.
    /// </summary>
    private static double TitleSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.9;

        // Word overlap ratio
        var wa = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var wb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int common = wa.Intersect(wb).Count();
        int total  = wa.Union(wb).Count();
        return total == 0 ? 0 : (double)common / total;
    }
    // ══════════════════════════════════════════════════════════════════════════
    // AUTO-EMBED ON WATCH FOLDER DETECTION  (Build 78)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called when a new file is detected by the watch folder AND
    /// AutoEmbedEnabled = true. Searches for metadata and embeds if the
    /// search result meets the configured confidence threshold.
    /// Files that don't meet the threshold are left in the panel for manual
    /// processing (highlighted yellow — IsNewFile = true).
    /// </summary>
    private async Task AutoEmbedFileAsync(VideoFile vf)
    {
        if (vf == null || !File.Exists(vf.FilePath)) return;

        // Small delay — let the file system settle (copy-in-progress guard)
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!File.Exists(vf.FilePath)) return;

        // Skip if file already has valid embedded metadata (non-empty title + year/episode info)
        // This prevents re-processing files that were already correctly tagged
        if (!string.IsNullOrWhiteSpace(vf.EmbeddedMetadata?.Title)
            && (!string.IsNullOrWhiteSpace(vf.EmbeddedMetadata?.Year)
                || vf.EmbeddedMetadata?.IsEpisode == true))
        {
            Log(
                $"[{DateTime.Now:HH:mm:ss}] ⏭ Auto-embed skipped: '{vf.FileName}' already has metadata " +
                $"(Title='{vf.EmbeddedMetadata.Title}', Year='{vf.EmbeddedMetadata.Year}')");
            return;
        }

        try
        {
            Log(
                $"[{DateTime.Now:HH:mm:ss}] 🔍 Auto-embed: searching for '{vf.ParsedTitle ?? vf.FileName}'…");

            // Search TMDB
            var query = vf.ParsedTitle ?? Path.GetFileNameWithoutExtension(vf.FileName);
            var year  = vf.ParsedYear;

            // Try episode parse first
            var epParsed = FilenameParser.ParseEpisode(
                Path.GetFileNameWithoutExtension(vf.FileName));

            MovieMetadata? meta = null;

            if (epParsed.HasValue)
            {
                // Use ParseEpisode's built-in showTitle — it correctly extracts
                // everything before the S##E## code and cleans it.
                // e.g. "Dr.Stone.S01E02.720p.x264-ESub..." → showTitle="Dr Stone"
                var showQuery = epParsed.Value.showTitle;

                Log(
                    $"[{DateTime.Now:HH:mm:ss}] 🔍 Auto-embed TV: query='{showQuery}' " +
                    $"S{epParsed.Value.season:D2}E{epParsed.Value.episode:D2}");

                // TV episode — search TV
                var (tvResults, _) = await _apiService.SearchTvAsync(showQuery, TmdbKey);
                if (tvResults?.Count > 0)
                {
                    var bestResult = PickBestTvResult(tvResults, showQuery);
                    if (bestResult != null && MeetsConfidenceThreshold(bestResult.Title, showQuery))
                    {
                        var (epMeta, _) = await _apiService.GetTvEpisodeAsync(
                            bestResult.TmdbId.ToString(), epParsed.Value.season,
                            epParsed.Value.episode, TmdbKey);
                        if (epMeta != null)
                        {
                            epMeta.IsEpisode    = true;
                            epMeta.TmdbSeriesId = bestResult.TmdbId.ToString();
                            meta = epMeta;
                        }
                    }
                }
            }
            else
            {
                // Movie — search TMDB with year if available
                // Pass year as separate param to TMDB — more accurate than appending to query string
                var (tmdbResults, _) = await _apiService.SearchTmdbAsync(query, TmdbKey, year);

                if (tmdbResults?.Count > 0)
                {
                    // Score all results — pick the BEST match, not just [0]
                    var best = tmdbResults
                        .Select(r => (result: r,
                            titleScore: TitleSimilarity(
                                NormaliseShowTitle(r.Title), NormaliseShowTitle(query)),
                            yearMatch: !string.IsNullOrWhiteSpace(year) && r.Year == year))
                        .OrderByDescending(x => x.yearMatch ? x.titleScore + 0.3 : x.titleScore)
                        .FirstOrDefault();

                    // Auto-embed requires BOTH confidence AND a reasonable title match
                    // Never auto-embed if year mismatch and title score is poor
                    bool passesConfidence = MeetsConfidenceThreshold(best.result.Title, query);
                    bool yearOk = string.IsNullOrWhiteSpace(year) || best.yearMatch
                        || best.titleScore >= 0.80; // very high title similarity = ignore year

                    if (passesConfidence && yearOk)
                    {
                        Log(
                            $"[{DateTime.Now:HH:mm:ss}] 🔍 Auto-embed: best match '{best.result.Title}' " +
                            $"({best.result.Year}) score={best.titleScore:F2} yearMatch={best.yearMatch}");

                        var (loadedMeta, _) = await _apiService.GetTmdbDetailsAsync(
                            best.result.TmdbId.ToString(), TmdbKey);
                        meta = loadedMeta;
                    }
                    else
                    {
                        Log(
                            $"[{DateTime.Now:HH:mm:ss}] ⚠ Auto-embed: rejected '{best.result.Title}' " +
                            $"({best.result.Year}) for query '{query}' ({year}) — " +
                            $"score={best.titleScore:F2} yearMatch={best.yearMatch} confidence={passesConfidence}");
                    }
                }
            }

            if (meta == null)
            {
                Log(
                    $"[{DateTime.Now:HH:mm:ss}] ⚠ Auto-embed: no confident match for '{query}' — " +
                    $"file queued for manual processing.");
                vf.IsNewFile = true; // keep yellow highlight
                return;
            }

            // Embed
            await EmbedTvEpisodeAsync(vf, meta, CancellationToken.None);

            Settings.LastAutoEmbedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            RaiseProperty(nameof(LastAutoEmbedTime));
            _ = App.ConfigService.SaveAsync();

            vf.IsNewFile = false;
            StatusText = $"✓ Auto-embedded: {Path.GetFileName(vf.FilePath)}";
            Log(
                $"[{DateTime.Now:HH:mm:ss}] ✓ Auto-embedded: {vf.FileName} → {meta.Title}");
        }
        catch (Exception ex)
        {
            Log(
                $"[{DateTime.Now:HH:mm:ss}] ✗ Auto-embed failed for '{vf.FileName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if the candidate title meets the user's configured
    /// confidence threshold when compared to the query.
    /// Level 0=Low (40%), 1=Medium (60%), 2=High (80%), 3=Exact only (100%).
    /// </summary>
    private bool MeetsConfidenceThreshold(string candidateTitle, string query)
    {
        var thresholds = new[] { 0.40, 0.60, 0.80, 1.00 };
        var level      = Math.Clamp(Settings.AutoEmbedConfidenceLevel, 0, 3);
        var threshold  = thresholds[level];
        var score      = TitleSimilarity(
            NormaliseShowTitle(candidateTitle),
            NormaliseShowTitle(query));
        return score >= threshold;
    }

    /// <summary>
    /// Cross-references the library's known TV shows against the parsed show titles
    /// from the TV batch and returns any episode gaps it finds, formatted as
    /// "ShowTitle S01E03" strings. Surfaces actionable rename hints in the log.
    /// </summary>
    private List<string> GetLibraryGapsForFiles(IEnumerable<string> showTitles)
    {
        var gaps = new List<string>();
        try
        {
            var normalised = showTitles
                .Select(NormaliseShowTitle)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var show in TvShowTree ?? Enumerable.Empty<Models.TvShowNode>())
            {
                if (!normalised.Contains(NormaliseShowTitle(show.ShowTitle))) continue;
                foreach (var season in show.Seasons ?? Enumerable.Empty<Models.TvSeasonNode>())
                    foreach (var ep in season.Episodes
                        .Where(e => e.IsMissingEpisode && e.Episode.HasValue))
                        gaps.Add($"{show.ShowTitle} S{season.SeasonNumber:D2}E{ep.Episode!.Value:D2}");
            }
        }
        catch { /* library not loaded or tree unavailable */ }
        return gaps;
    }


}
