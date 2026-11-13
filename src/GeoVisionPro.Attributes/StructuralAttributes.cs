using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;

namespace GeoVisionPro.Attributes;

public abstract class NeighborhoodAttribute : ISeismicAttribute
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual string Category => "Structural";
    public virtual string Unit => "";
    public abstract string Description { get; }
    public AttributeScope Scope => AttributeScope.Neighborhood;
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract void ComputeNeighborhood(float[]?[,] t, Span<float> output, ParameterSet p, double dt, double dxInline, double dxCrossline);

    protected static int HalfWindow(ParameterSet p, double dt, string key = "window", double def = 24) =>
        Math.Max(1, (int)Math.Round(p.GetDouble(key, def) / 1000 / dt / 2));

    protected static IEnumerable<float[]> Live(float[]?[,] t)
    {
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) if (t[a, b] is { } tr) yield return tr;
    }
}

/// <summary>Semblance-based coherence (Marfurt et al., 1998) over a 3×3 trace aperture without dip steering.</summary>
public sealed class CoherenceAttribute : NeighborhoodAttribute
{
    public override string Id => "coherence";
    public override string DisplayName => "Coherence (semblance)";
    public override string Description => "Σ_t(Σ_traces a)² / (N Σ_t Σ_traces a²) in a vertical window; 1 = laterally continuous, low values = discontinuities (faults, channels).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "Vertical window", 24, "ms", 2)];
    public override void ComputeNeighborhood(float[]?[,] t, Span<float> y, ParameterSet p, double dt, double dxi, double dxc)
    {
        var traces = Live(t).ToArray();
        int n = y.Length, h = HalfWindow(p, dt), m = traces.Length;
        var num = new double[n]; var den = new double[n];
        for (int k = 0; k < n; k++)
        {
            double s = 0, s2 = 0;
            foreach (var tr in traces) { double v = float.IsNaN(tr[k]) ? 0 : tr[k]; s += v; s2 += v * v; }
            num[k] = s * s; den[k] = s2;
        }
        for (int k = 0; k < n; k++)
        {
            double sn = 0, sd = 0;
            for (int w = Math.Max(0, k - h); w <= Math.Min(n - 1, k + h); w++) { sn += num[w]; sd += den[w]; }
            y[k] = sd > 0 && m > 1 ? (float)(sn / (m * sd)) : 1f;
        }
    }
}

/// <summary>Normalised variance (Van Bemmel &amp; Pepper, 2000) — essentially one minus a variance-based coherence.</summary>
public sealed class VarianceAttribute : NeighborhoodAttribute
{
    public override string Id => "variance";
    public override string DisplayName => "Variance (edge)";
    public override string Description => "Σ_t Σ_traces (a − ā)² / Σ_t Σ_traces a² in a vertical window; high values mark discontinuities.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "Vertical window", 24, "ms", 2)];
    public override void ComputeNeighborhood(float[]?[,] t, Span<float> y, ParameterSet p, double dt, double dxi, double dxc)
    {
        var traces = Live(t).ToArray();
        int n = y.Length, h = HalfWindow(p, dt), m = traces.Length;
        var num = new double[n]; var den = new double[n];
        for (int k = 0; k < n; k++)
        {
            double s = 0, s2 = 0;
            foreach (var tr in traces) { double v = float.IsNaN(tr[k]) ? 0 : tr[k]; s += v; s2 += v * v; }
            double mean = s / Math.Max(1, m);
            num[k] = s2 - m * mean * mean; den[k] = s2;
        }
        for (int k = 0; k < n; k++)
        {
            double sn = 0, sd = 0;
            for (int w = Math.Max(0, k - h); w <= Math.Min(n - 1, k + h); w++) { sn += num[w]; sd += den[w]; }
            y[k] = sd > 0 ? (float)(sn / sd) : 0f;
        }
    }
}

public enum DipOutput { InlineDip, CrosslineDip, DipMagnitude, Azimuth }

