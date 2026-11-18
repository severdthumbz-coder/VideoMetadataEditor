using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>How serious a log entry is. Drives the colour and the level filter on the Log tab.</summary>
public enum LogLevel { Debug, Info, Success, Warning, Error }

/// <summary>One line in the activity log (build 175).</summary>
public sealed class LogEntry
{
    public long      Seq           { get; init; }
    public DateTime  Time          { get; init; }
    public LogLevel  Level         { get; init; }
    public string    Category      { get; init; } = "General";
    public string    Message       { get; init; } = string.Empty;
    /// <summary>Extra lines shown in the details pane: exception, command line, probe result…</summary>
    public string?   Detail        { get; init; }
    /// <summary>The file this entry is about, when there is one.</summary>
    public string?   FilePath      { get; init; }
    /// <summary>How long the step or operation took, when it was timed.</summary>
    public TimeSpan? Elapsed       { get; init; }
    /// <summary>Groups every entry written while one operation (a batch, a scan…) was running.</summary>
    public string?   OperationId   { get; init; }
    public string?   OperationName { get; init; }

    public string TimeText  => Time.ToString("HH:mm:ss.fff");
    public string DateTimeText => Time.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string LevelText => LogService.LevelLabel(Level);
    public string LevelIcon => Level switch
    {
        LogLevel.Debug   => "·",
        LogLevel.Info    => "ℹ",
        LogLevel.Success => "✓",
        LogLevel.Warning => "⚠",
        LogLevel.Error   => "✕",
        _                => ""
    };
    public string FileName    => string.IsNullOrEmpty(FilePath) ? string.Empty : Path.GetFileName(FilePath);
    public string ElapsedText => Elapsed is TimeSpan t ? LogService.FormatElapsed(t) : string.Empty;
    public bool   HasDetail   => !string.IsNullOrWhiteSpace(Detail);
    public bool   HasFile     => !string.IsNullOrEmpty(FilePath);
    public bool   HasOperation => !string.IsNullOrEmpty(OperationId);
    public bool   IsError     => Level == LogLevel.Error;
    public bool   IsWarning   => Level == LogLevel.Warning;
    public bool   IsSuccess   => Level == LogLevel.Success;
    public bool   IsDebug     => Level == LogLevel.Debug;

    /// <summary>Compact one-liner for small log views, e.g. "[01:52:03] Renamed: a → b".</summary>
    public string Display => $"[{Time:HH:mm:ss}] {LevelIcon} {Message}";

    /// <summary>The line as written to the log file (detail on indented lines below).</summary>
    public string ToLine()
    {
        var sb = new StringBuilder();
        sb.Append(DateTimeText).Append("  ")
          .Append(LevelText.PadRight(5)).Append("  ")
          .Append(Category.PadRight(11)).Append("  ")
          .Append(Message);
        if (Elapsed is TimeSpan t)  sb.Append("  (").Append(LogService.FormatElapsed(t)).Append(')');
        if (HasFile)                sb.Append("  [").Append(FilePath).Append(']');
        if (HasDetail)
            foreach (var line in Detail!.Replace("\r", "").Split('\n'))
                sb.Append(Environment.NewLine).Append("        ").Append(line);
        return sb.ToString();
    }
}

/// <summary>
/// The activity log behind the Log tab (build 175).
///
/// Every entry has a millisecond timestamp, a level (Debug, Info, Success, Warning,
/// Error), a category (Rename, Copy/Move, Library, FFmpeg…), the file it's about, and
/// optional detail lines (exception, command line, probe result). Operations — a batch
/// rename, a copy, a library scan — are timed with <see cref="Begin"/>: they log when
/// they start, every entry written while they run is tagged with the operation, and
/// they log when they finish with how long they took and the outcome.
///
/// Entries are kept in memory for the Log tab and written to logs\VME_yyyy-MM-dd.log
/// next to the EXE (portable, like config.json), so a session can be read after a
/// restart or sent in a bug report. Logs older than <see cref="KeepDays"/> are removed.
///
/// The 164 existing "[HH:mm:ss] message" call sites go through <see cref="WriteLegacy"/>,
/// which works out the level and category from the text. WPF-free so it's unit-tested.
/// </summary>
public static class LogService
{
    public const int MemoryCap = 5000;
    public static int KeepDays { get; set; } = 14;

    /// <summary>Override for tests. Defaults to "logs" next to the EXE.</summary>
    public static string Directory { get; set; } = Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>Raised for every new entry, on the thread that wrote it.</summary>
    public static event Action<LogEntry>? EntryAdded;

