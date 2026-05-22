using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

public class MediaHealthViewModel : ViewModelBase
{
    public ObservableCollection<MediaHealthService.HealthResult> Results { get; } = new();

    private CancellationTokenSource? _cts;

    public ICommand ScanCommand        { get; }
    public ICommand CancelCommand      { get; }
    public ICommand CopyReportCommand  { get; }
    public ICommand ClearFolderCommand { get; }
    public ICommand FixAllFaststartCommand { get; }
    public ICommand DetectFfmpegCommand    { get; }

    public MediaHealthViewModel()
    {
        ScanCommand   = new AsyncRelayCommand(RunScanAsync, _ => !IsScanning);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsScanning);
        CopyReportCommand = new RelayCommand(_ => CopyReport(), _ => Results.Count > 0);
        ClearFolderCommand = new RelayCommand(_ =>
        {
            Folder = string.Empty;
            Results.Clear();
            Status = "Choose a folder and click Scan.";
            Progress = 0;
            RaiseCounts();
        });
        FixAllFaststartCommand = new AsyncRelayCommand(FixAllFaststartAsync,
            _ => !IsScanning && FfmpegAvailable && FaststartFixableCount > 0);
        DetectFfmpegCommand = new AsyncRelayCommand(RefreshFfmpegAsync);

        // Restore the last-used folder + recurse preference (display in the textbox).
        // Set the backing fields directly so we don't trigger a redundant save here.
        // Guard against early construction before ConfigService is ready.
        try
        {
            var saved = App.ConfigService?.Settings;
            if (saved != null)
            {
                if (!string.IsNullOrWhiteSpace(saved.MediaHealthFolder))
                    _folder = saved.MediaHealthFolder;
                _includeSubfolders = saved.MediaHealthRecursive;
            }
        }
        catch { /* settings not ready — keep defaults */ }

