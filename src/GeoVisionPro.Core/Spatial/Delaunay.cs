namespace GeoVisionPro.Core.Spatial;

/// <summary>Triangle given as three vertex indices.</summary>
public readonly record struct Triangle(int A, int B, int C);

/// <summary>
/// Bowyer–Watson incremental Delaunay triangulation in 2D. O(n^2) worst case; adequate for
/// interpretation-scale point sets (fault sticks, sparse picks, well tops, up to ~20k points).
/// </summary>
public static class Delaunay
{
    public static List<Triangle> Triangulate(IReadOnlyList<Point2D> points)
    {
        int n = points.Count;
        var result = new List<Triangle>();
        if (n < 3) return result;
        double minX = points.Min(p => p.X), minY = points.Min(p => p.Y);
        double maxX = points.Max(p => p.X), maxY = points.Max(p => p.Y);
        double d = Math.Max(maxX - minX, maxY - minY);
        if (d == 0) return result;
        double mx = (minX + maxX) / 2, my = (minY + maxY) / 2;
        var pts = new List<Point2D>(points)
        {
            new(mx - 20 * d, my - d),
            new(mx, my + 20 * d),
            new(mx + 20 * d, my - d)
        };
        var tris = new List<(int A, int B, int C, double Cx, double Cy, double R2)> { Make(pts, n, n + 1, n + 2) };
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            var bad = new List<int>();
            for (int t = 0; t < tris.Count; t++)
            {
                var tr = tris[t];
                double dx = p.X - tr.Cx, dy = p.Y - tr.Cy;
                if (dx * dx + dy * dy <= tr.R2 * (1 + 1e-12)) bad.Add(t);
            }
            var edges = new Dictionary<(int, int), int>();
            foreach (var t in bad)
            {
                var tr = tris[t];
                AddEdge(edges, tr.A, tr.B); AddEdge(edges, tr.B, tr.C); AddEdge(edges, tr.C, tr.A);
            }
            for (int k = bad.Count - 1; k >= 0; k--) tris.RemoveAt(bad[k]);
            foreach (var (e, count) in edges)
                if (count == 1) tris.Add(Make(pts, e.Item1, e.Item2, i));
        }
        foreach (var t in tris)
            if (t.A < n && t.B < n && t.C < n) result.Add(new Triangle(t.A, t.B, t.C));
        return result;
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    private static (int, int, int, double, double, double) Make(List<Point2D> p, int a, int b, int c)
    {
        double ax = p[a].X, ay = p[a].Y, bx = p[b].X, by = p[b].Y, cx = p[c].X, cy = p[c].Y;
        double dd = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(dd) < 1e-300) return (a, b, c, ax, ay, double.MaxValue);
        double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / dd;
        double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / dd;
        double r2 = (ax - ux) * (ax - ux) + (ay - uy) * (ay - uy);
        return (a, b, c, ux, uy, r2);
    }

    /// <summary>Barycentric linear interpolation of z at p within triangle (a,b,c); returns NaN outside.</summary>
    public static double InterpolateInTriangle(Point2D a, Point2D b, Point2D c, double za, double zb, double zc, Point2D p)
    {
        double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-300) return double.NaN;
        double l1 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
        double l2 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
        double l3 = 1 - l1 - l2;
        const double eps = -1e-9;
        if (l1 < eps || l2 < eps || l3 < eps) return double.NaN;
        return l1 * za + l2 * zb + l3 * zc;
    }
}