    private static readonly object Gate = new();
    private static readonly LinkedList<LogEntry> Recent = new();
    private static readonly AsyncLocal<Operation?> CurrentOp = new();
    private static long _seq;
    private static StreamWriter? _writer;
    private static string? _writerPath;
    private static DateTime _fileRetryAfterUtc;   // after a write failure, don't retry on every line

    /// <summary>The file today's entries are written to.</summary>
    public static string CurrentFilePath => Path.Combine(Directory, $"VME_{DateTime.Now:yyyy-MM-dd}.log");

    // ── Writing ──────────────────────────────────────────────────────────────

    public static LogEntry Write(LogLevel level, string category, string message,
                                 string? detail = null, string? filePath = null, TimeSpan? elapsed = null)
    {
        var op = CurrentOp.Value;
        var entry = new LogEntry
        {
            Seq           = Interlocked.Increment(ref _seq),
            Time          = DateTime.Now,
            Level         = level,
            Category      = string.IsNullOrWhiteSpace(category) ? "General" : category,
            Message       = message ?? string.Empty,
            Detail        = string.IsNullOrWhiteSpace(detail) ? null : detail,
            FilePath      = string.IsNullOrWhiteSpace(filePath) ? null : filePath,
            Elapsed       = elapsed,
            OperationId   = op?.Id,
            OperationName = op?.Name,
        };
        Add(entry);
        return entry;
    }

    public static LogEntry Debug(string category, string message, string? detail = null, string? file = null)
        => Write(LogLevel.Debug, category, message, detail, file);
    public static LogEntry Info(string category, string message, string? detail = null, string? file = null)
        => Write(LogLevel.Info, category, message, detail, file);
    public static LogEntry Success(string category, string message, string? detail = null, string? file = null)
        => Write(LogLevel.Success, category, message, detail, file);
    public static LogEntry Warn(string category, string message, string? detail = null, string? file = null)
        => Write(LogLevel.Warning, category, message, detail, file);
    public static LogEntry Error(string category, string message, string? detail = null, string? file = null)
        => Write(LogLevel.Error, category, message, detail, file);

    /// <summary>Logs an exception: its message on the line, the full exception in the detail.</summary>
    public static LogEntry Error(string category, string message, Exception ex, string? file = null)
        => Write(LogLevel.Error, category, $"{message}: {ex.Message}", ex.ToString(), file);

    /// <summary>
    /// Writes one of the older "[HH:mm:ss] ⚠ message" lines, working out the level and
    /// category from the text.
    /// </summary>
    public static LogEntry WriteLegacy(string raw)
    {
        var (level, category, message) = Classify(raw);
        return Write(level, category, message);
    }

    private static void Add(LogEntry entry)
    {
        lock (Gate)
        {
            Recent.AddLast(entry);
            while (Recent.Count > MemoryCap) Recent.RemoveFirst();
            WriteToFile(entry);
        }
        try { EntryAdded?.Invoke(entry); }
        catch { /* a broken listener must never break the operation that logged */ }
    }

    /// <summary>Entries already logged (oldest first) — the Log tab loads these at startup.</summary>
    public static List<LogEntry> Snapshot()
    {
        lock (Gate) return Recent.ToList();
    }

    // ── File ─────────────────────────────────────────────────────────────────

    private static void WriteToFile(LogEntry entry)
    {
        if (DateTime.UtcNow < _fileRetryAfterUtc) return;
        try
        {
            var path = CurrentFilePath;
            if (_writer == null || !string.Equals(_writerPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _writer?.Dispose();
                System.IO.Directory.CreateDirectory(Directory);
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                                           new UTF8Encoding(false)) { AutoFlush = true };
                _writerPath = path;
            }
            _writer.WriteLine(entry.ToLine());
        }
        catch
        {
            // The log file is best-effort (read-only folder, disk full…); the in-memory
            // log still works.
            try { _writer?.Dispose(); } catch { }
            _writer = null; _writerPath = null;
            _fileRetryAfterUtc = DateTime.UtcNow.AddSeconds(30);
        }
    }

    /// <summary>
    /// Marks the start of a session in the log file and removes logs older than
    /// <see cref="KeepDays"/>. Returns how many old files were removed.
    /// </summary>
    public static int StartSession(string headline, IEnumerable<string>? details = null)
    {
        int removed = PruneOldLogs();
        lock (Gate)
        {
            try
            {
                WriteToFile(new LogEntry { Time = DateTime.Now, Level = LogLevel.Info, Category = "App",
                                           Message = new string('═', 20) + " " + headline + " " + new string('═', 20) });
            }
            catch { }
        }
        Write(LogLevel.Info, "App", headline, details == null ? null : string.Join(Environment.NewLine, details));
        return removed;
    }

