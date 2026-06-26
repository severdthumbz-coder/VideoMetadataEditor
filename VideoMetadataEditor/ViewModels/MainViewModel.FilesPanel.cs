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
// │  MainViewModel.FilesPanel                                              
// │  Files Panel — file collection, load, refresh, delete, watch folder
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
    private void RemoveSelected()
    {
        if (SelectedFile != null) Files.Remove(SelectedFile);
    }

    /// <summary>Fires the DeleteFileRequested event — code-behind shows confirmation dialog.</summary>
    private void RequestDeleteFile()
    {
        if (SelectedFile == null) return;
        DeleteFileRequested?.Invoke(this, new DeleteFileEventArgs(SelectedFile.FilePath, SelectedFile.FileName));
    }

    /// <summary>Called by code-behind after user confirms deletion.</summary>
    public void ExecuteDeleteFile(string filePath)
    {
        try
        {
            var vf = Files.FirstOrDefault(f =>
                f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (vf != null)
            {
                Files.Remove(vf);
                if (SelectedFile == vf) ClearEditing();
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log( $"[{DateTime.Now:HH:mm:ss}] Deleted: {Path.GetFileName(filePath)}");
                StatusText = $"Deleted: {Path.GetFileName(filePath)}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Delete failed: {ex.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Delete error: {ex.Message}");
        }
    }

    /// <summary>Clears the file list and fires an event so code-behind can stop the player.</summary>
    private void ClearAllFiles()
    {
        // Stop the watcher so the cleared panel doesn't get re-populated by
        // the next poll scan. The watcher restarts when the user loads a new folder.
        if (Settings.WatchFolderEnabled)
            _watchFolderService.Stop();

        MediaPlayerStopRequested?.Invoke(this, EventArgs.Empty);
        SelectedFile = null;
        ClearFiles();
        ClearEditing();

        // Update status so user knows the watcher has stopped
        StatusText = "Files cleared. Watch Folder paused — load a folder to resume monitoring.";
    }

    public event EventHandler? MediaPlayerStopRequested;

    private async Task RefreshFilesAsync()
    {
        var ct = BeginOperation();
        IsBusy = true;
        ProgressValue = 0;
        var paths    = Files.Select(f => f.FilePath).ToList();
        var selected = SelectedFile?.FilePath;

        StatusText = $"Refreshing {paths.Count} file(s)…";
        MediaPlayerStopRequested?.Invoke(this, EventArgs.Empty);
        ClearFiles();
        ClearEditing();

        int total = paths.Count;

        try
        {
            // Profile from first existing path
            var firstPath = paths.FirstOrDefault(File.Exists);
            var profile = firstPath != null
                ? Services.DriveCapabilityService.GetProfile(firstPath)
                : Services.DriveCapabilityService.GetProfile(
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            int workers = profile.RecommendedMetadataWorkers;

            var dispatcher = System.Windows.Application.Current.Dispatcher;
            using var sem  = new SemaphoreSlim(workers);
            int processed  = 0;

            var readTasks = paths.Select((path, idx) => Task.Run(async () =>
            {
                if (!File.Exists(path)) return;
                await sem.WaitAsync(ct);
                if (ct.IsCancellationRequested) { sem.Release(); return; }
                try
                {
                    var format = MetadataService.DetectFormat(path);
                    var (supportsArt, supportsFullTags, warning) =
                        MetadataService.GetFormatCapabilities(format);
                    var info     = new FileInfo(path);
                    var embedded = _metadataService.ReadMetadataFast(path);
                    var (parsedTitle, parsedYear) =
                        FilenameParser.Parse(Path.GetFileNameWithoutExtension(path));

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
                        ParsedYear       = parsedYear
                    };
                    vf.PendingMetadata = vf.EmbeddedMetadata.Clone();

                    await dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        Files.Add(vf);
                        processed++;
                        ProgressValue = (int)((double)processed / total * 100);
                        StatusText    = $"Refreshing {processed} / {total} files…";
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{System.IO.Path.GetFileNameWithoutExtension(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Name ?? "VM")}] {ex.GetType().Name}: {ex.Message}"); }
                finally { sem.Release(); }
            })).ToList();

            await Task.WhenAll(readTasks);

            // Re-seed watch service with all refreshed file paths so the poll
            // timer doesn't re-fire them as new arrivals on the next tick
            if (_watchFolderService.IsActive)
            {
                foreach (var f in Files.Where(x => !x.IsSeparator))
                    _watchFolderService.AddKnownPath(f.FilePath);
            }

            if (selected != null)
            {
                var restored = Files.FirstOrDefault(f =>
                    f.FilePath.Equals(selected, StringComparison.OrdinalIgnoreCase));
                if (restored != null)
                    SelectedFile = restored;
            }

            StatusText = $"Refreshed {Files.Count(f => !f.IsSeparator)} file(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Refresh cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Refresh error: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    // ── Copy / Move ───────────────────────────────────────────────────────────

    // ── Undo last embed ───────────────────────────────────────────────────────

    private async Task UndoLastEmbedAsync()
    {
        var vf = SelectedFile;
        if (vf?.UndoMetadata == null) return;
        IsBusy = true;
        StatusText = $"Undoing embed for {vf.FileName}…";
        try
        {
            // Restore original tags
            var ok = await _metadataService.WriteMetadataAsync(
                vf.FilePath, vf.UndoMetadata, Settings, null);

            // Restore original filename if it changed
            if (vf.UndoFilePath != null &&
                !vf.UndoFilePath.Equals(vf.FilePath, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(vf.UndoFilePath))
            {
                File.Move(vf.FilePath, vf.UndoFilePath);
                vf.FilePath = vf.UndoFilePath;
                OriginalFileName = vf.FileName;
            }

            if (ok)
            {
                CopyMetadataTo(vf.UndoMetadata, EditingMetadata);
                vf.EmbeddedMetadata = vf.UndoMetadata.Clone();
                vf.PendingMetadata  = vf.UndoMetadata.Clone();
                vf.UndoFilePath = null;
                vf.UndoMetadata = null;
                vf.IsDone = false;
                vf.HasError = false;
                StatusText = $"Undo complete — {vf.FileName}";
            }
            else StatusText = "Undo failed — could not write original tags.";
        }
        catch (Exception ex)
        {
            StatusText = $"Undo error: {ex.Message}";
        }
        finally
        {
            EndOperation(resetProgress: false);
            RaiseProperty(nameof(UndoLastEmbedCommand));
        }
    }

    // ── Load more search results (pagination) ─────────────────────────────────

    private async Task LoadMoreResultsAsync()
    {
        _searchPage++;
        var next = _allSearchResults
            .Skip(_searchPage * PageSize)
            .Take(PageSize)
            .ToList();
        foreach (var r in next) SearchResults.Add(r);
        HasMoreResults = (_searchPage + 1) * PageSize < _allSearchResults.Count;
        StatusText = $"Showing {SearchResults.Count} of {_allSearchResults.Count} results.";
        await Task.CompletedTask;
    }

    // ── Bulk artwork download ─────────────────────────────────────────────────

    private async Task BulkDownloadArtworkAsync()
    {
        var targets = Files.Where(f => f.IsSelected &&
                                       f.EmbeddedMetadata.ArtworkBytes is not { Length: > 0 } &&
                                       !string.IsNullOrWhiteSpace(f.EmbeddedMetadata.TmdbId))
                           .ToList();
        if (targets.Count == 0) { StatusText = "No selected files need artwork (or lack TMDB IDs)."; return; }

        var ct = BeginOperation();
        IsBusy = true;
        int done = 0, ok = 0;
        StatusText = $"Downloading artwork for {targets.Count} file(s)…";
        ProgressValue = 0;

        try
        {
            var sem = new SemaphoreSlim(3);
            var tasks = targets.Select(async vf =>
            {
                await sem.WaitAsync();
                try
                {
                    (MovieMetadata? meta, string? _artErr) = await _apiService.GetTmdbDetailsAsync(
                        vf.EmbeddedMetadata.TmdbId, TmdbKey, ct);
                    if (meta?.ArtworkBytes is { Length: > 0 })
                    {
                        vf.EmbeddedMetadata.ArtworkBytes = meta.ArtworkBytes;
                        vf.PendingMetadata.ArtworkBytes  = meta.ArtworkBytes;
                        Interlocked.Increment(ref ok);
                        Log( $"[{DateTime.Now:HH:mm:ss}] Artwork: {vf.FileName}");
                    }
                }
                finally
                {
                    sem.Release();
                    Interlocked.Increment(ref done);
                    ProgressValue = (int)((double)done / targets.Count * 100);
                    StatusText = $"Downloading artwork {done} / {targets.Count}…";
                }
            });
            await Task.WhenAll(tasks);
            StatusText = $"Artwork downloaded for {ok} / {targets.Count} files.";
        }
        finally { EndOperation(); }
    }

    /// <summary>
    /// Writes poster artwork as sidecar image files next to each selected video, using
    /// the naming style from Settings (Kodi &lt;video&gt;-poster.jpg, or Plex/Jellyfin
    /// poster.jpg). For each file the artwork is taken from in-memory metadata, else read
    /// from the embedded tag, else downloaded via TMDB when a TmdbId is present. Files
    /// with no obtainable artwork are skipped (counted, not failed).
    /// </summary>
    private async Task ExportArtworkAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;

        var naming = Settings.ArtworkNamingStyle == 1
            ? Services.ArtworkSidecarService.ArtworkNaming.PlexJellyfin
            : Services.ArtworkSidecarService.ArtworkNaming.Kodi;

        var ct = BeginOperation();
        IsBusy = true;
        int done = 0, ok = 0, skipped = 0, failed = 0;
        StatusText = $"Exporting artwork for {selected.Count} file(s)…";
        ProgressValue = 0;

        try
        {
            foreach (var vf in selected)
            {
                if (ct.IsCancellationRequested) break;
                done++;
                ProgressValue = (int)((double)done / selected.Count * 100);
                StatusText = $"Exporting artwork {done}/{selected.Count}: {Path.GetFileName(vf.FilePath)}";

                try
                {
                    // 1. In-memory bytes (already loaded/downloaded).
                    byte[]? art = vf.EmbeddedMetadata.ArtworkBytes is { Length: > 0 }
                        ? vf.EmbeddedMetadata.ArtworkBytes
                        : vf.PendingMetadata.ArtworkBytes is { Length: > 0 }
                            ? vf.PendingMetadata.ArtworkBytes
                            : null;

                    // 2. Read embedded artwork from the file.
                    if (art is null)
                        art = await Task.Run(() => _metadataService.ReadArtworkOnly(vf.FilePath), ct);

                    // 3. Download from TMDB if we have an ID and still no art.
                    if (art is null && !string.IsNullOrWhiteSpace(vf.EmbeddedMetadata.TmdbId))
                    {
                        var (meta, _) = await _apiService.GetTmdbDetailsAsync(
                            vf.EmbeddedMetadata.TmdbId, TmdbKey, ct);
                        if (meta?.ArtworkBytes is { Length: > 0 })
                        {
                            art = meta.ArtworkBytes;
                            vf.EmbeddedMetadata.ArtworkBytes = art; // cache for reuse
                        }
                    }

                    if (art is not { Length: > 0 })
                    {
                        skipped++;
                        Log($"[{DateTime.Now:HH:mm:ss}] Artwork export skipped (no image): {Path.GetFileName(vf.FilePath)}");
                        continue;
                    }

                    var (wOk, path, err) = await Services.ArtworkSidecarService.WriteSidecarAsync(
                        vf.FilePath, art, naming, vf.EmbeddedMetadata.IsEpisode, overwrite: true, ct: ct);
                    if (wOk)
                    {
                        ok++;
                        Log($"[{DateTime.Now:HH:mm:ss}] Artwork → {Path.GetFileName(path)}");
                    }
                    else
                    {
                        failed++;
                        Log($"[{DateTime.Now:HH:mm:ss}] Artwork export failed: {Path.GetFileName(vf.FilePath)} — {err}");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Log($"[{DateTime.Now:HH:mm:ss}] Artwork export error: {Path.GetFileName(vf.FilePath)} — {ex.Message}");
                }
            }
            StatusText = $"Artwork export: {ok} written, {skipped} skipped, {failed} failed.";
        }
        catch (OperationCanceledException) { /* keep partial results */ }
        finally { EndOperation(); IsBusy = false; }
    }

    /// <summary>
    /// Imports .nfo sidecar metadata for the selected file(s) into the editable fields
    /// for review — nothing is written to the video until the user explicitly embeds.
    /// For a single selected file the parsed data is loaded into the editor; for multiple
    /// files each file's pending/retrieved metadata is populated from its own sidecar.
    /// Files with no sidecar or an unparseable one are skipped (counted, not failed).
    /// </summary>
    private async Task ImportNfoAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;

        var ct = BeginOperation();
        IsBusy = true;
        int done = 0, ok = 0, skipped = 0, failed = 0;
        StatusText = $"Importing NFO for {selected.Count} file(s)…";
        ProgressValue = 0;

        // Keep a reference to the single-file case so we can load it into the editor after.
        VideoFile? singleTarget = selected.Count == 1 ? selected[0] : null;

        try
        {
            foreach (var vf in selected)
            {
                if (ct.IsCancellationRequested) break;
                done++;
                ProgressValue = (int)((double)done / selected.Count * 100);
                StatusText = $"Importing NFO {done}/{selected.Count}: {Path.GetFileName(vf.FilePath)}";

                try
                {
                    var nfoPath = Services.NfoExportService.SidecarPathFor(vf.FilePath);
                    if (!File.Exists(nfoPath))
                    {
                        skipped++;
                        Log($"[{DateTime.Now:HH:mm:ss}] NFO import skipped (no sidecar): {Path.GetFileName(vf.FilePath)}");
                        continue;
                    }

                    var xml = await File.ReadAllTextAsync(nfoPath, ct);
                    var parsed = Services.NfoImportService.ParseNfo(xml);
                    if (parsed is null)
                    {
                        failed++;
                        Log($"[{DateTime.Now:HH:mm:ss}] NFO import failed (unparseable): {Path.GetFileName(nfoPath)}");
                        continue;
                    }

                    // Preserve the file's existing artwork — NFO carries none.
                    if (vf.EmbeddedMetadata.ArtworkBytes is { Length: > 0 })
                        parsed.ArtworkBytes = vf.EmbeddedMetadata.ArtworkBytes;

                    // Load into the file's metadata for review (NOT written to disk).
                    vf.RetrievedMetadata = parsed;
                    CopyMetadataTo(parsed, vf.PendingMetadata);
                    ok++;
                    Log($"[{DateTime.Now:HH:mm:ss}] NFO imported: {Path.GetFileName(nfoPath)}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Log($"[{DateTime.Now:HH:mm:ss}] NFO import error: {Path.GetFileName(vf.FilePath)} — {ex.Message}");
                }
            }

            // Single-file: surface the imported data in the editable fields right away.
            if (singleTarget?.RetrievedMetadata != null && SelectedFile == singleTarget)
            {
                RetrievedMetadata = singleTarget.RetrievedMetadata;
                ApplyRetrievedToEditing();
            }

            StatusText = ok > 0
                ? $"NFO import: {ok} loaded for review, {skipped} skipped, {failed} failed. Review, then Embed to write."
                : $"NFO import: {skipped} skipped, {failed} failed.";
        }
        catch (OperationCanceledException) { /* keep partial results */ }
        finally { EndOperation(); IsBusy = false; }
    }

    // ── Export CSV ───────────────────────────────────────────────────────────

    private async Task ExportCsvAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Export metadata to CSV",
            Filter     = "CSV Files|*.csv|All Files|*.*",
            FileName   = $"VideoMetadata_{DateTime.Now:yyyyMMdd}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        var ct = BeginOperation();
        IsBusy = true;
        StatusText = "Exporting CSV…";
        try
        {
            var lines = new List<string>
            {
                "\"File\",\"Title\",\"Year\",\"Genre\",\"Director\",\"Cast\",\"IMDB ID\",\"TMDB ID\",\"Size\",\"Format\",\"Watched\""
            };
            foreach (var vf in Files)
            {
                var m = vf.EmbeddedMetadata;
                lines.Add(string.Join(",", new[]
                {
                    Q(vf.FileName), Q(m.Title), Q(m.Year), Q(m.Genre),
                    Q(m.Director),  Q(m.Cast),  Q(m.ImdbId), Q(m.TmdbId),
                    Q(vf.FileSizeDisplay), Q(vf.Extension), Q(vf.IsWatched ? "Yes" : "No")
                }));
            }
            await File.WriteAllLinesAsync(dlg.FileName, lines, System.Text.Encoding.UTF8);
            StatusText = $"Exported {Files.Count(f => !f.IsSeparator)} file(s) to CSV.";
            Log( $"[{DateTime.Now:HH:mm:ss}] CSV export: {dlg.FileName}");
        }
        catch (Exception ex) { StatusText = $"Export error: {ex.Message}"; }
        finally { EndOperation(resetProgress: false); }

        static string Q(string? s) => $"\"{(s ?? "").Replace("\"", "\"\"")}\"";
    }

    // ── Export NFO sidecar files ──────────────────────────────────────────────

    private async Task ExportNfoAsync()
    {
        var selected = Files.Where(f => f.IsSelected && !f.IsSeparator).ToList();
        if (selected.Count == 0) return;

        IsBusy = true;
        ProgressValue = 0;
        StatusText = $"Exporting NFO for {selected.Count} file(s)…";

        try
        {
            var pairs = selected.Select(vf => (vf.FilePath, vf.EmbeddedMetadata)).ToList();
            var progress = new System.Progress<(int done, int total)>(p =>
            {
                ProgressValue = (int)((double)p.done / p.total * 100);
                StatusText    = $"Exporting NFO {p.done} / {p.total}…";
            });

            var ct = BeginOperation();
            var (ok, failed) = await Services.NfoExportService.ExportAsync(
                pairs, progress, ct, writeTvShowNfo: Settings.WriteTvShowNfo);
            StatusText = $"NFO export: {ok} ok, {failed} failed.";
            Log( $"[{DateTime.Now:HH:mm:ss}] NFO export: {ok} written.");
        }
        catch (Exception ex) { StatusText = $"NFO export error: {ex.Message}"; }
        finally { EndOperation(); }
    }

    // ── Duplicate detector ────────────────────────────────────────────────────

    // ── File Lock / Unlock ────────────────────────────────────────────────────

    /// <summary>True if the currently selected file is read-only on disk.</summary>
    public bool SelectedFileIsReadOnly => SelectedFile?.IsReadOnly == true;

    private async void ToggleLockCurrentFile()
    {
        if (SelectedFile == null) return;
        try { await ToggleLockCurrentFileAsync(); }
        catch (Exception ex) { StatusText = $"Lock toggle error: {ex.Message}"; }
    }

    private async Task ToggleLockCurrentFileAsync()
    {
        if (SelectedFile == null) return;
        bool currently = SelectedFile.IsReadOnly;

        bool success;
        if (currently)
        {
            // Unlocking — use the robust service method
            MediaPlayerStopRequested?.Invoke(this, EventArgs.Empty);
            await Task.Delay(120);
            success = await Services.FileLockService.TryClearReadOnlyAsync(SelectedFile.FilePath);
        }
        else
        {
            // Locking — simple attribute set
            success = SelectedFile.TrySetReadOnly(true);
        }

        if (success)
        {
            // Invalidate the cached IsReadOnly value and raise property notifications
            SelectedFile.RefreshReadOnly();
            RaiseProperty(nameof(SelectedFileIsReadOnly));
            StatusText = SelectedFile.IsReadOnly
                ? $"🔒 Locked: {SelectedFile.FileName}"
                : $"🔓 Unlocked: {SelectedFile.FileName}";
            Log( $"[{DateTime.Now:HH:mm:ss}] {StatusText}");
        }
        else
        {
            StatusText = "Could not change lock state — check NTFS permissions.";
        }
    }

    private async void FindDuplicates()
    {
        if (Files.Count < 2) return;
        // async void: exceptions must be caught here — they cannot propagate to caller
        try { await FindDuplicatesAsync(); }
        catch (Exception ex)
        {
            StatusText = $"Duplicate scan error: {ex.Message}";
            EndOperation();
        }
    }

    private async Task FindDuplicatesAsync()
    {
        // BeginOperation FIRST: refreshes the CancellationTokenSource so Cancel works
        var ct = BeginOperation();
        IsBusy = true;
        ProgressValue = 0;
        StatusText = $"Scanning {Files.Count(f => !f.IsSeparator)} files for duplicates…";
        Log( $"[{DateTime.Now:HH:mm:ss}] Duplicate scan started — {Files.Count(f => !f.IsSeparator)} files");

        try
        {
            var progress = new System.Progress<(string current, int done, int total)>(p =>
            {
                ProgressValue = p.total > 0 ? (int)((double)p.done / p.total * 100) : 0;
                StatusText    = $"Hashing {p.done}/{p.total}: {p.current}";
            });

            // DetectAsync is already async — awaiting directly is correct
            var groups = await Services.DuplicateDetectorService.DetectAsync(
                Files, useHashing: true, progress: progress, ct: ct);

            Duplicates = new ObservableCollection<Services.DuplicateGroup>(groups);

            int totalDupes = Duplicates.Sum(g => g.Files.Count - 1);
            if (Duplicates.Count == 0)
            {
                StatusText = "No duplicates found.";
                Log( $"[{DateTime.Now:HH:mm:ss}] No duplicates found.");
            }
            else
            {
                StatusText = $"Found {Duplicates.Count} duplicate group(s) — {totalDupes} redundant file(s).";
                Log( $"[{DateTime.Now:HH:mm:ss}] {StatusText}");
                foreach (var g in Duplicates)
                    Log(
                        $"[{DateTime.Now:HH:mm:ss}]   {g.ConfidenceLabel}: " +
                        string.Join(", ", g.Files.Select(f => f.FileName)));
                SelectedTabIndex = 5;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Duplicate scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Duplicate scan error: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task AnalyzeSpaceAsync()
    {
        var sources = Files.Where(f => f.IsSelected && !f.IsSeparator).Select(f => f.FilePath).ToList();
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(CopyDestination)) return;

        StatusText = "Analysing disk space…";
        var analysis = await Task.Run(() =>
            Services.FileCopyService.AnalyseDiskSpace(sources, CopyDestination));

        // Source info
        long totalBytes = sources.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        SpaceSourceInfo  = $"{sources.Count} file(s) ({Services.FileCopyService.FormatBytes(totalBytes)})";

        // Destination drive info
        long available = analysis.AvailableBytes;
        long totalDrive = 0;
        try
        {
            var root = Path.GetPathRoot(CopyDestination) ?? CopyDestination;
            var di   = new DriveInfo(root);
            totalDrive = di.TotalSize;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[{System.IO.Path.GetFileNameWithoutExtension(System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Name ?? "VM")}] {ex.GetType().Name}: {ex.Message}"); }

        SpaceDestInfo    = totalDrive > 0
            ? $"{Services.FileCopyService.FormatBytes(available)} / {Services.FileCopyService.FormatBytes(totalDrive)}"
            : Services.FileCopyService.FormatBytes(available);

        // Disk usage %
        SpaceDiskUsage   = totalDrive > 0
            ? $"{(double)(totalDrive - available) / totalDrive * 100:F1}%"
            : "N/A";

        SpaceIsSufficient = analysis.HasSufficientSpace;
        SpaceStatusMsg    = analysis.HasSufficientSpace
            ? "✓ Sufficient space available"
            : $"✗ Insufficient space — need {analysis.RequiredDisplay}, only {analysis.AvailableDisplay} free";

        SpaceAnalysisVisible = true;
        DiskSpaceStatus      = analysis.Message;
        StatusText           = analysis.Message;
    }

    private void BrowseCopyDestination()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select destination folder"
        };
        if (!string.IsNullOrWhiteSpace(CopyDestination) && Directory.Exists(CopyDestination))
            dlg.InitialDirectory = CopyDestination;

        if (dlg.ShowDialog() == true)
            CopyDestination = dlg.FolderName;
    }

    /// <summary>
    /// Loads a remux candidate file into the FILES panel, flagged as IsRemuxCandidate
    /// so the Preview panel shows Replace/Restore buttons. originalPath points at the
    /// untouched original. If the candidate is already in the panel, just re-selects it.
    /// </summary>
    public void LoadRemuxCandidate(string candidatePath, string originalPath)
    {
        try
        {
            if (!File.Exists(candidatePath)) return;

            var existing = Files.FirstOrDefault(f =>
                f.FilePath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsRemuxCandidate = true;
                existing.OriginalPath     = originalPath;
                SelectedFile = existing;
                return;
            }

            var format = MetadataService.DetectFormat(candidatePath);
            var (supportsArt, supportsFullTags, warning) =
                MetadataService.GetFormatCapabilities(format);
            var info     = new FileInfo(candidatePath);
            var embedded = _metadataService.ReadMetadataFast(candidatePath);
            var (parsedTitle, parsedYear) =
                FilenameParser.Parse(Path.GetFileNameWithoutExtension(candidatePath));

            if (!string.IsNullOrWhiteSpace(parsedTitle))
                embedded.Title = parsedTitle;
            else if (string.IsNullOrWhiteSpace(embedded.Title))
                embedded.Title = Path.GetFileNameWithoutExtension(candidatePath);
            if (!string.IsNullOrWhiteSpace(parsedYear))
                embedded.Year = parsedYear;

            var vf = new VideoFile
            {
                FilePath         = candidatePath,
                Format           = format,
                FileSizeBytes    = info.Length,
                SupportsArtwork  = supportsArt,
                SupportsFullTags = supportsFullTags,
                FormatWarning    = warning,
                EmbeddedMetadata = embedded,
                ParsedTitle      = parsedTitle,
                ParsedYear       = parsedYear,
                IsRemuxCandidate = true,
                OriginalPath     = originalPath
            };
            vf.PendingMetadata = vf.EmbeddedMetadata.Clone();
            Files.Add(vf);
            SelectedFile = vf;
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't load remux candidate: {ex.Message}";
        }
    }

    /// <summary>
    /// Commits a remux candidate: Replace (adopt candidate, delete original) or
    /// Restore (discard candidate, keep original). Updates the FILES panel afterward.
    /// </summary>
    public void CommitRemux(VideoFile candidate, bool replace)
    {
        if (candidate is not { IsRemuxCandidate: true }) return;

        // Read original metadata BEFORE ReplaceOriginal deletes the source file.
        // Use the full read (not Fast) so cover art is captured — otherwise the
        // remuxed file would lose its artwork on auto-embed.
        MovieMetadata? originalMeta = null;
        if (replace && !string.IsNullOrWhiteSpace(candidate.OriginalPath) &&
            File.Exists(candidate.OriginalPath))
        {
            try { originalMeta = _metadataService.ReadMetadata(candidate.OriginalPath); }
            catch { /* non-fatal — user can embed manually */ }
        }

        var result = replace
            ? Services.RemuxCommitService.ReplaceOriginal(candidate.FilePath, candidate.OriginalPath)
            : Services.RemuxCommitService.RestoreOriginal(candidate.FilePath, candidate.OriginalPath);

        if (!result.Success)
        {
            StatusText = result.Message;
            System.Windows.MessageBox.Show(result.Message, "Remux commit failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // Remove the candidate row from the panel
        Files.Remove(candidate);

        // On Replace: evict the old library cache entry for the original, the
        // candidate, AND the actual final path. The final path differs from both
        // when the container extension changed (e.g. original .mkv → remuxed .mp4),
        // and a stale cache entry under that final path would otherwise be served on
        // the next scan. The remuxed file has no tags yet (the auto-embed below
        // re-populates), so a fresh read must win.
        if (replace)
        {
            _libraryCacheService.Evict(candidate.OriginalPath ?? string.Empty);
            _libraryCacheService.Evict(candidate.FilePath);
            if (result.FinalPath != null)
                _libraryCacheService.Evict(result.FinalPath);
        }

        // On Replace, optionally surface the adopted file; on Restore, the original stays on disk
        StatusText = result.Message;

        if (replace && result.FinalPath != null && File.Exists(result.FinalPath))
        {
            // Load the now-clean final file so the user can immediately retry embedding
            var existing = Files.FirstOrDefault(f =>
                f.FilePath.Equals(result.FinalPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
                TryAddFile(result.FinalPath);
            else
                SelectedFile = existing;

            // Auto-embed the original metadata into the remuxed file.
            // The remux creates a clean container with no tags — without this step
            // the user would have to manually re-embed every time. We do it silently
            // in the background; if it fails the file is still usable (just needs
            // a manual embed) and the status bar shows the result.
            if (originalMeta != null && !string.IsNullOrWhiteSpace(originalMeta.Title))
            {
                _ = Task.Run(async () =>
                {
                    var writeOk = await _metadataService.WriteMetadataAsync(
                        result.FinalPath, originalMeta, Settings);
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (writeOk)
                        {
                            StatusText = $"✓ Metadata restored to {Path.GetFileName(result.FinalPath)}";
                            // Project verified disk content into the loaded file, grid,
                            // and cache via the single source-of-truth path. Passing the
                            // VideoFile lets SyncLibraryEntry refresh EmbeddedMetadata from
                            // the actual disk re-read and set the DiskVerified badge,
                            // rather than optimistically showing the intended metadata.
                            var loaded = Files.FirstOrDefault(f =>
                                f.FilePath.Equals(result.FinalPath, StringComparison.OrdinalIgnoreCase));
                            _libraryScanService?.MarkAsEmbedded(result.FinalPath);
                            SyncLibraryEntry(result.FinalPath, result.FinalPath, originalMeta, loaded);
                        }
                        else
                            StatusText = $"⚠ Metadata restore failed — embed manually.";
                    });
                });
            }
        }
    }

    /// <summary>
    /// Undoes the last batch embed: restores original tags and filenames for
    /// every file that succeeded in the most recent batch run.
    /// </summary>
    private async Task UndoBatchAsync()
    {
        if (_batchUndoFiles.Count == 0) return;

        var files = _batchUndoFiles.ToList(); // snapshot — don't hold the list lock
        var count  = files.Count;
        IsBusy     = true;
        StatusText = $"Undoing batch — reverting {count} file(s)…";

        int reverted = 0, failed = 0;

        foreach (var vf in files)
        {
            if (vf.UndoMetadata == null) continue;
            try
            {
                var ok = await _metadataService.WriteMetadataAsync(
                    vf.FilePath, vf.UndoMetadata, Settings, null);

                if (vf.UndoFilePath != null &&
                    !vf.UndoFilePath.Equals(vf.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(vf.UndoFilePath))
                {
                    File.Move(vf.FilePath, vf.UndoFilePath);
                    vf.FilePath = vf.UndoFilePath;
                }

                if (ok)
                {
                    vf.EmbeddedMetadata = vf.UndoMetadata.Clone();
                    vf.PendingMetadata  = vf.UndoMetadata.Clone();
                    vf.UndoFilePath     = null;
                    vf.UndoMetadata     = null;
                    vf.WriteStatus      = Models.WriteStatus.Untouched;
                    vf.IsDone           = false;
                    vf.HasError         = false;
                    reverted++;
                }
                else failed++;
            }
            catch { failed++; }
        }

        _batchUndoFiles.Clear();
        RaiseProperty(nameof(CanUndoBatch));
        RaiseProperty(nameof(UndoBatchCommand));

        EndOperation(resetProgress: false);
        StatusText = failed == 0
            ? $"Batch undo complete — {reverted} file(s) reverted."
            : $"Batch undo: {reverted} reverted, {failed} failed.";

        Log($"[{DateTime.Now:HH:mm:ss}] ↩ Batch undo: {reverted} reverted, {failed} failed.");
    }

}
