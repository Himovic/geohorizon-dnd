using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Seismic.Volumes;

/// <summary>
/// Random access to a regular post-stack seismic volume. Implementations must be thread-safe for reads.
/// Sections are returned as <see cref="Array2D"/> with Rows = traces and Cols = samples (time slices: Rows = inlines, Cols = crosslines).
/// </summary>
public interface ISeismicVolume : IDisposable
{
    string Name { get; }
    SurveyGeometry Geometry { get; }

    /// <summary>Reads one trace; returns false (and fills with zeros) when the bin is empty.</summary>
    bool TryReadTrace(int inline, int crossline, Span<float> destination);

    Array2D ReadInline(int inline);
    Array2D ReadCrossline(int crossline);
    /// <summary>Constant-time (sample index) slice [inline index, crossline index].</summary>
    Array2D ReadTimeSlice(int sampleIndex);
}

/// <summary>A writable volume (in-memory or chunked store).</summary>
public interface IWritableSeismicVolume : ISeismicVolume
{
    void WriteTrace(int inline, int crossline, ReadOnlySpan<float> samples);
    void Flush();
}

/// <summary>Pre-stack gathers organised by (inline, crossline) bins.</summary>
public interface IGatherDataset : IDisposable
{
    string Name { get; }
    SurveyGeometry Geometry { get; }
    /// <summary>Offsets (m) or incidence angles (degrees) of the gather traces.</summary>
    double[] Offsets { get; }
    bool IsAngleGather { get; }
    /// <summary>Returns [offset index, sample] or null if the bin is empty.</summary>
    Array2D? ReadGather(int inline, int crossline);
}

/// <summary>Arbitrary-line trace locations in fractional inline/crossline coordinates.</summary>
public sealed record ArbitraryPath(IReadOnlyList<(double Inline, double Crossline)> Points)
{
    /// <summary>Densifies a polyline defined by vertices to approximately one trace per bin.</summary>
    public static ArbitraryPath FromVertices(IReadOnlyList<(double Inline, double Crossline)> vertices, double stepInBins = 1.0)
    {
        var pts = new List<(double, double)>();
        for (int v = 0; v < vertices.Count - 1; v++)
        {
            var a = vertices[v]; var b = vertices[v + 1];
            double len = Math.Sqrt(Math.Pow(b.Inline - a.Inline, 2) + Math.Pow(b.Crossline - a.Crossline, 2));
            int n = Math.Max(1, (int)Math.Ceiling(len / stepInBins));
            for (int k = 0; k < n; k++)
            {
                double t = k / (double)n;
                pts.Add((a.Inline + t * (b.Inline - a.Inline), a.Crossline + t * (b.Crossline - a.Crossline)));
            }
        }
        if (vertices.Count > 0) pts.Add(vertices[^1]);
        return new ArbitraryPath(pts);
    }
}

/// <summary>Default implementations of sections and arbitrary lines based on trace access.</summary>
public abstract class SeismicVolumeBase : ISeismicVolume
{
    public string Name { get; protected set; } = string.Empty;
    public abstract SurveyGeometry Geometry { get; }
    public abstract bool TryReadTrace(int inline, int crossline, Span<float> destination);

    public virtual Array2D ReadInline(int inline)
    {
        var g = Geometry;
        var a = new Array2D(g.CrosslineCount, g.SampleCount);
        for (int j = 0; j < g.CrosslineCount; j++) TryReadTrace(inline, g.Crosslines.ValueAt(j), a.Row(j));
        return a;
    }

    public virtual Array2D ReadCrossline(int crossline)
    {
        var g = Geometry;
        var a = new Array2D(g.InlineCount, g.SampleCount);
        for (int i = 0; i < g.InlineCount; i++) TryReadTrace(g.Inlines.ValueAt(i), crossline, a.Row(i));
        return a;
    }

    public virtual Array2D ReadTimeSlice(int sampleIndex)
    {
        var g = Geometry;
        var a = new Array2D(g.InlineCount, g.CrosslineCount);
        var buf = new float[g.SampleCount];
        for (int i = 0; i < g.InlineCount; i++)
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                TryReadTrace(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j), buf);
                a[i, j] = buf[Math.Clamp(sampleIndex, 0, g.SampleCount - 1)];
            }
        return a;
    }

    public virtual void Dispose() { GC.SuppressFinalize(this); }
}

