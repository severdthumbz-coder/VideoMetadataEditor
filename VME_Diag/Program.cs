// v5 — test in-place write ON THE SAME DRIVE/FOLDER as the original (A:\),
// which is what the app's in-place pass and temp-copy (temp is beside original) do.
// My earlier tests all wrote to %TEMP% on C:. This replicates the real location.
//   dotnet run -- "A:\Movies\A Taste of Hunger (2021).mp4"
using TagLib;

if (args.Length == 0) { Console.WriteLine("Usage: dotnet run -- <file>"); return; }
var src = args[0];
const string token = "[VME:IMDB=tt11188560][VME:TMDB=766105][VME:RATING=7.0][VME:MPA=R]";
var dir = System.IO.Path.GetDirectoryName(src)!;
var ext = System.IO.Path.GetExtension(src);

// TEST 1: copy beside the original (same folder, like the app's .vme_tmp), write, read back
var tmp = System.IO.Path.Combine(dir, $".vme_diag_{Guid.NewGuid():N}{ext}");
Console.WriteLine($"Temp beside original: {tmp}");
try {
    System.IO.File.Copy(src, tmp, true);
    using (var f = TagLib.File.Create(tmp)) { f.Tag.Title="DIAG"; f.Tag.Comment=token; f.Save(); }
    using (var f = TagLib.File.Create(tmp)) {
        var c = f.Tag.Comment ?? "";
        Console.WriteLine($"[same-folder temp] present: {c==token} (read {c.Length})");
    }
} catch (Exception ex) { Console.WriteLine($"[same-folder temp] EX: {ex.Message}"); }
finally { try { System.IO.File.Delete(tmp); } catch {} }

// TEST 2: full temp-copy + File.Replace cycle in the SAME folder, then read FINAL original.
// WORKS ON A SEPARATE COPY so your real file is untouched.
var fakeOrig = System.IO.Path.Combine(dir, $".vme_orig_{Guid.NewGuid():N}{ext}");
var tmp2 = fakeOrig + ".vmetmp" + ext;   // keep .mp4 ext
tmp2 = System.IO.Path.Combine(dir, $".vme_tmp2_{Guid.NewGuid():N}{ext}");
var bak = System.IO.Path.Combine(dir, $".vme_bak_{Guid.NewGuid():N}{ext}");
try {
    System.IO.File.Copy(src, fakeOrig, true);                 // stand-in original on A:
    System.IO.File.Copy(fakeOrig, tmp2, true);                // app step 1
    using (var f = TagLib.File.Create(tmp2)) { f.Tag.Title="DIAG"; f.Tag.Comment=token; f.Save(); }  // app step 2
    System.IO.File.Replace(tmp2, fakeOrig, bak, true);        // app step 4 (atomic replace)
    try { System.IO.File.Delete(bak); } catch {}
    using (var f = TagLib.File.Create(fakeOrig)) {
        var c = f.Tag.Comment ?? "";
        Console.WriteLine($"[temp+replace on A:] present: {c==token} (read {c.Length})");
    }
} catch (Exception ex) { Console.WriteLine($"[temp+replace on A:] EX: {ex.Message}"); }
finally { foreach (var p in new[]{fakeOrig,tmp2,bak}) try { if(System.IO.File.Exists(p)) System.IO.File.Delete(p);} catch {} }

// TEST 3: write the REAL file in place, read back, then RESTORE it from a backup we keep.
// Safe: we back up first and restore after.
var safety = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $".vme_safety_{Guid.NewGuid():N}{ext}");
try {
    System.IO.File.Copy(src, safety, true);  // safety backup on C:
    using (var f = TagLib.File.Create(src)) { f.Tag.Comment=token; f.Save(); }
    using (var f = TagLib.File.Create(src)) {
        var c = f.Tag.Comment ?? "";
        Console.WriteLine($"[in-place on real A: file] present: {c==token} (read {c.Length})");
    }
    // restore original bytes
    System.IO.File.Copy(safety, src, true);
    Console.WriteLine("Original file restored from safety backup.");
} catch (Exception ex) { Console.WriteLine($"[in-place on real file] EX: {ex.Message}"); }
finally { try { System.IO.File.Delete(safety); } catch {} }

Console.WriteLine("\nDone. Paste output back.");
