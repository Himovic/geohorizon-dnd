namespace GeoVisionPro.Core.Spatial;

public readonly record struct Point2D(double X, double Y)
{
    public double DistanceTo(Point2D o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    public static Point2D operator +(Point2D a, Point2D b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2D operator *(Point2D a, double s) => new(a.X * s, a.Y * s);
}

public readonly record struct Point3D(double X, double Y, double Z)
{
    public double DistanceTo(Point3D o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    public static Point3D operator +(Point3D a, Point3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Point3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Point3D operator *(Point3D a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Point3D Normalized() { var l = Length; return l > 0 ? new(X / l, Y / l, Z / l) : this; }
    public static Point3D Cross(Point3D a, Point3D b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static double Dot(Point3D a, Point3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

/// <summary>
/// Affine transform from survey grid (inline, crossline) to map coordinates (X, Y):
/// X = X0 + A11*(il - il0) + A12*(xl - xl0); Y = Y0 + A21*(il - il0) + A22*(xl - xl0).
/// </summary>
public sealed record GridTransform(double Inline0, double Crossline0, double X0, double Y0, double A11, double A12, double A21, double A22)
{
    public static GridTransform Identity { get; } = new(0, 0, 0, 0, 1, 0, 0, 1);

    public Point2D ToMap(double inline, double crossline)
    {
        double di = inline - Inline0, dx = crossline - Crossline0;
        return new Point2D(X0 + A11 * di + A12 * dx, Y0 + A21 * di + A22 * dx);
    }

    public (double Inline, double Crossline) ToGrid(double x, double y)
    {
        double det = A11 * A22 - A12 * A21;
        if (Math.Abs(det) < 1e-15) return (double.NaN, double.NaN);
        double dx = x - X0, dy = y - Y0;
        double di = (A22 * dx - A12 * dy) / det;
        double dc = (-A21 * dx + A11 * dy) / det;
        return (Inline0 + di, Crossline0 + dc);
    }

    /// <summary>Inline bin spacing (map distance between consecutive inlines).</summary>
    public double InlineSpacing => Math.Sqrt(A11 * A11 + A21 * A21);
    public double CrosslineSpacing => Math.Sqrt(A12 * A12 + A22 * A22);

    /// <summary>
    /// Least-squares estimate of the transform from at least three non-collinear (il, xl, x, y) control points.
    /// </summary>
    public static GridTransform FromControlPoints(IReadOnlyList<(double Il, double Xl, double X, double Y)> pts)
    {
        if (pts.Count < 3) throw new ArgumentException("At least three control points are required.");
        double il0 = pts[0].Il, xl0 = pts[0].Xl;
        // Solve [1 di dc] * [c0 c1 c2]^T = X (and Y) by normal equations
        var ata = new double[3, 3];
        var atx = new double[3];
        var aty = new double[3];
        foreach (var p in pts)
        {
            double[] row = [1, p.Il - il0, p.Xl - xl0];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) ata[i, j] += row[i] * row[j];
                atx[i] += row[i] * p.X;
                aty[i] += row[i] * p.Y;
            }
        }
        var cx = Numerics.LinearSolvers.SolveDense(ata, atx);
        var cy = Numerics.LinearSolvers.SolveDense(ata, aty);
        return new GridTransform(il0, xl0, cx[0], cy[0], cx[1], cx[2], cy[1], cy[2]);
    }
}

public static class Polygon2D
{
    /// <summary>Even-odd ray casting point-in-polygon test.</summary>
    public static bool Contains(IReadOnlyList<Point2D> polygon, Point2D p)
    {
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double Area(IReadOnlyList<Point2D> polygon)
    {
        double s = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            s += (polygon[j].X + polygon[i].X) * (polygon[j].Y - polygon[i].Y);
        return Math.Abs(s) / 2;
    }

    /// <summary>Andrew monotone chain convex hull.</summary>
    public static List<Point2D> ConvexHull(IEnumerable<Point2D> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3) return pts;
        static double Cross(Point2D o, Point2D a, Point2D b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Point2D>();
        foreach (var p in pts)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--)
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }
}
