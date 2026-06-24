using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.ViewModels;

using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs  = System.Windows.Input.KeyEventArgs;

namespace VideoMetadataEditor.Views;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;
    private ToastNotification? _toast;
    private CompletionBanner?  _banner;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                $"[{DateTime.Now}] InitializeComponent FAILED:\n{ex}\n\n");
            throw;
        }

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Content is Grid rootGrid)
            {
                _toast = new ToastNotification
                {
                    VerticalAlignment   = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Visibility          = Visibility.Collapsed
                };
                Grid.SetRow(_toast, 0);
                Grid.SetRowSpan(_toast, 4);
                Panel.SetZIndex(_toast, 99);
                rootGrid.Children.Add(_toast);

                _banner = new CompletionBanner
                {
                    VerticalAlignment   = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Visibility          = Visibility.Collapsed
                };
                Grid.SetRow(_banner, 2);
                Panel.SetZIndex(_banner, 200);
                rootGrid.Children.Add(_banner);
            }

            VM.RenameSucceeded       += OnRenameSucceeded;
            VM.RenameFailed          += OnRenameFailed;
            VM.BatchCompleted        += OnBatchCompleted;
            VM.EmbedSucceeded        += OnEmbedSucceeded;
            VM.FilesLoaded           += OnFilesLoaded;
            VM.CopyCompleted         += OnCopyCompleted;
            VM.MoveCompleted         += OnMoveCompleted;
            VM.DeleteFileRequested   += OnDeleteFileRequested;
            VM.MediaPlayerStopRequested += (_, _) => StopPreview_Click(this, new RoutedEventArgs());

            // The Duplicates compare uses a custom WPF window; the ViewModel asks for it
            // via a callback so it stays free of the Window type. Return the deleted path.
            VM.DuplicatesVM.CompareDialogRequested = (left, right) =>
            {
                var dlg = new Views.DuplicateCompareDialog(left, right)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                return dlg.ShowDialog() == true ? dlg.DeletedPath : null;
            };

            // When a Health Check remux creates a candidate, load it into the FILES
            // panel (where Replace/Restore live) and switch to that tab.
            VM.MediaHealthVM.RemuxCandidateCreated += (candidatePath, originalPath) =>
            {
                VM.LoadRemuxCandidate(candidatePath, originalPath);
                if (MainTabControl != null) MainTabControl.SelectedIndex = 0; // Files tab
            };

            // When the user asks to re-embed semicolon-flagged files from Health Check,
            // load them into the FILES panel and switch to that tab for re-fetch + Embed.
            VM.MediaHealthVM.RequestReembed += paths =>
            {
                VM.LoadFilesForReembed(paths);
                if (MainTabControl != null) MainTabControl.SelectedIndex = 0; // Files tab
            };
            VM.WriteFailedDetailed   += OnWriteFailedDetailed;

            VM.RecoveryCandidatesFound += OnRecoveryCandidatesFound;

            // Wire sync delegate so Save Layout captures live column widths even
            // when the user resized columns without reordering them.
            VM.SyncLibraryGridBeforeSave = SyncColumnOrderFromDataGrid;

            // Trigger startup recovery NOW (after subscribing, to avoid race condition)
            VM.TriggerStartupRecovery();

            // Auto-load the previous folder when Watch Folder is enabled.
            // This is the missing piece: without it the FILES panel is always empty
            // on launch and the user has to manually re-add the folder every session.
            // Only runs when WatchFolderEnabled is on — respects the user's intent.
            if (VM.Settings.WatchFolderEnabled
                && !string.IsNullOrWhiteSpace(VM.Settings.LastFolderPath)
                && System.IO.Directory.Exists(VM.Settings.LastFolderPath))
            {
                _ = VM.LoadFolderDirectAsync(VM.Settings.LastFolderPath);
            }

            // If library entries were pre-loaded from cache, rebuild tabs now
            if (VM.LibraryEntries.Count > 0)
                VM.RebuildLibraryTabs();

            VM.RequestSwitchToRawData += (_, _) =>
            {
                // Switch to RAW DATA tab (index 0)
                if (MainTabControl != null) MainTabControl.SelectedIndex = 0;
            };

            // Stop player when selection changes
            _vmPropertyChangedHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(VM.SelectedFile))
                    StopPreview_Click(this, new RoutedEventArgs());
            };
            VM.PropertyChanged += _vmPropertyChangedHandler;
        }
        catch (Exception ex)
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log"),
                $"[{DateTime.Now}] OnLoaded FAILED:\n{ex}\n\n");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        VM.RenameSucceeded          -= OnRenameSucceeded;
        VM.RenameFailed             -= OnRenameFailed;
        VM.BatchCompleted           -= OnBatchCompleted;
        VM.EmbedSucceeded           -= OnEmbedSucceeded;
        VM.FilesLoaded              -= OnFilesLoaded;
        VM.DeleteFileRequested      -= OnDeleteFileRequested;
        VM.WriteFailedDetailed      -= OnWriteFailedDetailed;
        VM.RecoveryCandidatesFound  -= OnRecoveryCandidatesFound;
        if (_vmPropertyChangedHandler != null)
            VM.PropertyChanged      -= _vmPropertyChangedHandler;
        // Stop popout if open
        _popout?.Close();
        // Tear down watch folder watcher
        VM.DisposeWatchResources(); // clean up FSW, poll timer, library CTS
    }

    // ── Banner / Toast handlers ───────────────────────────────────────────────

    private void OnRenameSucceeded(object? sender, RenameEventArgs e) =>
        _banner?.ShowRich(
            "File Renamed Successfully",
            $"{e.OldName}  →  {e.NewName}",
            BannerType.Success, 5,
            new BannerChip { Icon = "✓", Label = "Metadata embedded" },
            new BannerChip { Icon = "↗", Label = "File renamed" });

    private void OnRenameFailed(object? sender, RenameEventArgs e) =>
        _banner?.Show(
            "Rename Failed",
            string.IsNullOrWhiteSpace(e.Error) ? e.OldName : e.Error,
            BannerType.Error, 7);

    private void OnEmbedSucceeded(object? sender, EmbedEventArgs e) =>
        _banner?.Show(
            e.WasRenamed ? "Metadata Embedded + Renamed" : "Metadata Embedded",
            e.FileName,
            BannerType.Success, 4);

    private void OnCopyCompleted(object? sender, TransferEventArgs e)
    {
        var dest = System.IO.Path.GetFileName(
            e.Destination.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
        var size = VideoMetadataEditor.Services.FileCopyService.FormatBytes(e.BytesMoved);
        var type = e.HasErrors ? BannerType.Warning : BannerType.Copy;

        _banner?.ShowRich(
            e.HasErrors ? "Copy Completed with Errors" : "Copy Complete",
            "→  " + dest,
            type, 6,
            new BannerChip { Icon = "⧉", Label = e.Succeeded + " copied" },
            new BannerChip { Icon = "✓",  Label = e.Verified  + " verified" },
            e.HasErrors
                ? new BannerChip { Icon = "✕", Label = e.Failed + " failed" }
                : new BannerChip { Icon = "📦", Label = size });
    }

    private void OnMoveCompleted(object? sender, TransferEventArgs e)
    {
        var dest = System.IO.Path.GetFileName(
            e.Destination.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
        var size = VideoMetadataEditor.Services.FileCopyService.FormatBytes(e.BytesMoved);
        var type = e.HasErrors ? BannerType.Warning : BannerType.Move;

        _banner?.ShowRich(
            e.HasErrors ? "Move Completed with Errors" : "Move Complete",
            "↗  " + dest,
            type, 6,
            new BannerChip { Icon = "↗", Label = e.Succeeded + " moved" },
            new BannerChip { Icon = "✓",  Label = e.Verified  + " verified" },
            e.HasErrors
                ? new BannerChip { Icon = "✕", Label = e.Failed + " failed" }
                : new BannerChip { Icon = "📦", Label = size });
    }
    private void OnBatchCompleted(object? sender, BatchEventArgs e)
    {
        if (e.FailCount == 0)
        {
            var chips = new List<BannerChip>
                { new() { Icon = "✓", Label = $"{e.SuccessCount} processed" } };
            if (e.RenamedCount > 0)
                chips.Add(new() { Icon = "↗", Label = $"{e.RenamedCount} renamed" });
            if (e.SuccessCount > 0)
                chips.Add(new BannerChip
                {
                    Icon    = "↩",
                    Label   = "Undo Batch",
                    Command = VM.UndoBatchCommand,
                    ToolTip = $"Restore all {e.SuccessCount} files to their state before this batch"
                });

            _banner?.ShowRich(
                $"Batch Complete  ·  {e.SuccessCount} file{(e.SuccessCount != 1 ? "s" : "")} processed",
                e.RenamedCount > 0
                    ? $"{e.RenamedCount} renamed and embedded"
                    : "All metadata embedded",
                BannerType.Success, 7, chips.ToArray());
        }
        else
        {
            var chips = new List<BannerChip>
            {
                new() { Icon = "✓", Label = $"{e.SuccessCount} ok" },
                new() { Icon = "✕", Label = $"{e.FailCount} failed" }
            };
            if (e.SuccessCount > 0)
                chips.Add(new BannerChip
                {
                    Icon    = "↩",
                    Label   = "Undo Batch",
                    Command = VM.UndoBatchCommand,
                    ToolTip = $"Revert the {e.SuccessCount} files that succeeded"
                });

            _banner?.ShowRich(
                $"Batch Finished  ·  {e.FailCount} error{(e.FailCount != 1 ? "s" : "")}",
                $"{e.SuccessCount} succeeded · {e.FailCount} failed",
                BannerType.Warning, 8, chips.ToArray());
        }
    }

    private void OnFilesLoaded(object? sender, FilesLoadedEventArgs e)
    {
        // Only show banner when loading a folder with multiple files
        if (e.Added < 2) return;

        _banner?.ShowRich(
            $"Folder Loaded  ·  {e.Added} file{(e.Added != 1 ? "s" : "")} added",
            $"From: {e.FolderName}",
            BannerType.Info, 5,
            new BannerChip { Icon = "📂", Label = $"{e.Added} loaded" },
            new BannerChip { Icon = "🔍", Label = "Auto-searching…" });
    }

    // ── Delete file — confirmation dialog ─────────────────────────────────────

    private void OnDeleteFileRequested(object? sender, DeleteFileEventArgs e)
    {
        var result = MessageBox.Show(
            $"Permanently delete this file from disk?\n\n{e.FileName}\n\nThis cannot be undone.",
            "Delete File — Are you sure?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);  // default to No for safety

        if (result == MessageBoxResult.Yes)
            VM.ExecuteDeleteFile(e.FilePath);
    }

    // ── Drag & Drop ───────────────────────────────────────────────────────────

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        int added = 0;
        foreach (var path in files)
        {
            if (System.IO.Directory.Exists(path))
            {
                var exts = new[] { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };
                var found = System.IO.Directory.EnumerateFiles(path, "*.*",
                    System.IO.SearchOption.AllDirectories)
                    .Where(f => exts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    .ToList();
                foreach (var f in found) { VM.TryAddFile(f); added++; }

                // Banner for drag-dropped folders
                if (found.Count >= 2)
                    _banner?.ShowRich(
                        $"Folder Loaded  ·  {found.Count} file{(found.Count != 1 ? "s" : "")} added",
                        $"From: {System.IO.Path.GetFileName(path.TrimEnd('\\', '/'))}",
                        BannerType.Info, 5,
                        new BannerChip { Icon = "📂", Label = $"{found.Count} loaded" },
                        new BannerChip { Icon = "🔍", Label = "Auto-searching…" });
            }
            else
            {
                VM.TryAddFile(path);
                added++;
            }
        }
    }

    private void Artwork_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length > 0) VM.SetArtworkFromFile(files[0]);
    }

    private void Artwork_Click(object sender, MouseButtonEventArgs e) => VM.PickArtwork();

    // ── Media Health Check ──────────────────────────────────────────────────────

    private void BrowseHealthFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder to scan for media health problems",
            Multiselect = false
        };
        if (dlg.ShowDialog() == true)
            VM.MediaHealthVM.Folder = dlg.FolderName;
    }

    /// <summary>Handles Hyperlink clicks in the Help tab's Credits section.</summary>
    private void HelpLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* browser unavailable — silently ignore */ }
        e.Handled = true;
    }

    private void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.gyan.dev/ffmpeg/builds/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Couldn't open the browser. Download ffmpeg manually from:\n" +
                "https://www.gyan.dev/ffmpeg/builds/\n\n" + ex.Message,
                "Download ffmpeg", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }

    private void DownloadMkvToolNix_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "https://mkvtoolnix.download/downloads.html",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var nativeDir = Services.NativeLibraryExtractor.NativeDir;
            System.Windows.MessageBox.Show(
                "Couldn't open the browser. Download MKVToolNix manually from:\n" +
                "https://mkvtoolnix.download/downloads.html\n\n" +
                "Then drop mkvpropedit.exe into:\n" + nativeDir + "\n\n" +
                ex.Message,
                "Download MKVToolNix", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }

    private async void RemuxToMkv_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is Services.MediaHealthService.HealthResult r)
            await VM.MediaHealthVM.RemuxSelectedAsync(r, ".mkv");
    }

    private async void RemuxToMp4_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is Services.MediaHealthService.HealthResult r)
            await VM.MediaHealthVM.RemuxSelectedAsync(r, ".mp4");
    }

    private void HealthOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is Services.MediaHealthService.HealthResult r
            && System.IO.File.Exists(r.FilePath))
        {
            try { Process.Start("explorer.exe", $"/select,\"{r.FilePath}\""); }
            catch { /* ignore */ }
        }
    }

    private void HealthFixExtension_Click(object sender, RoutedEventArgs e)
    {
        if (HealthGrid.SelectedItem is not Services.MediaHealthService.HealthResult r) return;

        if (r.Issue != Services.MediaHealthService.IssueType.ExtensionMismatch)
        {
            System.Windows.MessageBox.Show(
                "This option is only available for Extension Mismatch issues.\n\n" +
                "Select a file flagged with Extension Mismatch first.",
                "Not applicable", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var correctExt = Services.MediaHealthService.GetCorrectExtension(r);
        if (correctExt == null)
        {
            System.Windows.MessageBox.Show(
                "Could not determine the correct extension for this file.",
                "Fix Extension", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var newPath = System.IO.Path.ChangeExtension(r.FilePath, correctExt);
        var confirm = System.Windows.MessageBox.Show(
            $"Rename this file?\n\n" +
            $"  From:  {r.FileName}\n" +
            $"  To:    {System.IO.Path.GetFileName(newPath)}\n\n" +
            "This only renames the file — no remux, no re-encoding, instant.",
            "Fix Extension — Rename Only",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            if (System.IO.File.Exists(newPath))
            {
                System.Windows.MessageBox.Show(
                    $"A file already exists at:\n{System.IO.Path.GetFileName(newPath)}\n\nRename cancelled.",
                    "File already exists", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            System.IO.File.Move(r.FilePath, newPath);
            VM.StatusText = $"✓ Renamed to {System.IO.Path.GetFileName(newPath)}";
            VM.MediaHealthVM.ReplaceResult(r, newPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Rename failed: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    // ── FILES panel context menu ────────────────────────────────────────────

    /// <summary>Resolves the VideoFile a context-menu click targets, via the menu DataContext.</summary>
    private static string? FilePathFromMenu(object sender)
    {
        if (sender is MenuItem mi && mi.DataContext is Models.VideoFile vf)
            return vf.FilePath;
        return null;
    }

    private async void FilesRemuxToMkv_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePathFromMenu(sender) ?? VM.SelectedFile?.FilePath;
        if (path != null) await RemuxFileFromPanelAsync(path, ".mkv");
    }

    private async void FilesRemuxToMp4_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePathFromMenu(sender) ?? VM.SelectedFile?.FilePath;
        if (path != null) await RemuxFileFromPanelAsync(path, ".mp4");
    }

    private async System.Threading.Tasks.Task RemuxFileFromPanelAsync(string path, string targetExt)
    {
        if (!Services.FfmpegService.IsAvailable)
        {
            System.Windows.MessageBox.Show(
                "ffmpeg is required for remuxing. Install it in Settings → External Tools.",
                "ffmpeg not found", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }
        VM.StatusText = $"Remuxing {System.IO.Path.GetFileName(path)} → {targetExt}…";
        var res = await Services.FfmpegService.RemuxAsync(path, targetExt);
        VM.StatusText = res.Message;

        if (res.Success && res.OutputPath != null)
        {
            // Load the candidate into the FILES panel with Replace/Restore buttons
            VM.LoadRemuxCandidate(res.OutputPath, path);
            System.Windows.MessageBox.Show(
                $"A clean remuxed copy was created:\n{System.IO.Path.GetFileName(res.OutputPath)}\n\n" +
                "It's now loaded in the FILES panel (marked REMUXED). Test it / try embedding, " +
                "then use the buttons under the Preview panel:\n\n" +
                "  ✓ Replace Original — keep the remux, delete the original\n" +
                "  ↩ Restore Original — discard the remux, keep the original\n\n" +
                "Nothing is deleted until you choose.",
                "Remux Complete",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }
        else
        {
            System.Windows.MessageBox.Show(res.Message, "Remux Failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void FilesOpenInFallback_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePathFromMenu(sender) ?? VM.SelectedFile?.FilePath;
        if (path == null) return;

        var fallback = VM.FallbackAppPath;
        if (string.IsNullOrWhiteSpace(fallback))
        {
            System.Windows.MessageBox.Show(
                "No fallback application is set.\n\n" +
                "Set one in Settings → Write Error Diagnostics (e.g. TagScanner.exe).",
                "No fallback app", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }
        try
        {
            if (fallback.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Process.Start(fallback, $"\"{path}\"");
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Couldn't open: {ex.Message}",
                "Open in fallback app", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }

    private void FilesOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = FilePathFromMenu(sender) ?? VM.SelectedFile?.FilePath;
        if (path != null && System.IO.File.Exists(path))
        {
            try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch { /* ignore */ }
        }
    }

    // ── Remux candidate commit ──────────────────────────────────────────────

    private void ReplaceOriginal_Click(object sender, RoutedEventArgs e)
    {
        var f = VM.SelectedFile;
        if (f is not { IsRemuxCandidate: true }) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Replace the original with this remuxed copy?\n\n" +
            $"• {System.IO.Path.GetFileName(f.OriginalPath)} → Recycle Bin\n" +
            $"• {f.FileName} → renamed to take its place\n\n" +
            "This keeps the clean remuxed version.",
            "Replace Original",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        // Stop preview so the file isn't locked
        StopPreview_Click(this, new RoutedEventArgs());
        VM.CommitRemux(f, replace: true);
    }

    private void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        var f = VM.SelectedFile;
        if (f is not { IsRemuxCandidate: true }) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Discard this remuxed copy and keep the original?\n\n" +
            $"• {f.FileName} → Recycle Bin\n" +
            $"• {System.IO.Path.GetFileName(f.OriginalPath)} → kept unchanged",
            "Restore Original",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        StopPreview_Click(this, new RoutedEventArgs());
        VM.CommitRemux(f, replace: false);
    }

    // ── Media Player ──────────────────────────────────────────────────────────

    private void PreviewPlayer_Click(object sender, MouseButtonEventArgs e) => TogglePreview();

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePreview();

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        PreviewPlayer.Stop();
        PreviewPlayer.Source = null;
        VM.IsPreviewPlaying = false;
        if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶ Play";
    }

    private void TogglePreview()
    {
        if (VM.SelectedFile == null) return;

        // Stop popout if open — prevent dual audio
        _popout?.StopForMainPlayer();

        if (VM.IsPreviewPlaying)
        {
            PreviewPlayer.Pause();
            VM.IsPreviewPlaying = false;
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "▶ Play";
        }
        else
        {
            if (PreviewPlayer.Source == null ||
                PreviewPlayer.Source.LocalPath != VM.SelectedFile.FilePath)
            {
                PreviewPlayer.Source = new Uri(VM.SelectedFile.FilePath);
            }
            PreviewPlayer.Play();
            VM.IsPreviewPlaying = true;
            if (PlayPauseBtn != null) PlayPauseBtn.Content = "⏸ Pause";
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VM.SearchApiCommand.CanExecute(null))
            VM.SearchApiCommand.Execute(null);
    }

    private void IdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && VM.LookupByIdCommand.CanExecute(null))
            VM.LookupByIdCommand.Execute(null);
    }

    private async void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VM.SelectedSearchResult == null) return;
        try { await VM.LoadSearchResultDetailsAsync(VM.SelectedSearchResult); }
        catch (Exception ex) { VM.StatusText = $"Search error: {ex.Message}"; }
    }

    /// <summary>
    /// Re-loads a result that is already selected when the user clicks it again.
    /// WPF's SelectionChanged only fires on *new* selections — clicking an
    /// already-selected item is silently ignored. This handler catches that case.
    /// </summary>
    private async void SearchResults_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb) return;
        var hit = lb.InputHitTest(e.GetPosition(lb)) as System.Windows.DependencyObject;
        if (hit == null) return;

        // Walk up visual tree to find the ListBoxItem that was clicked
        var parent = hit;
        while (parent != null)
        {
            if (parent is System.Windows.Controls.ListBoxItem lbi && lbi.IsSelected
                && lbi.DataContext is Models.SearchResult result)
            {
                // Already selected — force a re-load
                await VM.LoadSearchResultDetailsAsync(result);
                return;
            }
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
    }

    // ── Preview popout ────────────────────────────────────────────────────────

    private PreviewPopout? _popout;
    private System.ComponentModel.PropertyChangedEventHandler? _vmPropertyChangedHandler;

    private void PopoutPreview_Click(object sender, RoutedEventArgs e)
    {
        var vf = VM.SelectedFile;
        if (vf == null) return;

        // Close any existing popout first
        if (_popout != null && _popout.IsLoaded)
        {
            _popout.Close();
            _popout = null;
            return;
        }

        // Stop the inline player before handing off to the popout
        StopPreview_Click(this, new RoutedEventArgs());

        _popout = new PreviewPopout(vf.FilePath, vf.FileName)
        {
            Owner = this
        };
        _popout.Closed += (_, _) => _popout = null;
        _popout.Show();
        _popout.StartPlayback();
    }

    // ── Write failure verbose dialog ──────────────────────────────────────────

    private void OnWriteFailedDetailed(object? sender, (VideoFile File, string Detail) e)
    {
        // Show a detailed, actionable dialog when VerboseWriteErrors is on
        var win = new System.Windows.Window
        {
            Title           = $"Write Failed — {e.File.FileName}",
            Width           = 880,
            MinWidth        = 680,
            Height          = 440,
            MinHeight       = 360,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner           = this,
            ResizeMode      = ResizeMode.CanResize,
            Background      = (System.Windows.Media.Brush)Application.Current.Resources["SurfaceBrush"]
        };

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text       = $"🔴  Could not write metadata to: {e.File.FileName}",
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new Thickness(0, 0, 0, 12),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ErrorBrush"]
        };
        Grid.SetRow(header, 0);

        var detail = new TextBox
        {
            Text         = e.Detail,
            IsReadOnly   = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
            FontSize     = 12,
            Background   = (System.Windows.Media.Brush)Application.Current.Resources["SurfaceAltBrush"],
            BorderThickness = new Thickness(1),
            Padding      = new Thickness(10)
        };
        Grid.SetRow(detail, 1);

        var btnPanel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var copyBtn = new Button
        {
            Content = "📋 Copy to Clipboard",
            Padding = new Thickness(14, 7, 14, 7),
            Margin  = new Thickness(0, 0, 8, 6)
        };
        copyBtn.Click += (_, _) =>
        {
            System.Windows.Clipboard.SetText(e.Detail);
            copyBtn.Content = "✓ Copied!";
        };

        // "Open in [App]" button — only shown when a fallback application is configured
        if (VM.HasFallbackApp)
        {
            var openInBtn = new Button
            {
                Content = $"🔧 Open in {VM.FallbackAppName}",
                Padding  = new Thickness(14, 7, 14, 7),
                Margin   = new Thickness(0, 0, 8, 6),
                ToolTip  = $"Open the failed file directly in {VM.FallbackAppName}"
            };
            openInBtn.Click += (_, _) =>
            {
                VM.LaunchFallbackApp(e.File.FilePath);
                win.Close();
            };
            btnPanel.Children.Add(openInBtn);
        }

        // Remux buttons — the most reliable fix for persistent write failures.
        // Primary target is chosen by file type: MKV files get .mp4 first (wider
        // tagger compatibility), everything else gets .mkv first (flexible container).
        // Both options always shown — user may want the other format.
        var ext            = System.IO.Path.GetExtension(e.File.FilePath).ToLowerInvariant();
        var primaryExt     = ext == ".mkv" ? ".mp4" : ".mkv";
        var primaryLabel   = ext == ".mkv" ? "🔧 Remux → .mp4 (fix + faststart)" : "🔧 Remux → .mkv";
        var secondaryExt   = ext == ".mkv" ? ".mkv"  : ".mp4";
        var secondaryLabel = ext == ".mkv" ? "🔧 Remux → .mkv" : "🔧 Remux → .mp4 (fix + faststart)";
        const string remuxTip = "Losslessly rebuilds the container — fixes files that refuse embedding. " +
                                "Original is kept until you choose Replace or Restore.";

        var remuxPrimaryBtn = new Button
        {
            Content = primaryLabel,
            Padding = new Thickness(14, 7, 14, 7),
            Margin  = new Thickness(0, 0, 8, 6),
            ToolTip = remuxTip
        };
        remuxPrimaryBtn.Click += async (_, _) =>
        {
            win.Close();
            await VM.MediaHealthVM.RemuxSelectedAsync(
                Services.MediaHealthService.Analyse(e.File.FilePath), primaryExt);
        };
        btnPanel.Children.Add(remuxPrimaryBtn);

        var remuxSecondaryBtn = new Button
        {
            Content = secondaryLabel,
            Padding = new Thickness(14, 7, 14, 7),
            Margin  = new Thickness(0, 0, 8, 6),
            ToolTip = remuxTip
        };
        remuxSecondaryBtn.Click += async (_, _) =>
        {
            win.Close();
            await VM.MediaHealthVM.RemuxSelectedAsync(
                Services.MediaHealthService.Analyse(e.File.FilePath), secondaryExt);
        };
        btnPanel.Children.Add(remuxSecondaryBtn);

        var closeBtn = new Button
        {
            Content  = "Close",
            Padding  = new Thickness(14, 7, 14, 7),
            Margin   = new Thickness(0, 0, 0, 6),
            IsDefault = true,
            IsCancel  = true
        };
        closeBtn.Click += (_, _) => win.Close();

        btnPanel.Children.Add(copyBtn);
        btnPanel.Children.Add(closeBtn);
        Grid.SetRow(btnPanel, 2);

        grid.Children.Add(header);
        grid.Children.Add(detail);
        grid.Children.Add(btnPanel);
        win.Content = grid;
        win.ShowDialog();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
        e.Handled = true;
    }

    // ── Log tab ───────────────────────────────────────────────────────────────

    private void ClearLog_Click(object sender, RoutedEventArgs e)
        => VM.ConsoleLog.Clear();

    private void RenamePreset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem item &&
            item.Content is string preset)
        {
            VM.Settings.RenamePattern = preset;
            VM.OnSettingChanged();
        }
    }

    private void MovieRenamePreset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string preset)
            VM.MovieRenamePattern = preset;
    }

    private void TvRenamePreset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is string preset)
            VM.TvRenamePattern = preset;
    }

    // ── Library Tab handlers ──────────────────────────────────────────────────

    private void LibraryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LibraryGrid.SelectedItem is VideoMetadataEditor.Models.LibraryEntry entry)
        {
            if (VM.LibraryRowDoubleClickCommand?.CanExecute(entry) == true)
                VM.LibraryRowDoubleClickCommand.Execute(entry);
        }
    }

    private void LoadSelectedBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = LibraryGrid.SelectedItems;
        if (selected == null || selected.Count == 0) return;
        VM.LoadLibrarySelectionCommand.Execute(selected);
    }

    private void LibraryGrid_ColumnReordered(object sender,
        System.Windows.Controls.DataGridColumnEventArgs e)
    {
        var colMap = GetColMap();
        foreach (var col in VM.LibraryColumns)
        {
            if (!colMap.TryGetValue(col.Binding, out var dgCol)) continue;
            col.DisplayIndex = dgCol.DisplayIndex;
            if (dgCol.ActualWidth > 0) col.Width = dgCol.ActualWidth;
        }
        VM.SaveLibraryColumnSettings();
    }

    private void LibraryGrid_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Captures column widths after a resize drag (no built-in ColumnWidthChanged event in WPF)
        var colMap = GetColMap();
        bool changed = false;
        foreach (var col in VM.LibraryColumns)
        {
            if (!colMap.TryGetValue(col.Binding, out var dgCol)) continue;
            if (dgCol.ActualWidth > 0 && Math.Abs(dgCol.ActualWidth - col.Width) > 1.0)
            {
                col.Width = dgCol.ActualWidth;
                changed = true;
            }
        }
        if (!changed) return;

        _colResizeDebounce?.Stop();
        _colResizeDebounce = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(600) };
        _colResizeDebounce.Tick += (_, _) =>
        {
            _colResizeDebounce.Stop();
            VM.SaveLibraryColumnSettings();
        };
        _colResizeDebounce.Start();
    }
    private System.Windows.Threading.DispatcherTimer? _colResizeDebounce;

    private void LibraryTreeView_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LibraryTreeView?.SelectedItem is VideoMetadataEditor.Models.LibraryEntry entry)
            VM.LibraryRowDoubleClickCommand?.Execute(entry);
    }

    // ── Column chooser popup ──────────────────────────────────────────────────

    private System.Windows.Controls.Primitives.Popup? _colPopup;

    private void ColChooserBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_colPopup == null)
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Show / Hide Columns",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            foreach (var col in VM.LibraryColumns)
            {
                var cb = new System.Windows.Controls.CheckBox
                {
                    Content    = col.Header,
                    IsChecked  = col.IsVisible,
                    Margin     = new Thickness(0, 2, 0, 2),
                    DataContext = col
                };
                cb.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty,
                    new System.Windows.Data.Binding("IsVisible")
                    { Mode = System.Windows.Data.BindingMode.TwoWay });
                cb.Checked   += (_, _) => ApplyColumnVisibility(col, true);
                cb.Unchecked += (_, _) => ApplyColumnVisibility(col, false);
                panel.Children.Add(cb);
            }

            var border = new System.Windows.Controls.Border
            {
                Background      = TryFindResource("SurfaceBrush") as System.Windows.Media.Brush
                                  ?? System.Windows.Media.Brushes.White,
                BorderBrush     = TryFindResource("BorderBrush") as System.Windows.Media.Brush
                                  ?? System.Windows.Media.Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Child = new System.Windows.Controls.ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility =
                        System.Windows.Controls.ScrollBarVisibility.Auto,
                    Content = panel
                }
            };

            _colPopup = new System.Windows.Controls.Primitives.Popup
            {
                Child              = border,
                PlacementTarget    = ColChooserBtn,
                Placement          = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen          = false,
                AllowsTransparency = true
            };
        }
        _colPopup.IsOpen = !_colPopup.IsOpen;
    }

    private void ApplyColumnVisibility(VideoMetadataEditor.Models.LibraryColumn col, bool visible)
    {
        col.IsVisible = visible;
        var dgCol = LibraryGrid.Columns
            .FirstOrDefault(c => c.Header?.ToString() == col.Header);
        if (dgCol != null)
            dgCol.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        VM.SaveLibraryColumnSettings();
    }

    /// <summary>
    /// Called by ViewModel after tab switch to sync all DataGrid column visibility
    /// from the LibraryColumns model. Uses the column Binding property as a stable
    /// key since Header can change (e.g. "Title" ↔ "Episode Title").
    /// </summary>
    // Stable binding-path → DataGridColumn map (lazily initialised).
    private Dictionary<string, DataGridColumn>? _colMap;

    private Dictionary<string, DataGridColumn> GetColMap() =>
        _colMap ??= new Dictionary<string, DataGridColumn>(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"]               = ColTitle,
            ["Year"]                = ColYear,
            ["Genre"]               = ColGenre,
            ["Director"]            = ColDirector,
            ["Cast"]                = ColCast,
            ["RatingDisplay"]       = ColRating,
            ["MpaRating"]           = ColMpa,
            ["Duration"]            = ColDuration,
            ["FileSizeDisplay"]     = ColSize,
            ["Format"]              = ColFormat,
            ["Resolution"]          = ColRes,
            ["DownloadDateDisplay"] = ColDate,
            ["ImdbId"]              = ColImdb,
            ["Description"]         = ColDesc,
            ["ShowTitle"]           = ColShow,
            ["SeasonDisplay"]       = ColSeason,
            ["EpisodeDisplay"]      = ColEpNum,
            ["EpisodeTitle"]        = ColEpTitle,
            ["AiredDate"]           = ColAired,
            ["CoverArt"]            = ColCover,
            ["SubtitleSummary"]     = ColSubs,
        };

    public void SyncLibraryColumnVisibility()
    {
        var colMap = GetColMap();

        // Apply visibility and header text
        foreach (var col in VM.LibraryColumns)
        {
            if (!colMap.TryGetValue(col.Binding, out var dgCol)) continue;
            dgCol.Visibility = col.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(col.Header))
                dgCol.Header = col.Header;
        }

        // Apply DisplayIndex in strict ascending order (WPF requirement)
        var ordered = VM.LibraryColumns
            .Where(c => c.IsVisible)
            .Select(c => (
                Col:   colMap.TryGetValue(c.Binding, out var dc) ? dc : null,
                Order: c.DisplayIndex))
            .Where(x => x.Col != null)
            .OrderBy(x => x.Order)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            try { ordered[i].Col!.DisplayIndex = i; }
            catch { /* ignore WPF index conflicts */ }
        }
    }

    // ── Recovery dialog ───────────────────────────────────────────────────────

    private void OnRecoveryCandidatesFound(
        object? sender,
        IReadOnlyList<VideoMetadataEditor.Services.RecoveryCandidate> candidates)
    {
        // Log to console first
        foreach (var c in candidates)
            VM.ConsoleLog.Insert(0,
                $"[{DateTime.Now:HH:mm:ss}] ⚠ Recovery candidate: {c.FileName} ({c.Description})");

        // Open the recovery dialog
        var dlg = new VideoMetadataEditor.Views.RecoveryDialog(
            candidates,
            msg =>
            {
                VM.ConsoleLog.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
                VM.StatusText = msg;
            },
            owner: this);

        dlg.Show(); // non-modal — user can still interact with the main window
    }
    /// <summary>
    /// Reads the live DataGrid column DisplayIndex and ActualWidth back into
    /// LibraryColumns so SaveLibraryColumnSettings captures the true current order.
    /// </summary>
    public void SyncColumnOrderFromDataGrid()
    {
        if (LibraryGrid == null) return;
        var colMap = GetColMap();
        foreach (var col in VM.LibraryColumns)
        {
            if (!colMap.TryGetValue(col.Binding, out var dgCol)) continue;
            col.DisplayIndex = dgCol.DisplayIndex;
            if (dgCol.ActualWidth > 10) col.Width = dgCol.ActualWidth;
        }
    }

    private void DirectEpisodeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            VM.ApplyDirectEpisodeEntry();
            e.Handled = true;
        }
    }

    private void TraktClientId_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Re-evaluate Connect button CanExecute when Client ID is typed
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void TraktSecretBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        // PasswordBox can't TwoWay-bind in WPF — write to Settings directly
        if (sender is System.Windows.Controls.PasswordBox pb && !VM.TraktClientSecretLocked)
        {
            VM.Settings.TraktClientSecret = pb.Password;
            // Force CanExecute to re-evaluate so Connect button enables after typing
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private void TvTreeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Toggle between DataGrid list and TreeView for TV tabs
        VM.LibraryTvTreeView = !VM.LibraryTvTreeView;
    }

    private void LibraryTree_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Walk up from the OriginalSource to find the TreeViewItem that was double-clicked.
        // Using tv.SelectedItem alone is unreliable — SelectedItem may not update before
        // MouseDoubleClick fires, especially when clicking the expand arrow.
        var source = e?.OriginalSource as System.Windows.DependencyObject;
        while (source != null
               && source is not System.Windows.Controls.TreeViewItem
               && source is not System.Windows.Controls.TreeView)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        object? item = null;
        if (source is System.Windows.Controls.TreeViewItem tvi)
            item = tvi.DataContext;
        else if (sender is System.Windows.Controls.TreeView tv)
            item = tv.SelectedItem;

        switch (item)
        {
            case VideoMetadataEditor.Models.LibraryEntry ep when !ep.IsMissingEpisode:
                VM.TreeLoadEpisodeCommand?.Execute(ep);
                if (e != null) e.Handled = true;
                break;
            case VideoMetadataEditor.Models.TvShowNode show:
                VM.TreeLoadShowCommand?.Execute(show);
                if (e != null) e.Handled = true;
                break;
            case VideoMetadataEditor.Models.TvSeasonNode season:
                VM.TreeLoadSeasonCommand?.Execute(season);
                if (e != null) e.Handled = true;
                break;
        }
    }

    private void LibraryTree_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TreeView tv) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Enter:
                LibraryTree_DoubleClick(sender, null!);
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Space:
                // Toggle watched on selected episode
                if (tv.SelectedItem is VideoMetadataEditor.Models.LibraryEntry ep && !ep.IsMissingEpisode)
                {
                    ep.IsWatched = !ep.IsWatched;
                    VM.NotifyTvTreeChanged();
                    e.Handled = true;
                }
                break;
        }
    }



    // Duplicate comparison uses DuplicateCompareDialog (separate window)

    private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Unsubscribe all VM events to prevent memory leaks
        VM.WriteFailedDetailed      -= OnWriteFailedDetailed;
        VM.RecoveryCandidatesFound  -= OnRecoveryCandidatesFound;
        VM.BatchCompleted           -= OnBatchCompleted;
        VM.EmbedSucceeded           -= OnEmbedSucceeded;
        VM.RenameFailed             -= OnRenameFailed;
        VM.RenameSucceeded          -= OnRenameSucceeded;
        VM.FilesLoaded              -= OnFilesLoaded;
        VM.CopyCompleted            -= OnCopyCompleted;
        VM.MoveCompleted            -= OnMoveCompleted;
        VM.DeleteFileRequested      -= OnDeleteFileRequested;

        // Cancel in-flight operations
        VM._traktDeviceCts?.Cancel();
        VM._traktDeviceCts?.Dispose();
        VM._searchDetailCts?.Cancel();
        VM._searchDetailCts?.Dispose();

        // Dispose watch services (own FileSystemWatcher — unmanaged resource)
        VM.DisposeWatchServices();

        // Flush library cache synchronously before exit
        try
        {
            var livePaths = VM.LibraryEntries.Select(e => e.FilePath).ToList();
            if (livePaths.Count > 0) VM.FlushLibraryCacheSync(livePaths);
        }
        catch { /* non-fatal — rebuilds on next scan */ }
    }

    // ── Full Write Diagnostic (Settings → Enable full diagnostic dump) ───────────
    private async void RunSelfTest_Click(object sender, RoutedEventArgs e)
    {
        // Default the picker to the Move/Copy destination, else the Library folder.
        var initialDir = VM.Settings.LastCopyDestination;
        if (string.IsNullOrWhiteSpace(initialDir) || !System.IO.Directory.Exists(initialDir))
            initialDir = VM.Settings.LibraryFolderPath;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select a video file to run the full write diagnostic",
            Filter = "Video files|*.mp4;*.m4v;*.mkv;*.mov;*.avi;*.wmv;*.webm|All files|*.*"
        };
        if (!string.IsNullOrWhiteSpace(initialDir) && System.IO.Directory.Exists(initialDir))
            dlg.InitialDirectory = initialDir;
        if (dlg.ShowDialog(this) != true) return;

        var filePath = dlg.FileName;
        ShowSelfTestDialog(filePath);
    }

    private void ShowSelfTestDialog(string filePath)
    {
        var win = new System.Windows.Window
        {
            Title       = $"Write Diagnostic — {System.IO.Path.GetFileName(filePath)}",
            Width       = 820,
            MinWidth    = 640,
            Height      = 620,
            MinHeight   = 420,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner       = this,
            ResizeMode  = ResizeMode.CanResize,
            Background  = (System.Windows.Media.Brush)Application.Current.Resources["SurfaceBrush"]
        };

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // steps
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // interpretation
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // buttons

        var header = new TextBlock
        {
            Text = $"Running 16-step write diagnostic on a safe copy of:\n{filePath}\n\nThe window stays open until all checks finish — buttons unlock when complete.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var stepsPanel = new StackPanel();
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stepsPanel
        };
        Grid.SetRow(scroller, 1);
        root.Children.Add(scroller);

        var interpretation = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10),
            FontStyle = FontStyles.Italic
        };
        Grid.SetRow(interpretation, 2);
        root.Children.Add(interpretation);

        var btnPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var exportBtn = new Button { Content = "💾 Export Log…", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var closeBtn  = new Button { Content = "Close", Padding = new Thickness(14, 7, 14, 7), IsEnabled = false };
        // Both buttons stay disabled until the diagnostic finishes, so the user can't
        // close the window (or export a partial log) while steps are still running in
        // the background. They are enabled together when the run completes.
        closeBtn.Click += (_, _) => win.Close();
        btnPanel.Children.Add(exportBtn);
        btnPanel.Children.Add(closeBtn);
        Grid.SetRow(btnPanel, 3);
        root.Children.Add(btnPanel);

        win.Content = root;

        // Render one step row with a green check or red cross / grey info dot.
        void AddStepRow(Services.WriteSelfTest.StepResult s)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var (glyph, colour) = s.Outcome switch
            {
                Services.WriteSelfTest.Outcome.Pass => ("✓", System.Windows.Media.Brushes.SeaGreen),
                Services.WriteSelfTest.Outcome.Fail => ("✗", System.Windows.Media.Brushes.IndianRed),
                _                                    => ("•", System.Windows.Media.Brushes.Gray),
            };
            row.Children.Add(new TextBlock
            {
                Text = glyph, Foreground = colour, FontWeight = FontWeights.Bold,
                Width = 22, FontSize = 15, VerticalAlignment = VerticalAlignment.Top
            });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{s.Number}. {s.Name}", FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = s.Detail, TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ForegroundMutedBrush"],
                FontSize = 12
            });
            row.Children.Add(text);
            stepsPanel.Children.Add(row);
        }

        Services.WriteSelfTest.Report? report = null;

        exportBtn.Click += (_, _) =>
        {
            if (report == null) return;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export diagnostic log",
                Filter = "Text log|*.txt|All files|*.*",
                FileName = System.IO.Path.GetFileNameWithoutExtension(filePath) + ".write-diagnostic.txt"
            };
            var dest = VM.Settings.LastCopyDestination;
            if (!string.IsNullOrWhiteSpace(dest) && System.IO.Directory.Exists(dest))
                save.InitialDirectory = dest;
            if (save.ShowDialog(win) == true)
            {
                try
                {
                    System.IO.File.WriteAllText(save.FileName, Services.WriteSelfTest.FormatLog(report));
                    VM.StatusText = $"Diagnostic log saved: {System.IO.Path.GetFileName(save.FileName)}";
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(win, $"Could not save log:\n{ex.Message}",
                        "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        };

        var closed = false;
        var running = true;
        win.Closed += (_, _) => closed = true;
        // While the diagnostic is running, block the title-bar X / Alt+F4 too, so the
        // window can't be dismissed (leaving background work orphaned) before it finishes.
        win.Closing += (_, ce) => { if (running) ce.Cancel = true; };
        win.Show();

        // Run the engine off the UI thread, marshalling each step back for live display.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var rep = Services.WriteSelfTest.Run(filePath, step =>
            {
                if (closed) return;
                // Non-blocking dispatch so the worker never stalls waiting on the UI thread.
                Dispatcher.BeginInvoke(() => { if (!closed) AddStepRow(step); });
            });
            Dispatcher.BeginInvoke(() =>
            {
                running = false;            // run finished — closing is now permitted
                if (closed) return;
                report = rep;
                header.Text = $"Write diagnostic complete for:\n{filePath}";
                interpretation.Text = $"Result: {rep.Passed} passed, {rep.Failed} failed.\n\n{rep.Interpretation}";
                interpretation.Foreground = rep.AllPassed
                    ? System.Windows.Media.Brushes.SeaGreen
                    : (System.Windows.Media.Brush)Application.Current.Resources["ForegroundBrush"];
                exportBtn.IsEnabled = true;  // both buttons enabled together on completion
                closeBtn.IsEnabled  = true;
            });
        });
    }
}
