using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace GeoVisionPro.Reporting;

/// <summary>
/// Minimal, dependency-free PDF 1.4 writer: A4 pages, standard Helvetica fonts (WinAnsi), text, lines, rectangles and
/// embedded RGB images (Flate compressed). Coordinates are in points from the top-left of the page.
/// </summary>
public sealed class PdfDocument
{
    public const double PageWidth = 595.28, PageHeight = 841.89;
    private readonly List<StringBuilder> _pages = [];
    private readonly List<(int Width, int Height, byte[] Rgb)> _images = [];
    private readonly List<HashSet<int>> _pageImages = [];
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    public string Title { get; set; } = "GeoVision Pro Report";
    public string Author { get; set; } = "GeoVision Pro";

    public int PageCount => _pages.Count;
    private StringBuilder Cur => _pages.Count > 0 ? _pages[^1] : throw new InvalidOperationException("Add a page first.");

    public void AddPage() { _pages.Add(new StringBuilder()); _pageImages.Add([]); }

    private static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 32) sb.Append(' ');
            else if (c > 255) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string N(double v) => v.ToString("0.###", Ci);

    public void Text(double x, double y, string text, double size = 10, bool bold = false, (double R, double G, double B)? color = null)
    {
        var (r, g, b) = color ?? (0, 0, 0);
        Cur.Append($"BT {N(r)} {N(g)} {N(b)} rg /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(PageHeight - y - size)} Td ({Esc(text)}) Tj ET\n");
    }

    /// <summary>Approximate Helvetica text width in points (average glyph width 0.5 em, bold 0.55 em).</summary>
    public static double TextWidth(string s, double size, bool bold = false) => s.Length * size * (bold ? 0.55 : 0.5);

    /// <summary>Writes wrapped text within a width; returns the y below the last line.</summary>
    public double Paragraph(double x, double y, double width, string text, double size = 10, double leading = 1.35)
    {
        foreach (var para in text.Split('\n'))
        {
            var words = para.Split(' ');
            var line = new StringBuilder();
            foreach (var w in words)
            {
                var candidate = line.Length == 0 ? w : line + " " + w;
                if (TextWidth(candidate, size) > width && line.Length > 0)
                {
                    Text(x, y, line.ToString(), size); y += size * leading;
                    line.Clear().Append(w);
                }
                else { line.Clear().Append(candidate); }
            }
            if (line.Length > 0) { Text(x, y, line.ToString(), size); y += size * leading; }
        }
        return y;
    }

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5, (double R, double G, double B)? color = null)
    {
        var (r, g, b) = color ?? (0, 0, 0);
        Cur.Append($"{N(r)} {N(g)} {N(b)} RG {N(width)} w {N(x1)} {N(PageHeight - y1)} m {N(x2)} {N(PageHeight - y2)} l S\n");
    }

    public void Rect(double x, double y, double w, double h, (double R, double G, double B)? fill = null, (double R, double G, double B)? stroke = null)
    {
        if (fill is { } f) Cur.Append($"{N(f.R)} {N(f.G)} {N(f.B)} rg {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re f\n");
        if (stroke is { } s) Cur.Append($"{N(s.R)} {N(s.G)} {N(s.B)} RG 0.5 w {N(x)} {N(PageHeight - y - h)} {N(w)} {N(h)} re S\n");
    }

    /// <summary>Embeds a BGRA32 image (as produced by the renderers) scaled into the given box.</summary>
    public void Image(double x, double y, double w, double h, uint[] bgra, int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            uint p = bgra[i];
            double a = ((p >> 24) & 0xFF) / 255.0;
            // composite on white
            rgb[3 * i] = (byte)(((p >> 16) & 0xFF) * a + 255 * (1 - a));
            rgb[3 * i + 1] = (byte)(((p >> 8) & 0xFF) * a + 255 * (1 - a));
            rgb[3 * i + 2] = (byte)((p & 0xFF) * a + 255 * (1 - a));
        }
        _images.Add((width, height, rgb));
        int id = _images.Count;
        _pageImages[^1].Add(id);
        Cur.Append($"q {N(w)} 0 0 {N(h)} {N(x)} {N(PageHeight - y - h)} cm /Im{id} Do Q\n");
    }

    /// <summary>Draws a simple table; returns the y below it.</summary>
    public double Table(double x, double y, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<double> columnWidths, double size = 8.5)
    {
        double rowH = size * 1.8;
        double tw = columnWidths.Sum();
        Rect(x, y, tw, rowH, fill: (0.15, 0.2, 0.28));
        double cx = x;
        for (int c = 0; c < headers.Count; c++) { Text(cx + 3, y + rowH * 0.25, Clip(headers[c], columnWidths[c], size), size, true, (1, 1, 1)); cx += columnWidths[c]; }
        y += rowH;
        for (int r = 0; r < rows.Count; r++)
        {
            if (y > PageHeight - 60) { AddPage(); y = 50; }
            if (r % 2 == 1) Rect(x, y, tw, rowH, fill: (0.93, 0.95, 0.97));
            cx = x;
            for (int c = 0; c < rows[r].Count && c < columnWidths.Count; c++) { Text(cx + 3, y + rowH * 0.25, Clip(rows[r][c], columnWidths[c], size), size); cx += columnWidths[c]; }
            y += rowH;
        }
        Line(x, y, x + tw, y, 0.5, (0.6, 0.6, 0.6));
        return y + 6;
    }

    private static string Clip(string s, double width, double size)
    {
        int max = (int)(width / (size * 0.5)) - 1;
        return s.Length > max && max > 3 ? s[..(max - 1)] + "…".Replace("…", "..") : s;
    }

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void W(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b); }
        int NewObj() { offsets.Add(ms.Position); return offsets.Count; }
        W("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
        // object numbering: 1 catalog, 2 pages, 3 font F1, 4 font F2, 5 info, then images, then page/content pairs
        int nImages = _images.Count, nPages = _pages.Count;
        int firstImage = 6, firstPage = firstImage + nImages;
        var pageIds = Enumerable.Range(0, nPages).Select(i => firstPage + 2 * i).ToArray();
        NewObj(); W("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        NewObj(); W($"2 0 obj << /Type /Pages /Kids [{string.Join(" ", pageIds.Select(p => $"{p} 0 R"))}] /Count {nPages} >> endobj\n");
        NewObj(); W("3 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> endobj\n");
        NewObj(); W("4 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >> endobj\n");
        NewObj(); W($"5 0 obj << /Title ({Esc(Title)}) /Author ({Esc(Author)}) /Producer (GeoVision Pro) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >> endobj\n");
        for (int i = 0; i < nImages; i++)
        {
            var (w, h, rgb) = _images[i];
            var comp = Deflate(rgb);
            NewObj();
            W($"{firstImage + i} 0 obj << /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {comp.Length} >>\nstream\n");
            ms.Write(comp); W("\nendstream endobj\n");
        }
        for (int p = 0; p < nPages; p++)
        {
            var content = Deflate(Encoding.Latin1.GetBytes(_pages[p].ToString()));
            var imgRefs = string.Join(" ", _pageImages[p].Select(id => $"/Im{id} {firstImage + id - 1} 0 R"));
            NewObj();
            W($"{pageIds[p]} 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Contents {pageIds[p] + 1} 0 R " +
              $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << {imgRefs} >> >> >> endobj\n");
            NewObj();
            W($"{pageIds[p] + 1} 0 obj << /Length {content.Length} /Filter /FlateDecode >>\nstream\n");
            ms.Write(content); W("\nendstream endobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer << /Size {offsets.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());
}