/// <summary>
/// Gradient structure tensor (GST) dip estimation: for a locally planar event a(x,y,t) = f(t − p_x x − p_y y),
/// p = −⟨∇_x a · ∂_t a⟩ / ⟨(∂_t a)²⟩ averaged over a vertical window (least-squares slope).
/// </summary>
public sealed class DipAzimuthAttribute : NeighborhoodAttribute
{
    public override string Id => "dip";
    public override string DisplayName => "Dip / Azimuth (structure tensor)";
    public override string Unit => "ms/m | deg";
    public override string Description => "Apparent time dips along inline and crossline (ms per m), total dip magnitude (ms/m) or dip azimuth (deg, clockwise from the inline axis).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [Param.Num("window", "Vertical window", 24, "ms", 2), Param.Choice("output", "Output", "DipMagnitude", Enum.GetNames<DipOutput>())];

    public override void ComputeNeighborhood(float[]?[,] t, Span<float> y, ParameterSet p, double dt, double dxi, double dxc)
    {
        var c = t[1, 1];
        int n = y.Length;
        if (c is null) { y.Clear(); return; }
        var output = Enum.Parse<DipOutput>(p.GetString("output", "DipMagnitude"));
        int h = HalfWindow(p, dt);
        var gxgt = new double[n]; var gygt = new double[n]; var gtgt = new double[n];
        for (int k = 0; k < n; k++)
        {
            double gx = Grad(t[2, 1], t[0, 1], c, k) / dxi;
            double gy = Grad(t[1, 2], t[1, 0], c, k) / dxc;
            double gt = (c[Math.Min(n - 1, k + 1)] - c[Math.Max(0, k - 1)]) / (dt * (Math.Min(n - 1, k + 1) - Math.Max(0, k - 1)));
            gxgt[k] = gx * gt; gygt[k] = gy * gt; gtgt[k] = gt * gt;
        }
        for (int k = 0; k < n; k++)
        {
            double a = 0, b = 0, d = 0;
            for (int w = Math.Max(0, k - h); w <= Math.Min(n - 1, k + h); w++) { a += gxgt[w]; b += gygt[w]; d += gtgt[w]; }
            double px = d > 0 ? -a / d : 0, py = d > 0 ? -b / d : 0; // s/m
            y[k] = output switch
            {
                DipOutput.InlineDip => (float)(px * 1000),
                DipOutput.CrosslineDip => (float)(py * 1000),
                DipOutput.DipMagnitude => (float)(Math.Sqrt(px * px + py * py) * 1000),
                _ => (float)((Math.Atan2(py, px) * 180 / Math.PI + 360) % 360)
            };
        }
    }

    internal static double Grad(float[]? plus, float[]? minus, float[] centre, int k)
    {
        if (plus is not null && minus is not null) return (plus[k] - minus[k]) / 2.0;
        if (plus is not null) return plus[k] - centre[k];
        if (minus is not null) return centre[k] - minus[k];
        return 0;
    }
}

/// <summary>
/// Chaos (Randen et al., 2000) from the eigenvalues λ1 ≥ λ2 ≥ λ3 of the smoothed 3D gradient structure tensor:
/// chaos = 2λ2/(λ1 + λ3) − 1. High values indicate chaotic reflection patterns.
/// </summary>
public sealed class ChaosAttribute : NeighborhoodAttribute
{
    public override string Id => "chaos";
    public override string DisplayName => "Chaos";
    public override string Category => "Advanced";
    public override string Description => "Measure of local disorder of the gradient orientation field (−1..1).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "Vertical window", 32, "ms", 2)];
    public override void ComputeNeighborhood(float[]?[,] t, Span<float> y, ParameterSet p, double dt, double dxi, double dxc)
    {
        var c = t[1, 1];
        int n = y.Length;
        if (c is null) { y.Clear(); return; }
        int h = HalfWindow(p, dt, def: 32);
        // gradients in sample/trace units (dimensionless orientation)
        var T = new double[n, 6];
        for (int k = 0; k < n; k++)
        {
            double gx = DipAzimuthAttribute.Grad(t[2, 1], t[0, 1], c, k);
            double gy = DipAzimuthAttribute.Grad(t[1, 2], t[1, 0], c, k);
            double gt = (c[Math.Min(n - 1, k + 1)] - c[Math.Max(0, k - 1)]) / 2.0;
            T[k, 0] = gx * gx; T[k, 1] = gy * gy; T[k, 2] = gt * gt; T[k, 3] = gx * gy; T[k, 4] = gx * gt; T[k, 5] = gy * gt;
        }
        var s = new double[6];
        for (int k = 0; k < n; k++)
        {
            Array.Clear(s);
            for (int w = Math.Max(0, k - h); w <= Math.Min(n - 1, k + h); w++) for (int q = 0; q < 6; q++) s[q] += T[w, q];
            var (l1, l2, l3) = SymmetricEigenvalues3(s[0], s[1], s[2], s[3], s[4], s[5]);
            y[k] = l1 + l3 > 1e-30 ? (float)(2 * l2 / (l1 + l3) - 1) : 0f;
        }
    }

