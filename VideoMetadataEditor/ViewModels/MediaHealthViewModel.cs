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
    public ICommand FixAllIssuesCommand    { get; }
    public ICommand DetectFfmpegCommand    { get; }
    public ICommand ReembedSemicolonCommand { get; }

    /// <summary>
    /// Raised when the user asks to re-embed the semicolon-flagged files. Carries the
    /// affected file paths; the main window loads them into the FILES panel so the user
    /// can re-fetch metadata and embed cleanly on the current (Build 105+) codec.
    /// </summary>
    public event Action<IReadOnlyList<string>>? RequestReembed;

    private readonly Services.IDialogService _dialogs;
    private readonly Services.IClipboardService _clipboard;

    public MediaHealthViewModel(Services.IDialogService? dialogs = null,
        Services.IClipboardService? clipboard = null)
    {
        _dialogs   = dialogs ?? Services.NullDialogService.Instance;
        _clipboard = clipboard ?? Services.NullClipboardService.Instance;
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
        // Build 169: enabled only when there's something it can actually fix. It used to
        // light up for issues it never handles (e.g. unknown container), and clicking it
        // then did nothing visible.
        FixAllIssuesCommand = new AsyncRelayCommand(FixAllIssuesAsync,
            _ => !IsScanning && AutoFixableCount > 0);
        DetectFfmpegCommand = new AsyncRelayCommand(RefreshFfmpegAsync);
        ReembedSemicolonCommand = new RelayCommand(_ => ReembedSemicolonFiles(),
            _ => !IsScanning && SemicolonFlaggedCount > 0);

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
    public  bool FfmpegAvailable { get => _ffmpegAvailable; private set { _ffmpegAvailable = value; RaiseProperty(); RaiseProperty(nameof(AutoFixableCount)); } }

    private string _ffmpegStatus = "Checking for ffmpeg…";
    public  string FfmpegStatus { get => _ffmpegStatus; private set { _ffmpegStatus = value; RaiseProperty(); } }

    public int FaststartFixableCount => Results.Count(r =>
        r.Issue == MediaHealthService.IssueType.NoFaststart);

    public int ExtensionMismatchCount => Results.Count(r =>
        r.Issue == MediaHealthService.IssueType.ExtensionMismatch);

    /// <summary>
    /// What Fix All Issues can act on right now: extension mismatches always, faststart
    /// only when ffmpeg is available. Shown on the button.
    /// </summary>
    public int AutoFixableCount =>
        ExtensionMismatchCount + (FfmpegAvailable ? FaststartFixableCount : 0);

    public int SemicolonFlaggedCount => Results.Count(r =>
        r.Issue == MediaHealthService.IssueType.SemicolonInComment);

    private void ReembedSemicolonFiles()
    {
        var paths = Results
            .Where(r => r.Issue == MediaHealthService.IssueType.SemicolonInComment)
            .Select(r => r.FilePath)
            .ToList();
        if (paths.Count == 0) return;

        var confirm = _dialogs.Show(
            $"Load {paths.Count} semicolon-flagged file(s) into the FILES panel for re-embedding?\n\n" +
            "These files' stored tags may have been truncated by an older build. They will be added to " +
            "the main FILES list so you can re-fetch their metadata and Embed — the current build writes " +
            "a clean comment with the semicolon sanitised.\n\n" +
            "Note: tags that were already lost to truncation cannot be recovered from the file itself; " +
            "re-fetching from the metadata provider restores them.",
            "Re-embed flagged files",
            Services.DialogButtons.OkCancel,
            Services.DialogIcon.Information);
        if (confirm != Services.DialogResult.Ok) return;

        RequestReembed?.Invoke(paths);
    }

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
                // Build 168: never health-check (or "fix") VME's own temp files.
                .Where(f => !Services.WorkingFileFilter.IsWorkingFile(f))
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
                        // Second pass: flag a semicolon in the embedded comment (a file
                        // that may hold a comment truncated by a pre-Build-105 write).
                        var comment = WriteSelfTest.TryReadComment(files[i]);
                        r = MediaHealthService.CheckEmbeddedComment(r, comment);
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
        RaiseProperty(nameof(SemicolonFlaggedCount));
        RaiseProperty(nameof(ExtensionMismatchCount));
        RaiseProperty(nameof(AutoFixableCount));
    }

    /// <summary>
    /// Replaces a HealthResult in the list with a fresh scan of the renamed/fixed
    /// file at newPath. Called after Fix Extension renames a file so the grid
    /// reflects the corrected state without a full re-scan.
    /// </summary>
    public void ReplaceResult(MediaHealthService.HealthResult old, string newPath)
    {
        var idx = Results.IndexOf(old);
        if (idx < 0) return;
        var updated = MediaHealthService.Analyse(newPath);
        Results[idx] = updated;
        RaiseCounts();
    }

    /// <summary>
    /// Stage 2: losslessly adds faststart to every MP4 flagged with NoFaststart.
    /// Uses ffmpeg stream-copy — no re-encode, every stream kept, timestamps preserved.
    /// </summary>
    private async Task FixAllFaststartAsync()
    {
        var targets = Results
            .Where(r => r.Issue == MediaHealthService.IssueType.NoFaststart)
            .ToList();
        if (targets.Count == 0) return;

        var confirm = _dialogs.Show(
            $"Add faststart to {targets.Count} MP4 file(s)?\n\n" +
            "This is a lossless remux (no quality loss, no re-encode) that keeps every stream — " +
            "video, all audio and subtitle tracks, and cover art. Each file is rewritten in place " +
            "with its 'moov' atom moved to the front.\n\n" +
            "Original timestamps are preserved. Continue?",
            "Fix Faststart",
            Services.DialogButtons.YesNo,
            Services.DialogIcon.Question);
        if (confirm != Services.DialogResult.Yes) return;

        BeginFixing();
        var outcome = new FixOutcome();
        using (var history = OperationJournal.Begin("Fix Faststart", $"Fix Faststart of {targets.Count} file(s)"))
        {
            try
            {
                await RunFaststartBatchAsync(targets, outcome, _cts!.Token);
            }
            finally
            {
                EndFixing();
            }
            history.Summary = $"Fix Faststart — {outcome.Faststart} fixed" +
                              (outcome.Failed > 0 ? $", {outcome.Failed} failed" : "");
        }

        Status = outcome.Cancelled
            ? $"Cancelled — {outcome.Faststart} file(s) fixed before stopping."
            : outcome.Failed == 0
                ? $"✓ Added faststart to {outcome.Faststart} file(s)."
                : $"Fixed {outcome.Faststart}, failed {outcome.Failed}. See details below.";

        ShowCompletion("Faststart", targets.Count, outcome);
    }

    // ── Shared fix plumbing (build 169) ───────────────────────────────────────

    private sealed class FixOutcome
    {
        public int Renamed;
        public int Faststart;
        public int Failed;
        public int Skipped;
        public bool Cancelled;
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Errors = new();
    }

    /// <summary>Shows the red "don't close" banner and disables the other buttons.</summary>
    private void BeginFixing()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsScanning = true;
        IsFixing   = true;
        Progress   = 0;
        CommandManager.InvalidateRequerySuggested();
    }

    private void EndFixing()
    {
        IsScanning = false;
        IsFixing   = false;
        RaiseCounts();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Runs faststart over <paramref name="targets"/> with drive-appropriate parallelism,
    /// updating progress as it goes and marking each fixed row OK. Cancel stops cleanly
    /// (ffmpeg is killed; any file mid-fix keeps its original).
    /// </summary>
    private async Task RunFaststartBatchAsync(
        List<MediaHealthService.HealthResult> targets, FixOutcome outcome, CancellationToken ct,
        int progressOffset = 0, int progressTotal = 0)
    {
        if (targets.Count == 0) return;
        if (progressTotal <= 0) progressTotal = targets.Count;

        var succeeded = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(
            StringComparer.OrdinalIgnoreCase);

        // faststart is I/O-bound (stream copy): parallel on SSD/NVMe, sequential-ish on HDD.
        var driveProfile = Services.DriveCapabilityService.GetProfile(targets[0].FilePath);
        int workers = driveProfile.RecommendedScanWorkers;
        int done = 0;

        try
        {
            await Parallel.ForEachAsync(targets,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (r, innerCt) =>
                {
                    var res = await FfmpegService.AddFaststartAsync(r.FilePath, innerCt);
                    if (res.Success)
                    {
                        Interlocked.Increment(ref outcome.Faststart);
                        succeeded[r.FilePath] = true;
                    }
                    else if (!innerCt.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref outcome.Failed);
                        outcome.Errors.Enqueue($"{r.FileName}: {res.Message}");
                    }

                    var d = Interlocked.Increment(ref done);
                    Progress = (int)((progressOffset + d) * 100.0 / progressTotal);
                    Status   = $"Fixing faststart {d}/{targets.Count} ({workers} worker(s))… don't close VME.";
                });
        }
        catch (OperationCanceledException)
        {
            outcome.Cancelled = true;
        }

        // Update rows on the UI thread (ObservableCollection isn't thread-safe)
        for (int i = 0; i < Results.Count; i++)
        {
            var r = Results[i];
            if (succeeded.ContainsKey(r.FilePath)
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
    }

    private void ShowCompletion(string what, int total, FixOutcome o)
    {
        var lines = new List<string>();
        if (o.Renamed   > 0) lines.Add($"✓ {o.Renamed} extension(s) corrected");
        if (o.Faststart > 0) lines.Add($"✓ {o.Faststart} file(s) given faststart (lossless, all streams kept)");
        if (o.Skipped   > 0) lines.Add($"– {o.Skipped} skipped");
        if (o.Failed    > 0) lines.Add($"✕ {o.Failed} failed");
        if (lines.Count == 0) lines.Add("Nothing was changed.");

        var errors = o.Errors.ToList();
        var msg = (o.Cancelled ? $"{what} cancelled.\n\n" : $"{what} complete.\n\n") +
                  string.Join("\n", lines) +
                  (errors.Count > 0
                      ? "\n\nFirst problems:\n" + string.Join("\n", errors.Take(10)) +
                        (errors.Count > 10 ? $"\n…and {errors.Count - 10} more" : string.Empty)
                      : string.Empty);

        _dialogs.Show(msg,
            o.Failed == 0 && !o.Cancelled ? $"Health Check — {what} Complete" : $"Health Check — {what}",
            Services.DialogButtons.Ok,
            o.Failed == 0 && !o.Cancelled ? Services.DialogIcon.Information : Services.DialogIcon.Warning);
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
            _dialogs.Show(
                "ffmpeg is required for remuxing. Install it in Settings → External Tools.",
                "ffmpeg not found", Services.DialogButtons.Ok,
                Services.DialogIcon.Warning);
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
            _dialogs.Show(
                $"A clean remuxed copy was created:\n{System.IO.Path.GetFileName(res.OutputPath)}\n\n" +
                "It's now loaded in the FILES panel (marked REMUXED). Test it / try embedding, " +
                "then use the buttons under the Preview panel:\n\n" +
                "  ✓ Replace Original — keep the remux, delete the original\n" +
                "  ↩ Restore Original — discard the remux, keep the original\n\n" +
                "Nothing is deleted until you choose.",
                "Remux Complete", Services.DialogButtons.Ok,
                Services.DialogIcon.Information);
        }
        else
        {
            Status = $"Remux failed: {res.Message}";
            _dialogs.Show(res.Message, "Remux Failed",
                Services.DialogButtons.Ok, Services.DialogIcon.Warning);
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
        Status = _clipboard.SetText(sb.ToString())
            ? "Report copied to clipboard."
            : "Couldn't access clipboard.";
    }

    /// <summary>
    /// Sequences auto-fixable issues in order of safety:
    ///   1. Extension mismatches  — rename only, lossless, instant
    ///   2. No-faststart MP4s    — lossless container rewrite (needs ffmpeg)
    /// ZeroBytes, unreadable, unknown-container and semicolon files are left alone.
    ///
    /// Build 169: now confirms first (saying exactly what it will do), shows the red
    /// "don't close" banner and live progress, refreshes the rows it fixed, can be
    /// cancelled, and ends with the same summary dialog as Fix All Faststart. Before,
    /// it ran silently and only changed the status line.
    /// </summary>
    private async Task FixAllIssuesAsync()
    {
        var mismatches = Results
            .Where(r => r.Issue == MediaHealthService.IssueType.ExtensionMismatch)
            .ToList();
        var noFaststart = Results
            .Where(r => r.Issue == MediaHealthService.IssueType.NoFaststart)
            .ToList();

        var canFaststart = FfmpegAvailable;
        var plan = new List<string>();
        if (mismatches.Count > 0)
            plan.Add($"• Rename {mismatches.Count} file(s) whose extension doesn't match their real format (instant, no rewrite)");
        if (noFaststart.Count > 0)
            plan.Add(canFaststart
                ? $"• Add faststart to {noFaststart.Count} MP4(s) (lossless remux, all streams kept — takes a while)"
                : $"• Skip {noFaststart.Count} MP4(s) needing faststart — ffmpeg isn't installed (Settings → External Tools)");

        int skippedOther = Results.Count(r =>
            r.Issue is MediaHealthService.IssueType.ZeroBytes
                    or MediaHealthService.IssueType.UnreadableHeader
                    or MediaHealthService.IssueType.UnknownContainer
                    or MediaHealthService.IssueType.SemicolonInComment);
        if (skippedOther > 0)
            plan.Add($"• Leave {skippedOther} file(s) with issues that need manual attention");

        if (mismatches.Count == 0 && (noFaststart.Count == 0 || !canFaststart))
        {
            _dialogs.Show(
                "There's nothing Fix All Issues can fix automatically.\n\n" + string.Join("\n", plan),
                "Fix All Issues", Services.DialogButtons.Ok, Services.DialogIcon.Information);
            return;
        }

        var confirm = _dialogs.Show(
            "Fix All Issues will:\n\n" + string.Join("\n", plan) + "\n\nContinue?",
            "Fix All Issues", Services.DialogButtons.YesNo, Services.DialogIcon.Question);
        if (confirm != Services.DialogResult.Yes) return;

        BeginFixing();
        using var history = OperationJournal.Begin("Fix All Issues", "Fix All Issues");
        var outcome = new FixOutcome { Skipped = canFaststart ? 0 : noFaststart.Count };
        var ct = _cts!.Token;
        int total = mismatches.Count + (canFaststart ? noFaststart.Count : 0);

        try
        {
            // ── Phase 1: rename extension mismatches ───────────────────────────
            int i = 0;
            foreach (var r in mismatches)
            {
                if (ct.IsCancellationRequested) { outcome.Cancelled = true; break; }
                i++;
                Status = $"Correcting extension {i}/{mismatches.Count}: {r.FileName}";
                var correctExt = MediaHealthService.GetCorrectExtension(r);
                if (correctExt == null)
                {
                    outcome.Failed++;
                    outcome.Errors.Enqueue($"{r.FileName}: couldn't tell which extension it should have.");
                    continue;
                }
                var newPath = Path.ChangeExtension(r.FilePath, correctExt);
                if (File.Exists(newPath))
                {
                    outcome.Failed++;
                    outcome.Errors.Enqueue($"{r.FileName}: {Path.GetFileName(newPath)} already exists — not overwritten.");
                    continue;
                }
                try
                {
                    File.Move(r.FilePath, newPath);
                    OperationJournal.Note("ExtensionFix", r.FilePath, newPath);   // build 174: undoable
                    ReplaceResult(r, newPath);
                    outcome.Renamed++;
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    outcome.Errors.Enqueue($"{r.FileName}: {ex.Message}");
                }
                Progress = (int)(i * 100.0 / Math.Max(1, total));
            }

            // ── Phase 2: faststart ─────────────────────────────────────────────
            if (!outcome.Cancelled && canFaststart && noFaststart.Count > 0)
                await RunFaststartBatchAsync(noFaststart, outcome, ct,
                    progressOffset: mismatches.Count, progressTotal: total);
        }
        finally
        {
            EndFixing();
        }

        history.Summary = $"Fix All Issues — {outcome.Renamed} extension(s) corrected, {outcome.Faststart} faststart(s) added" +
                          (outcome.Failed > 0 ? $", {outcome.Failed} failed" : "");
        Status = (outcome.Cancelled ? "Cancelled — " : "Fix All: ") +
                 $"{outcome.Renamed} extension(s) corrected, {outcome.Faststart} faststart(s) added" +
                 (outcome.Failed > 0 ? $", {outcome.Failed} failed." : ".");
        ShowCompletion("Fix All Issues", total, outcome);
        RaiseProperty(nameof(ReembedSemicolonCommand));
    }
}
