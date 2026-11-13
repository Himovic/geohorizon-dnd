using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.Visualization2D;

public enum SeismicDisplayMode { VariableDensity, Wiggle, WiggleVariableArea, VariableDensityWiggle }
public enum ClipMode { Percentile, Absolute, MaxAbsolute, MinMax }

/// <summary>Display settings for seismic sections and maps.</summary>
public sealed record SeismicDisplaySettings
{
    public SeismicDisplayMode Mode { get; init; } = SeismicDisplayMode.VariableDensity;
    public ColorMap ColorMap { get; init; } = ColorMaps.Seismic;
    public ClipMode ClipMode { get; init; } = ClipMode.Percentile;
    /// <summary>Percentile (e.g. 99) for Percentile mode, or amplitude for Absolute mode.</summary>
    public double ClipValue { get; init; } = 99;
    public double Gain { get; init; } = 1.0;
    public bool ReversePolarity { get; init; }
    /// <summary>Display-only AGC window in samples (0 = off).</summary>
    public int DisplayAgcSamples { get; init; }
    /// <summary>Wiggle excursion in trace spacings at the clip amplitude.</summary>
    public double WiggleExcursion { get; init; } = 1.5;
    public uint WiggleColor { get; init; } = 0xFF000000;
    public uint VariableAreaColor { get; init; } = 0xFF000000;
    public uint BackgroundColor { get; init; } = 0xFFFFFFFF;
    public bool Interpolate { get; init; } = true;
}

/// <summary>Visible window of a section: fractional trace and sample ranges (inclusive start, exclusive end).</summary>
public readonly record struct SectionViewport(double TraceStart, double TraceEnd, double SampleStart, double SampleEnd)
{
    public static SectionViewport Full(Array2D section) => new(-0.5, section.Rows - 0.5, 0, section.Cols - 1);
    public double TraceSpan => TraceEnd - TraceStart;
    public double SampleSpan => SampleEnd - SampleStart;
}

/// <summary>
/// Rasterises seismic sections (rows = traces along X, cols = samples down Y) into BGRA32 pixel buffers, independent of
/// any UI framework. Supports variable density (bilinear), wiggle, wiggle variable-area and combined displays.
/// </summary>
public static class SectionRenderer
{
    public static (float Low, float High) ComputeClip(Array2D data, SeismicDisplaySettings s)
    {
        switch (s.ClipMode)
        {
            case ClipMode.Absolute: return ((float)-s.ClipValue, (float)s.ClipValue);
            case ClipMode.MaxAbsolute: { var m = Math.Max(Math.Abs(data.Range().Min), Math.Abs(data.Range().Max)); return (-m, m); }
            case ClipMode.MinMax: return data.Range();
            default: { var c = data.AbsPercentile(s.ClipValue); if (c <= 0) c = 1; return (-c, c); }
        }
    }

    /// <summary>Optional display AGC (copy).</summary>
    public static Array2D PrepareForDisplay(Array2D data, SeismicDisplaySettings s)
    {
        if (s.DisplayAgcSamples <= 1 && !s.ReversePolarity && s.Gain == 1) return data;
        var d = data.Clone();
        for (int r = 0; r < d.Rows; r++)
        {
            var row = d.Row(r);
            if (s.DisplayAgcSamples > 1)
            {
                int n = row.Length, h = s.DisplayAgcSamples / 2;
                var cum = new double[n + 1];
                for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + row[i] * (double)row[i];
                double eps = Math.Sqrt(cum[n] / Math.Max(1, n)) * 1e-3 + 1e-30;
                var tmp = row.ToArray();
                for (int i = 0; i < n; i++)
                {
                    int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
                    row[i] = (float)(tmp[i] / (Math.Sqrt((cum[b + 1] - cum[a]) / (b - a + 1)) + eps));
                }
            }
            float f = (float)(s.Gain * (s.ReversePolarity ? -1 : 1));
            if (f != 1) for (int i = 0; i < row.Length; i++) row[i] *= f;
        }
        return d;
    }

    public static uint[] Render(Array2D data, SeismicDisplaySettings s, SectionViewport vp, int width, int height, (float Low, float High)? clip = null)
    {
        var pixels = new uint[width * height];
        if (width <= 0 || height <= 0 || data.Rows == 0 || data.Cols == 0) return pixels;
        var d = PrepareForDisplay(data, s);
        var (lo, hi) = clip ?? ComputeClip(d, s);
        if (s.DisplayAgcSamples > 1 && clip is null) (lo, hi) = ComputeClip(d, s with { ClipMode = ClipMode.Percentile, ClipValue = Math.Min(s.ClipValue, 99.5) });
        bool vd = s.Mode is SeismicDisplayMode.VariableDensity or SeismicDisplayMode.VariableDensityWiggle;
        if (vd) RenderDensity(d, s.ColorMap, lo, hi, vp, width, height, pixels, s.Interpolate);
        else Array.Fill(pixels, s.BackgroundColor);
        if (s.Mode is SeismicDisplayMode.Wiggle or SeismicDisplayMode.WiggleVariableArea or SeismicDisplayMode.VariableDensityWiggle)
            RenderWiggles(d, s, Math.Max(Math.Abs(lo), Math.Abs(hi)), vp, width, height, pixels, fill: s.Mode == SeismicDisplayMode.WiggleVariableArea);
        return pixels;
    }

