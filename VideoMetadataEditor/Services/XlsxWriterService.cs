using System.IO;
using System.IO.Compression;
using System.Text;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Minimal XLSX writer — no external NuGet dependencies.
/// Uses System.IO.Compression to build the ZIP container that XLSX requires,
/// then writes standard OpenXML parts. Supports text, numbers, and basic styles.
/// Excel, LibreOffice Calc, and Google Sheets all open the output correctly.
/// </summary>
public static class XlsxWriterService
{
    /// <summary>
    /// Writes <paramref name="entries"/> to an XLSX file at <paramref name="outputPath"/>.
    /// Only columns with <see cref="LibraryColumn.IsVisible"/> = true are included,
    /// ordered by <see cref="LibraryColumn.DisplayIndex"/>.
    /// </summary>
    public static void Write(
        string outputPath,
        IReadOnlyList<LibraryEntry> entries,
        IReadOnlyList<LibraryColumn> columns)
    {
        // Collect visible columns in display order
        var cols = columns
            .Where(c => c.IsVisible && c.Binding != "CoverArt") // skip binary cover
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        using var fs     = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var zip    = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        // ── Shared strings table ──────────────────────────────────────────────
        // All string values are stored here; cells reference by index.
        // This reduces file size and is required for correct Excel parsing.
        var strings = new List<string>();
        var strIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        int AddString(string s)
        {
            s ??= string.Empty;
            if (strIndex.TryGetValue(s, out int i)) return i;
            i = strings.Count;
            strings.Add(s);
            strIndex[s] = i;
            return i;
        }

        // ── Build rows data ───────────────────────────────────────────────────
        // row[0] = headers, row[1..] = data
        var rows = new List<string[]>();
        rows.Add(cols.Select(c => c.Header).ToArray());
        foreach (var entry in entries)
        {
            var row = new string[cols.Count];
            for (int ci = 0; ci < cols.Count; ci++)
            {
                var prop = typeof(LibraryEntry).GetProperty(cols[ci].Binding);
                row[ci] = prop?.GetValue(entry)?.ToString() ?? string.Empty;
            }
            rows.Add(row);
        }

        // Pre-register all strings
        foreach (var row in rows)
            foreach (var cell in row)
                AddString(cell);

        // ── [Content_Types].xml ───────────────────────────────────────────────
        WriteEntry(zip, "[Content_Types].xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">
  <Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>
  <Default Extension=""xml""  ContentType=""application/xml""/>
  <Override PartName=""/xl/workbook.xml""        ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>
  <Override PartName=""/xl/worksheets/sheet1.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>
  <Override PartName=""/xl/sharedStrings.xml""   ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml""/>
  <Override PartName=""/xl/styles.xml""          ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>
</Types>");

        // ── _rels/.rels ───────────────────────────────────────────────────────
        WriteEntry(zip, "_rels/.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>
</Relationships>");

        // ── xl/_rels/workbook.xml.rels ────────────────────────────────────────
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">
  <Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet1.xml""/>
  <Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings"" Target=""sharedStrings.xml""/>
  <Relationship Id=""rId3"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>
</Relationships>");

        // ── xl/workbook.xml ───────────────────────────────────────────────────
        WriteEntry(zip, "xl/workbook.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main""
          xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships"">
  <sheets>
    <sheet name=""Library"" sheetId=""1"" r:id=""rId1""/>
  </sheets>
</workbook>");

        // ── xl/styles.xml — bold header style (styleIndex=1) ─────────────────
        WriteEntry(zip, "xl/styles.xml", @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""2"">
    <font><sz val=""11""/><name val=""Calibri""/></font>
    <font><b/><sz val=""11""/><name val=""Calibri""/></font>
  </fonts>
  <fills count=""2"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
  </fills>
  <borders count=""1"">
    <border><left/><right/><top/><bottom/><diagonal/></border>
  </borders>
  <cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
  <cellXfs count=""2"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""0"" borderId=""0"" xfId=""0""/>
  </cellXfs>
</styleSheet>");

        // ── xl/sharedStrings.xml ──────────────────────────────────────────────
        var ssb = new StringBuilder();
        ssb.AppendLine($@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<sst xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" count=""{strings.Count}"" uniqueCount=""{strings.Count}"">");
        foreach (var s in strings)
        {
            ssb.AppendLine($"  <si><t xml:space=\"preserve\">{XmlEscape(s)}</t></si>");
        }
        ssb.Append("</sst>");
        WriteEntry(zip, "xl/sharedStrings.xml", ssb.ToString());

        // ── xl/worksheets/sheet1.xml ──────────────────────────────────────────
        var wb = new StringBuilder();
        wb.AppendLine(@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <sheetData>");

        for (int ri = 0; ri < rows.Count; ri++)
        {
            int rowNum   = ri + 1;
            bool isHeader = ri == 0;
            wb.AppendLine($"    <row r=\"{rowNum}\">");
            var row = rows[ri];
            for (int ci = 0; ci < row.Length; ci++)
            {
                string cellRef = $"{ColLetter(ci)}{rowNum}";
                int    sIdx    = strIndex[row[ci]];
                // s=type shared string, style=1 for bold header
                string style = isHeader ? " s=\"1\"" : "";
                wb.AppendLine($"      <c r=\"{cellRef}\" t=\"s\"{style}><v>{sIdx}</v></c>");
            }
            wb.AppendLine("    </row>");
        }

        wb.AppendLine("  </sheetData>");
        // Auto-filter on header row
        if (cols.Count > 0)
        {
            string lastCol = ColLetter(cols.Count - 1);
            wb.AppendLine($"  <autoFilter ref=\"A1:{lastCol}1\"/>");
        }
        wb.Append("</worksheet>");
        WriteEntry(zip, "xl/worksheets/sheet1.xml", wb.ToString());
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
        w.Write(content);
    }

    private static string ColLetter(int zeroIndex)
    {
        // A-Z for columns 0-25, then AA, AB, ... (supports up to 702 columns)
        string result = string.Empty;
        int n = zeroIndex + 1;
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            result  = (char)('A' + rem) + result;
            n       = (n - 1) / 26;
        }
        return result;
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;")
         .Replace("'", "&apos;");
}
