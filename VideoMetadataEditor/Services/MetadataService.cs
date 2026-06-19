using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using TagLib;
using VideoMetadataEditor.Models;
using File    = TagLib.File;
using SysFile = System.IO.File;

namespace VideoMetadataEditor.Services;

public class MetadataService
{
    // ── Format Detection ─────────────────────────────────────────────────────

    public static VideoFormat DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4"  => VideoFormat.MP4,
            "mkv"  => VideoFormat.MKV,
            "mov"  => VideoFormat.MOV,
            "wmv"  => VideoFormat.WMV,
            "avi"  => VideoFormat.AVI,
            "m4v"  => VideoFormat.M4V,
            "webm" => VideoFormat.WebM,
            _      => VideoFormat.Unknown
        };
    }

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        return ext is "mp4" or "mkv" or "mov" or "wmv" or "m4v" or "webm";
    }

    public static (bool supportsArtwork, bool supportsFullTags, string? warning) GetFormatCapabilities(VideoFormat format)
    {
        return format switch
        {
            VideoFormat.WebM => (false, false, "WebM has limited tag support. Genre, cast, and artwork fields may not be saved."),
            VideoFormat.WMV  => (true,  false, "WMV tag support is limited. Some fields may not embed correctly."),
            VideoFormat.MOV  => (true,  true,  "MOV metadata compatibility varies by player. Test embedded tags before bulk processing."),
            VideoFormat.MKV  => (true,  true,  null),
            VideoFormat.MP4  => (true,  true,  null),
            VideoFormat.M4V  => (true,  true,  null),
            VideoFormat.AVI  => (false, false, "AVI has very limited tag support (Title only via RIFF INFO). Genre, Director, Cast, Year and artwork may not be saved. Consider converting to MKV or MP4 for full metadata support."),
            _                => (false, false, "Unknown format — metadata embedding may not be supported.")
        };
    }

    // ── Read Metadata ─────────────────────────────────────────────────────────

    public MovieMetadata ReadMetadata(string filePath)
        => ReadMetadataCore(filePath, includeArtwork: true);

    public MovieMetadata ReadMetadataFast(string filePath)
        => ReadMetadataCore(filePath, includeArtwork: false);

    public byte[]? ReadArtworkOnly(string filePath)
    {
        try
        {
            using var tagFile = File.Create(filePath);
            if (tagFile.Tag.Pictures is { Length: > 0 })
                return tagFile.Tag.Pictures[0].Data.Data;
        }
        catch { }
        return null;
    }

    private MovieMetadata ReadMetadataCore(string filePath, bool includeArtwork)
    {
        var meta = new MovieMetadata();
        try
        {
            using var tagFile = File.Create(filePath);
            var tag = tagFile.Tag;

            meta.Title    = tag.Title ?? string.Empty;
            meta.Year     = tag.Year > 0 ? tag.Year.ToString() : string.Empty;
            meta.Genre    = tag.Genres is { Length: > 0 }
                ? string.Join(", ", tag.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).OrderBy(g => g))
                : string.Empty;
            meta.Director = tag.Composers is { Length: > 0 }
                ? string.Join(", ", tag.Composers.Where(c => !string.IsNullOrWhiteSpace(c)))
                : string.Empty;
            meta.Cast     = tag.Performers is { Length: > 0 }
                ? string.Join(", ", tag.Performers.Where(p => !string.IsNullOrWhiteSpace(p)))
                : string.Empty;

            var comment      = tag.Comment ?? string.Empty;
            meta.Description = ExtractDescription(comment);
            meta.ImdbId      = ExtractTagValue(comment, "IMDB");
            meta.TmdbId      = ExtractTagValue(comment, "TMDB");

            var ratingStr = ExtractTagValue(comment, "RATING");
            meta.MpaRating = ExtractTagValue(comment, "MPA");
            meta.IsWatched = ExtractTagValue(comment, "WATCHED") == "1";
            // TV / Episode fields
            meta.IsEpisode     = ExtractTagValue(comment, "EP_MODE") == "1";
            meta.ShowTitle     = ExtractTagValue(comment, "SHOW");
            var seasonStr      = ExtractTagValue(comment, "SEASON");
            if (int.TryParse(seasonStr, out var sn)) meta.Season = sn;
            var episodeStr     = ExtractTagValue(comment, "EPISODE");
            if (int.TryParse(episodeStr, out var en)) meta.Episode = en;
            meta.EpisodeTitle  = ExtractTagValue(comment, "ETITLE");
            meta.AiredDate     = ExtractTagValue(comment, "AIRED");
            meta.TvdbId        = ExtractTagValue(comment, "TVDB");
            meta.TmdbSeriesId  = ExtractTagValue(comment, "TMDB_SERIES");
            if (float.TryParse(ratingStr,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r) && r > 0)
                meta.Rating = r;

            if (string.IsNullOrWhiteSpace(meta.Description))
                meta.Description = tag.Description ?? string.Empty;

            // ── Standard tag fallbacks ────────────────────────────────────────
            // Files tagged by external tools (TagScanner, tinyMediaManager, Plex)
            // write to standard fields, not VME comment tokens. Fill any gaps so
            // their data is visible without re-tagging everything through VME.

            // IMDB ID — MP4 stores it in tag.Comment naively; some taggers use
            // a dedicated custom field accessible via tag.GetField / tag.Comment
            if (string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                // TagLib# exposes freeform atoms; try the common storage location
                if (tagFile.Tag is TagLib.Mpeg4.AppleTag appleTag)
                {
                    var imdbAtom = appleTag.GetDashBox("com.apple.iTunes", "IMDB")
                                ?? appleTag.GetDashBox("com.apple.iTunes", "imdb")
                                ?? appleTag.GetDashBox("com.apple.iTunes", "iTunEXTC");
                    if (!string.IsNullOrWhiteSpace(imdbAtom))
                        meta.ImdbId = imdbAtom.Trim();
                }
            }

            // Rating — some taggers write a float in tag.BeatsPerMinute (Plex hack),
            // or in freeform fields. The most common standard approach is a 0–10 float.
            if (meta.Rating <= 0 && tagFile.Tag is TagLib.Mpeg4.AppleTag appleTag2)
            {
                var ratingAtom = appleTag2.GetDashBox("com.apple.iTunes", "RATING")
                              ?? appleTag2.GetDashBox("com.apple.iTunes", "rating");
                if (!string.IsNullOrWhiteSpace(ratingAtom)
                    && float.TryParse(ratingAtom,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float extRating)
                    && extRating > 0)
                    meta.Rating = extRating;
            }

            // MPA / content rating — standard iTunes contentrating atom
            if (string.IsNullOrWhiteSpace(meta.MpaRating) && tagFile.Tag is TagLib.Mpeg4.AppleTag appleTag3)
            {
                var mpa = appleTag3.GetDashBox("com.apple.iTunes", "iTunEXTC")
                       ?? appleTag3.GetDashBox("com.apple.iTunes", "contentrating")
                       ?? appleTag3.GetDashBox("com.apple.iTunes", "CONTENTRATING");
                if (!string.IsNullOrWhiteSpace(mpa))
                    meta.MpaRating = mpa.Trim();
            }

            if (includeArtwork && tag.Pictures is { Length: > 0 })
                meta.ArtworkBytes = tag.Pictures[0].Data.Data;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read metadata error ({filePath}): {ex.Message}");
        }
        return meta;
    }

    // ── Write Metadata ────────────────────────────────────────────────────────

    public async Task<WriteResult> WriteMetadataDetailedAsync(
        string filePath, MovieMetadata metadata, AppSettings settings,
        IProgress<string>? progress = null, int maxRetries = 3, int retryDelayMs = 500,
        CancellationToken ct = default)
    {
        var attemptErrors = new List<string>();

        // ── Artwork: ensure we have the embedded artwork before writing ────────
        // Batch processing uses lazy artwork loading. If the file was never
        // selected, ArtworkBytes will be null even though the file has embedded
        // art. Read it now so we don't accidentally strip artwork on write.
        if (metadata.ArtworkBytes is not { Length: > 0 })
        {
            var existing = ReadArtworkOnly(filePath);
            if (existing is { Length: > 0 })
                metadata.ArtworkBytes = existing;
        }

        var dir        = Path.GetDirectoryName(filePath) ?? "";
        var ext        = Path.GetExtension(filePath);
        var tempPath   = Path.Combine(dir, $".vme_tmp_{Guid.NewGuid():N}{ext}");
        var backupPath = Path.Combine(dir, $".vme_bak_{Guid.NewGuid():N}{ext}");

        try
        {
            progress?.Report($"Writing tags: {Path.GetFileName(filePath)}");

            // ── Space check ───────────────────────────────────────────────────
            long fileSize  = new FileInfo(filePath).Length;
            long freeSpace = new DriveInfo(Path.GetPathRoot(filePath) ?? filePath).AvailableFreeSpace;
            if (freeSpace < fileSize + 10 * 1024 * 1024)
                throw new IOException(
                    $"Insufficient disk space ({FormatBytes(freeSpace)} free, need {FormatBytes(fileSize)}).");

            // ── Step 1: Copy original → temp (shared read, no lock on original) ─
            progress?.Report($"Copying to temp: {Path.GetFileName(filePath)}");
            await Task.Run(() => SysFile.Copy(filePath, tempPath, overwrite: true), ct);

            // ── Step 2: Write tags to temp using TagLib# ──────────────────────
            // Write into the TEMP file — not the original — so any TagLib#
            // rewrite (for poorly-muxed moov-at-end files) operates on the copy.
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Embedding tags: {Path.GetFileName(filePath)}");

            var (tagOk, tagErr) = await Task.Run(() => WriteTagsSafe(tempPath, metadata, settings), ct);

            // Automatic artwork-free retry: if the write failed verification AND we
            // were embedding artwork, the picture atom may be disturbing the comment
            // atom on this container. Retry once without artwork on a fresh temp copy.
            if (!tagOk && metadata.ArtworkBytes is { Length: > 0 }
                && (tagErr?.Contains("verification failed") ?? false))
            {
                attemptErrors.Add($"With-artwork write failed verification: {tagErr}");
                progress?.Report($"Retrying without artwork: {Path.GetFileName(filePath)}");
                await Task.Run(() => SysFile.Copy(filePath, tempPath, overwrite: true), ct);
                var noArt = metadata.Clone();
                noArt.ArtworkBytes = null;
                (tagOk, tagErr) = await Task.Run(() => WriteTagsSafe(tempPath, noArt, settings), ct);
                if (tagOk)
                    attemptErrors.Add("Succeeded without artwork — cover art left unchanged on this file.");
            }

            if (!tagOk)
                throw new InvalidOperationException(
                    $"Tag write failed: {tagErr ?? "TagLib# returned false"}");

            // ── Step 2b: MKV artwork via MkvPropEdit (if available) ──────────
            // TagLib# stores MKV cover art as a generic binary attachment.
            // Many media servers ignore it. MkvPropEdit writes a proper
            // Matroska attachment named "cover.jpg" which all players recognise.
            // We apply this to the TEMP file before the atomic replace so the
            // original is never touched until the whole operation succeeds.
            if (string.Equals(ext, ".mkv", StringComparison.OrdinalIgnoreCase)
                && metadata.ArtworkBytes is { Length: > 0 }
                && MkvPropEditService.IsAvailable)
            {
                progress?.Report($"Embedding MKV artwork via mkvpropedit: {Path.GetFileName(filePath)}");
                var (mkvOk, mkvErr) = await MkvPropEditService.SetArtworkAsync(
                    tempPath, metadata.ArtworkBytes, ct);
                if (!mkvOk)
                {
                    // Non-fatal: TagLib# already wrote the attachment above.
                    // Log the warning but continue with the atomic replace.
                    attemptErrors.Add($"mkvpropedit artwork warning: {mkvErr}");
                }
            }

            // ── Step 3: Verify the temp file is valid and non-empty ───────────
            var tempInfo = new FileInfo(tempPath);
            if (!tempInfo.Exists || tempInfo.Length < fileSize / 2)
                throw new InvalidOperationException(
                    $"Temp file sanity check failed (expected ~{FormatBytes(fileSize)}, got {FormatBytes(tempInfo.Length)}).");

            // ── Step 4: Atomic NTFS replace ───────────────────────────────────
            progress?.Report($"Applying: {Path.GetFileName(filePath)}");
            await Task.Run(() =>
            {
                if (SysFile.Exists(backupPath)) SysFile.Delete(backupPath);
                SysFile.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
                // Force mtime update so the library cache always invalidates after an embed.
                // File.Replace() sometimes preserves the destination's original mtime on Windows,
                // which causes the 2-second cache freshness check to return stale data.
                try { SysFile.SetLastWriteTimeUtc(filePath, DateTime.UtcNow); } catch { }
            }, ct);

            // ── Step 5: Remove backup (best-effort, non-fatal) ────────────────
            await Task.Run(() =>
            {
                try { if (SysFile.Exists(backupPath)) SysFile.Delete(backupPath); }
                catch { /* non-fatal — recovery system will clean this up */ }
            });

            progress?.Report($"Done: {Path.GetFileName(filePath)}");
            return new WriteResult(true, null, attemptErrors);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            var msg = ex is InvalidOperationException ? ex.Message : $"Temp-copy: {ex.Message}";
            attemptErrors.Add(msg);
            progress?.Report($"{msg} — falling back to in-place write");
            TryDeleteFile(tempPath);
        }

        // ── Pass 2: In-place TagLib# write with retries ───────────────────────
        // Only reached when temp-copy failed (e.g. disk full, cross-volume issues).
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                progress?.Report(attempt == 1
                    ? $"In-place write: {Path.GetFileName(filePath)}"
                    : $"Retry {attempt}/{maxRetries}: {Path.GetFileName(filePath)}");

                var (ok, err) = await Task.Run(() => WriteTagsSafe(filePath, metadata, settings), ct);
                if (ok)
                {
                    progress?.Report($"Done (in-place): {Path.GetFileName(filePath)}");
                    return new WriteResult(true, null, attemptErrors);
                }
                attemptErrors.Add($"In-place attempt {attempt}: {err ?? "unknown"}");
            }
            catch (Exception ex)
            {
                attemptErrors.Add($"In-place attempt {attempt}: {ex.Message}");
                progress?.Report($"Retry {attempt} error: {ex.Message}");
            }

            if (attempt < maxRetries)
                await Task.Delay(retryDelayMs * attempt, ct);
        }

        var diagnosis = await DiagnoseWriteFailureAsync(filePath, attemptErrors);
        progress?.Report($"ERROR: all write methods failed for {Path.GetFileName(filePath)}");
        return new WriteResult(false, diagnosis, attemptErrors);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (SysFile.Exists(path)) SysFile.Delete(path); } catch { }
    }

    /// <summary>
    /// Writes tags to <paramref name="filePath"/> using TagLib#.
    /// Returns (success, errorMessage). Never throws — all exceptions are captured.
    ///
    /// Key fixes over old WriteTagsSync:
    /// 1. Uses LocalFileAbstraction with explicit file handle management so TagLib#
    ///    can truncate/rewrite files where the moov atom is at the end.
    /// 2. Returns a (bool, string?) tuple so the caller gets the actual error text
    ///    instead of catching a generic exception.
    /// 3. Calls tagFile.Save() inside the using block so the handle is still open
    ///    when TagLib# needs to seek/rewrite — handles moov-at-end MP4s correctly.
    /// </summary>
    private (bool success, string? error) WriteTagsSafe(
        string filePath, MovieMetadata metadata, AppSettings settings)
    {
        try
        {
            // The VME comment token block — hoisted out of the write scope so the
            // post-write verification step (which runs AFTER the write handle is
            // closed) can compare against it.
            string comment = string.Empty;

            // Write tags in their OWN scope so the TagLib# file handle is fully
            // released before verification re-opens the file. Verifying while the
            // write handle was still open could read a partially-flushed / locked
            // state and report phantom "missing field" failures on files that
            // actually wrote correctly. (Regression fixed in Build 97.)
            using (var tagFile = File.Create(filePath))
            {
                var tag = tagFile.Tag;

            tag.Title = string.IsNullOrWhiteSpace(metadata.Title)
                ? Path.GetFileNameWithoutExtension(filePath)
                : metadata.Title;

            tag.Genres = string.IsNullOrWhiteSpace(metadata.Genre)
                ? []
                : metadata.Genre
                    .Split(',', StringSplitOptions.TrimEntries)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .OrderBy(g => g)
                    .ToArray();

            tag.Composers  = string.IsNullOrWhiteSpace(metadata.Director)
                ? []
                : [metadata.Director];

            tag.Performers = string.IsNullOrWhiteSpace(metadata.Cast)
                ? []
                : metadata.Cast
                    .Split(',', StringSplitOptions.TrimEntries)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToArray();

            if (uint.TryParse(metadata.Year, out uint year))
                tag.Year = year;

            comment         = BuildComment(metadata.Description, metadata.ImdbId, metadata.TmdbId,
                    metadata.Rating, metadata.MpaRating, metadata.IsWatched,
                    metadata.IsEpisode, metadata.ShowTitle, metadata.Season,
                    metadata.Episode, metadata.EpisodeTitle, metadata.AiredDate,
                    metadata.TvdbId, metadata.TmdbSeriesId);
            tag.Comment     = comment;
            tag.Description = comment;

            // Artwork: compress and embed if present; preserve existing if none provided
            if (metadata.ArtworkBytes is { Length: > 0 })
            {
                var artwork = CompressArtwork(metadata.ArtworkBytes, settings.ArtworkMaxPx, settings.ArtworkJpegQuality);
                tag.Pictures =
                [
                    new Picture(new ByteVector(artwork))
                    {
                        Type        = PictureType.FrontCover,
                        MimeType    = "image/jpeg",
                        Description = "Cover"
                    }
                ];
            }
            // If ArtworkBytes is null here, preserve whatever Pictures the tag already has
            // (we read it above but it may still be null if the file has no embedded art)

                // Save() may rewrite the entire file for moov-at-end MP4s.
                // This is by design — TagLib# handles this transparently when
                // the file handle is still open via the using block.
                tagFile.Save();
            } // ← write handle fully released here, BEFORE verification re-opens the file

            // ── Post-write verification ────────────────────────────────────────
            // Re-read the file (now that the write handle is closed) and verify the
            // data was stored — both the standard Title field AND the full VME token
            // set (rating, IDs, MPA, watched, and all TV episode fields). A format
            // that drops, say, the episode tokens would previously have passed the
            // Title-only check and reverted silently on the next scan.
            try
            {
                using var verify = File.Create(filePath);

                // 1. Title (standard field, most reliable across containers)
                if (!string.IsNullOrWhiteSpace(metadata.Title))
                {
                    var writtenTitle  = verify.Tag.Title ?? string.Empty;
                    var expectedTitle = metadata.Title;
                    if (!writtenTitle.Equals(expectedTitle, StringComparison.OrdinalIgnoreCase))
                        return (false,
                            $"Post-write verification failed — Title was not committed " +
                            $"(expected '{expectedTitle}', read back '{writtenTitle}'). " +
                            $"This format may not support embedded metadata reliably.");
                }

                // 2. Full VME token set — only enforced for formats that claim full
                //    tag support (MP4/M4V/MKV/MOV). WebM/WMV/AVI cannot reliably store
                //    the comment token block and already surface a capability warning,
                //    so failing the write on a missing token there would be a false
                //    negative — the user was already told those fields may not save.
                var caps = GetFormatCapabilities(DetectFormat(filePath));
                if (caps.supportsFullTags)
                {
                    var readBackComment = verify.Tag.Comment ?? string.Empty;
                    var missing = VmeCommentCodec.VerifyWritten(comment, readBackComment);
                    if (missing.Count > 0)
                    {
                        var diag =
                            $"[diag] tagTypes={verify.TagTypes} " +
                            $"intendedLen={comment.Length} readbackLen={readBackComment.Length} " +
                            $"readback='{(readBackComment.Length > 120 ? readBackComment.Substring(0,120) : readBackComment)}'";
                        System.Diagnostics.Debug.WriteLine(
                            $"[WriteVerify] MISMATCH on {Path.GetFileName(filePath)}\n" +
                            $"  missing : {string.Join(", ", missing)}\n" +
                            $"  intended: '{comment}'\n" +
                            $"  readback: '{readBackComment}'");
                        return (false,
                            $"Post-write verification failed — these fields were not committed: " +
                            $"{string.Join(", ", missing)}. " +
                            $"This file may be damaged or the container may not support embedded metadata. " +
                            diag);
                    }
                }
            }
            catch { /* verification is best-effort — don't fail on a verify read error */ }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public async Task<bool> WriteMetadataAsync(
        string filePath, MovieMetadata metadata, AppSettings settings,
        IProgress<string>? progress = null, int maxRetries = 3, int retryDelayMs = 500,
        CancellationToken ct = default)
    {
        var result = await WriteMetadataDetailedAsync(filePath, metadata, settings, progress, maxRetries, retryDelayMs, ct);
        return result.Success;
    }

    // ── Write failure diagnosis ────────────────────────────────────────────────

    private static async Task<WriteDiagnosis> DiagnoseWriteFailureAsync(
        string filePath, IReadOnlyList<string> attemptErrors)
    {
        return await Task.Run(() =>
        {
            var reasons = new List<string>();
            var options = new List<string>();

            // 1. Read-only attribute
            try
            {
                if (new System.IO.FileInfo(filePath).IsReadOnly)
                {
                    reasons.Add("The file has the Windows read-only attribute set.");
                    options.Add("Click 🔓 Unlock to clear the read-only attribute and retry.");
                    return new WriteDiagnosis(WriteDiagnosisCategory.ReadOnly, reasons, options);
                }
            }
            catch { }

            // 2. Process lock
            var lockers = FileLockService.GetLockingProcesses(filePath);
            if (lockers.Count > 0)
            {
                bool selfLocked = lockers.Any(p => p.Pid == System.Environment.ProcessId);
                if (selfLocked)
                {
                    reasons.Add("This application's preview player is holding the file open.");
                    options.Add("Stop the preview (⏹ Stop), then retry embedding.");
                }
                else
                {
                    var names = string.Join(", ", lockers.Select(p => $"{p.Name} (PID {p.Pid})"));
                    reasons.Add($"The file is locked by: {names}");
                    options.Add($"Close {names} and retry.");
                }
                return new WriteDiagnosis(WriteDiagnosisCategory.FileLocked, reasons, options);
            }

            // 3. NTFS permissions
            try { new System.IO.FileInfo(filePath).GetAccessControl(); }
            catch (UnauthorizedAccessException)
            {
                reasons.Add("Windows is denying write access (NTFS permissions).");
                options.Add("Right-click → Properties → Security → add Write permission.");
                options.Add("Run the app as Administrator.");
                return new WriteDiagnosis(WriteDiagnosisCategory.PermissionDenied, reasons, options);
            }
            catch { }

            // 4. Network / read-only volume
            try
            {
                var drive = new System.IO.DriveInfo(Path.GetPathRoot(filePath) ?? filePath);
                if (drive.DriveType == System.IO.DriveType.Network)
                {
                    reasons.Add("The file is on a network share that may be read-only.");
                    options.Add("Check share permissions with your network administrator.");
                    return new WriteDiagnosis(WriteDiagnosisCategory.NetworkVolume, reasons, options);
                }
                if (drive.DriveType == System.IO.DriveType.CDRom)
                {
                    reasons.Add("The file is on read-only optical media.");
                    options.Add("Copy the file to a local drive first.");
                    return new WriteDiagnosis(WriteDiagnosisCategory.ReadOnlyVolume, reasons, options);
                }
            }
            catch { }

            // 5. TagLib# format / moov-atom issue
            var lastErr = attemptErrors.LastOrDefault() ?? "";
            if (lastErr.Contains("Unsupported") || lastErr.Contains("format")
                || lastErr.Contains("codec") || lastErr.Contains("moov"))
            {
                reasons.Add("TagLib# cannot write tags to this specific file encoding or container variant.");
                reasons.Add("Common cause: the MP4's moov atom (metadata index) is at the end of the file " +
                            "and TagLib# failed to rewrite the container structure.");
                options.Add("Export metadata to a .nfo sidecar (📋 LOG tab → Export NFO).");
                options.Add("Re-encode the file to a standard fast-start MP4 (mp4 -movflags faststart).");
                return new WriteDiagnosis(WriteDiagnosisCategory.FormatUnsupported, reasons, options);
            }

            // 6. Unknown
            reasons.Add("Both the standard write and the in-place fallback failed.");
            if (attemptErrors.Any())
            {
                reasons.Add($"First error: {attemptErrors.First()}");
                if (attemptErrors.Count > 1)
                    reasons.Add($"Last error: {attemptErrors.Last()}");
            }
            options.Add("Check the Processing Log (📋 LOG) for all attempt messages.");
            options.Add("Export metadata to a .nfo sidecar as an alternative.");
            options.Add("Unlock and relock the file, then retry.");
            options.Add("Ensure sufficient free disk space for the temp copy.");
            return new WriteDiagnosis(WriteDiagnosisCategory.Unknown, reasons, options);
        });
    }

    // ── ID storage helpers ────────────────────────────────────────────────────

    internal static string BuildComment(
        string description, string imdbId, string tmdbId,
        float rating = 0f, string mpaRating = "", bool isWatched = false,
        bool isEpisode = false, string showTitle = "", int? season = null,
        int? episode = null, string episodeTitle = "", string airedDate = "",
        string tvdbId = "", string tmdbSeriesId = "")
        => VmeCommentCodec.Encode(description, imdbId, tmdbId, rating, mpaRating, isWatched,
            isEpisode, showTitle, season, episode, episodeTitle, airedDate, tvdbId, tmdbSeriesId);

    internal static string ExtractDescription(string comment)
        => VmeCommentCodec.GetDescription(comment);

    internal static string ExtractTagValue(string comment, string key)
        => VmeCommentCodec.Get(comment, key);

    // ── Artwork Compression ───────────────────────────────────────────────────

    public byte[] CompressArtwork(byte[] input, int maxPx, int jpegQuality)
    {
        try
        {
            using var ms       = new MemoryStream(input);
            using var original = Image.FromStream(ms);

            int w = original.Width, h = original.Height;
            if (w > maxPx || h > maxPx)
            {
                double ratio = Math.Min((double)maxPx / w, (double)maxPx / h);
                w = (int)(w * ratio);
                h = (int)(h * ratio);
            }

            using var resized  = new Bitmap(original, w, h);
            using var outMs    = new MemoryStream();
            var encoder        = GetJpegEncoder();
            var encoderParams  = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)jpegQuality);
            resized.Save(outMs, encoder, encoderParams);
            return outMs.ToArray();
        }
        catch { return input; }
    }

    private static ImageCodecInfo GetJpegEncoder() =>
        ImageCodecInfo.GetImageDecoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
}