    /// <summary>Eigenvalues (descending) of a symmetric 3×3 matrix [[a,d,e],[d,b,f],[e,f,c]] by the trigonometric method.</summary>
    public static (double, double, double) SymmetricEigenvalues3(double a, double b, double c, double d, double e, double f)
    {
        double p1 = d * d + e * e + f * f;
        if (p1 < 1e-300)
        {
            var arr = new[] { a, b, c }.OrderByDescending(v => v).ToArray();
            return (arr[0], arr[1], arr[2]);
        }
        double q = (a + b + c) / 3;
        double p2 = (a - q) * (a - q) + (b - q) * (b - q) + (c - q) * (c - q) + 2 * p1;
        double p = Math.Sqrt(p2 / 6);
        double b11 = (a - q) / p, b22 = (b - q) / p, b33 = (c - q) / p, b12 = d / p, b13 = e / p, b23 = f / p;
        double detB = b11 * (b22 * b33 - b23 * b23) - b12 * (b12 * b33 - b23 * b13) + b13 * (b12 * b23 - b22 * b13);
        double r = Math.Clamp(detB / 2, -1, 1);
        double phi = Math.Acos(r) / 3;
        double l1 = q + 2 * p * Math.Cos(phi);
        double l3 = q + 2 * p * Math.Cos(phi + 2 * Math.PI / 3);
        double l2 = 3 * q - l1 - l3;
        return (l1, l2, l3);
    }
}

/// <summary>Sobel lateral amplitude gradient magnitude (edge detection on constant-time planes).</summary>
public sealed class EdgeDetectionAttribute : NeighborhoodAttribute
{
    public override string Id => "edge";
    public override string DisplayName => "Edge Detection (Sobel)";
    public override string Description => "Magnitude of the 3×3 Sobel gradient of amplitude on constant-time planes, normalised by local RMS.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "RMS normalisation window", 40, "ms", 2)];
    public override void ComputeNeighborhood(float[]?[,] t, Span<float> y, ParameterSet p, double dt, double dxi, double dxc)
    {
        int n = y.Length;
        var c = t[1, 1];
        if (c is null) { y.Clear(); return; }
        float V(int a, int b, int k) => t[a, b] is { } tr ? tr[k] : c[k];
        int h = HalfWindow(p, dt, def: 40);
        var cum = new double[n + 1];
        for (int k = 0; k < n; k++) cum[k + 1] = cum[k] + c[k] * (double)c[k];
        for (int k = 0; k < n; k++)
        {
            double gx = (V(2, 0, k) + 2 * V(2, 1, k) + V(2, 2, k)) - (V(0, 0, k) + 2 * V(0, 1, k) + V(0, 2, k));
            double gy = (V(0, 2, k) + 2 * V(1, 2, k) + V(2, 2, k)) - (V(0, 0, k) + 2 * V(1, 0, k) + V(2, 0, k));
            int a = Math.Max(0, k - h), b = Math.Min(n - 1, k + h);
            double rms = Math.Sqrt((cum[b + 1] - cum[a]) / (b - a + 1));
            y[k] = rms > 0 ? (float)(Math.Sqrt(gx * gx + gy * gy) / (8 * rms)) : 0f;
        }
    }
}
