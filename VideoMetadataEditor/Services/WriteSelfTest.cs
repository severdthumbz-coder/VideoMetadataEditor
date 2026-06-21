using System.IO;
using System.Text;
using TagLib;
using File     = TagLib.File;
using SysFile  = System.IO.File;
using IOPath   = System.IO.Path;
using IODir    = System.IO.Directory;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Built-in "Full File Write Diagnostic" — folds the entire v1–v16 standalone
/// investigation (that isolated the Build 105 semicolon-truncation bug) into a single
/// reproducible tool. Given a file path, it runs a battery of checks against a working
/// COPY of the file (the original is never modified) and reports pass/fail/info for
/// each, plus an overall interpretation and an exportable log.
///
/// Pure of any WPF/UI dependency so the engine itself is unit-testable. The dialog in
/// the View layer renders the results and handles file/save pickers.
/// </summary>
public static class WriteSelfTest
{
    public enum Outcome { Pass, Fail, Info }

    public record StepResult(int Number, string Name, Outcome Outcome, string Detail);

    public record Report(
        string FilePath,
        IReadOnlyList<StepResult> Steps,
        string Interpretation)
    {
        public int Passed => Steps.Count(s => s.Outcome == Outcome.Pass);
        public int Failed => Steps.Count(s => s.Outcome == Outcome.Fail);
        public bool AllPassed => Failed == 0;
    }

    // The canonical token block used across the original diagnostics.
    private const string Token = "[VME:IMDB=tt11188560][VME:TMDB=766105][VME:RATING=7.0][VME:MPA=R]";

    /// <summary>
    /// Run the full diagnostic against <paramref name="filePath"/>. Progress is reported
    /// per step (number, name) so the UI can stream results with a check/cross as each
    /// completes. Never throws — any internal failure becomes a Fail/Info step.
    /// </summary>
    public static Report Run(string filePath, Action<StepResult>? onStep = null)
    {
        var steps = new List<StepResult>();
        void Add(StepResult r) { steps.Add(r); onStep?.Invoke(r); }

        if (!SysFile.Exists(filePath))
        {
            var r = new StepResult(0, "File exists", Outcome.Fail, $"File not found: {filePath}");
            Add(r);
            return new Report(filePath, steps, "The selected file does not exist.");
        }

        var ext = IOPath.GetExtension(filePath);
        var dir = IOPath.GetDirectoryName(filePath) ?? IOPath.GetTempPath();

        // Local helper: write a comment to a fresh copy and read it back.
        // Returns (ok, readLength, exceptionMessage?). Works on a copy in the given
        // location (temp dir by default; the source folder for the same-drive tests).
        (bool ok, int readLen, string? ex) WriteRead(string comment, bool besideOriginal = false,
            byte[]? artwork = null, bool clearFirst = false, bool removeApple = false)
        {
            var copyDir = besideOriginal ? dir : IOPath.GetTempPath();
            var work = IOPath.Combine(copyDir, $".vme_selftest_{Guid.NewGuid():N}{ext}");
            try
            {
                SysFile.Copy(filePath, work, true);
                if (clearFirst)
                {
                    using var fc = File.Create(work); fc.Tag.Comment = null; fc.Save();
                }
                if (removeApple)
                {
                    using var fr = File.Create(work); fr.RemoveTags(TagTypes.Apple); fr.Save();
                }
                using (var f = File.Create(work))
                {
                    f.Tag.Comment = comment;
                    if (artwork != null)
                        f.Tag.Pictures = new IPicture[]
                        {
                            new Picture(new ByteVector(artwork))
                                { Type = PictureType.FrontCover, MimeType = "image/jpeg", Description = "Cover" }
                        };
                    f.Save();
                }
                using (var f = File.Create(work))
                {
                    var c = f.Tag.Comment ?? "";
                    return (c == comment, c.Length, null);
                }
            }
            catch (Exception ex) { return (false, -1, ex.Message); }
            finally { try { if (SysFile.Exists(work)) SysFile.Delete(work); } catch { } }
        }

        byte[] FakeJpeg(int len)
        {
            var a = new byte[Math.Max(8, len)];
            new Random(1).NextBytes(a);
            a[0] = 0xFF; a[1] = 0xD8; a[2] = 0xFF; a[^2] = 0xFF; a[^1] = 0xD9;
            return a;
        }

        // Read the file's own existing comment + artwork once, for the real-data tests.
        string existingComment = "";
        byte[]? existingArt = null;
        try
        {
            using var f = File.Create(filePath);
            existingComment = f.Tag.Comment ?? "";
            if (f.Tag.Pictures is { Length: > 0 } p) existingArt = p[0].Data?.Data;
        }
        catch { }

        // ── 1. Container opens & basic round-trip (v1) ───────────────────────────
        {
            var (ok, _, ex) = WriteRead(Token);
            Add(new StepResult(1, "Container opens & comment round-trips",
                ok ? Outcome.Pass : Outcome.Fail,
                ex != null ? $"TagLib# error: {ex}" :
                ok ? "A basic token comment writes and reads back correctly."
                   : "The basic token comment did not survive a write."));
        }

        // ── 2. Comment + Description together (v2) ───────────────────────────────
        {
            var work = IOPath.Combine(IOPath.GetTempPath(), $".vme_selftest_{Guid.NewGuid():N}{ext}");
            bool ok; string? ex = null;
            try
            {
                SysFile.Copy(filePath, work, true);
                using (var f = File.Create(work)) { f.Tag.Comment = Token; f.Tag.Description = Token; f.Save(); }
                using (var f = File.Create(work)) ok = (f.Tag.Comment ?? "") == Token;
            }
            catch (Exception e) { ok = false; ex = e.Message; }
            finally { try { if (SysFile.Exists(work)) SysFile.Delete(work); } catch { } }
            Add(new StepResult(2, "Comment + Description fields together",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "Setting both Comment and Description preserves the comment."
                          : "Writing Comment and Description together dropped the comment.")));
        }

