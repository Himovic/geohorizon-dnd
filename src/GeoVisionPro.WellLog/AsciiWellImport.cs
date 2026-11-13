using System.Globalization;
using GeoVisionPro.Domain;

namespace GeoVisionPro.WellLog;

/// <summary>
/// Column-oriented ASCII/CSV import of well data (logs, tops, deviation surveys, checkshots).
/// Delimiter auto-detected (comma, semicolon, tab, whitespace); lines starting with '#' are comments;
/// the first non-numeric line is treated as the column header.
/// </summary>
public static class AsciiWellImport
{
    public sealed record Table(string[] Headers, List<string[]> Rows);

    public static Table ReadTable(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList();
        if (lines.Count == 0) return new Table([], []);
        char? delim = DetectDelimiter(lines.Take(10));
        string[] Split(string l) => delim is { } d ? l.Split(d).Select(s => s.Trim().Trim('"')).ToArray()
            : l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var first = Split(lines[0]);
        bool header = first.Any(f => !double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        var headers = header ? first : first.Select((_, i) => $"COL{i + 1}").ToArray();
        var rows = lines.Skip(header ? 1 : 0).Select(Split).ToList();
        return new Table(headers, rows);
    }

    private static char? DetectDelimiter(IEnumerable<string> sample)
    {
        var s = sample.ToList();
        foreach (var d in new[] { ',', ';', '\t' })
            if (s.All(l => l.Contains(d))) return d;
        return null;
    }

    private static int Col(Table t, params string[] names)
    {
        for (int i = 0; i < t.Headers.Length; i++)
            if (names.Any(n => t.Headers[i].Equals(n, StringComparison.OrdinalIgnoreCase))) return i;
        for (int i = 0; i < t.Headers.Length; i++)
            if (names.Any(n => t.Headers[i].Contains(n, StringComparison.OrdinalIgnoreCase))) return i;
        return -1;
    }

    private static double D(string[] row, int i) =>
        i >= 0 && i < row.Length && double.TryParse(row[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    /// <summary>Tops: columns [Well], Name/Top/Surface, MD/Depth. Returns (wellName or null, top name, md).</summary>
    public static List<(string? Well, string Name, double Md)> ReadTops(string text)
    {
        var t = ReadTable(text);
        int w = Col(t, "WELL", "WELLNAME", "UWI");
        int n = Col(t, "TOP", "NAME", "SURFACE", "FORMATION", "MARKER");
        int md = Col(t, "MD", "DEPTH", "TOPMD");
        if (n < 0) n = w == 0 ? 1 : 0;
        if (md < 0) md = t.Headers.Length - 1;
        var res = new List<(string?, string, double)>();
        foreach (var r in t.Rows)
        {
            double d = D(r, md);
            if (double.IsNaN(d) || n >= r.Length) continue;
            res.Add((w >= 0 && w < r.Length ? r[w] : null, r[n], d));
        }
        return res;
    }

    /// <summary>Deviation survey: MD, INC, AZI.</summary>
    public static List<DeviationStation> ReadDeviation(string text)
    {
        var t = ReadTable(text);
        int md = Col(t, "MD", "DEPTH"), inc = Col(t, "INC", "INCL", "DEVI", "INCLINATION"), azi = Col(t, "AZI", "AZIM", "HAZI", "AZIMUTH");
        if (md < 0) md = 0; if (inc < 0) inc = 1; if (azi < 0) azi = 2;
        return t.Rows.Select(r => new DeviationStation(D(r, md), D(r, inc), D(r, azi)))
            .Where(s => !double.IsNaN(s.MeasuredDepth) && !double.IsNaN(s.Inclination) && !double.IsNaN(s.Azimuth))
            .OrderBy(s => s.MeasuredDepth).ToList();
    }

    /// <summary>Checkshot / time-depth table: MD (or TVDSS) and TWT (ms) or OWT (ms).</summary>
    public static List<TimeDepthPoint> ReadTimeDepth(string text, bool timeIsOneWay = false, bool timeInSeconds = false)
    {
        var t = ReadTable(text);
        int md = Col(t, "MD", "DEPTH", "Z");
        int tvdss = Col(t, "TVDSS", "TVD_SS");
        int twt = Col(t, "TWT", "TIME", "T", "OWT");
        if (twt >= 0 && t.Headers[twt].Contains("OWT", StringComparison.OrdinalIgnoreCase)) timeIsOneWay = true;
        if (md < 0) md = 0;
        if (twt < 0) twt = t.Headers.Length - 1;
        var res = new List<TimeDepthPoint>();
        foreach (var r in t.Rows)
        {
            double d = D(r, md), time = D(r, twt);
            if (double.IsNaN(d) || double.IsNaN(time)) continue;
            if (!timeInSeconds) time /= 1000;
            if (timeIsOneWay) time *= 2;
            res.Add(new TimeDepthPoint(d, time, tvdss >= 0 ? D(r, tvdss) : double.NaN));
        }
        return res.OrderBy(p => p.MeasuredDepth).ToList();
    }

    /// <summary>Generic columnar log file: first depth-like column is the index, others become curves.</summary>
    public static List<LogCurve> ReadLogs(string text, Guid wellId, string source, double nullValue = -999.25)
    {
        var t = ReadTable(text);
        int di = Col(t, "DEPT", "DEPTH", "MD");
        if (di < 0) di = 0;
        var depths = t.Rows.Select(r => D(r, di)).ToArray();
        var curves = new List<LogCurve>();
        for (int c = 0; c < t.Headers.Length; c++)
        {
            if (c == di) continue;
            var v = t.Rows.Select(r => { var x = D(r, c); return Math.Abs(x - nullValue) < 1e-6 ? float.NaN : (float)x; }).ToArray();
            var name = t.Headers[c];
            curves.Add(new LogCurve { WellId = wellId, Name = name, Mnemonic = name, Unit = "", Family = CurveFamilies.Detect(name, ""), Depths = depths, Values = v, Source = source });
        }
        return curves;
    }
}
