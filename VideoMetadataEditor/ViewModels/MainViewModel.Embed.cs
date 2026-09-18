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
        // Snapshot SelectedFile — it can become null between the UI action and
        // this synchronous method if the user deselects a file very quickly.
        var file = SelectedFile;
        CopyMetadataTo(RetrievedMetadata, EditingMetadata);
        // Block auto-search debounce from overwriting what the user just applied
        _tvEpisodeManuallyLoaded = RetrievedMetadata.IsEpisode;

        if (file != null)
        {
            // Update PendingMetadata so data survives re-selection and is ready to embed
            CopyMetadataTo(RetrievedMetadata, file.PendingMetadata);

            // Keep the file's own RetrievedMetadata consistent with what was just applied.
            // Batch Process renames from (RetrievedMetadata ?? PendingMetadata); if this
            // file was searched-and-applied individually, its RetrievedMetadata could be
            // stale (e.g. IsEpisode not set), causing the batch to pick the movie rename
            // pattern for an episode. Syncing it here keeps both objects in agreement.
            file.RetrievedMetadata = RetrievedMetadata.Clone();

            // Update EmbeddedMetadata TV fields so the ground-truth reset in
            // OnSelectedFileChanged doesn't clobber IsEpisode back to false
            // before the user has had a chance to embed. Only TV fields are updated
            // here — the actual on-disk tags are unchanged until explicit embed.
            if (RetrievedMetadata.IsEpisode)
            {
                file.EmbeddedMetadata.IsEpisode    = true;
                file.EmbeddedMetadata.ShowTitle    = RetrievedMetadata.ShowTitle;
                file.EmbeddedMetadata.Season       = RetrievedMetadata.Season;
                file.EmbeddedMetadata.Episode      = RetrievedMetadata.Episode;
                file.EmbeddedMetadata.EpisodeTitle = RetrievedMetadata.EpisodeTitle;
                file.EmbeddedMetadata.AiredDate    = RetrievedMetadata.AiredDate;
                file.EmbeddedMetadata.TmdbSeriesId = RetrievedMetadata.TmdbSeriesId;
                // Also patch IDs so ground-truth reset doesn't lose them
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.TmdbId))
                    file.EmbeddedMetadata.TmdbId = RetrievedMetadata.TmdbId;
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.ImdbId))
                    file.EmbeddedMetadata.ImdbId = RetrievedMetadata.ImdbId;
                if (!string.IsNullOrWhiteSpace(RetrievedMetadata.TvdbId))
                    file.EmbeddedMetadata.TvdbId = RetrievedMetadata.TvdbId;
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
                        // A watch-detected entry for the pre-rename name may now be stale
                        // (the file moved to newName). Drop any panel rows whose file is gone.
                        SweepStalePanelEntries();
                        CollapseDuplicateEntries();
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
                // Always mark this path as recently embedded so the next Scan Library
                // forces a fresh TagLib# read — regardless of whether the file is
                // currently in LibraryEntries. This is the unconditional guarantee.
                _libraryScanService?.MarkAsEmbedded(targetFile.FilePath);
                // Single source-of-truth projection: re-reads disk and updates the
                // grid entry, the cache, AND the FILES-panel snapshot in one place.
                SyncLibraryEntry(currentPath, targetFile.FilePath, metadataToWrite, targetFile);
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

                    // Container-level failure (not access/permission/volume) on an MP4-family
                    // file: offer a lossless remux, which often rebuilds a container TagLib#
                    // can write into. On success the write is retried automatically.
                    if (Services.RemuxSuggestionService.ShouldSuggestRemux(diag.Category, targetFile.FilePath))
                    {
                        bool retried = await TryRemuxAndRetryWriteAsync(targetFile, metadataToWrite, ct);
                        if (retried)
                        {
                            ok = targetFile.WriteStatus == Models.WriteStatus.Success;
                        }
                        else if (Settings.VerboseWriteErrors)
                        {
                            WriteFailedDetailed?.Invoke(this, (targetFile, detail));
                        }
                    }
                    else if (Settings.VerboseWriteErrors)
                    {
                        WriteFailedDetailed?.Invoke(this, (targetFile, detail));
                    }
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
        int? episodeEnd = (Settings.DetectEpisodeRange && meta.IsEpisode)
            ? FilenameParser.ParseEpisodeRange(Path.GetFileNameWithoutExtension(targetFile.FilePath))
            : null;
        int? absoluteEpisode = await ResolveAbsoluteEpisodeAsync(targetFile.FilePath, meta, ct);
        var (success, newPath, error) = await _renameService.RenameFileAsync(
            targetFile.FilePath, pattern, meta, resolution, fmt, episodeEnd, absoluteEpisode, ct);

        if (success)
        {
            var oldName = Path.GetFileName(targetFile.FilePath);
            // Snapshot BEFORE updating FilePath so undo can restore the old name
            targetFile.UndoFilePath = targetFile.FilePath;
            targetFile.UndoMetadata = targetFile.EmbeddedMetadata.Clone();
            RaiseProperty(nameof(UndoLastEmbedCommand));

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
                    vf.FilePath, Settings.RenamePattern, corrected, res, fmt, null, null, ct);
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

        int? episodeEnd = (Settings.DetectEpisodeRange && meta.IsEpisode)
            ? FilenameParser.ParseEpisodeRange(Path.GetFileNameWithoutExtension(vf.FileName))
            : null;
        int? absoluteEpisode = await ResolveAbsoluteEpisodeAsync(vf.FilePath, meta, ct);
        return await _renameService.RenameFileAsync(vf.FilePath, pattern, meta, res, fmt, episodeEnd, absoluteEpisode, ct);
    }

    /// <summary>
    /// Resolves the absolute episode number for {AbsoluteEpisode}, when the
    /// AniListAutoDetectAbsolute setting is on and the file is a TV episode.
    /// Filename parse first (cheap, offline); AniList relations-graph fallback only
    /// when the filename has no absolute number AND an AniList ID is present.
    /// Returns null otherwise — the token then resolves empty. Never throws.
    /// </summary>
    private async Task<int?> ResolveAbsoluteEpisodeAsync(
        string filePath, MovieMetadata meta, CancellationToken ct)
    {
        if (!Settings.AniListAutoDetectAbsolute || !meta.IsEpisode) return null;

        // 1. Primary: parse an absolute number from the source filename.
        var fromName = FilenameParser.ParseAbsoluteEpisode(
            Path.GetFileNameWithoutExtension(filePath));
        if (fromName.HasValue) return fromName;

        // 2. Fallback: AniList relations-graph sum, only if we have an AniList ID and
        //    a concrete in-season episode to add. Best-effort; null on any uncertainty.
        if (!string.IsNullOrWhiteSpace(meta.AniListId) && meta.Episode.HasValue)
        {
            try
            {
                return await _aniListService.ComputeAbsoluteEpisodeAsync(
                    meta.AniListId, meta.Episode.Value, ct);
            }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Batch field editing: apply one or more SHARED field values across all selected
    /// files. Opens the batch-edit dialog, applies the chosen edits to each file's
    /// existing metadata, writes through the normal metadata-write path, and records an
    /// Undo Batch snapshot so the whole operation can be reverted with ↩ Undo Batch.
    /// Does NOT rename (this only touches embedded fields) and preserves existing artwork.
    /// </summary>
    private async Task BatchEditFieldsAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;

        // Determine the selection's content-type composition so the dialog can grey out
        // fields that don't apply (Show title for movies, Year for episodes).
        bool anyEpisode = selected.Any(f => f.EmbeddedMetadata.IsEpisode);
        bool anyMovie   = selected.Any(f => !f.EmbeddedMetadata.IsEpisode);
        var kind = anyEpisode && anyMovie
            ? Views.BatchEditDialog.SelectionKind.Mixed
            : anyEpisode
                ? Views.BatchEditDialog.SelectionKind.AllEpisodes
                : Views.BatchEditDialog.SelectionKind.AllMovies;

        // Collect the edits via the dialog (UI thread).
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new Views.BatchEditDialog(selected.Count, kind, owner);
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        var edits = dlg.Result;

        // Warn before discarding a previous undo snapshot (same contract as BatchProcess).
        if (_batchUndoFiles.Count > 0)
        {
            var proceed = System.Windows.MessageBox.Show(
                $"Starting a batch edit will clear the Undo Batch snapshot for the previous {_batchUndoFiles.Count} file(s).\n\n" +
                "If you want to undo the previous batch first, click No and use the ↩ Undo Batch button.\n\n" +
                "Continue with the batch edit?",
                "Previous Undo Batch will be cleared",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes);
            if (proceed != System.Windows.MessageBoxResult.Yes) return;
        }

        var ct = BeginOperation();
        IsBusy = true;
        _batchUndoFiles.Clear();
        RaiseProperty(nameof(CanUndoBatch));

        var progress = new System.Progress<string>(msg =>
        {
            Log($"[{DateTime.Now:HH:mm:ss}] {msg}");
            StatusText = msg;
        });

        int done = 0, ok = 0, fail = 0;
        try
        {
            foreach (var vf in selected)
            {
                if (ct.IsCancellationRequested) break;
                done++;
                StatusText = $"Batch edit {done}/{selected.Count}: {Path.GetFileName(vf.FilePath)}";

                try
                {
                    // Start from the file's current on-disk metadata (source of truth),
                    // clone it, and apply only the ticked field edits.
                    var edited = vf.EmbeddedMetadata.Clone();

                    // Preserve existing artwork so a field-only edit never drops the poster.
                    if (edited.ArtworkBytes is not { Length: > 0 }
                        && vf.EmbeddedMetadata.ArtworkBytes is { Length: > 0 })
                        edited.ArtworkBytes = vf.EmbeddedMetadata.ArtworkBytes;

                    Services.BatchFieldEditService.ApplyEdits(edited, edits);

                    var writeResult = await _metadataService.WriteMetadataDetailedAsync(
                        vf.FilePath, edited, Settings, progress, ct: ct);

                    if (writeResult.Success)
                    {
                        // Undo snapshot BEFORE we overwrite the in-memory metadata.
                        vf.UndoFilePath = vf.FilePath;
                        vf.UndoMetadata = vf.EmbeddedMetadata.Clone();
                        lock (_batchUndoFiles) _batchUndoFiles.Add(vf);

                        // Reflect the change in the UI/model.
                        vf.EmbeddedMetadata = edited;
                        vf.PendingMetadata  = edited.Clone();
                        if (SelectedFile == vf) CopyMetadataTo(edited, EditingMetadata);
                        vf.WriteStatus = Models.WriteStatus.Success;
                        _libraryScanService?.MarkAsEmbedded(vf.FilePath);
                        ok++;
                    }
                    else
                    {
                        vf.WriteStatus = Models.WriteStatus.Failed;
                        var why = writeResult.AttemptErrors.Count > 0
                            ? string.Join("; ", writeResult.AttemptErrors)
                            : "write failed";
                        Log($"[{DateTime.Now:HH:mm:ss}] Batch edit failed: {Path.GetFileName(vf.FilePath)} — {why}");
                        fail++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    vf.WriteStatus = Models.WriteStatus.Failed;
                    Log($"[{DateTime.Now:HH:mm:ss}] Batch edit error: {Path.GetFileName(vf.FilePath)} — {ex.Message}");
                    fail++;
                }
            }
        }
        catch (OperationCanceledException) { /* user cancelled — keep partial results */ }
        finally
        {
            RaiseProperty(nameof(CanUndoBatch));
            StatusText = $"Batch edit complete: {ok} updated, {fail} failed.";
            Log($"[{DateTime.Now:HH:mm:ss}] Batch edit complete: {ok} updated, {fail} failed.");
            EndOperation();
            IsBusy = false;
        }
    }

    private async Task BatchProcessAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;
        var ct = BeginOperation();
        IsBusy = true;
        int done = 0, successCount = 0, failCount = 0, renamedCount = 0;

        // If a previous batch undo snapshot exists, warn before discarding it.
        // A new batch start clears the snapshot — Undo Batch would no longer apply to
        // the previous run. This is a one-time prompt; if the user proceeds they accept
        // losing the previous undo window.
        if (_batchUndoFiles.Count > 0)
        {
            var proceed = System.Windows.MessageBox.Show(
                $"Starting a new batch will clear the Undo Batch snapshot for the previous {_batchUndoFiles.Count} file(s).\n\n" +
                "If you want to undo the previous batch first, click No and use the ↩ Undo Batch button.\n\n" +
                "Continue with the new batch?",
                "Previous Undo Batch will be cleared",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes);
            if (proceed != System.Windows.MessageBoxResult.Yes)
            {
                EndOperation();
                IsBusy = false;
                return;
            }
        }

        // Clear previous batch undo state so the new batch replaces it
        _batchUndoFiles.Clear();
        RaiseProperty(nameof(CanUndoBatch));

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
                    // Add to batch undo list so all successes can be reverted together
                    lock (_batchUndoFiles) _batchUndoFiles.Add(vf);

                    vf.WriteStatus = Models.WriteStatus.Success;
                    vf.IsNewFile   = false; // clear yellow highlight on successful embed
                    // Mark for forced fresh read on next Scan Library
                    _libraryScanService?.MarkAsEmbedded(vf.FilePath);
                    var oldName = Path.GetFileName(vf.FilePath);
                    var (renamed, newPath, err) = await SafeRenameAsync(
                        vf, meta, ct, isFromBatch: true);

                    if (renamed && File.Exists(newPath))
                    {
                        vf.FilePath = newPath;
                        // Update the embedded mark to the new path after rename
                        _libraryScanService?.MarkAsEmbedded(newPath);
                        // Register renamed path so Watch Folder poll doesn't re-detect it
                        _watchFolderService.AddKnownPath(newPath);
                        if (!Path.GetFileName(newPath).Equals(oldName, StringComparison.OrdinalIgnoreCase))
                            Interlocked.Increment(ref renamedCount);
                    }

                    // Single source-of-truth projection — also refreshes the FILES-panel
                    // snapshot, which the batch path previously left stale.
                    var preBatchPath = vf.UndoFilePath ?? vf.FilePath;
                    SyncLibraryEntry(preBatchPath, vf.FilePath, meta, vf);

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
        RaiseProperty(nameof(CanUndoBatch));
        RaiseProperty(nameof(UndoBatchCommand));

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

    // ── Remux-to-fix on container-level write failure ─────────────────────────

    /// <summary>
    /// Offers a lossless remux when a metadata write failed at the container level, and
    /// — if the user accepts and the remux succeeds — adopts the clean file and retries
    /// the write once. Returns true if a retry was attempted (regardless of its outcome),
    /// false if the user declined or remux/ffmpeg was unavailable. On a successful retry
    /// the target file's WriteStatus is set to Success and its path points at the new file.
    /// </summary>
    private async Task<bool> TryRemuxAndRetryWriteAsync(
        Models.VideoFile targetFile, Models.MovieMetadata metadataToWrite,
        CancellationToken ct)
    {
        if (!Services.FfmpegService.IsAvailable)
        {
            // No ffmpeg → can't remux; fall back to the normal diagnostic surface.
            Log( $"[{DateTime.Now:HH:mm:ss}] Remux suggested but ffmpeg is not available — skipping.");
            return false;
        }

        var prompt = System.Windows.MessageBox.Show(
            $"Metadata couldn't be written into this file's container:\n\n" +
            $"  {targetFile.FileName}\n\n" +
            "This usually means the MP4 container is structured in a way the tag writer " +
            "can't update in place. A lossless remux rebuilds the container from the same " +
            "streams — no re-encoding, no quality loss — and typically fixes it.\n\n" +
            "Remux this file now and retry the write?\n\n" +
            "(The original is replaced only after the remux succeeds.)",
            "Remux may fix this write",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.Yes);
        if (prompt != System.Windows.MessageBoxResult.Yes) return false;

        var originalPath = targetFile.FilePath;
        StatusText = $"Remuxing {targetFile.FileName} to a clean container…";
        Log( $"[{DateTime.Now:HH:mm:ss}] Remuxing (write-fix): {targetFile.FileName}");

        // Remux to a clean MP4 candidate (<stem>.remux.mp4) alongside the original.
        var remux = await Services.FfmpegService.RemuxAsync(originalPath, ".mp4", ct);
        if (!remux.Success || string.IsNullOrWhiteSpace(remux.OutputPath))
        {
            StatusText = $"Remux failed: {remux.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Remux failed: {remux.Message}");
            System.Windows.MessageBox.Show(
                $"The remux did not complete:\n\n{remux.Message}\n\nThe original file is unchanged.",
                "Remux failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        // Adopt the clean candidate in place of the original (non-destructive commit).
        var commit = Services.RemuxCommitService.ReplaceOriginal(remux.OutputPath, originalPath);
        if (!commit.Success || string.IsNullOrWhiteSpace(commit.FinalPath))
        {
            StatusText = $"Remux commit failed: {commit.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Remux commit failed: {commit.Message}");
            System.Windows.MessageBox.Show(
                $"The remuxed copy was created but could not replace the original:\n\n" +
                $"{commit.Message}\n\nBoth files may still be present; check the folder.",
                "Remux commit failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        // Point the file at the committed container and retry the write once.
        targetFile.FilePath = commit.FinalPath;
        if (SelectedFile == targetFile)
            OriginalFileName = targetFile.FileName;
        Log( $"[{DateTime.Now:HH:mm:ss}] Remux committed → {Path.GetFileName(commit.FinalPath)}; retrying write…");
        StatusText = $"Remux done. Retrying write for {targetFile.FileName}…";

        var retry = await _metadataService.WriteMetadataDetailedAsync(
            commit.FinalPath, metadataToWrite, Settings, ct: ct);

        if (retry.Success)
        {
            targetFile.WriteStatus  = Models.WriteStatus.Success;
            targetFile.WriteErrorDetail = string.Empty;
            CopyMetadataTo(metadataToWrite, targetFile.EmbeddedMetadata);
            CopyMetadataTo(metadataToWrite, targetFile.PendingMetadata);
            _libraryScanService?.MarkAsEmbedded(commit.FinalPath);
            StatusText = $"Write succeeded after remux: {targetFile.FileName}";
            Log( $"[{DateTime.Now:HH:mm:ss}] ✓ Write succeeded after remux: {targetFile.FileName}");
            EmbedSucceeded?.Invoke(this, new EmbedEventArgs(targetFile.FileName, false));
        }
        else
        {
            targetFile.WriteStatus = Models.WriteStatus.Failed;
            var why = retry.Diagnosis != null
                ? BuildDiagnosticMessage(targetFile.FileName, retry.Diagnosis)
                : "Write still failed after remux.";
            targetFile.WriteErrorDetail = why;
            StatusText = $"Write still failed after remux: {targetFile.FileName}";
            Log( $"[{DateTime.Now:HH:mm:ss}] ✗ Write still failed after remux: {targetFile.FileName}");
            if (Settings.VerboseWriteErrors)
                WriteFailedDetailed?.Invoke(this, (targetFile, why));
        }
        return true;
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

    // Cache for the physical-episode folder scan used by TvShowTree. Enumerating each
    // season folder from disk on every TvShowTree evaluation was the main cause of the
    // tree feeling slow — the getter is raised ~10 different ways (filter, sort, watched
    // state, and once per background artwork load), and each rebuild re-walked every
    // folder. Folder contents only change on a library scan or a watch event, so we
    // cache the scan per directory and invalidate explicitly. Key = folder path,
    // Value = set of episode numbers found physically in that folder.
    private readonly Dictionary<string, HashSet<int>> _physicalEpisodeCache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clears the TvShowTree folder-scan cache. Call when files may have changed
    /// on disk (library scan, watch add/remove) so the next tree build re-reads folders.</summary>
    public void InvalidatePhysicalEpisodeCache()
    {
        _physicalEpisodeCache.Clear();
    }

    /// <summary>
    /// Returns the episode numbers physically present in <paramref name="dir"/> for the
    /// given season, reading the folder from disk only once and caching the result.
    /// </summary>
    private HashSet<int> GetPhysicalEpisodes(string dir, int seasonNum)
    {
        var key = $"{dir}|S{seasonNum:D2}";
        if (_physicalEpisodeCache.TryGetValue(key, out var cached)) return cached;

        var found = new HashSet<int>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var baseName = Path.GetFileNameWithoutExtension(f);
                var epM = System.Text.RegularExpressions.Regex.Match(baseName,
                    $@"(?i)[Ss]{seasonNum:D2}[Ee](\d{{1,3}})");
                if (epM.Success && int.TryParse(epM.Groups[1].Value, out var epNum))
                    found.Add(epNum);
            }
        }
        catch { /* folder unreadable — treat as no extra physical episodes */ }

        _physicalEpisodeCache[key] = found;
        return found;
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
                        // Find files with S##E## codes that aren't in LibraryEntries
                        // (files with wrong/missing metadata show as "untagged" not "missing").
                        // Cached per folder so this doesn't re-hit disk on every tree rebuild.
                        var knownPhysical = new HashSet<int>();
                        var seasonFolders = orderedEps
                            .Select(e => Path.GetDirectoryName(e.FilePath))
                            .Where(d => d != null).Distinct();
                        var seasonNum = orderedEps[0].Season ?? 0;
                        foreach (var dir in seasonFolders)
                        {
                            if (string.IsNullOrWhiteSpace(dir)) continue;
                            foreach (var epNum in GetPhysicalEpisodes(dir, seasonNum))
                                knownPhysical.Add(epNum);
                        }
                        var withGaps = InsertMissingEpisodes(orderedEps, knownPhysical, orderedEps.Select(e => e.Episode ?? 0).ToHashSet());
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
        HashSet<int>? knownPhysicalEpisodes = null,
        HashSet<int>? alreadyTaggedEpisodes = null)
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
                // A file is "untagged" only if it physically exists on disk AND
                // is NOT already represented by a real LibraryEntry in this season.
                // Without the second check, files that ARE properly tagged still
                // appear as "(untagged)" because the directory scanner also finds them.
                bool physicalExists = knownPhysicalEpisodes?.Contains(ep) == true
                                   && alreadyTaggedEpisodes?.Contains(ep) != true;
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
        // Reuse a single timer + handler rather than allocating a new DispatcherTimer
        // and Tick closure on every keystroke.
        if (_renamePreviewDebounce == null)
        {
            _renamePreviewDebounce = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(150) };
            _renamePreviewDebounce.Tick += RenamePreviewDebounce_Tick;
        }
        _renamePreviewDebounce.Stop();
        _renamePreviewDebounce.Start();
    }

    private void RenamePreviewDebounce_Tick(object? sender, EventArgs e)
    {
        _renamePreviewDebounce?.Stop();
        var ext        = SelectedFile?.Extension?.ToLowerInvariant() ?? "mp4";
        var resolution = MediaInfo?.ResolutionDisplay ?? string.Empty;
        var fmt        = SelectedFile?.Extension ?? string.Empty;
        var pattern    = IsEpisodeMode ? Settings.TvRenamePattern : Settings.RenamePattern;
        int? episodeEnd = (Settings.DetectEpisodeRange && IsEpisodeMode && SelectedFile != null)
            ? FilenameParser.ParseEpisodeRange(Path.GetFileNameWithoutExtension(SelectedFile.FilePath))
            : null;
        int? absoluteEpisode = (Settings.AniListAutoDetectAbsolute && IsEpisodeMode && SelectedFile != null)
            ? FilenameParser.ParseAbsoluteEpisode(Path.GetFileNameWithoutExtension(SelectedFile.FilePath))
            : null;
        RenamePreview  = _renameService.Preview(pattern, EditingMetadata, "." + ext, resolution, fmt, episodeEnd, absoluteEpisode);
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
        if (!Settings.WatchFolderEnabled)
            return;

        // Watch every distinct folder the loaded files actually came from — not just
        // Settings.LastFolderPath. This covers "Add Files", files added from several
        // folders, and mixed loads, so a new sibling of ANY loaded file is detected.
        var folders = Files
            .Where(f => !f.IsSeparator && !string.IsNullOrWhiteSpace(f.FilePath))
            .Select(f => Path.GetDirectoryName(f.FilePath))
            .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Fall back to the last loaded folder if no files are currently listed
        // (e.g. the user enabled the toggle before adding files).
        if (folders.Count == 0
            && !string.IsNullOrWhiteSpace(Settings.LastFolderPath)
            && Directory.Exists(Settings.LastFolderPath))
        {
            folders.Add(Settings.LastFolderPath);
        }

        if (folders.Count == 0) return;

        var existingPaths = Files.Where(f => !f.IsSeparator).Select(f => f.FilePath);
        _watchFolderService.Start(folders, existingPaths,
            Settings.WatchFolderPollMinutes, Settings.WatchFolderRecursive);

        // Scan the watched folders for orphans from a previous crashed session.
        CheckForRecovery(folders);

        var names = string.Join(", ", folders.Select(Path.GetFileName));
        Log( $"[{DateTime.Now:HH:mm:ss}] 📁 Watch folder active on {folders.Count} folder(s): {names}");
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

    /// <summary>
    /// Removes Files-panel entries whose file no longer exists on disk. Fixes the case
    /// where Watch Folder detects a freshly-arrived file (creating an entry), then that
    /// file is auto-renamed — leaving a stale entry pointing at the old, now-gone path
    /// alongside the correctly-renamed one. This is a lightweight, targeted version of
    /// what the Refresh button does (which clears and rebuilds the whole panel).
    ///
    /// Safe for undo: an entry is removed ONLY when its file is gone AND it holds no
    /// undo state. The live, renamed entry (whose file exists and carries UndoFilePath/
    /// UndoMetadata) is never touched. Gated behind a setting (default on) so it can be
    /// disabled without a rebuild if it ever misbehaves.
    /// </summary>
    private void SweepStalePanelEntries()
    {
        if (!Settings.AutoCleanStalePanelEntries) return;

        // Files is an ObservableCollection bound to the UI — mutating it must happen on
        // the UI thread. ApplyToFileAsync may resume on a thread-pool thread after its
        // awaits, so marshal to the dispatcher rather than assume the caller's context.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(SweepStalePanelEntriesCore);
        }
        else
        {
            SweepStalePanelEntriesCore();
        }
    }

    private void SweepStalePanelEntriesCore()
    {
        var stale = Files
            .Where(f => !f.IsSeparator
                        && f.UndoFilePath == null          // never remove an undoable entry
                        && f.UndoMetadata == null
                        && !string.IsNullOrWhiteSpace(f.FilePath)
                        && !File.Exists(f.FilePath))        // file is genuinely gone
            .ToList();

        foreach (var f in stale)
        {
            Files.Remove(f);
            Log( $"[{DateTime.Now:HH:mm:ss}] Cleaned stale panel entry: {f.FileName} (file no longer at that path)");
        }
    }

    /// <summary>
    /// Collapses multiple Files-panel entries that point at the SAME physical file into
    /// one. This is the deterministic (post-hoc) fix for the watch-detect + auto-embed
    /// rename race: the rename fires a FileSystemWatcher event that re-detects the file
    /// and adds a second entry (typically unverified/yellow) beside the embedded+verified
    /// one (green). Rather than try to win that timing race up front, we reconcile after:
    /// group by canonical path, and where a group has more than one entry, keep the best
    /// survivor and remove the others.
    ///
    /// Survivor precedence (never removes an entry holding undo state):
    ///   1. an entry holding undo state (so Undo Last Embed / Undo Batch never break),
    ///   2. else a DiskVerified == true entry (reflects real embedded+verified state),
    ///   3. else the entry with the most-complete metadata (title+rating present),
    ///   4. else the first.
    /// Marshalled to the UI thread, gated behind the same setting as the stale sweep.
    /// </summary>
    private void CollapseDuplicateEntries()
    {
        if (!Settings.AutoCleanStalePanelEntries) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.InvokeAsync(CollapseDuplicateEntriesCore);
        else
            CollapseDuplicateEntriesCore();
    }

    private void CollapseDuplicateEntriesCore()
    {
        string Canon(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

        var groups = Files
            .Where(f => !f.IsSeparator && !string.IsNullOrWhiteSpace(f.FilePath))
            .GroupBy(f => Canon(f.FilePath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in groups.ToList())
        {
            var entries = group.ToList();

            // Choose the survivor by precedence.
            var survivor =
                entries.FirstOrDefault(e => e.UndoFilePath != null || e.UndoMetadata != null)
                ?? entries.FirstOrDefault(e => e.DiskVerified == true)
                ?? entries.OrderByDescending(e =>
                        (string.IsNullOrWhiteSpace(e.EmbeddedMetadata?.Title) ? 0 : 1) +
                        ((e.EmbeddedMetadata?.Rating ?? 0f) > 0f ? 1 : 0))
                    .First();

            foreach (var dup in entries.Where(e => !ReferenceEquals(e, survivor)))
            {
                // Safety: never remove an entry that itself holds undo state, even if it
                // wasn't chosen as survivor (shouldn't happen given precedence, but guard
                // anyway so undo can never be orphaned).
                if (dup.UndoFilePath != null || dup.UndoMetadata != null) continue;
                Files.Remove(dup);
                Log( $"[{DateTime.Now:HH:mm:ss}] Merged duplicate panel entry: {dup.FileName} (same file as an existing entry)");
            }
        }
    }

    /// <summary>
    /// True if the Files panel already contains an entry for the same physical file as
    /// <paramref name="path"/>. Compares by canonical full path rather than raw string,
    /// so two forms of the same path (differing only by separators, casing, or
    /// normalization after a rename) are correctly treated as the same file. This is the
    /// guard against watch-detection creating a second entry for a file that's already
    /// listed — e.g. when the auto-embed rename fires a FileSystemWatcher event for the
    /// new name before that name is registered as known.
    /// </summary>
    private bool FilesContainsSamePath(string path)
    {
        string Canon(string p)
        {
            try { return Path.GetFullPath(p); } catch { return p; }
        }
        var target = Canon(path);
        return Files.Any(f => !f.IsSeparator
                              && !string.IsNullOrWhiteSpace(f.FilePath)
                              && Canon(f.FilePath).Equals(target, StringComparison.OrdinalIgnoreCase));
    }

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
        if (FilesContainsSamePath(path))
            return;

        // Wait briefly for the file to finish writing
        TryAddFile(path, isNew: true);
        string CanonLookup(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        var targetCanon = CanonLookup(path);
        var justAdded = Files.FirstOrDefault(f => !f.IsSeparator
            && !string.IsNullOrWhiteSpace(f.FilePath)
            && CanonLookup(f.FilePath).Equals(targetCanon, StringComparison.OrdinalIgnoreCase));
        if (justAdded != null)
        {
            justAdded.IsNewFile = true;
            _watchFolderService.AddKnownPath(path);
            StatusText = $"Watch folder: {justAdded.FileName} added.";

            // Auto-search in the background so results are ready when the user
            // clicks the file — they don't have to wait for the search to start.
            //
            // BUT skip it when auto-embed is enabled: in that case AutoEmbedFileAsync
            // (triggered from TryAddFile) already searches and embeds this same file.
            // Running a second, independent search here was redundant (double the TMDB
            // calls) and — because that search wrote its result to the entry after
            // auto-embed had already embedded+renamed — produced a divergent duplicate
            // panel row (e.g. a yellow, unverified entry carrying a later-fetched rating
            // alongside the green verified one). One flow, one entry.
            if (!Settings.AutoEmbedEnabled
                && !string.IsNullOrWhiteSpace(justAdded.EmbeddedMetadata.Title))
            {
                var title = justAdded.ParsedTitle.Length > 0
                    ? justAdded.ParsedTitle : justAdded.EmbeddedMetadata.Title;
                var year  = justAdded.ParsedYear.Length > 0
                    ? justAdded.ParsedYear  : justAdded.EmbeddedMetadata.Year;
                _ = Task.Run(() =>
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                        () => ScheduleAutoSearch(justAdded, title, year)));
            }

            // If this detection re-added a file that's already represented (e.g. the
            // auto-embed rename fired a watcher event for the new name), collapse the
            // duplicate now, keeping the verified entry. Deterministic reconciliation —
            // runs after the entry exists, so it's immune to the detection timing race.
            CollapseDuplicateEntries();
        }
    }

    public void TryAddFile(string path, bool isNew = false)
    {
        // Silently ignore VME temp/backup files
        if (IsVmeTempPath(path)) return;
        if (!MetadataService.IsSupported(path)) return;
        if (FilesContainsSamePath(path)) return;

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
                    if (FilesContainsSamePath(path))
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

        // Count already-tagged files (have IsEpisode=true with ShowTitle embedded)
        var alreadyTagged = selected
            .Where(f => f.EmbeddedMetadata.IsEpisode &&
                        !string.IsNullOrWhiteSpace(f.EmbeddedMetadata.ShowTitle))
            .ToList();

        // If any are already tagged, ask whether to skip them
        bool skipTagged = false;
        if (alreadyTagged.Count > 0)
        {
            var answer = System.Windows.MessageBox.Show(
                $"{alreadyTagged.Count} of {selected.Count} selected file(s) already have TV episode metadata embedded.\n\n" +
                "Skip already-tagged files and only process untagged ones?",
                "TV Batch — Already Tagged Files",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Yes);

            if (answer == System.Windows.MessageBoxResult.Cancel) return;
            skipTagged = answer == System.Windows.MessageBoxResult.Yes;
        }

        if (skipTagged)
            selected = selected.Except(alreadyTagged).ToList();

        if (selected.Count == 0)
        {
            StatusText = "TV Batch — all selected files are already tagged.";
            return;
        }

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

        // Snapshot the pre-write path so SyncLibraryEntry can look up the existing
        // LibraryEntry by old path, even if the file gets renamed after the write.
        var preWritePath = file.FilePath;

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

        // Always mark embedded so next Scan Library forces a fresh TagLib# read.
        _libraryScanService?.MarkAsEmbedded(file.FilePath);

        // Sync the in-memory LibraryEntry + cache so the Library grid reflects the
        // embedded TV metadata immediately and survives the next scan without reverting.
        // lookupPath = path before rename; currentPath = final path after rename.
        // Single source-of-truth projection — grid, cache, and FILES-panel snapshot.
        SyncLibraryEntry(preWritePath, file.FilePath, meta, file);

        // This is the embed path the watch-folder auto-embed actually uses (not
        // ApplyToFileAsync). The rename above fires a FileSystemWatcher event that can
        // re-detect the file and add a duplicate panel entry, and a pre-rename entry can
        // be left pointing at the old (now-gone) path. Reconcile here — the point where
        // the phantom coexists with this verified entry — so duplicates are collapsed and
        // dead-path rows removed. Earlier builds placed this on ApplyToFileAsync only,
        // which auto-embed never calls, so it never ran for watched files.
        CollapseDuplicateEntries();
        SweepStalePanelEntries();

        // The re-detection that creates the phantom is asynchronous (FSW event + the
        // "wait for file to finish writing" delay), so it may land a few seconds AFTER
        // this point. Run the reconciliation again on a short delay to catch that
        // late-born duplicate. Self-marshalling + idempotent, so a redundant run is a
        // no-op. ~4s comfortably clears the watch's own settle delay.
        _ = Task.Delay(TimeSpan.FromSeconds(4)).ContinueWith(_ =>
        {
            CollapseDuplicateEntries();
            SweepStalePanelEntries();
        });

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
