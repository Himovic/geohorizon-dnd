using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Interpretation;

public enum EventType { Peak, Trough, ZeroCrossingPositive, ZeroCrossingNegative, None }

/// <summary>A reversible change set on a horizon grid (supports undo/redo).</summary>
public sealed class HorizonEdit
{
    public Horizon Horizon { get; }
    public List<(int Index, double OldValue, double NewValue, byte OldSource, byte NewSource)> Changes { get; } = [];
    public string Description { get; }
    public HorizonEdit(Horizon horizon, string description) { Horizon = horizon; Description = description; }

    public void Set(int index, double value, PickSource src)
    {
        Changes.Add((index, Horizon.Values[index], value, Horizon.Sources[index], (byte)(double.IsNaN(value) ? PickSource.None : src)));
        Horizon.Values[index] = value;
        Horizon.Sources[index] = (byte)(double.IsNaN(value) ? PickSource.None : src);
    }

    public void Undo() { for (int k = Changes.Count - 1; k >= 0; k--) { var c = Changes[k]; Horizon.Values[c.Index] = c.OldValue; Horizon.Sources[c.Index] = c.OldSource; } Horizon.Touch(); }
    public void Redo() { foreach (var c in Changes) { Horizon.Values[c.Index] = c.NewValue; Horizon.Sources[c.Index] = c.NewSource; } Horizon.Touch(); }
}

public static class EventSnapping
{
    /// <summary>
    /// Snaps a time to the nearest event of the requested type within ±window samples; returns the refined
    /// fractional sample index (parabolic interpolation for extrema, linear for zero crossings) or NaN.
    /// </summary>
    public static double Snap(ReadOnlySpan<float> trace, double sampleIndex, EventType type, int window)
    {
        if (type == EventType.None) return sampleIndex;
        int c = (int)Math.Round(sampleIndex);
        int n = trace.Length;
        double best = double.NaN, bestDist = double.MaxValue;
        for (int i = Math.Max(1, c - window); i <= Math.Min(n - 2, c + window); i++)
        {
            double pos = double.NaN;
            switch (type)
            {
                case EventType.Peak when trace[i] >= trace[i - 1] && trace[i] > trace[i + 1] && trace[i] > 0:
                case EventType.Trough when trace[i] <= trace[i - 1] && trace[i] < trace[i + 1] && trace[i] < 0:
                    pos = Dsp.ParabolicPeak(trace, i);
                    break;
                case EventType.ZeroCrossingPositive when trace[i] <= 0 && trace[i + 1] > 0:
                case EventType.ZeroCrossingNegative when trace[i] >= 0 && trace[i + 1] < 0:
                    pos = i + (trace[i] == trace[i + 1] ? 0 : trace[i] / (double)(trace[i] - trace[i + 1]));
                    break;
            }
            if (double.IsNaN(pos)) continue;
            double d = Math.Abs(pos - sampleIndex);
            if (d < bestDist) { bestDist = d; best = pos; }
        }
        return best;
    }
}

/// <summary>Parameters of the seeded auto-tracker.</summary>
public sealed record TrackingParameters
{
    public EventType Event { get; init; } = EventType.Peak;
    /// <summary>Half search window around the predicted time (samples).</summary>
    public int SearchWindow { get; init; } = 4;
    /// <summary>Half length of the waveform correlation window (samples).</summary>
    public int CorrelationHalfWindow { get; init; } = 8;
    /// <summary>Minimum normalised cross-correlation between neighbouring waveforms.</summary>
    public double MinCorrelation { get; init; } = 0.7;
    /// <summary>Maximum time jump between adjacent traces (samples).</summary>
    public double MaxJump { get; init; } = 3;
    /// <summary>Overwrite existing picks.</summary>
    public bool Overwrite { get; init; }
    public long MaxPicks { get; init; } = 50_000_000;
}

/// <summary>
/// Seeded 3D horizon auto-tracker (breadth-first flood fill). From each accepted pick, the four neighbouring bins are
/// visited; the event is searched within a window around the current time and accepted when the waveform similarity
/// (normalised cross-correlation over a window) and the time jump satisfy the thresholds.
/// </summary>
public sealed class HorizonAutoTracker
{
    private readonly ISeismicVolume _volume;
    private readonly Dictionary<(int, int), float[]> _cache = [];
    private readonly int _cacheLimit;

    public HorizonAutoTracker(ISeismicVolume volume, int traceCacheLimit = 200_000)
    {
        _volume = volume; _cacheLimit = traceCacheLimit;
    }

