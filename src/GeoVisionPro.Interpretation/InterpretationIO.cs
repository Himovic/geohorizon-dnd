using System.Globalization;
using System.Text;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Interpretation;

public enum HorizonXyzColumns { MapXYZ, InlineCrosslineZ, InlineCrosslineXYZ }

/// <summary>Import/export of horizons (XYZ, inline/crossline/time ASCII, ZMAP+ grids) and fault sticks.</summary>
public static class InterpretationIO
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static IEnumerable<double[]> NumericRows(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('@')) continue;
            var parts = line.Split([' ', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
            var vals = new double[parts.Length];
            bool ok = true;
            for (int i = 0; i < parts.Length; i++) if (!double.TryParse(parts[i], NumberStyles.Float, Ci, out vals[i])) { ok = false; break; }
            if (ok && vals.Length >= 3) yield return vals;
        }
    }

    /// <summary>
    /// Imports a horizon from ASCII columns. Z values are in milliseconds (time) or metres (depth); times are stored in seconds.
    /// Map coordinates are converted to the nearest bin with the survey transform.
    /// </summary>
    public static Horizon ImportXyz(string text, SurveyGeometry g, string name, HorizonXyzColumns columns, bool zInMilliseconds = true, double nullValue = -999.25)
    {
        var h = Horizon.CreateFor(g, name, "#FFB300");
        int count = 0, outside = 0;
        foreach (var r in NumericRows(text))
        {
            double z = r[^1];
            if (Math.Abs(z - nullValue) < 1e-6) continue;
            int il, xl;
            if (columns == HorizonXyzColumns.MapXYZ)
            {
                var (fi, fx) = g.Transform.ToGrid(r[0], r[1]);
                il = g.Inlines.First + (int)Math.Round((fi - g.Inlines.First) / g.Inlines.Step) * g.Inlines.Step;
                xl = g.Crosslines.First + (int)Math.Round((fx - g.Crosslines.First) / g.Crosslines.Step) * g.Crosslines.Step;
            }
            else { il = (int)Math.Round(r[0]); xl = (int)Math.Round(r[1]); }
            if (!h.TryIndex(il, xl, out _, out _)) { outside++; continue; }
            h.SetValue(il, xl, zInMilliseconds && g.Domain == Core.Units.DomainType.Time ? z / 1000 : z, PickSource.Imported);
            count++;
        }
        h.Description = $"Imported {count} points ({outside} outside survey)";
        return h;
    }

    public static string ExportXyz(Horizon h, SurveyGeometry g, bool includeInlineCrossline = true, bool zInMilliseconds = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# GeoVision Pro horizon '{h.Name}' ({h.Domain})");
        sb.AppendLine(includeInlineCrossline ? "# X Y INLINE CROSSLINE Z" : "# X Y Z");
        for (int i = 0; i < h.InlineCount; i++)
            for (int j = 0; j < h.CrosslineCount; j++)
            {
                double v = h.Values[i * h.CrosslineCount + j];
                if (double.IsNaN(v)) continue;
                int il = h.InlineAt(i), xl = h.CrosslineAt(j);
                var p = g.MapLocation(il, xl);
                double z = zInMilliseconds && h.Domain == Core.Units.DomainType.Time ? v * 1000 : v;
                sb.AppendLine(includeInlineCrossline
                    ? string.Create(Ci, $"{p.X:0.00} {p.Y:0.00} {il} {xl} {z:0.####}")
                    : string.Create(Ci, $"{p.X:0.00} {p.Y:0.00} {z:0.####}"));
            }
        return sb.ToString();
    }

    /// <summary>
    /// Writes a ZMAP+ grid of the horizon resampled onto a regular map-aligned grid (nodes = bins when the survey is
    /// not rotated; for rotated surveys, nearest-bin sampling is used).
    /// </summary>
    public static string ExportZmap(Horizon h, SurveyGeometry g, double nullValue = 1e30, bool zInMilliseconds = true)
    {
        var outline = g.MapOutline();
        double xmin = outline.Min(p => p.X), xmax = outline.Max(p => p.X), ymin = outline.Min(p => p.Y), ymax = outline.Max(p => p.Y);
        double dx = Math.Min(g.Transform.InlineSpacing, g.Transform.CrosslineSpacing);
        if (dx <= 0) dx = 25;
        int nx = (int)Math.Ceiling((xmax - xmin) / dx) + 1, ny = (int)Math.Ceiling((ymax - ymin) / dx) + 1;
        var sb = new StringBuilder();
        sb.AppendLine($"! GeoVision Pro ZMAP+ export of {h.Name}");
        sb.AppendLine($"@{h.Name.Replace(',', '_')}, GRID, 5");
        sb.AppendLine(string.Create(Ci, $"15, {nullValue:E6}, , 7, 1"));
        sb.AppendLine(string.Create(Ci, $"{ny}, {nx}, {xmin:0.###}, {xmax:0.###}, {ymin:0.###}, {ymax:0.###}"));
        sb.AppendLine("0.0, 0.0, 0.0");
        sb.AppendLine("@");
        int col = 0;
        for (int ix = 0; ix < nx; ix++)
        {
            for (int iy = ny - 1; iy >= 0; iy--) // ZMAP columns run north to south
            {
                double x = xmin + ix * dx, y = ymin + iy * dx;
                var (fi, fx) = g.Transform.ToGrid(x, y);
                double v = h.GetValue(g.Inlines.First + (int)Math.Round((fi - g.Inlines.First) / g.Inlines.Step) * g.Inlines.Step,
                    g.Crosslines.First + (int)Math.Round((fx - g.Crosslines.First) / g.Crosslines.Step) * g.Crosslines.Step);
                if (!double.IsNaN(v) && zInMilliseconds && h.Domain == Core.Units.DomainType.Time) v *= 1000;
                sb.Append((double.IsNaN(v) ? nullValue : v).ToString("E7", Ci).PadLeft(15));
                if (++col == 5) { sb.AppendLine(); col = 0; }
            }
            if (col != 0) { sb.AppendLine(); col = 0; }
        }
        return sb.ToString();
    }

    /// <summary>Reads a ZMAP+ grid into (x, y, z) points (null nodes skipped) for import via the survey transform.</summary>
    public static List<(double X, double Y, double Z)> ReadZmap(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('!')).ToList();
        int at = lines.FindIndex(l => l.StartsWith('@'));
        if (at < 0) throw new FormatException("Not a ZMAP+ file (missing '@' header).");
        var h2 = lines[at + 1].Split(',').Select(s => s.Trim()).ToArray();
        double nullV = double.Parse(h2[1], Ci);
        var h3 = lines[at + 2].Split(',').Select(s => s.Trim()).ToArray();
        int ny = int.Parse(h3[0], Ci), nx = int.Parse(h3[1], Ci);
        double xmin = double.Parse(h3[2], Ci), xmax = double.Parse(h3[3], Ci), ymin = double.Parse(h3[4], Ci), ymax = double.Parse(h3[5], Ci);
        int end = lines.FindIndex(at + 1, l => l == "@" || l.StartsWith("@") && l.Length == 1);
        var vals = lines.Skip(end + 1).SelectMany(l => l.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)).Select(s => double.Parse(s, Ci)).ToList();
        var res = new List<(double, double, double)>();
        double dx = nx > 1 ? (xmax - xmin) / (nx - 1) : 0, dy = ny > 1 ? (ymax - ymin) / (ny - 1) : 0;
        for (int k = 0; k < Math.Min(vals.Count, nx * ny); k++)
        {
            int ix = k / ny, iy = ny - 1 - k % ny;
            double v = vals[k];
            if (Math.Abs(v - nullV) <= Math.Abs(nullV) * 1e-6) continue;
            res.Add((xmin + ix * dx, ymin + iy * dy, v));
        }
        return res;
    }

    public static Horizon ImportZmap(string text, SurveyGeometry g, string name, bool zInMilliseconds = true)
    {
        var pts = ReadZmap(text);
        var sb = new StringBuilder();
        foreach (var (x, y, z) in pts) sb.AppendLine(string.Create(Ci, $"{x} {y} {z}"));
        return ImportXyz(sb.ToString(), g, name, HorizonXyzColumns.MapXYZ, zInMilliseconds);
    }

    /// <summary>Fault sticks as ASCII: "X Y Z STICK" or "INLINE CROSSLINE TIME_MS STICK" rows.</summary>
    public static string ExportFaultSticks(Fault f, SurveyGeometry g)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# GeoVision Pro fault '{f.Name}': X Y INLINE CROSSLINE Z(ms|m) STICK");
        for (int s = 0; s < f.Sticks.Count; s++)
            foreach (var p in f.Sticks[s].Points)
            {
                var m = g.MapLocation(p.X, p.Y);
                double z = f.Domain == Core.Units.DomainType.Time ? p.Z * 1000 : p.Z;
                sb.AppendLine(string.Create(Ci, $"{m.X:0.00} {m.Y:0.00} {p.X:0.##} {p.Y:0.##} {z:0.###} {s + 1}"));
            }
        return sb.ToString();
    }

    /// <summary>Imports fault sticks from rows "X Y Z STICK" (map) or "IL XL Z STICK" when <paramref name="mapCoordinates"/> is false.</summary>
    public static Fault ImportFaultSticks(string text, SurveyGeometry g, string name, bool mapCoordinates, bool zInMilliseconds = true)
    {
        var f = new Fault { Name = name, Domain = g.Domain };
        var sticks = new SortedDictionary<int, FaultStick>();
        foreach (var r in NumericRows(text))
        {
            if (r.Length < 4) continue;
            int id = (int)r[^1];
            double z = r[^2];
            double il, xl;
            if (mapCoordinates) (il, xl) = g.Transform.ToGrid(r[0], r[1]);
            else { il = r[0]; xl = r[1]; }
            if (r.Length >= 6) { il = r[2]; xl = r[3]; }
            if (!sticks.TryGetValue(id, out var st)) sticks[id] = st = new FaultStick();
            st.Points.Add(new Point3D(il, xl, zInMilliseconds && g.Domain == Core.Units.DomainType.Time ? z / 1000 : z));
        }
        f.Sticks = sticks.Values.ToList();
        return f;
    }
}
