namespace GeoVisionPro.Reporting;

/// <summary>A section of a technical report: heading, paragraphs, tables and figures.</summary>
public sealed class ReportSection
{
    public required string Heading { get; init; }
    public List<string> Paragraphs { get; } = [];
    public List<(string Caption, DataTable Table)> Tables { get; } = [];
    public List<(string Caption, uint[] Pixels, int Width, int Height)> Figures { get; } = [];
}

/// <summary>Lays out a multi-section technical report into a PDF with title page, numbered sections, tables and figures.</summary>
public sealed class ReportBuilder
{
    public string Title { get; set; } = "GeoVision Pro Technical Report";
    public string Subtitle { get; set; } = string.Empty;
    public string Author { get; set; } = Environment.UserName;
    public List<ReportSection> Sections { get; } = [];

    private static readonly (double, double, double) Accent = (0.239, 0.545, 0.859);   // #3D8BDB
    private static readonly (double, double, double) Dark = (0.09, 0.106, 0.141);      // #171B24

    public PdfDocument Build()
    {
        var pdf = new PdfDocument { Title = Title, Author = Author };
        pdf.AddPage();
        pdf.Rect(0, 0, PdfDocument.PageWidth, 220, fill: Dark);
        pdf.Text(50, 90, Title, 22, true, (1, 1, 1));
        if (!string.IsNullOrEmpty(Subtitle)) pdf.Text(50, 125, Subtitle, 13, false, (0.8, 0.85, 0.9));
        pdf.Rect(50, 160, 120, 3, fill: Accent);
        pdf.Text(50, 250, $"Prepared: {DateTime.Now:yyyy-MM-dd HH:mm}", 11);
        pdf.Text(50, 268, $"Author: {Author}", 11);
        pdf.Text(50, 286, "Software: GeoVision Pro", 11);
        double y = 330;
        pdf.Text(50, y, "Contents", 14, true); y += 24;
        for (int i = 0; i < Sections.Count; i++) { pdf.Text(60, y, $"{i + 1}. {Sections[i].Heading}", 11); y += 16; }

        int sectionNo = 0;
        foreach (var s in Sections)
        {
            sectionNo++;
            pdf.AddPage();
            y = 50;
            pdf.Text(50, y, $"{sectionNo}. {s.Heading}", 15, true, Accent); y += 26;
            pdf.Line(50, y - 6, PdfDocument.PageWidth - 50, y - 6, 0.6, (0.75, 0.78, 0.82));
            foreach (var p in s.Paragraphs)
            {
                if (y > PdfDocument.PageHeight - 80) { pdf.AddPage(); y = 50; }
                y = pdf.Paragraph(50, y, PdfDocument.PageWidth - 100, p, 10) + 6;
            }
            foreach (var (caption, table) in s.Tables)
            {
                if (y > PdfDocument.PageHeight - 120) { pdf.AddPage(); y = 50; }
                pdf.Text(50, y, caption, 10, true); y += 16;
                double tw = PdfDocument.PageWidth - 100;
                var widths = Enumerable.Repeat(tw / Math.Max(1, table.Columns.Count), table.Columns.Count).ToList();
                var rows = table.Rows.Select(r => (IReadOnlyList<string>)r.Select(CsvExport.Format).ToList()).ToList();
                y = pdf.Table(50, y, table.Columns, rows, widths) + 8;
            }
            foreach (var (caption, px, w, h) in s.Figures)
            {
                double maxW = PdfDocument.PageWidth - 100;
                double scale = Math.Min(maxW / w, 380.0 / h);
                double fw = w * scale, fh = h * scale;
                if (y + fh + 30 > PdfDocument.PageHeight - 40) { pdf.AddPage(); y = 50; }
                pdf.Image(50, y, fw, fh, px, w, h);
                pdf.Rect(50, y, fw, fh, stroke: (0.6, 0.6, 0.6));
                y += fh + 6;
                pdf.Text(50, y, caption, 9, false, (0.3, 0.3, 0.3)); y += 22;
            }
        }
        // footer page numbers
        return pdf;
    }
}
