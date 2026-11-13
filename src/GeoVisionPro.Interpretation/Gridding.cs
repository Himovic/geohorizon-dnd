using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Interpretation;

public enum GriddingMethod { InverseDistance, Laplace, MinimumCurvature }

/// <summary>Interpolation (gap filling), smoothing and arithmetic on regular horizon grids.</summary>
public static class Gridding
{
    /// <summary>
    /// Inverse-distance-weighted interpolation (power p) of undefined cells from defined cells within a search radius
    /// (in cells). Uses a coarse spatial bucket index for speed.
    /// </summary>
    public static Grid2D InverseDistance(Grid2D grid, double radius, double power = 2, int maxPoints = 16)
    {
        var res = grid.Clone();
        int bucket = Math.Max(4, (int)radius);
        var buckets = new Dictionary<(int, int), List<(int I, int J, double V)>>();
        for (int i = 0; i < grid.NI; i++)
            for (int j = 0; j < grid.NJ; j++)
            {
                double v = grid[i, j];
                if (double.IsNaN(v)) continue;
                var key = (i / bucket, j / bucket);
                if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = [];
                l.Add((i, j, v));
            }
        int br = (int)Math.Ceiling(radius / bucket);
        Parallel.For(0, grid.NI, i =>
        {
            var cand = new List<(double D2, double V)>();
            for (int j = 0; j < grid.NJ; j++)
            {
                if (!double.IsNaN(grid[i, j])) continue;
                cand.Clear();
                int bi = i / bucket, bj = j / bucket;
                for (int a = bi - br; a <= bi + br; a++)
                    for (int b = bj - br; b <= bj + br; b++)
                        if (buckets.TryGetValue((a, b), out var l))
                            foreach (var p in l)
                            {
                                double d2 = (p.I - i) * (double)(p.I - i) + (p.J - j) * (double)(p.J - j);
                                if (d2 <= radius * radius) cand.Add((d2, p.V));
                            }
                if (cand.Count == 0) continue;
                cand.Sort((x, y) => x.D2.CompareTo(y.D2));
                double sw = 0, sv = 0;
                foreach (var (d2, v) in cand.Take(maxPoints))
                {
                    double w = 1 / Math.Pow(Math.Max(d2, 1e-12), power / 2);
                    sw += w; sv += w * v;
                }
                res[i, j] = sv / sw;
            }
        });
        return res;
    }

    /// <summary>
    /// Iterative surface interpolation with fixed data cells: Laplace (∇²z = 0, harmonic, no overshoot) or
    /// minimum curvature (∇⁴z = 0, smooth, may overshoot) solved by Gauss–Seidel relaxation, initialised by IDW.
    /// Only cells inside the convex footprint reachable within <paramref name="maxDistance"/> cells of data are filled.
    /// </summary>
    public static Grid2D Interpolate(Grid2D grid, GriddingMethod method, double maxDistance = 50, int iterations = 400, double tolerance = 1e-6,
        CancellationToken ct = default)
    {
        var init = InverseDistance(grid, maxDistance, 2, 12);
        if (method == GriddingMethod.InverseDistance) return init;
        int ni = grid.NI, nj = grid.NJ;
        var fixedMask = new bool[ni * nj];
        var active = new bool[ni * nj];
        for (int k = 0; k < ni * nj; k++) { fixedMask[k] = !double.IsNaN(grid.Values[k]); active[k] = !double.IsNaN(init.Values[k]); }
        var z = init.Values;
        double range = 1;
        { var (mn, mx) = grid.Range(); if (!double.IsNaN(mn)) range = Math.Max(1e-12, mx - mn); }
        for (int it = 0; it < iterations; it++)
        {
            if ((it & 15) == 0) ct.ThrowIfCancellationRequested();
            double maxChange = 0;
            for (int i = 0; i < ni; i++)
                for (int j = 0; j < nj; j++)
                {
                    int k = i * nj + j;
                    if (fixedMask[k] || !active[k]) continue;
                    double newV;
                    if (method == GriddingMethod.Laplace || i < 2 || j < 2 || i >= ni - 2 || j >= nj - 2)
                    {
                        double s = 0; int c = 0;
                        if (i > 0 && active[k - nj]) { s += z[k - nj]; c++; }
                        if (i < ni - 1 && active[k + nj]) { s += z[k + nj]; c++; }
                        if (j > 0 && active[k - 1]) { s += z[k - 1]; c++; }
                        if (j < nj - 1 && active[k + 1]) { s += z[k + 1]; c++; }
                        if (c == 0) continue;
                        newV = s / c;
                    }
                    else
                    {
                        // biharmonic 13-point stencil: 20 z0 - 8 Σ(n) + 2 Σ(diag) + Σ(n2) = 0
                        bool ok = true;
                        foreach (var o in new[] { -nj, nj, -1, 1, -nj - 1, -nj + 1, nj - 1, nj + 1, -2 * nj, 2 * nj, -2, 2 })
                            if (!active[k + o]) { ok = false; break; }
                        if (!ok)
                        {
                            newV = (z[k - nj] * (active[k - nj] ? 1 : 0) + z[k + nj] * (active[k + nj] ? 1 : 0) + z[k - 1] * (active[k - 1] ? 1 : 0) + z[k + 1] * (active[k + 1] ? 1 : 0));
                            int c = (active[k - nj] ? 1 : 0) + (active[k + nj] ? 1 : 0) + (active[k - 1] ? 1 : 0) + (active[k + 1] ? 1 : 0);
                            if (c == 0) continue;
                            newV /= c;
                        }
                        else
                        {
                            double n1 = z[k - nj] + z[k + nj] + z[k - 1] + z[k + 1];
                            double d1 = z[k - nj - 1] + z[k - nj + 1] + z[k + nj - 1] + z[k + nj + 1];
                            double n2 = z[k - 2 * nj] + z[k + 2 * nj] + z[k - 2] + z[k + 2];
                            newV = (8 * n1 - 2 * d1 - n2) / 20;
                        }
                    }
                    maxChange = Math.Max(maxChange, Math.Abs(newV - z[k]));
                    z[k] = newV;
                }
            if (maxChange / range < tolerance) break;
        }
        return init;
    }