    public static int PruneOldLogs()
    {
        int removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return 0;
            var cutoff = DateTime.Now.Date.AddDays(-Math.Max(1, KeepDays));
            foreach (var f in System.IO.Directory.EnumerateFiles(Directory, "VME_*.log"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (stem.Length < 14 ||
                    !DateTime.TryParseExact(stem.Substring(4, 10), "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var day)) continue;
                if (day >= cutoff) continue;
                try { File.Delete(f); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>Closes the log file (app exit, tests).</summary>
    public static void Close()
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null; _writerPath = null;
            _fileRetryAfterUtc = default;
        }
    }

    /// <summary>Forgets the in-memory entries (tests).</summary>
    public static void ResetForTests()
    {
        lock (Gate) Recent.Clear();
        Close();
    }

    // ── Operations ───────────────────────────────────────────────────────────

    /// <summary>The operation running on this async flow, if any.</summary>
    public static Operation? CurrentOperation => CurrentOp.Value;

    /// <summary>
    /// Starts a timed operation: logs "▶ name started", tags everything logged until
    /// it's disposed, then logs "■ name finished — outcome (took 3.2 s)".
    /// </summary>
    public static Operation Begin(string category, string name, string? detail = null)
    {
        var op = new Operation(category, name, CurrentOp.Value);
        CurrentOp.Value = op;
        Write(LogLevel.Info, category, $"▶ {name} — started", detail);
        return op;
    }

    public sealed class Operation : IDisposable
    {
        private readonly Operation? _parent;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _closed;

        public string Id       { get; } = Guid.NewGuid().ToString("N")[..8];
        public string Name     { get; }
        public string Category { get; }
        /// <summary>Set before disposing: "12 renamed, 1 skipped".</summary>
        public string? Outcome { get; set; }
        public TimeSpan Elapsed => _sw.Elapsed;

        private LogLevel _endLevel = LogLevel.Success;
        private string?  _endDetail;
        private bool     _cancelled;

        internal Operation(string category, string name, Operation? parent)
        { Category = category; Name = name; _parent = parent; }

        /// <summary>Marks the operation as having problems (it still finishes normally).</summary>
        public void Warn(string? outcome = null)
        {
            if (_endLevel < LogLevel.Warning) _endLevel = LogLevel.Warning;
            if (outcome != null) Outcome = outcome;
        }

        public void Fail(Exception ex)
        {
            _endLevel = LogLevel.Error;
            Outcome = ex.Message;
            _endDetail = ex.ToString();
        }

        public void Fail(string why) { _endLevel = LogLevel.Error; Outcome = why; }

        public void Cancel() { _cancelled = true; if (_endLevel < LogLevel.Warning) _endLevel = LogLevel.Warning; }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _sw.Stop();
            var verb = _cancelled ? "cancelled" : _endLevel == LogLevel.Error ? "failed" : "finished";
            var text = $"■ {Name} — {verb}";
            if (!string.IsNullOrWhiteSpace(Outcome)) text += $": {Outcome}";
            // Still inside this operation, so the closing line carries its id.
            Write(_endLevel, Category, text, _endDetail, null, _sw.Elapsed);
            CurrentOp.Value = _parent;
        }
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    public static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Debug   => "DEBUG",
        LogLevel.Info    => "INFO",
        LogLevel.Success => "OK",
        LogLevel.Warning => "WARN",
        LogLevel.Error   => "ERROR",
        _                => level.ToString().ToUpperInvariant()
    };

    public static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalSeconds < 1)  return $"{Math.Max(0, (int)Math.Round(t.TotalMilliseconds))} ms";
        if (t.TotalMinutes < 1)  return $"{t.TotalSeconds:0.0} s";
        if (t.TotalHours   < 1)  return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
        return $"{(int)t.TotalHours}h {t.Minutes:D2}m";
    }

    // ── Classifying older free-text lines ────────────────────────────────────

    private static readonly Regex LeadingTime = new(@"^\s*\[\d{1,2}:\d{2}:\d{2}\]\s*", RegexOptions.CultureInvariant);
    private static readonly Regex ErrorWords  = new(@"\b(error|exception|aborted|blocked|could ?n[o']t)\b",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CountFailed = new(@"\b(\d+)\s+(failed|error\(s\)|errors?)\b",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FailedWord  = new(@"\bfail(ed|ure)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WarnWords   = new(@"\b(cancel+ed|cancel requested|skipped:|not available|missing|stale|offline)\b",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FallbackWords = new(@"\b(falling back|retrying|will retry|trying again)\b",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OkWords     = new(@"\b(succeeded|completed?|verified|restored|committed|refreshed)\b",
                                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // First match wins, so the more specific keys come first.
    private static readonly (string Key, string Category)[] CategoryKeys =
    {
        // Embed pipeline lines (build 176: "Copying to temp" is the embed's safe copy, not Move/Copy).
        ("writing tags", "Embed"),       ("copying to temp", "Embed"),     ("embedding tags", "Embed"),
        ("applying:", "Embed"),          ("done:", "Embed"),               ("in-place write", "Embed"),
        ("size verified", "Copy/Move"),  ("workers)", "Files"),
        ("settings saved", "Settings"),  ("watch folder", "Watch"),        ("library watch", "Library"),
        ("trakt", "Trakt"),              ("opensubtitles", "Subtitles"),   ("subtitle", "Subtitles"),
        ("update check", "Update"),      ("checking github", "Update"),
        ("history undo", "History"),     ("batch undo", "History"),        ("undo", "History"),
        ("health check", "Health"),      ("duplicate", "Duplicates"),
        ("tv batch", "TV"),              ("tv:", "TV"),                    ("episode", "TV"),
        ("remux", "FFmpeg"),             ("faststart", "FFmpeg"),          ("ffmpeg", "FFmpeg"),
        ("batch edit", "Batch Edit"),
        ("rename", "Rename"),
        ("fastcopy", "Copy/Move"),       ("built-in engine", "Copy/Move"), ("copy", "Copy/Move"),
        ("move", "Copy/Move"),           ("launching", "Copy/Move"),       ("aborted", "Copy/Move"),
        ("library", "Library"),          ("thumbnail", "Library"),         ("scan", "Library"),
        ("anilist", "Search"),           ("tmdb", "Search"),               ("omdb", "Search"),
        ("search", "Search"),
        ("artwork", "Artwork"),          ("poster", "Artwork"),
        ("nfo", "NFO"),
        ("csv", "Export"),               ("xlsx", "Export"),               ("export", "Export"),
        ("write", "Embed"),              ("embed", "Embed"),               ("apply", "Embed"),
        ("retry ", "Embed"),
        ("read-only", "Embed"),          ("blocked", "Embed"),             ("player", "Embed"),
        ("locking process", "Embed"),
        ("settings", "Settings"),
        ("delete", "Files"),             ("panel entry", "Files"),         ("files panel", "Files"),
        ("startup", "App"),
    };

    /// <summary>Works out level and category for a free-text line and strips its timestamp.</summary>
    public static (LogLevel Level, string Category, string Message) Classify(string raw)
    {
        var msg = LeadingTime.Replace(raw ?? string.Empty, string.Empty).TrimEnd();
        var trimmed = msg.TrimStart();

        LogLevel level = LogLevel.Info;
        bool leadingMark = false;
        if (trimmed.Length > 0)
        {
            switch (trimmed[0])
            {
                case '✕': case '✗': case '❌': level = LogLevel.Error;   leadingMark = true; break;
                case '⚠':                       level = LogLevel.Warning; leadingMark = true; break;
                case '✓': case '✔':             level = LogLevel.Success; leadingMark = true; break;
            }
        }

        if (!leadingMark)
        {
            var counted = CountFailed.Matches(msg);
            bool anyCountedFailure = counted.Any(m => int.TryParse(m.Groups[1].Value, out var n) && n > 0);
            bool onlyZeroCounts    = counted.Count > 0 && !anyCountedFailure;
            // "3 succeeded, 0 failed" is a success; "2 succeeded, 1 failed" is a warning.
            var withoutCounts = CountFailed.Replace(msg, string.Empty);

            // Build 177: "X failed — falling back to Y" is a step that recovered (or is still
            // trying), not the final error — that comes on its own line if it happens.
            bool recovering = FallbackWords.IsMatch(msg);

            if (anyCountedFailure || (recovering && (ErrorWords.IsMatch(withoutCounts) || FailedWord.IsMatch(withoutCounts))))
                                                         level = LogLevel.Warning;
            else if (ErrorWords.IsMatch(withoutCounts) ||
                     FailedWord.IsMatch(withoutCounts))  level = LogLevel.Error;
            else if (WarnWords.IsMatch(msg))             level = LogLevel.Warning;
            else if (OkWords.IsMatch(msg) || onlyZeroCounts ||
                     msg.StartsWith("Done:", StringComparison.Ordinal)) level = LogLevel.Success;
        }
        else
        {
            // The level column shows the mark, so drop it from the text.
            msg = trimmed.Substring(1).TrimStart('\uFE0F', ' ', '\t');
        }

        var lower = msg.ToLowerInvariant();
        var category = "General";
        foreach (var (key, cat) in CategoryKeys)
            if (lower.Contains(key)) { category = cat; break; }

        return (level, category, msg);
    }
}
