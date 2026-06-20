// v16 — punctuation in the description breaks the write. Isolate WHICH punctuation
// char. Both failing files have a ';' in the description.
//   dotnet run -- "A:\Movies\A Taste of Hunger (2021).mp4"
using TagLib; using System.Linq;
if (args.Length == 0) { Console.WriteLine("Usage: dotnet run -- <file>"); return; }
var src = args[0]; var ext = System.IO.Path.GetExtension(src);
var dump = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"vme_artdump");
var stem = System.IO.Path.GetFileNameWithoutExtension(src);
var real = System.IO.File.ReadAllText(System.IO.Path.Combine(dump, stem + ".comment.txt"));

string Copy(){ var w=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"v16_"+Guid.NewGuid().ToString("N")+ext);
    System.IO.File.Copy(src,w,true); return w; }
string WR(string c){ var w=Copy();
    try{ using(var f=TagLib.File.Create(w)){ f.Tag.Comment=c; f.Save(); }
         using(var f=TagLib.File.Create(w)){ var r=f.Tag.Comment??""; return r==c?"OK":$"FAIL({r.Length})"; } }
    catch{ return "EX"; } finally{ try{System.IO.File.Delete(w);}catch{} } }

// Replace ONE punctuation type at a time (with space, preserving length)
foreach (var ch in new[]{';',',','.','?','!',':','\'','"','-','(',')'})
{
    if (!real.Contains(ch)) { Console.WriteLine($"  '{ch}' : (not present)"); continue; }
    int count = real.Count(c=>c==ch);
    Console.WriteLine($"  remove '{ch}' (x{count}) -> {WR(real.Replace(ch,' '))}");
}
Console.WriteLine($"\nbaseline real        -> {WR(real)}");
// Show the exact bytes around each semicolon
int idx=-1; while((idx=real.IndexOf(';',idx+1))>=0)
    Console.WriteLine($"  ';' at pos {idx}: context '{real.Substring(Math.Max(0,idx-10), Math.Min(22,real.Length-Math.Max(0,idx-10)))}'");
Console.WriteLine("\nDone. Paste output back.");
