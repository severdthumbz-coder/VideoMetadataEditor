using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Persistent history of everything VME does to files (build 174).
///
/// Every rename, move, copy, extension fix, faststart and remux is recorded in
/// "history.jsonl" next to the EXE (portable, like config.json), one batch per line, so
/// it survives restarts. Renames, moves and extension fixes can be undone later from
/// Log → History, not just in the current session.
///
/// Recording uses an ambient batch: an operation opens a batch with <see cref="Begin"/>,
/// and low-level code (the shared rename helper, the copy engine) calls
/// <see cref="Note"/>. Notes join the open batch; with no batch open, a note becomes a
/// one-item entry of its own. AsyncLocal carries the batch through awaits and the
/// parallel workers an operation starts. WPF-free so it's unit-tested.
/// </summary>
public static class OperationJournal
{
    public const int MaxRecords = 500;

    /// <summary>Override for tests. Defaults to history.jsonl next to the EXE.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "history.jsonl");

    private static readonly object FileLock = new();
    private static readonly AsyncLocal<Batch?> Current = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Model ───────────────────────────────────────────────────────────────

    public sealed class Item
    {
        public string From { get; set; } = string.Empty;
        public string To   { get; set; } = string.Empty;
        /// <summary>Rename, Move, Copy, ExtensionFix, Faststart, Remux…</summary>
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class Record
    {
        public string   Id      { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime When    { get; set; } = DateTime.Now;
        public string   Kind    { get; set; } = string.Empty;
        public string   Summary { get; set; } = string.Empty;
        public List<Item> Items { get; set; } = new();
        public bool     Undone  { get; set; }
        /// <summary>For an "Undo" entry: the record it reversed.</summary>
        public string?  UndoOf  { get; set; }

        /// <summary>Kinds whose items are pure path changes and can be put back.</summary>
        public static bool IsReversibleKind(string kind) =>
            kind is "Rename" or "Move" or "ExtensionFix";

        // Ordinal, not ignore-case: a case-only rename ("the matrix.mp4" → "The Matrix.mp4")
        // is a real change on Windows and must be undoable too.
        [JsonIgnore]
        public IEnumerable<Item> ReversibleItems =>
            Items.Where(i => IsReversibleKind(i.Kind) &&
                             !string.Equals(i.From, i.To, StringComparison.Ordinal));

        [JsonIgnore]
        public bool CanUndo => !Undone && UndoOf == null && ReversibleItems.Any();
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public sealed class Batch : IDisposable
    {
        private readonly Batch? _parent;
        internal readonly Record Rec;
        private bool _closed;

        internal Batch(string kind, string summary, Batch? parent)
        {
            Rec = new Record { Kind = kind, Summary = summary };
            _parent = parent;
        }

        /// <summary>Replace the summary once the outcome is known ("Renamed 12 of 14").</summary>
        public string Summary { get => Rec.Summary; set => Rec.Summary = value; }

        internal void Add(Item item) { lock (Rec) Rec.Items.Add(item); }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            Current.Value = _parent;
            if (Rec.Items.Count > 0) Append(Rec);   // nothing happened → nothing recorded
        }
    }

    /// <summary>
    /// Opens a batch; dispose it to write the entry. Batches nest: an inner batch (e.g.
    /// a rename inside Batch Process) is recorded separately.
    /// </summary>
    public static Batch Begin(string kind, string summary)
    {
        var b = new Batch(kind, summary, Current.Value);
        Current.Value = b;
        return b;
    }

    /// <summary>Records one change. Joins the open batch, or becomes its own entry.</summary>
    public static void Note(string kind, string from, string to)
    {
        var item = new Item { Kind = kind, From = from, To = to };
        var batch = Current.Value;
        if (batch != null) { batch.Add(item); return; }

        Append(new Record
        {
            Kind = kind,
            Summary = from.Equals(to, StringComparison.OrdinalIgnoreCase)
                ? $"{kind}: {Path.GetFileName(from)}"
                : $"{kind}: {Path.GetFileName(from)} → {Path.GetFileName(to)}",
            Items = { item }
        });
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private static void Append(Record rec)
    {
        try
        {
            lock (FileLock)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(rec, Json) + Environment.NewLine);
                TrimIfNeeded();
            }
        }
        catch { /* history is best-effort — never fail the operation itself */ }
    }

    /// <summary>All records, newest first. Unreadable lines are skipped.</summary>
    public static List<Record> Load()
    {
        lock (FileLock) return ReadAll().AsEnumerable().Reverse().ToList();
    }

    private static List<Record> ReadAll()
    {
        var list = new List<Record>();
        if (!File.Exists(FilePath)) return list;
        foreach (var line in File.ReadAllLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<Record>(line, Json);
                if (r != null) list.Add(r);
            }
            catch { /* skip a damaged line */ }
        }
        return list;
    }