        _ = RefreshFfmpegAsync();
    }

    // ── FFmpeg availability ─────────────────────────────────────────────────
    private bool _ffmpegAvailable;
    public  bool FfmpegAvailable { get => _ffmpegAvailable; private set { _ffmpegAvailable = value; RaiseProperty(); } }

    private string _ffmpegStatus = "Checking for ffmpeg…";
    public  string FfmpegStatus { get => _ffmpegStatus; private set { _ffmpegStatus = value; RaiseProperty(); } }

    public int FaststartFixableCount => Results.Count(r =>
        r.Issue == MediaHealthService.IssueType.NoFaststart);

    private async Task RefreshFfmpegAsync()
    {
        var ver = await FfmpegService.GetVersionAsync();
        FfmpegAvailable = ver != null;
        FfmpegStatus = ver != null
            ? $"✓ ffmpeg {ver} detected"
            : "ffmpeg not found — place ffmpeg.exe in the native\\ folder or install it";
    }

    // ── Scan target ─────────────────────────────────────────────────────────
    private string _folder = string.Empty;
    public  string Folder
    {
        get => _folder;
        set
        {
            if (_folder == value) return;
            _folder = value;
            RaiseProperty();
            try
            {
                if (App.ConfigService != null)
                {
                    App.ConfigService.Settings.MediaHealthFolder = value;
                    _ = App.ConfigService.SaveAsync();
                }
            }
            catch { /* persistence best-effort */ }
        }
    }

    private bool _includeSubfolders = true;
    public  bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (_includeSubfolders == value) return;
            _includeSubfolders = value;
            RaiseProperty();
            try
            {
                if (App.ConfigService != null)
                {
                    App.ConfigService.Settings.MediaHealthRecursive = value;
                    _ = App.ConfigService.SaveAsync();
                }
            }
            catch { /* persistence best-effort */ }
        }
    }

    // ── State ───────────────────────────────────────────────────────────────
    private bool _isScanning;
    public  bool IsScanning { get => _isScanning; private set { _isScanning = value; RaiseProperty(); } }

    // Distinct from IsScanning so the UI can show a prominent "fixing — don't close"
    // warning during a Fix All Faststart run (which mutates files).
    private bool _isFixing;
    public  bool IsFixing { get => _isFixing; private set { _isFixing = value; RaiseProperty(); } }

    private int _progress;
    public  int Progress { get => _progress; private set { _progress = value; RaiseProperty(); } }

    private string _status = "Choose a folder and click Scan.";
    public  string Status { get => _status; private set { _status = value; RaiseProperty(); } }

    // ── Summary counts ────────────────────────────────────────────────────────
    public int OkCount      => Results.Count(r => r.Status == MediaHealthService.HealthStatus.Ok);
    public int WarningCount => Results.Count(r => r.Status == MediaHealthService.HealthStatus.Warning);
    public int ErrorCount   => Results.Count(r => r.Status == MediaHealthService.HealthStatus.Error);
    public string SummaryLine => Results.Count == 0
        ? string.Empty
        : $"{Results.Count} scanned  ·  ✓ {OkCount} OK  ·  ⚠ {WarningCount} warnings  ·  ✕ {ErrorCount} errors";

    // ── Scan ────────────────────────────────────────────────────────────────
    private async Task RunScanAsync()
    {
        if (string.IsNullOrWhiteSpace(Folder) || !Directory.Exists(Folder))
        {
            Status = "Set a valid folder first.";
            return;
        }

        IsScanning = true;
        Progress   = 0;
        Results.Clear();
        Status = "Collecting files…";

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".wmv", ".webm",
                  ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".flv", ".vob", ".asf", ".qt", ".mka" };

            var files = Directory.EnumerateFiles(Folder, "*",
                    IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => ext.Contains(Path.GetExtension(f)))
                .ToList();

            if (files.Count == 0)
            {
                Status = "No video files found in that folder.";
                IsScanning = false;
                return;
            }

            int done = 0;
            // Detection is IO-light (reads ~64 KB) — run in parallel for speed
            var results = new System.Collections.Concurrent.ConcurrentBag<
                (int order, MediaHealthService.HealthResult result)>();

            await Task.Run(() =>
            {
                Parallel.For(0, files.Count,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                        CancellationToken = ct
                    },
                    i =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var r = MediaHealthService.Analyse(files[i]);
                        results.Add((i, r));
                        var d = Interlocked.Increment(ref done);
                        if (d % 25 == 0 || d == files.Count)
                        {
                            Progress = (int)(d * 100.0 / files.Count);
                            Status   = $"Analysing {d:N0}/{files.Count:N0}…";
                        }
                    });
            }, ct);

            if (ct.IsCancellationRequested)
            {
                Status = "Scan cancelled.";
                return;
            }

            // Add results: problems first (errors, then warnings), then OK
            var ordered = results
                .OrderByDescending(x => (int)x.result.Status) // Error=2, Warning=1, Ok=0
                .ThenBy(x => x.order)
                .Select(x => x.result);

            foreach (var r in ordered) Results.Add(r);

            Progress = 100;
            Status   = WarningCount + ErrorCount == 0
                ? $"✓ All {Results.Count:N0} files are compliant."
                : $"Found {ErrorCount} error(s) and {WarningCount} warning(s) in {Results.Count:N0} files.";

            RaiseCounts();
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RaiseCounts()
    {
        RaiseProperty(nameof(OkCount));
        RaiseProperty(nameof(WarningCount));
        RaiseProperty(nameof(ErrorCount));
        RaiseProperty(nameof(SummaryLine));
        RaiseProperty(nameof(FaststartFixableCount));
    }

    /// <summary>
    /// Stage 2: losslessly adds faststart to every MP4 flagged with NoFaststart.
    /// Uses ffmpeg stream-copy — no re-encode, original timestamps preserved.
    /// </summary>
    private async Task FixAllFaststartAsync()
    {
        var targets = Results
            .Where(r => r.Issue == MediaHealthService.IssueType.NoFaststart)
            .ToList();
        if (targets.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"Add faststart to {targets.Count} MP4 file(s)?\n\n" +
            "This is a lossless remux (no quality loss, no re-encode). " +
            "Each file is rewritten in place with its 'moov' atom moved to the front.\n\n" +
            "Original timestamps are preserved. Continue?",
            "Fix Faststart",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        IsScanning = true;
        IsFixing   = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        int fixedCount = 0, failed = 0, done = 0;
        var errors  = new System.Collections.Concurrent.ConcurrentBag<string>();
        // Map FilePath → fixed result so we can update rows on the UI thread afterwards
        var succeededPaths = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        // Parallelise ffmpeg invocations — same approach as Deep Scan.
        // ffmpeg stream-copy is light on CPU but IO-bound; cap at ProcessorCount-1
        // so the UI thread stays responsive and we don't thrash the disk.
        int workers = Math.Max(1, Environment.ProcessorCount - 1);

        await Parallel.ForEachAsync(targets,
            new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
            async (r, innerCt) =>
            {
                var res = await FfmpegService.AddFaststartAsync(r.FilePath, innerCt);
                if (res.Success)
                {
                    Interlocked.Increment(ref fixedCount);
                    succeededPaths[r.FilePath] = true;
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    errors.Add($"{r.FileName}: {res.Message}");
                }

                var d = Interlocked.Increment(ref done);
                Progress = (int)(d * 100.0 / targets.Count);
                Status   = $"Fixing faststart {d}/{targets.Count} ({workers} workers)…";
            });

        // Update rows on the UI thread (ObservableCollection isn't thread-safe)
        for (int i = 0; i < Results.Count; i++)
        {
            var r = Results[i];
            if (succeededPaths.ContainsKey(r.FilePath)
                && r.Issue == MediaHealthService.IssueType.NoFaststart)
            {
                Results[i] = r with
                {
                    Status       = MediaHealthService.HealthStatus.Ok,
                    Issue        = MediaHealthService.IssueType.None,
                    Detail       = "Faststart added — no issues.",
                    SuggestedFix = string.Empty
                };
            }
        }

        var errorList = errors.ToList();

        IsScanning = false;
        IsFixing   = false;
        Status = failed == 0
            ? $"✓ Added faststart to {fixedCount} file(s)."
            : $"Fixed {fixedCount}, failed {failed}. See details below.";
        RaiseCounts();

        // Completion notification — matches Move/Copy/Batch style
        var msg = failed == 0
            ? $"Faststart applied successfully.\n\n" +
              $"✓ {fixedCount} of {targets.Count} file(s) processed.\n" +
              "All fixes were lossless stream-copies (no quality loss)."
            : $"Faststart fix complete with some failures.\n\n" +
              $"✓ {fixedCount} succeeded\n✕ {failed} failed (of {targets.Count})\n\n" +
              "First failures:\n" + string.Join("\n", errorList.Take(10));

        System.Windows.MessageBox.Show(msg,
            failed == 0 ? "Health Check — Fix Complete" : "Health Check — Partial Success",
            System.Windows.MessageBoxButton.OK,
            failed == 0 ? System.Windows.MessageBoxImage.Information
                        : System.Windows.MessageBoxImage.Warning);
    }

    // ── Remux to fix tagger-hostile / mismatched containers ───────────────────

    /// <summary>
    /// Losslessly remuxes the selected result's file to a clean container.
    /// This is the real fix for files that refuse metadata embedding (the
    /// "TagScanner-only" cases): rebuilding the container from clean streams
    /// resolves structural issues a faststart alone can't.
    /// targetExt: ".mkv" (most permissive) or ".mp4".
    /// </summary>
    /// <summary>Raised after a successful Health Check remux so the host can load the
    /// candidate into the FILES panel (where Replace/Restore live). Args: (candidatePath, originalPath).</summary>
    public event Action<string, string>? RemuxCandidateCreated;

    public async Task RemuxSelectedAsync(
        MediaHealthService.HealthResult target, string targetExt)
    {
        if (!FfmpegAvailable)
        {
            System.Windows.MessageBox.Show(
                "ffmpeg is required for remuxing. Install it in Settings → External Tools.",
                "ffmpeg not found", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        IsScanning = true;
        Status = $"Remuxing {target.FileName} → {targetExt}…";
        var res = await FfmpegService.RemuxAsync(target.FilePath, targetExt);
        IsScanning = false;

        if (res.Success && res.OutputPath != null)
        {
            Status = res.Message;
            // Hand the candidate to the host so it loads in the FILES panel with
            // Replace/Restore buttons — same workflow as a FILES-panel remux.
            RemuxCandidateCreated?.Invoke(res.OutputPath, target.FilePath);
            System.Windows.MessageBox.Show(
                $"A clean remuxed copy was created:\n{System.IO.Path.GetFileName(res.OutputPath)}\n\n" +
                "It's now loaded in the FILES panel (marked REMUXED). Test it / try embedding, " +
                "then use the buttons under the Preview panel:\n\n" +
                "  ✓ Replace Original — keep the remux, delete the original\n" +
                "  ↩ Restore Original — discard the remux, keep the original\n\n" +
                "Nothing is deleted until you choose.",
                "Remux Complete", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
        else
        {
            Status = $"Remux failed: {res.Message}";
            System.Windows.MessageBox.Show(res.Message, "Remux Failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void CopyReport()
    {
        if (Results.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Media Health Check Report");
        sb.AppendLine(SummaryLine);
        sb.AppendLine(new string('-', 60));
        foreach (var r in Results)
        {
            var icon = r.Status switch
            {
                MediaHealthService.HealthStatus.Ok      => "OK ",
                MediaHealthService.HealthStatus.Warning => "WARN",
                _                                        => "ERR ",
            };
            sb.AppendLine($"[{icon}] {r.FileName}");
            sb.AppendLine($"        Container: {r.DetectedContainer}  ·  Ext: {r.Extension}");
            if (r.Status != MediaHealthService.HealthStatus.Ok)
            {
                sb.AppendLine($"        Issue: {r.Detail}");
                sb.AppendLine($"        Fix:   {r.SuggestedFix}");
            }
        }
        try { System.Windows.Clipboard.SetText(sb.ToString()); Status = "Report copied to clipboard."; }
        catch { Status = "Couldn't access clipboard."; }
    }
}