        // ── 3. Full field set without artwork (v3/v4) ────────────────────────────
        {
            var work = IOPath.Combine(IOPath.GetTempPath(), $".vme_selftest_{Guid.NewGuid():N}{ext}");
            bool ok; string? ex = null;
            try
            {
                SysFile.Copy(filePath, work, true);
                using (var f = File.Create(work))
                {
                    f.Tag.Title = "DIAG"; f.Tag.Genres = new[] { "Drama" };
                    f.Tag.Performers = new[] { "Cast" }; f.Tag.Composers = new[] { "Dir" };
                    f.Tag.Year = 2021; f.Tag.Comment = Token; f.Tag.Description = Token;
                    f.Save();
                }
                using (var f = File.Create(work)) ok = (f.Tag.Comment ?? "") == Token;
            }
            catch (Exception e) { ok = false; ex = e.Message; }
            finally { try { if (SysFile.Exists(work)) SysFile.Delete(work); } catch { } }
            Add(new StepResult(3, "Full field set (no artwork)",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "All standard fields plus tokens write correctly."
                          : "Writing the full field set dropped the comment.")));
        }

        // ── 4. Full field set WITH synthetic artwork (v4) ────────────────────────
        {
            var (ok, _, ex) = WriteRead(Token, artwork: FakeJpeg(40_000));
            Add(new StepResult(4, "Full field set with artwork",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "Tokens survive alongside an embedded cover image."
                          : "Embedding artwork dropped the comment.")));
        }

        // ── 5. Write beside the original (same drive/folder) (v5) ────────────────
        {
            var (ok, _, ex) = WriteRead(Token, besideOriginal: true);
            Add(new StepResult(5, "Write on the file's own drive",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "Writing a temp copy beside the original on the same drive works."
                          : "Writing on the file's own drive failed.")));
        }

        // ── 6. Overwrite an existing comment (v6) ────────────────────────────────
        {
            var (ok, _, ex) = WriteRead("A new comment of moderate length to overwrite whatever exists.");
            Add(new StepResult(6, "Overwrite existing comment",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "An existing comment can be replaced."
                          : "Replacing the existing comment failed.")));
        }

        // ── 7. Comment length tolerance 100–1000 (v7) ────────────────────────────
        {
            var lens = new[] { 100, 250, 500, 1000 };
            var failed = new List<int>(); string? ex = null;
            foreach (var L in lens)
            {
                var (ok, _, e) = WriteRead(new string('A', L));
                if (e != null) { ex = e; break; }
                if (!ok) failed.Add(L);
            }
            Add(new StepResult(7, "Comment length tolerance (100–1000)",
                ex != null ? Outcome.Fail : failed.Count == 0 ? Outcome.Pass : Outcome.Fail,
                ex ?? (failed.Count == 0 ? "Synthetic comments of every tested length round-trip."
                          : $"Failed at length(s): {string.Join(", ", failed)}.")));
        }

        // ── 8. Real compressed-artwork shape (v8) ────────────────────────────────
        {
            if (existingArt is { Length: > 0 })
            {
                var (ok, _, ex) = WriteRead(Token, artwork: existingArt);
                Add(new StepResult(8, "File's own artwork as write payload",
                    ok ? Outcome.Pass : Outcome.Fail,
                    ex ?? (ok ? $"The file's existing {existingArt.Length}-byte cover writes back fine."
                              : "Re-embedding the file's own artwork dropped the comment.")));
            }
            else
                Add(new StepResult(8, "File's own artwork as write payload", Outcome.Info,
                    "File has no embedded artwork to test with."));
        }

        // ── 9. moov atom position / faststart (v9) ───────────────────────────────
        {
            var (layout, atEnd) = AtomLayout(filePath);
            Add(new StepResult(9, "MP4 atom layout (faststart check)",
                Outcome.Info,
                layout + (atEnd ? "  ⚠ moov-at-end files need a full rewrite to tag — remux to faststart if writes are unreliable."
                                 : "")));
        }

        // ── 10. Determinism: write same comment 5× (v13) ─────────────────────────
        {
            int ok = 0; string? ex = null;
            for (int i = 0; i < 5; i++)
            {
                var (o, _, e) = WriteRead("Determinism check comment, moderate length, no semicolons.");
                if (e != null) { ex = e; break; }
                if (o) ok++;
            }
            Add(new StepResult(10, "Write determinism (5 repeats)",
                ex != null ? Outcome.Fail : ok == 5 ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok == 5 ? "Identical writes succeed consistently (deterministic)."
                          : $"Only {ok}/5 repeats succeeded — writes are intermittent (environmental/drive).")));
        }

        // ── 11. Real existing description round-trip (v10/v14) ───────────────────
        {
            if (!string.IsNullOrEmpty(existingComment))
            {
                var realPlusToken = StripVmeTokens(existingComment).Trim();
                realPlusToken = (realPlusToken.Length > 0 ? realPlusToken + "\n" : "") + Token;
                var (ok, readLen, ex) = WriteRead(realPlusToken);
                Add(new StepResult(11, "File's real description + tokens",
                    ok ? Outcome.Pass : Outcome.Fail,
                    ex ?? (ok ? "The file's own description text round-trips with tokens intact."
                              : $"FAILED — wrote {realPlusToken.Length} chars, read back {readLen}. " +
                                "The real description contains something the synthetic text did not.")));
            }
            else
                Add(new StepResult(11, "File's real description + tokens", Outcome.Info,
                    "File has no existing comment/description to test with."));
        }

        // ── 12. Semicolon truncation probe (v15/v16 — the Build 105 bug) ─────────
        {
            var withSemi = "Part one of the description; part two after a semicolon. " + Token;
            var noSemi   = withSemi.Replace(';', ',');
            var (okSemi, lenSemi, exSemi) = WriteRead(withSemi);
            var (okNo, _, _) = WriteRead(noSemi);
            Outcome oc; string detail;
            if (exSemi != null) { oc = Outcome.Fail; detail = $"Error: {exSemi}"; }
            else if (!okSemi && okNo)
            {
                oc = Outcome.Fail;
                detail = $"SEMICOLON TRUNCATION DETECTED — a comment with ';' read back only {lenSemi} chars " +
                         "(truncated at the semicolon), but the same comment with ';' replaced by ',' wrote fully. " +
                         "This is the Build 105 bug; ensure you are on Build 105+ where Encode sanitizes semicolons.";
            }
            else if (okSemi) { oc = Outcome.Pass; detail = "A comment containing a semicolon writes fully (no truncation)."; }
            else { oc = Outcome.Fail; detail = "Both semicolon and comma variants failed — see other steps."; }
            Add(new StepResult(12, "Semicolon truncation probe", oc, detail));
        }

        // ── 13. VME Encode output is semicolon-safe (Build 105 fix) ──────────────
        {
            var encoded = VmeCommentCodec.Encode(
                description: "A; B; C — prose with semicolons.",
                imdbId: "tt11188560", tmdbId: "766105", rating: 7.0f, mpaRating: "R");
            bool clean = !encoded.Contains(';');
            Add(new StepResult(13, "Encode() sanitizes semicolons",
                clean ? Outcome.Pass : Outcome.Fail,
                clean ? "VmeCommentCodec.Encode produces no raw semicolons (Build 105 fix active)."
                      : "Encode still emits a raw ';' — the Build 105 sanitization is missing."));
        }

        // ── 14. Full encoded VME comment round-trips on this file ────────────────
        {
            var encoded = VmeCommentCodec.Encode(
                description: StripVmeTokens(existingComment).Trim(),
                imdbId: "tt11188560", tmdbId: "766105", rating: 7.0f, mpaRating: "R");
            var (ok, readLen, ex) = WriteRead(encoded);
            var decoded = ok ? VmeCommentCodec.Decode(encoded) : null;
            Add(new StepResult(14, "Full VME comment writes & decodes",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? $"The real encoded comment round-trips and decodes (IMDB={decoded?.ImdbId})."
                          : $"FAILED — wrote {encoded.Length}, read {readLen}.")));
        }

        // ── 15. Atomic temp→replace cycle (v5 TEST2) ─────────────────────────────
        {
            var fakeOrig = IOPath.Combine(dir, $".vme_selftest_orig_{Guid.NewGuid():N}{ext}");
            var tmp = IOPath.Combine(dir, $".vme_selftest_tmp_{Guid.NewGuid():N}{ext}");
            var bak = IOPath.Combine(dir, $".vme_selftest_bak_{Guid.NewGuid():N}{ext}");
            bool ok; string? ex = null;
            try
            {
                SysFile.Copy(filePath, fakeOrig, true);
                SysFile.Copy(fakeOrig, tmp, true);
                using (var f = File.Create(tmp)) { f.Tag.Comment = Token; f.Save(); }
                SysFile.Replace(tmp, fakeOrig, bak, true);
                using (var f = File.Create(fakeOrig)) ok = (f.Tag.Comment ?? "") == Token;
            }
            catch (Exception e) { ok = false; ex = e.Message; }
            finally { foreach (var p in new[] { fakeOrig, tmp, bak }) try { if (SysFile.Exists(p)) SysFile.Delete(p); } catch { } }
            Add(new StepResult(15, "Atomic temp-write + replace on this drive",
                ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "The temp-write-then-atomic-replace cycle (the app's real write path) works here."
                          : "The atomic replace cycle failed on this drive.")));
        }

        // ── 16. End-to-end through the real codec + verifier ─────────────────────
        {
            var encoded = VmeCommentCodec.Encode(
                description: "Final end-to-end check; with a semicolon to be safe.",
                imdbId: "tt11188560", tmdbId: "766105", rating: 7.0f, mpaRating: "R");
            var work = IOPath.Combine(IOPath.GetTempPath(), $".vme_selftest_{Guid.NewGuid():N}{ext}");
            bool ok = false; string? ex = null; IReadOnlyList<string> missing = Array.Empty<string>();
            try
            {
                SysFile.Copy(filePath, work, true);
                using (var f = File.Create(work)) { f.Tag.Comment = encoded; f.Save(); }
                using (var f = File.Create(work))
                {
                    var readBack = f.Tag.Comment ?? "";
                    missing = VmeCommentCodec.VerifyWritten(encoded, readBack);
                    ok = missing.Count == 0;
                }
            }
            catch (Exception e) { ex = e.Message; }
            finally { try { if (SysFile.Exists(work)) SysFile.Delete(work); } catch { } }
            Add(new StepResult(16, "End-to-end write + VerifyWritten",
                ex != null ? Outcome.Fail : ok ? Outcome.Pass : Outcome.Fail,
                ex ?? (ok ? "A real encoded write passes post-write verification — this file will embed correctly."
                          : $"Verification reported missing fields: {string.Join(", ", missing)}.")));
        }

        return new Report(filePath, steps, Interpret(steps));
    }

    // ── Interpretation: translate the step pattern into a plain-language verdict ──
    private static string Interpret(List<StepResult> steps)
    {
        StepResult? S(int n) => steps.FirstOrDefault(s => s.Number == n);
        bool Failed(int n) => S(n)?.Outcome == Outcome.Fail;
        bool Passed(int n) => S(n)?.Outcome == Outcome.Pass;

        if (steps.All(s => s.Outcome != Outcome.Fail))
            return "All checks passed. This file embeds metadata correctly. If you previously saw a "
                 + "revert, re-embed it once on this build and rescan — the stored tags should now persist.";

        // The signature of the Build 105 bug: semicolon probe fails but synthetic writes pass.
        if (Failed(12) && Passed(1) && Passed(7))
            return "ROOT CAUSE: this file's metadata write is broken by a SEMICOLON in the comment text "
                 + "(TagLib# truncates the comment atom at the first ';'). If step 13 also failed, you are on a "
                 + "pre-Build-105 build — update so Encode sanitizes semicolons. If step 13 passed, the app will "
                 + "write this file correctly; the failure is only reproduced by the raw probe.";

        if (Failed(11) && Passed(7))
            return "The file's REAL description text fails to write while synthetic text of the same length "
                 + "succeeds — a specific character in the description is the trigger (most often a semicolon; "
                 + "see step 12). Build 105 sanitizes this; ensure you are updated.";

        if (Failed(10))
            return "Writes are INTERMITTENT on this drive (step 10), which points to an environmental cause — "
                 + "drive, antivirus, or file-locking — rather than the file content. Try a different drive or "
                 + "temporarily exclude the folder from real-time scanning.";

        if (S(9)?.Detail.Contains("moov-at-end") == true && (Failed(1) || Failed(15)))
            return "This file is MOOV-AT-END and the in-place write path is unreliable for it. Use "
                 + "Remux → .mp4 (fix + faststart) then Replace Original; the rebuilt file will tag cleanly.";

        if (Failed(1))
            return "The container fails even a basic comment write. The file may be damaged or in a format that "
                 + "cannot reliably hold embedded metadata. Remux to .mp4, or export a .nfo sidecar as an alternative.";

        return "Some checks failed. Review the step details above; the failing steps localize where the write "
             + "breaks down. Export this log so it can be analysed for a future build improvement.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string StripVmeTokens(string comment) =>
        string.IsNullOrEmpty(comment)
            ? ""
            : System.Text.RegularExpressions.Regex.Replace(comment, @"\[VME:[A-Z_]+=[^\]]*\]", "");

    private static (string layout, bool moovAtEnd) AtomLayout(string path)
    {
        try
        {
            var fi = new System.IO.FileInfo(path);
            int n = (int)Math.Min(300_000, fi.Length);
            var head = new byte[n];
            using (var fs = SysFile.OpenRead(path)) fs.Read(head, 0, n);
            int Find(string fourcc)
            {
                var b = Encoding.ASCII.GetBytes(fourcc);
                for (int i = 0; i < n - b.Length; i++)
                {
                    bool m = true;
                    for (int j = 0; j < b.Length; j++) if (head[i + j] != b[j]) { m = false; break; }
                    if (m) return i;
                }
                return -1;
            }
            int moov = Find("moov"), mdat = Find("mdat");
            bool atEnd = moov < 0 || (mdat >= 0 && moov > mdat);
            return ($"ftyp@{Find("ftyp")} moov@{moov} mdat@{mdat} — {(atEnd ? "MOOV-AT-END" : "faststart OK")}", atEnd);
        }
        catch (Exception ex) { return ($"(atom scan failed: {ex.Message})", false); }
    }

    /// <summary>
    /// Best-effort read of a file's embedded comment text (for the Health Check
    /// semicolon scan). Returns null on any error. Read-only — never modifies the file.
    /// </summary>
    public static string? TryReadComment(string filePath)
    {
        try
        {
            using var f = File.Create(filePath);
            return f.Tag.Comment ?? string.Empty;
        }
        catch { return null; }
    }

    /// <summary>Render a full report as an exportable plain-text log.</summary>
    public static string FormatLog(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Video Metadata Editor — Full File Write Diagnostic");
        sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"File      : {report.FilePath}");
        sb.AppendLine($"Result    : {report.Passed} passed, {report.Failed} failed of {report.Steps.Count}");
        sb.AppendLine(new string('=', 70));
        foreach (var s in report.Steps)
        {
            var mark = s.Outcome switch { Outcome.Pass => "[PASS]", Outcome.Fail => "[FAIL]", _ => "[INFO]" };
            sb.AppendLine($"{mark} {s.Number,2}. {s.Name}");
            sb.AppendLine($"        {s.Detail}");
        }
        sb.AppendLine(new string('=', 70));
        sb.AppendLine("INTERPRETATION:");
        sb.AppendLine(report.Interpretation);
        return sb.ToString();
    }
}
