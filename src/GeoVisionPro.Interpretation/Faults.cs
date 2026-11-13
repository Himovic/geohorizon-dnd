using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Interpretation;

/// <summary>Triangulated surface in survey coordinates (X = inline, Y = crossline, Z = time/depth).</summary>
public sealed record SurfaceMesh(IReadOnlyList<Point3D> Vertices, IReadOnlyList<Triangle> Triangles);

/// <summary>Measured fault displacement at one stick.</summary>
public sealed record FaultThrow(int StickIndex, double Inline, double Crossline, double HangingWallTime, double FootWallTime, double Throw);

public static class FaultModeling
{
    /// <summary>Resamples a polyline to n points equally spaced by arc length (in index-time space with the given scaling).</summary>
    public static List<Point3D> ResampleStick(IReadOnlyList<Point3D> pts, int n, double timeScale)
    {
        if (pts.Count == 0) return [];
        if (pts.Count == 1) return Enumerable.Repeat(pts[0], n).ToList();
        var sorted = pts.OrderBy(p => p.Z).ToList();
        var cum = new double[sorted.Count];
        for (int i = 1; i < sorted.Count; i++)
        {
            var a = sorted[i - 1]; var b = sorted[i];
            cum[i] = cum[i - 1] + Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2) + Math.Pow((b.Z - a.Z) * timeScale, 2));
        }
        var res = new List<Point3D>(n);
        for (int k = 0; k < n; k++)
        {
            double s = cum[^1] * k / Math.Max(1, n - 1);
            int i = Array.BinarySearch(cum, s);
            if (i < 0) i = ~i;
            i = Math.Clamp(i, 1, sorted.Count - 1);
            double seg = cum[i] - cum[i - 1];
            double t = seg > 0 ? (s - cum[i - 1]) / seg : 0;
            res.Add(sorted[i - 1] + (sorted[i] - sorted[i - 1]) * t);
        }
        return res;
    }

    /// <summary>
    /// Builds a fault surface by ribbon triangulation between consecutive sticks (ordered along strike), after
    /// resampling every stick to the same number of nodes. Time is scaled to bin units for spacing.
    /// </summary>
    public static SurfaceMesh BuildSurface(Fault fault, int nodesPerStick = 20, double timeScale = 250)
    {
        var sticks = fault.Sticks.Where(s => s.Points.Count >= 2).ToList();
        if (sticks.Count == 0) return new SurfaceMesh([], []);
        // order sticks along the principal strike direction
        var centers = sticks.Select(s => new Point2D(s.Points.Average(p => p.X), s.Points.Average(p => p.Y))).ToList();
        double mx = centers.Average(c => c.X), my = centers.Average(c => c.Y);
        double sxx = centers.Sum(c => (c.X - mx) * (c.X - mx)), syy = centers.Sum(c => (c.Y - my) * (c.Y - my)), sxy = centers.Sum(c => (c.X - mx) * (c.Y - my));
        double angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var order = Enumerable.Range(0, sticks.Count).OrderBy(i => (centers[i].X - mx) * Math.Cos(angle) + (centers[i].Y - my) * Math.Sin(angle)).ToList();
        var verts = new List<Point3D>();
        foreach (var i in order) verts.AddRange(ResampleStick(sticks[i].Points, nodesPerStick, timeScale));
        var tris = new List<Triangle>();
        for (int s = 0; s + 1 < order.Count; s++)
            for (int k = 0; k + 1 < nodesPerStick; k++)
            {
                int a = s * nodesPerStick + k, b = a + 1, c = a + nodesPerStick, d = c + 1;
                tris.Add(new Triangle(a, c, b));
                tris.Add(new Triangle(b, c, d));
            }
        return new SurfaceMesh(verts, tris);
    }

    /// <summary>
    /// Fault–horizon intersection: for each stick, the point where the stick crosses the horizon surface
    /// (horizon time sampled at the stick's map position).
    /// </summary>
    public static List<Point3D> HorizonIntersection(Fault fault, Horizon horizon)
    {
        var res = new List<Point3D>();
        foreach (var stick in fault.Sticks)
        {
            var pts = stick.Points.OrderBy(p => p.Z).ToList();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[i + 1];
                double ha = horizon.GetValue((int)Math.Round(a.X), (int)Math.Round(a.Y));
                double hb = horizon.GetValue((int)Math.Round(b.X), (int)Math.Round(b.Y));
                if (double.IsNaN(ha) || double.IsNaN(hb)) continue;
                double fa = a.Z - ha, fb = b.Z - hb;
                if (fa == 0) { res.Add(a); break; }
                if (fa * fb < 0)
                {
                    double t = fa / (fa - fb);
                    res.Add(a + (b - a) * t);
                    break;
                }
            }
        }
        return res;
    }

    /// <summary>
    /// Vertical throw at each stick: the horizon is sampled at a perpendicular offset (in bins) on both sides of the fault
    /// trace at the intersection; throw = |t_side1 − t_side2| (time units of the horizon).
    /// </summary>
    public static List<FaultThrow> MeasureThrow(Fault fault, Horizon horizon, double offsetBins = 4)
    {
        var res = new List<FaultThrow>();
        var inter = HorizonIntersection(fault, horizon);
        if (inter.Count < 1) return res;
        for (int k = 0; k < inter.Count; k++)
        {
            var p = inter[k];
            // fault strike from neighbouring intersections
            var q = inter[Math.Min(inter.Count - 1, k + 1)]; var o = inter[Math.Max(0, k - 1)];
            double sx = q.X - o.X, sy = q.Y - o.Y;
            double len = Math.Sqrt(sx * sx + sy * sy);
            if (len < 1e-9) { sx = 1; sy = 0; len = 1; }
            double nx = -sy / len, ny = sx / len;
            double t1 = horizon.GetValue((int)Math.Round(p.X + nx * offsetBins), (int)Math.Round(p.Y + ny * offsetBins));
            double t2 = horizon.GetValue((int)Math.Round(p.X - nx * offsetBins), (int)Math.Round(p.Y - ny * offsetBins));
            if (double.IsNaN(t1) || double.IsNaN(t2)) continue;
            // hanging wall is the deeper side for a normal fault
            res.Add(new FaultThrow(k, p.X, p.Y, Math.Max(t1, t2), Math.Min(t1, t2), Math.Abs(t1 - t2)));
        }
        return res;
    }

    /// <summary>Linear interpolation of an intermediate stick between two sticks (fault interpolation along strike).</summary>
    public static FaultStick InterpolateStick(FaultStick a, FaultStick b, double t, int nodes = 20, double timeScale = 250)
    {
        var ra = ResampleStick(a.Points, nodes, timeScale);
        var rb = ResampleStick(b.Points, nodes, timeScale);
        return new FaultStick { Points = ra.Zip(rb, (p, q) => p + (q - p) * t).ToList() };
    }
}