    private float[] Trace(int i, int j)
    {
        if (_cache.TryGetValue((i, j), out var t)) return t;
        var g = _volume.Geometry;
        t = new float[g.SampleCount];
        _volume.TryReadTrace(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j), t);
        if (_cache.Count > _cacheLimit) _cache.Clear();
        _cache[(i, j)] = t;
        return t;
    }

    private static double WindowCorrelation(float[] a, double ca, float[] b, double cb, int h)
    {
        double sa = 0, sb = 0, sab = 0, saa = 0, sbb = 0; int n = 0;
        for (int k = -h; k <= h; k++)
        {
            float va = Dsp.SampleLinear(a, ca + k), vb = Dsp.SampleLinear(b, cb + k);
            if (float.IsNaN(va) || float.IsNaN(vb)) continue;
            sa += va; sb += vb; sab += va * (double)vb; saa += va * (double)va; sbb += vb * (double)vb; n++;
        }
        if (n < 3) return 0;
        double cov = sab - sa * sb / n, va2 = saa - sa * sa / n, vb2 = sbb - sb * sb / n;
        return va2 <= 0 || vb2 <= 0 ? 0 : cov / Math.Sqrt(va2 * vb2);
    }

    /// <summary>
    /// Tracks from seeds (inline, crossline, time in s). If <paramref name="restrictToInline"/> is set, tracking stays on that inline (2D tracking).
    /// Returns the edit (for undo) — the horizon is modified in place.
    /// </summary>
    public HorizonEdit Track(Horizon horizon, IEnumerable<(int Inline, int Crossline, double Time)> seeds, TrackingParameters p,
        int? restrictToInline = null, int? restrictToCrossline = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = _volume.Geometry;
        var edit = new HorizonEdit(horizon, "Auto-track");
        var queue = new Queue<(int I, int J, double S)>();
        var visited = new HashSet<long>();
        int nj = g.CrosslineCount;
        foreach (var (il, xl, t) in seeds)
        {
            if (!g.ContainsTrace(il, xl)) continue;
            int i = g.InlineIndex(il), j = g.CrosslineIndex(xl);
            var tr = Trace(i, j);
            double s = EventSnapping.Snap(tr, g.SampleAt(t), p.Event, p.SearchWindow);
            if (double.IsNaN(s)) s = g.SampleAt(t);
            horizon.TryIndex(il, xl, out var hi, out var hj);
            edit.Set(hi * horizon.CrosslineCount + hj, g.TimeAt(s), PickSource.Manual);
            visited.Add((long)i * nj + j);
            queue.Enqueue((i, j, s));
        }
        long picks = 0;
        (int, int)[] dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (queue.Count > 0)
        {
            if ((picks & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ProgressInfo(0, $"Tracked {picks:N0} picks, queue {queue.Count:N0}"));
            }
            var (ci, cj, cs) = queue.Dequeue();
            var ctr = Trace(ci, cj);
            foreach (var (di, dj) in dirs)
            {
                int ni = ci + di, nj2 = cj + dj;
                if (ni < 0 || nj2 < 0 || ni >= g.InlineCount || nj2 >= g.CrosslineCount) continue;
                if (restrictToInline is { } ril && g.Inlines.ValueAt(ni) != ril) continue;
                if (restrictToCrossline is { } rxl && g.Crosslines.ValueAt(nj2) != rxl) continue;
                long key = (long)ni * nj + nj2;
                if (!visited.Add(key)) continue;
                int il = g.Inlines.ValueAt(ni), xl = g.Crosslines.ValueAt(nj2);
                if (!horizon.TryIndex(il, xl, out var hi, out var hj)) continue;
                int hidx = hi * horizon.CrosslineCount + hj;
                if (!p.Overwrite && !double.IsNaN(horizon.Values[hidx]) && horizon.Sources[hidx] != (byte)PickSource.Interpolated) continue;
                var ntr = Trace(ni, nj2);
                double s = EventSnapping.Snap(ntr, cs, p.Event, p.SearchWindow);
                if (double.IsNaN(s) || Math.Abs(s - cs) > p.MaxJump) continue;
                double corr = WindowCorrelation(ctr, cs, ntr, s, p.CorrelationHalfWindow);
                if (corr < p.MinCorrelation) continue;
                edit.Set(hidx, g.TimeAt(s), PickSource.AutoTracked);
                queue.Enqueue((ni, nj2, s));
                if (++picks >= p.MaxPicks) { queue.Clear(); break; }
            }
        }
        horizon.Touch();
        progress?.Report(new ProgressInfo(1, $"Auto-tracking complete: {picks:N0} picks"));
        return edit;
    }
}

public static class ManualPicking
{
    /// <summary>
    /// Interpolates between user clicks along a section (trace positions, times in s), optionally snapping each trace to
    /// the event, and writes the picks into the horizon. <paramref name="locations"/> maps section trace index to (il, xl).
    /// </summary>
    public static HorizonEdit PickSegment(Horizon horizon, Array2D section, IReadOnlyList<(int Inline, int Crossline)> locations,
        IReadOnlyList<(double Trace, double Time)> clicks, SurveyGeometry g, EventType snap, int window)
    {
        var edit = new HorizonEdit(horizon, "Manual pick");
        var ordered = clicks.OrderBy(c => c.Trace).ToList();
        if (ordered.Count == 0) return edit;
        var xs = ordered.Select(c => c.Trace).ToArray(); var ts = ordered.Select(c => c.Time).ToArray();
        int t0 = (int)Math.Round(xs[0]), t1 = (int)Math.Round(xs[^1]);
        for (int tr = Math.Max(0, t0); tr <= Math.Min(section.Rows - 1, t1); tr++)
        {
            double time = xs.Length == 1 ? ts[0] : Dsp.Interp(xs, ts, tr, clamp: true);
            double s = g.SampleAt(time);
            double snapped = EventSnapping.Snap(section.Row(tr), s, snap, window);
            if (!double.IsNaN(snapped)) s = snapped;
            var (il, xl) = locations[tr];
            if (horizon.TryIndex(il, xl, out var i, out var j)) edit.Set(i * horizon.CrosslineCount + j, g.TimeAt(s), PickSource.Manual);
        }
        horizon.Touch();
        return edit;
    }

    /// <summary>Deletes picks within a trace range of a section.</summary>
    public static HorizonEdit Erase(Horizon horizon, IReadOnlyList<(int Inline, int Crossline)> locations, int fromTrace, int toTrace)
    {
        var edit = new HorizonEdit(horizon, "Erase picks");
        for (int tr = Math.Max(0, Math.Min(fromTrace, toTrace)); tr <= Math.Min(locations.Count - 1, Math.Max(fromTrace, toTrace)); tr++)
        {
            var (il, xl) = locations[tr];
            if (horizon.TryIndex(il, xl, out var i, out var j)) edit.Set(i * horizon.CrosslineCount + j, double.NaN, PickSource.None);
        }
        horizon.Touch();
        return edit;
    }
}
