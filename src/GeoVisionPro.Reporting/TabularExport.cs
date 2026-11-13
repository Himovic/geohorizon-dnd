using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace GeoVisionPro.Reporting;

/// <summary>A named table of cells (strings or numbers) for CSV/XLSX export.</summary>
public sealed class DataTable
{
    public string Name { get; }
    public IReadOnlyList<string> Columns { get; }
    public List<object?[]> Rows { get; } = [];
    public DataTable(string name, IReadOnlyList<string> columns) { Name = name; Columns = columns; }
    public DataTable Add(params object?[] row) { Rows.Add(row); return this; }
}

public static class CsvExport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    public static string Escape(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Format(object? v) => v switch
    {
        null => "",
        double d when double.IsNaN(d) => "",
        float f when float.IsNaN(f) => "",
        IFormattable f => f.ToString(null, Ci),
        _ => v.ToString() ?? ""
    };

    public static string ToCsv(DataTable t)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", t.Columns.Select(Escape)));
        foreach (var r in t.Rows) sb.AppendLine(string.Join(",", r.Select(v => Escape(Format(v)))));
        return sb.ToString();
    }

    public static void Write(string path, DataTable t) => File.WriteAllText(path, ToCsv(t), new UTF8Encoding(false));
}

/// <summary>
/// Minimal Office Open XML (XLSX) writer: one worksheet per table, inline strings and numeric cells, bold header row.
/// Opens in Excel, LibreOffice and other spreadsheet applications.
/// </summary>
public static class XlsxExport
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static string ColumnName(int c)
    {
        var s = "";
        c++;
        while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
        return s;
    }

    private static string SafeSheetName(string n, int idx)
    {
        var s = new string(n.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        if (s.Length == 0) s = $"Sheet{idx}";
        return s.Length > 31 ? s[..31] : s;
    }

    public static void Write(string path, IReadOnlyList<DataTable> tables)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Entry(string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        var sheetsCt = string.Concat(tables.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        Entry("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            sheetsCt + "</Types>");
        Entry("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        var sheets = string.Concat(tables.Select((t, i) => $"<sheet name=\"{SecurityElement.Escape(SafeSheetName(t.Name, i + 1))}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"));
        Entry("xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            $"<sheets>{sheets}</sheets></workbook>");
        var rels = string.Concat(tables.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"));
        Entry("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels +
            $"<Relationship Id=\"rId{tables.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Entry("xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf fontId=\"0\"/><xf fontId=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");
        for (int i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            sb.Append("<row r=\"1\">");
            for (int c = 0; c < t.Columns.Count; c++) sb.Append($"<c r=\"{ColumnName(c)}1\" t=\"inlineStr\" s=\"1\"><is><t>{SecurityElement.Escape(t.Columns[c])}</t></is></c>");
            sb.Append("</row>");
            for (int r = 0; r < t.Rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 2}\">");
                var row = t.Rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    string refc = $"{ColumnName(c)}{r + 2}";
                    switch (row[c])
                    {
                        case null: break;
                        case double d when double.IsFinite(d): sb.Append($"<c r=\"{refc}\"><v>{d.ToString("R", Ci)}</v></c>"); break;
                        case float f when float.IsFinite(f): sb.Append($"<c r=\"{refc}\"><v>{f.ToString("R", Ci)}</v></c>"); break;
                        case int n: sb.Append($"<c r=\"{refc}\"><v>{n}</v></c>"); break;
                        case long l: sb.Append($"<c r=\"{refc}\"><v>{l}</v></c>"); break;
                        case double or float: break;
                        default: sb.Append($"<c r=\"{refc}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(row[c]!.ToString())}</t></is></c>"); break;
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            Entry($"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
        }
    }
}