    private static void WriteAll(List<Record> oldestFirst)
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllLines(tmp, oldestFirst.Select(r => JsonSerializer.Serialize(r, Json)));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static void TrimIfNeeded()
    {
        // Cheap check first: only rewrite when the file has grown well past the cap.
        if (new FileInfo(FilePath).Length < 512 * 1024) return;
        var all = ReadAll();
        if (all.Count > MaxRecords) WriteAll(all.Skip(all.Count - MaxRecords).ToList());
    }

    // ── Undo ─────────────────────────────────────────────────────────────────

    public sealed record UndoOutcome(int Restored, List<string> Skipped);

    // Sidecars that travel with a video (subtitles, NFO, artwork) — moved back with it.
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".nfo", ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>
    /// Puts every reversible item of <paramref name="rec"/> back (newest change first).
    /// Never overwrites: an item is skipped if the file is no longer where VME put it or
    /// something already exists at the original location. Sidecars sharing the video's
    /// name (subtitles, .nfo, posters) are moved back with it. The original entry is
    /// marked undone and an "Undo" entry is recorded.
    /// </summary>
    public static UndoOutcome Undo(Record rec)
    {
        int restored = 0;
        var skipped = new List<string>();
        var undoItems = new List<Item>();

        foreach (var item in rec.ReversibleItems.Reverse())
        {
            var name = Path.GetFileName(item.To);
            if (!File.Exists(item.To)) { skipped.Add($"{name}: no longer at {item.To}"); continue; }

            // Case-only rename: same file to Windows, so "From exists" is expected. Rename it
            // back through a temporary name (a direct case-only move is a no-op on NTFS).
            if (string.Equals(item.From, item.To, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var temp = Path.Combine(Path.GetDirectoryName(item.To) ?? string.Empty,
                        $".vme_undo_{Guid.NewGuid():N}{Path.GetExtension(item.To)}");
                    File.Move(item.To, temp);
                    File.Move(temp, item.From);
                    restored++;
                    undoItems.Add(new Item { Kind = item.Kind, From = item.To, To = item.From });
                }
                catch (Exception ex) { skipped.Add($"{name}: {ex.Message}"); }
                continue;
            }

            if (File.Exists(item.From)) { skipped.Add($"{name}: {item.From} already exists"); continue; }

            try
            {
                var fromDir = Path.GetDirectoryName(item.From);
                if (!string.IsNullOrEmpty(fromDir)) Directory.CreateDirectory(fromDir);
                File.Move(item.To, item.From);
                restored++;
                undoItems.Add(new Item { Kind = item.Kind, From = item.To, To = item.From });
                MoveSidecarsBack(item.To, item.From);
            }
            catch (Exception ex) { skipped.Add($"{name}: {ex.Message}"); }
        }

        // Fully undone only when everything went back. A partial undo stays retryable
        // (e.g. after freeing a name that was taken); nothing restored = nothing changed.
        bool complete = restored > 0 && skipped.Count == 0;
        if (restored == 0) return new UndoOutcome(0, skipped);

        try
        {
            lock (FileLock)
            {
                var all = ReadAll();
                var target = all.FirstOrDefault(r => r.Id == rec.Id);
                if (target != null && complete) target.Undone = true;
                if (undoItems.Count > 0)
                    all.Add(new Record
                    {
                        Kind = "Undo",
                        Summary = $"Undid “{rec.Summary}” — {restored} file(s) put back",
                        UndoOf = rec.Id,
                        Items = undoItems
                    });
                WriteAll(all);
            }
            if (complete) rec.Undone = true;
        }
        catch { /* the files were still restored; only the history update failed */ }

        return new UndoOutcome(restored, skipped);
    }

    /// <summary>
    /// After a video goes back from <paramref name="videoNow"/> to <paramref name="videoBack"/>,
    /// moves its sidecars ("Name.en.srt", "Name.nfo", "Name-poster.jpg"…) back too,
    /// renaming them to match. Never overwrites.
    /// </summary>
    private static void MoveSidecarsBack(string videoNow, string videoBack)
    {
        try
        {
            var nowDir   = Path.GetDirectoryName(videoNow);
            var backDir  = Path.GetDirectoryName(videoBack);
            var nowStem  = Path.GetFileNameWithoutExtension(videoNow);
            var backStem = Path.GetFileNameWithoutExtension(videoBack);
            if (string.IsNullOrEmpty(nowDir) || string.IsNullOrEmpty(backDir) || !Directory.Exists(nowDir)) return;

            foreach (var f in Directory.EnumerateFiles(nowDir))
            {
                var fname = Path.GetFileName(f);
                if (!SidecarExtensions.Contains(Path.GetExtension(f))) continue;
                if (!(fname.StartsWith(nowStem + ".", StringComparison.OrdinalIgnoreCase) ||
                      fname.StartsWith(nowStem + "-", StringComparison.OrdinalIgnoreCase))) continue;

                var dest = Path.Combine(backDir, backStem + fname.Substring(nowStem.Length));
                if (File.Exists(dest)) continue;
                try { File.Move(f, dest); } catch { /* leave it — the video is back */ }
            }
        }
        catch { }
    }
}