public static class SeismicVolumeExtensions
{
    public static float[] ReadTrace(this ISeismicVolume v, int inline, int crossline)
    {
        var t = new float[v.Geometry.SampleCount];
        v.TryReadTrace(inline, crossline, t);
        return t;
    }

    /// <summary>
    /// Extracts an arbitrary section with bilinear interpolation between the four neighbouring traces.
    /// </summary>
    public static Array2D ReadArbitrary(this ISeismicVolume v, ArbitraryPath path)
    {
        var g = v.Geometry;
        var a = new Array2D(path.Points.Count, g.SampleCount);
        var t00 = new float[g.SampleCount]; var t01 = new float[g.SampleCount];
        var t10 = new float[g.SampleCount]; var t11 = new float[g.SampleCount];
        for (int p = 0; p < path.Points.Count; p++)
        {
            var (ilF, xlF) = path.Points[p];
            double fi = (ilF - g.Inlines.First) / g.Inlines.Step;
            double fj = (xlF - g.Crosslines.First) / g.Crosslines.Step;
            int i0 = Math.Clamp((int)Math.Floor(fi), 0, Math.Max(0, g.InlineCount - 1));
            int j0 = Math.Clamp((int)Math.Floor(fj), 0, Math.Max(0, g.CrosslineCount - 1));
            int i1 = Math.Min(i0 + 1, g.InlineCount - 1), j1 = Math.Min(j0 + 1, g.CrosslineCount - 1);
            double wi = Math.Clamp(fi - i0, 0, 1), wj = Math.Clamp(fj - j0, 0, 1);
            v.TryReadTrace(g.Inlines.ValueAt(i0), g.Crosslines.ValueAt(j0), t00);
            v.TryReadTrace(g.Inlines.ValueAt(i0), g.Crosslines.ValueAt(j1), t01);
            v.TryReadTrace(g.Inlines.ValueAt(i1), g.Crosslines.ValueAt(j0), t10);
            v.TryReadTrace(g.Inlines.ValueAt(i1), g.Crosslines.ValueAt(j1), t11);
            var row = a.Row(p);
            for (int k = 0; k < g.SampleCount; k++)
                row[k] = (float)((1 - wi) * ((1 - wj) * t00[k] + wj * t01[k]) + wi * ((1 - wj) * t10[k] + wj * t11[k]));
        }
        return a;
    }

    /// <summary>Nearest trace to a map coordinate, or null if outside the survey.</summary>
    public static (int Inline, int Crossline)? NearestTrace(this SurveyGeometry g, Point2D map)
    {
        var (fi, fx) = g.Transform.ToGrid(map.X, map.Y);
        int i = (int)Math.Round((fi - g.Inlines.First) / g.Inlines.Step);
        int j = (int)Math.Round((fx - g.Crosslines.First) / g.Crosslines.Step);
        if (i < 0 || j < 0 || i >= g.InlineCount || j >= g.CrosslineCount) return null;
        return (g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j));
    }

    /// <summary>Amplitude statistics estimated from a regular subset of traces.</summary>
    public static (double Min, double Max, double Rms) EstimateStatistics(this ISeismicVolume v, int maxTraces = 400)
    {
        var g = v.Geometry;
        int stepI = Math.Max(1, (int)Math.Sqrt(g.TraceCount / (double)maxTraces));
        double mn = double.MaxValue, mx = double.MinValue, ss = 0; long n = 0;
        var t = new float[g.SampleCount];
        for (int i = 0; i < g.InlineCount; i += stepI)
            for (int j = 0; j < g.CrosslineCount; j += stepI)
            {
                if (!v.TryReadTrace(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j), t)) continue;
                foreach (var s in t) { if (float.IsNaN(s)) continue; mn = Math.Min(mn, s); mx = Math.Max(mx, s); ss += s * (double)s; n++; }
            }
        return n == 0 ? (0, 0, 0) : (mn, mx, Math.Sqrt(ss / n));
    }

    /// <summary>Copies a volume trace-by-trace into a writable target, applying an optional per-trace function.</summary>
    public static void CopyTo(this ISeismicVolume source, IWritableSeismicVolume target, Action<int, int, Span<float>>? transform = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = source.Geometry;
        var t = new float[g.SampleCount];
        for (int i = 0; i < g.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int il = g.Inlines.ValueAt(i);
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                int xl = g.Crosslines.ValueAt(j);
                if (!source.TryReadTrace(il, xl, t)) continue;
                transform?.Invoke(il, xl, t);
                target.WriteTrace(il, xl, t);
            }
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"Inline {il}"));
        }
        target.Flush();
    }
}
