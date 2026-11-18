using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Log tab (build 175): the structured activity log.
///
/// Every entry from <see cref="LogService"/> lands here (newest first) with its time to
/// the millisecond, level, category, file, how long it took and any detail. The tab
/// filters by level, category, free text and operation ("show everything that happened
/// during this batch rename"), exports what's shown, and opens the logs folder where
/// each day's log is kept for <see cref="LogService.KeepDays"/> days.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Activity log, newest first (capped at <see cref="LogService.MemoryCap"/>).</summary>
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    /// <summary>"All" plus every category that has appeared this session.</summary>
    public ObservableCollection<string> LogCategories { get; } = new() { "All" };

    private ICollectionView? _logView;
    public ICollectionView LogView => _logView ??= InitLogView();

    private ICollectionView? _recentLogView;
    /// <summary>Unfiltered view for the small log on the Retrieved Data tab (the Log tab's filters don't apply).</summary>
    public ICollectionView RecentLogView => _recentLogView ??= new System.Windows.Data.ListCollectionView(LogEntries);

    private ICollectionView InitLogView()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(LogEntries);
        view.Filter = o => o is LogEntry e && PassesLogFilter(e);
        return view;
    }

    private void InitActivityLog()
    {
        // Entries written before the window existed (startup checks) come from the buffer.
        foreach (var e in LogService.Snapshot()) InsertLogEntry(e);
        LogService.EntryAdded += OnLogEntryAdded;
    }

    /// <summary>Stops listening (app exit).</summary>
    internal void DetachActivityLog() => LogService.EntryAdded -= OnLogEntryAdded;

    private void OnLogEntryAdded(LogEntry e)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) InsertLogEntry(e);
        else d.InvokeAsync(() => InsertLogEntry(e), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InsertLogEntry(LogEntry e)
    {
        // Entries from different threads can arrive slightly out of order — keep them
        // sorted by sequence number (newest first). Almost always index 0.
        int i = 0;
        while (i < LogEntries.Count && LogEntries[i].Seq > e.Seq) i++;
        LogEntries.Insert(i, e);
        CountLogLevel(e, +1);

        while (LogEntries.Count > LogService.MemoryCap)
        {
            var last = LogEntries[^1];
            LogEntries.RemoveAt(LogEntries.Count - 1);
            CountLogLevel(last, -1);
        }

        if (!LogCategories.Contains(e.Category))
        {
            // Keep "All" first and the rest alphabetical.
            int at = 1;
            while (at < LogCategories.Count &&
                   string.Compare(LogCategories[at], e.Category, StringComparison.OrdinalIgnoreCase) < 0) at++;
            LogCategories.Insert(at, e.Category);
        }
        RaiseLogCounts();
    }

    private int _logErrorCount, _logWarningCount;
    public int LogErrorCount   => _logErrorCount;
    public int LogWarningCount => _logWarningCount;
    public int LogTotalCount   => LogEntries.Count;
    public int LogShownCount   => LogEntries.Count(PassesLogFilter);

    private void CountLogLevel(LogEntry e, int delta)
    {
        if (e.Level == LogLevel.Error)   _logErrorCount   += delta;
        if (e.Level == LogLevel.Warning) _logWarningCount += delta;
    }

    private void RaiseLogCounts()
    {
        RaiseProperty(nameof(LogErrorCount));
        RaiseProperty(nameof(LogWarningCount));
        RaiseProperty(nameof(LogTotalCount));
        RaiseProperty(nameof(LogCountSummary));
    }

    /// <summary>"Showing 120 of 2,340 · 3 errors · 12 warnings".</summary>
    public string LogCountSummary
    {
        get
        {
            var total = LogEntries.Count;
            var text = IsLogFilterActive ? $"Showing {LogShownCount:N0} of {total:N0}" : $"{total:N0} entries";
            if (_logErrorCount > 0)   text += $"  ·  {_logErrorCount} error{(_logErrorCount == 1 ? "" : "s")}";
            if (_logWarningCount > 0) text += $"  ·  {_logWarningCount} warning{(_logWarningCount == 1 ? "" : "s")}";
            return text;
        }
    }

    // ── Filters ───────────────────────────────────────────────────────────────

    private bool _logShowDebug = true, _logShowInfo = true, _logShowSuccess = true,
                 _logShowWarning = true, _logShowError = true;

    public bool LogShowDebug   { get => _logShowDebug;   set { Set(ref _logShowDebug, value);   RefreshLogView(); } }
    public bool LogShowInfo    { get => _logShowInfo;    set { Set(ref _logShowInfo, value);    RefreshLogView(); } }
    public bool LogShowSuccess { get => _logShowSuccess; set { Set(ref _logShowSuccess, value); RefreshLogView(); } }
    public bool LogShowWarning { get => _logShowWarning; set { Set(ref _logShowWarning, value); RefreshLogView(); } }
    public bool LogShowError   { get => _logShowError;   set { Set(ref _logShowError, value);   RefreshLogView(); } }

    private string _logCategoryFilter = "All";
    public string LogCategoryFilter
    {
        get => _logCategoryFilter;
        set { Set(ref _logCategoryFilter, string.IsNullOrEmpty(value) ? "All" : value); RefreshLogView(); }
    }

    private string _logSearchText = string.Empty;
    public string LogSearchText
    {
        get => _logSearchText;
        set { Set(ref _logSearchText, value ?? string.Empty); RefreshLogView(); }
    }

    private string? _logOperationFilter;
    private string  _logOperationFilterName = string.Empty;
    /// <summary>Name of the operation the list is narrowed to, or empty.</summary>
    public string LogOperationFilterName => _logOperationFilterName;
    public bool   IsLogOperationFilterActive => _logOperationFilter != null;

    public bool IsLogFilterActive =>
        !(_logShowDebug && _logShowInfo && _logShowSuccess && _logShowWarning && _logShowError) ||
        _logCategoryFilter != "All" || !string.IsNullOrWhiteSpace(_logSearchText) || _logOperationFilter != null;

    private bool PassesLogFilter(LogEntry e)
    {
        bool levelOk = e.Level switch
        {
            LogLevel.Debug   => _logShowDebug,
            LogLevel.Info    => _logShowInfo,
            LogLevel.Success => _logShowSuccess,
            LogLevel.Warning => _logShowWarning,
            LogLevel.Error   => _logShowError,
            _                => true
        };
        if (!levelOk) return false;
        if (_logCategoryFilter != "All" &&
            !string.Equals(e.Category, _logCategoryFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (_logOperationFilter != null && e.OperationId != _logOperationFilter) return false;

        var q = _logSearchText.Trim();
        if (q.Length == 0) return true;
        return e.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
            || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (e.FilePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.Detail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (e.OperationName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void RefreshLogView()
    {
        LogView.Refresh();
        RaiseProperty(nameof(IsLogFilterActive));
        RaiseProperty(nameof(LogCountSummary));
    }

    // ── Selection / details pane ──────────────────────────────────────────────

    private LogEntry? _selectedLogEntry;
    public LogEntry? SelectedLogEntry
    {
        get => _selectedLogEntry;
        set { Set(ref _selectedLogEntry, value); RaiseProperty(nameof(HasSelectedLogEntry)); }
    }
    public bool HasSelectedLogEntry => _selectedLogEntry != null;

    // ── Commands ──────────────────────────────────────────────────────────────

    private ICommand? _clearLogCommand;
    /// <summary>Clears the list on screen. Today's log file keeps everything.</summary>
    public ICommand ClearLogCommand => _clearLogCommand ??= new RelayCommand(_ => ClearActivityLog());

    internal void ClearActivityLog()
    {
        LogEntries.Clear();
        _logErrorCount = _logWarningCount = 0;
        SelectedLogEntry = null;
        RaiseLogCounts();
    }

    private ICommand? _resetLogFiltersCommand;
    public ICommand ResetLogFiltersCommand => _resetLogFiltersCommand ??= new RelayCommand(_ =>
    {
        _logShowDebug = _logShowInfo = _logShowSuccess = _logShowWarning = _logShowError = true;
        _logCategoryFilter = "All";
        _logSearchText = string.Empty;
        _logOperationFilter = null;
        _logOperationFilterName = string.Empty;
        foreach (var p in new[] { nameof(LogShowDebug), nameof(LogShowInfo), nameof(LogShowSuccess),
                                  nameof(LogShowWarning), nameof(LogShowError), nameof(LogCategoryFilter),
                                  nameof(LogSearchText), nameof(LogOperationFilterName),
                                  nameof(IsLogOperationFilterActive) })
            RaiseProperty(p);
        RefreshLogView();
    });

    private ICommand? _problemsOnlyCommand;
    /// <summary>One click: errors and warnings only.</summary>
    public ICommand LogProblemsOnlyCommand => _problemsOnlyCommand ??= new RelayCommand(_ =>
    {
        LogShowDebug = false; LogShowInfo = false; LogShowSuccess = false;
        LogShowWarning = true; LogShowError = true;
    });

    private ICommand? _showLogOperationCommand;
    /// <summary>Narrows the list to everything logged during the selected entry's operation.</summary>
    public ICommand ShowLogOperationCommand => _showLogOperationCommand ??= new RelayCommand(p =>
    {
        var e = p as LogEntry ?? SelectedLogEntry;
        if (e?.OperationId == null) return;
        _logOperationFilter = e.OperationId;
        _logOperationFilterName = e.OperationName ?? "operation";
        RaiseProperty(nameof(LogOperationFilterName));
        RaiseProperty(nameof(IsLogOperationFilterActive));
        RefreshLogView();
    }, p => (p as LogEntry ?? SelectedLogEntry)?.OperationId != null);

    private ICommand? _clearLogOperationCommand;
    public ICommand ClearLogOperationFilterCommand => _clearLogOperationCommand ??= new RelayCommand(_ =>
    {
        _logOperationFilter = null;
        _logOperationFilterName = string.Empty;
        RaiseProperty(nameof(LogOperationFilterName));
        RaiseProperty(nameof(IsLogOperationFilterActive));
        RefreshLogView();
    });

    private ICommand? _copyLogEntryCommand;
    /// <summary>Copies the selected entry (with its detail) as it appears in the log file.</summary>
    public ICommand CopyLogEntryCommand => _copyLogEntryCommand ??= new RelayCommand(p =>
    {
        var e = p as LogEntry ?? SelectedLogEntry;
        if (e == null) return;
        try { System.Windows.Clipboard.SetText(e.ToLine()); StatusText = "Log entry copied."; }
        catch { StatusText = "Couldn't reach the clipboard — try again."; }
    });

    private ICommand? _copyShownLogCommand;
    /// <summary>Copies every entry currently shown (oldest first).</summary>
    public ICommand CopyShownLogCommand => _copyShownLogCommand ??= new RelayCommand(_ =>
    {
        var lines = LogView.Cast<LogEntry>().Reverse().Select(e => e.ToLine()).ToList();
        if (lines.Count == 0) return;
        try { System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines)); StatusText = $"Copied {lines.Count} log entries."; }
        catch { StatusText = "Couldn't reach the clipboard — try again."; }
    });

    private ICommand? _exportLogCommand;
    /// <summary>Saves the entries currently shown (oldest first) to a text file.</summary>
    public ICommand ExportLogCommand => _exportLogCommand ??= new RelayCommand(_ =>
    {
        var shown = LogView.Cast<LogEntry>().Reverse().ToList();
        if (shown.Count == 0) { StatusText = "Nothing to export — the log list is empty."; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export log",
            Filter = "Log file|*.log|Text file|*.txt",
            FileName = $"VME log {DateTime.Now:yyyy-MM-dd HHmm}.log"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var header = new[]
            {
                $"Video Metadata Editor {AppVersionText} — log export {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                IsLogFilterActive ? $"Filtered: {LogCountSummary}" : $"{shown.Count} entries",
                string.Empty
            };
            File.WriteAllLines(dlg.FileName, header.Concat(shown.Select(e => e.ToLine())));
            LogService.Info("Export", $"Log exported ({shown.Count} entries)", file: dlg.FileName);
            StatusText = $"Exported {shown.Count} log entries → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { LogService.Error("Export", "Log export failed", ex, dlg.FileName); }
    });

    private ICommand? _openLogFolderCommand;
    /// <summary>Opens the logs folder next to the EXE with today's file selected.</summary>
    public ICommand OpenLogFolderCommand => _openLogFolderCommand ??= new RelayCommand(_ =>
    {
        try
        {
            Directory.CreateDirectory(LogService.Directory);
            var today = LogService.CurrentFilePath;
            var args = File.Exists(today) ? $"/select,\"{today}\"" : $"\"{LogService.Directory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = $"Couldn't open the logs folder: {ex.Message}"; }
    });

    private ICommand? _openLogEntryFileCommand;
    /// <summary>Shows the selected entry's file in Explorer (or its folder if it has moved).</summary>
    public ICommand OpenLogEntryFileCommand => _openLogEntryFileCommand ??= new RelayCommand(p =>
    {
        var e = p as LogEntry ?? SelectedLogEntry;
        var path = e?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(Path.GetDirectoryName(path)))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"") { UseShellExecute = true });
            else StatusText = "That file is no longer there.";
        }
        catch (Exception ex) { StatusText = $"Couldn't open Explorer: {ex.Message}"; }
    }, p => !string.IsNullOrEmpty((p as LogEntry ?? SelectedLogEntry)?.FilePath));

    private static string AppVersionText =>
        "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?");
}