    /// <summary>NaN-aware moving-window smoothing (mean or median) over a (2r+1)² neighbourhood; undefined cells stay undefined.</summary>
    public static Grid2D Smooth(Grid2D grid, int radius, bool median = false)
    {
        var res = grid.Clone();
        Parallel.For(0, grid.NI, i =>
        {
            var buf = new List<double>();
            for (int j = 0; j < grid.NJ; j++)
            {
                if (double.IsNaN(grid[i, j])) continue;
                buf.Clear();
                for (int a = Math.Max(0, i - radius); a <= Math.Min(grid.NI - 1, i + radius); a++)
                    for (int b = Math.Max(0, j - radius); b <= Math.Min(grid.NJ - 1, j + radius); b++)
                    {
                        double v = grid[a, b];
                        if (!double.IsNaN(v)) buf.Add(v);
                    }
                if (median) { buf.Sort(); res[i, j] = buf[buf.Count / 2]; }
                else res[i, j] = buf.Average();
            }
        });
        return res;
    }

    public static Grid2D Difference(Grid2D a, Grid2D b)
    {
        if (a.NI != b.NI || a.NJ != b.NJ) throw new ArgumentException("Grids must have identical dimensions.");
        var r = new Grid2D(a.NI, a.NJ);
        for (int k = 0; k < a.Values.Length; k++) r.Values[k] = a.Values[k] - b.Values[k];
        return r;
    }

    /// <summary>Applies an interpolated/smoothed grid back to a horizon as a reversible edit.</summary>
    public static HorizonEdit ApplyToHorizon(Horizon h, Grid2D grid, PickSource source, bool onlyUndefined, string description)
    {
        var edit = new HorizonEdit(h, description);
        for (int k = 0; k < h.Values.Length; k++)
        {
            double v = grid.Values[k];
            if (double.IsNaN(v)) continue;
            if (onlyUndefined && !double.IsNaN(h.Values[k])) continue;
            if (!onlyUndefined && v == h.Values[k]) continue;
            edit.Set(k, v, onlyUndefined ? source : (PickSource)(h.Sources[k] == 0 ? (byte)source : h.Sources[k]));
        }
        h.Touch();
        return edit;
    }

    /// <summary>Statistics of the difference between two horizons (isochron/QC), in milliseconds when time.</summary>
    public static StatisticsSummaryShort Compare(Horizon a, Horizon b)
    {
        var d = Difference(a.AsGrid(), b.AsGrid()).Values.Where(v => !double.IsNaN(v)).ToList();
        if (d.Count == 0) return new(0, double.NaN, double.NaN, double.NaN, double.NaN);
        double mean = d.Average();
        return new(d.Count, d.Min(), d.Max(), mean, Math.Sqrt(d.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, d.Count - 1)));
    }
}

public sealed record StatisticsSummaryShort(int Count, double Min, double Max, double Mean, double StdDev);

public static class Flattening
{
    /// <summary>
    /// Flattens a section on a horizon: each trace is shifted so that the horizon time maps to <paramref name="referenceTime"/>.
    /// Traces where the horizon is undefined are zeroed. Locations map section rows to (il, xl).
    /// </summary>
    public static Array2D Flatten(Array2D section, IReadOnlyList<(int Inline, int Crossline)> locations, Horizon horizon, SurveyGeometry g, double referenceTime)
    {
        var res = new Array2D(section.Rows, section.Cols);
        for (int r = 0; r < section.Rows; r++)
        {
            double t = horizon.GetValue(locations[r].Inline, locations[r].Crossline);
            if (double.IsNaN(t)) continue;
            double shift = (t - referenceTime) / g.SampleInterval;
            var src = section.Row(r);
            var dst = res.Row(r);
            for (int k = 0; k < section.Cols; k++)
            {
                float v = Core.Numerics.Dsp.SampleLinear(src, k + shift);
                dst[k] = float.IsNaN(v) ? 0 : v;
            }
        }
        return res;
    }
}