    private static void RenderDensity(Array2D d, ColorMap cmap, float lo, float hi, SectionViewport vp, int w, int h, uint[] px, bool interp)
    {
        double scale = hi > lo ? 1.0 / (hi - lo) : 1;
        int rows = d.Rows, cols = d.Cols;
        Parallel.For(0, h, y =>
        {
            double fs = vp.SampleStart + (y + 0.5) / h * vp.SampleSpan;
            int s0 = (int)Math.Floor(fs);
            double ws = fs - s0;
            if (!interp) { s0 = (int)Math.Round(fs); ws = 0; }
            int s1 = Math.Min(cols - 1, s0 + 1);
            bool sOk = s0 >= 0 && s0 < cols;
            for (int x = 0; x < w; x++)
            {
                double ft = vp.TraceStart + (x + 0.5) / w * vp.TraceSpan;
                if (!sOk) { px[y * w + x] = 0; continue; }
                int t0 = (int)Math.Floor(ft);
                double wt = ft - t0;
                if (!interp) { t0 = (int)Math.Round(ft); wt = 0; }
                if (t0 < -1 || t0 >= rows) { px[y * w + x] = 0; continue; }
                int ta = Math.Clamp(t0, 0, rows - 1), tb = Math.Clamp(t0 + 1, 0, rows - 1);
                float v00 = d.Data[ta * cols + s0], v01 = d.Data[ta * cols + s1], v10 = d.Data[tb * cols + s0], v11 = d.Data[tb * cols + s1];
                double v = (1 - wt) * ((1 - ws) * v00 + ws * v01) + wt * ((1 - ws) * v10 + ws * v11);
                px[y * w + x] = double.IsNaN(v) ? 0 : cmap.Lookup((v - lo) * scale);
            }
        });
    }

    private static void RenderWiggles(Array2D d, SeismicDisplaySettings s, float clip, SectionViewport vp, int w, int h, uint[] px, bool fill)
    {
        double pxPerTrace = w / vp.TraceSpan;
        if (pxPerTrace < 2) return; // too dense for wiggles
        double ampScale = clip > 0 ? s.WiggleExcursion * pxPerTrace / clip : 0;
        int tFirst = Math.Max(0, (int)Math.Floor(vp.TraceStart)), tLast = Math.Min(d.Rows - 1, (int)Math.Ceiling(vp.TraceEnd));
        for (int t = tFirst; t <= tLast; t++)
        {
            double x0 = (t - vp.TraceStart) / vp.TraceSpan * w - 0.5;
            var row = d.Row(t);
            int prevX = int.MinValue, prevY = 0;
            for (int y = 0; y < h; y++)
            {
                double fs = vp.SampleStart + (y + 0.5) / h * vp.SampleSpan;
                if (fs < 0 || fs > row.Length - 1) { prevX = int.MinValue; continue; }
                int i0 = (int)fs; double f = fs - i0;
                double v = i0 + 1 < row.Length ? row[i0] * (1 - f) + row[i0 + 1] * f : row[i0];
                if (double.IsNaN(v)) { prevX = int.MinValue; continue; }
                double dx = Math.Clamp(v * ampScale, -3 * pxPerTrace, 3 * pxPerTrace);
                int xx = (int)Math.Round(x0 + dx);
                if (fill && dx > 0)
                {
                    int xs = (int)Math.Round(x0), xe = xx;
                    for (int k = Math.Max(0, xs); k <= Math.Min(w - 1, xe); k++) px[y * w + k] = s.VariableAreaColor;
                }
                if (prevX != int.MinValue) DrawLine(px, w, h, prevX, prevY, xx, y, s.WiggleColor);
                prevX = xx; prevY = y;
            }
        }
    }

    public static void DrawLine(uint[] px, int w, int h, int x0, int y0, int x1, int y1, uint color)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, err = dx + dy;
        int guard = 0;
        while (guard++ < 100000)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h) px[y0 * w + x0] = color;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>Renders a map grid (time slice or attribute map) with NaN transparent. Rows map to Y (top = first row).</summary>
    public static uint[] RenderMap(Grid2D grid, ColorMap cmap, double min, double max, int width, int height)
    {
        var px = new uint[width * height];
        double scale = max > min ? 1 / (max - min) : 1;
        for (int y = 0; y < height; y++)
        {
            int i = Math.Clamp((int)((y + 0.5) / height * grid.NI), 0, grid.NI - 1);
            for (int x = 0; x < width; x++)
            {
                int j = Math.Clamp((int)((x + 0.5) / width * grid.NJ), 0, grid.NJ - 1);
                double v = grid[i, j];
                px[y * width + x] = double.IsNaN(v) ? 0 : cmap.Lookup((v - min) * scale);
            }
        }
        return px;
    }

    /// <summary>Converts a 2D array (e.g. time slice [il, xl]) to a grid for map rendering.</summary>
    public static Grid2D ToGrid(Array2D a) => new(a.Rows, a.Cols, a.Data.Select(v => (double)v).ToArray());
}
