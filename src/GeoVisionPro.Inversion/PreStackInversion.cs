using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.Inversion;

/// <summary>Elastic properties estimated by simultaneous inversion (all are estimates, not measurements).</summary>
public sealed record ElasticInversionResult(float[] Ip, float[] Is, float[] Density, float[] VpVs, float[] LambdaRho, float[] MuRho, float[] PoissonsRatio,
    double[] AngleCorrelations, string Notes);

/// <summary>
/// Simultaneous pre-stack inversion (Hampson, Russell &amp; Bankhead, 2005) of an NMO-corrected angle gather.
/// Unknowns: L_p = ln Ip and deviations ΔL_s, ΔL_d from background linear trends ln Is = k ln Ip + k_c, ln ρ = m ln Ip + m_c
/// (fitted to the low-frequency models). For each angle θ the Fatti equation gives
/// T(θ) = ½c1 W D L_p + ½c2 W D L_s + c3 W D L_d with c1 = (1 + tan²θ)/… (see SCIENTIFIC_ALGORITHMS.md).
/// The regularised least-squares system is solved by conjugate gradients (matrix-free).
/// </summary>
public sealed class SimultaneousInversion
{
    private readonly double[] _angles;
    private readonly Wavelet[] _wavelets;
    private readonly double _dt;

    public SimultaneousInversion(double[] anglesDeg, IReadOnlyList<Wavelet> waveletsPerAngle, double dt)
    {
        if (anglesDeg.Length != waveletsPerAngle.Count) throw new ArgumentException("One wavelet per angle is required.");
        _angles = anglesDeg; _dt = dt;
        _wavelets = waveletsPerAngle.Select(w => Math.Abs(w.SampleInterval - dt) > 1e-12 ? w.Resample(dt) : w).ToArray();
    }

    /// <param name="gather">[angle, sample] NMO-corrected, scaled to reflectivity units.</param>
    /// <param name="ipModel">Background P-impedance, <paramref name="isModel"/> S-impedance, <paramref name="rhoModel"/> density (same samples).</param>
    public ElasticInversionResult Invert(Array2D gather, float[] ipModel, float[] isModel, float[] rhoModel, double vsVpBackground = 0.5,
        double modelWeight = 0.05, int iterations = 60, CancellationToken ct = default)
    {
        int n = gather.Cols, na = _angles.Length;
        var lp0 = ipModel.Select(v => Math.Log(Math.Max(v, 1e-6))).ToArray();
        var ls0 = isModel.Select(v => Math.Log(Math.Max(v, 1e-6))).ToArray();
        var ld0 = rhoModel.Select(v => Math.Log(Math.Max(v, 1e-6))).ToArray();
        var fitS = Statistics.LinearRegression(lp0, ls0);
        var fitD = Statistics.LinearRegression(lp0, ld0);
        double k = double.IsNaN(fitS.Slope) ? 1 : fitS.Slope, kc = double.IsNaN(fitS.Intercept) ? ls0.Average() - lp0.Average() : fitS.Intercept;
        double m = double.IsNaN(fitD.Slope) ? 0.25 : fitD.Slope, mc = double.IsNaN(fitD.Intercept) ? ld0.Average() - 0.25 * lp0.Average() : fitD.Intercept;
        var dls0 = ls0.Select((v, i) => v - (k * lp0[i] + kc)).ToArray();
        var dld0 = ld0.Select((v, i) => v - (m * lp0[i] + mc)).ToArray();
        double K = vsVpBackground * vsVpBackground;

        // Fatti coefficients for derivatives of ln Ip, ln Is, ln rho (reflectivity = ½ Δ ln)
        var c1 = new double[na]; var c2 = new double[na]; var c3 = new double[na];
        for (int a = 0; a < na; a++)
        {
            double t = _angles[a] * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t), t2 = Math.Tan(t) * Math.Tan(t);
            c1[a] = 0.5 * (1 + t2);
            c2[a] = -4 * K * s2;
            c3[a] = -(0.5 * t2 - 2 * K * s2);
        }
        // effective coefficients in terms of (Lp, ΔLs, ΔLd): ln Is = k Lp + ΔLs ; ln ρ = m Lp + ΔLd
        var cp = new double[na];
        for (int a = 0; a < na; a++) cp[a] = c1[a] + k * c2[a] + m * c3[a]; // multiplies D Lp (D = Δ, not ½Δ, folded below)

        int N = 3 * n;
        float[] D(ReadOnlySpan<double> x, int off) { var r = new float[n]; for (int i = 1; i < n; i++) r[i] = (float)(x[off + i] - x[off + i - 1]); return r; }
        void Dt(ReadOnlySpan<float> c, double[] outp, int off, double scale)
        {
            for (int j = 0; j < n; j++) outp[off + j] += scale * ((j >= 1 ? c[j] : 0) - (j + 1 < n ? c[j + 1] : 0));
        }
        float[] Conv(Wavelet w, float[] r) => Dsp.ConvolveSame(r, w.Samples, w.CenterIndex);
        float[] Corr(Wavelet w, float[] y) => Dsp.ConvolveSame(y, w.Samples.Reverse().ToArray(), w.Length - 1 - w.CenterIndex);

        // forward operator for angle a: d_a = W_a (cp D Lp + c2 D ΔLs + c3 D ΔLd)   (½ factor included in c's)
        float[] Forward(double[] x, int a)
        {
            var dp = D(x, 0); var ds = D(x, n); var dd = D(x, 2 * n);
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = (float)(cp[a] * dp[i] + c2[a] * ds[i] + c3[a] * dd[i]);
            return Conv(_wavelets[a], r);
        }
        void Adjoint(float[] y, int a, double[] outp)
        {
            var c = Corr(_wavelets[a], y);
            Dt(c, outp, 0, cp[a]); Dt(c, outp, n, c2[a]); Dt(c, outp, 2 * n, c3[a]);
        }
        double wE = _wavelets.Average(w => w.Samples.Sum(v => v * (double)v));
        double mu = modelWeight * wE;
        var x0 = new double[N];
        Array.Copy(lp0, 0, x0, 0, n); Array.Copy(dls0, 0, x0, n, n); Array.Copy(dld0, 0, x0, 2 * n, n);
        var xm = (double[])x0.Clone();
        void ApplyA(double[] x, double[] y)
        {
            Array.Clear(y);
            for (int a = 0; a < na; a++) Adjoint(Forward(x, a), a, y);
            // stronger damping on ΔLs and ΔLd keeps them close to the trends (as in Hampson et al.)
            for (int i = 0; i < n; i++) { y[i] += mu * x[i]; y[n + i] += 4 * mu * x[n + i]; y[2 * n + i] += 8 * mu * x[2 * n + i]; }
        }
        var b = new double[N];
        for (int a = 0; a < na; a++)
        {
            var row = gather.Row(a).ToArray();
            for (int i = 0; i < n; i++) if (float.IsNaN(row[i])) row[i] = 0;
            Adjoint(row, a, b);
        }
        for (int i = 0; i < n; i++) { b[i] += mu * xm[i]; b[n + i] += 4 * mu * xm[n + i]; b[2 * n + i] += 8 * mu * xm[2 * n + i]; }
        LinearSolvers.ConjugateGradient(ApplyA, b, x0, iterations, 1e-8, ct);

        var ip = new float[n]; var isv = new float[n]; var rho = new float[n];
        var vpvs = new float[n]; var lr = new float[n]; var mr = new float[n]; var pr = new float[n];
        for (int i = 0; i < n; i++)
        {
            double lp = x0[i];
            ip[i] = (float)Math.Exp(lp);
            isv[i] = (float)Math.Exp(k * lp + kc + x0[n + i]);
            rho[i] = (float)Math.Exp(m * lp + mc + x0[2 * n + i]);
            double r = ip[i] / isv[i];
            vpvs[i] = (float)r;
            lr[i] = (float)((ip[i] * (double)ip[i] - 2 * isv[i] * (double)isv[i]) * 1e-6);
            mr[i] = (float)(isv[i] * (double)isv[i] * 1e-6);
            pr[i] = (float)((r * r - 2) / (2 * (r * r - 1)));
        }
        var corrs = new double[na];
        for (int a = 0; a < na; a++) corrs[a] = Dsp.Correlation(Forward(x0, a), gather.Row(a));
        return new ElasticInversionResult(ip, isv, rho, vpvs, lr, mr, pr, corrs,
            $"Background trends: ln Is = {k:0.###} ln Ip + {kc:0.###}; ln ρ = {m:0.###} ln Ip + {mc:0.###}. Density is poorly constrained for maximum angles below ~35°.");
    }
}

/// <summary>Elastic impedance (Connolly, 1999) and normalised EI (Whitcombe, 2002).</summary>
public static class ElasticImpedance
{
    /// <summary>EI(θ) = Vp^(1+tan²θ) · Vs^(−8K sin²θ) · ρ^(1−4K sin²θ), K = (Vs/Vp)² (constant background).</summary>
    public static double Compute(double vp, double vs, double rho, double angleDeg, double k)
    {
        double t = angleDeg * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t), t2 = Math.Tan(t) * Math.Tan(t);
        return Math.Pow(vp, 1 + t2) * Math.Pow(vs, -8 * k * s2) * Math.Pow(rho, 1 - 4 * k * s2);
    }

    /// <summary>Normalised EI = Vp0 ρ0 [(Vp/Vp0)^a (Vs/Vs0)^b (ρ/ρ0)^c] — same units and scale as AI at θ = 0.</summary>
    public static double Normalized(double vp, double vs, double rho, double angleDeg, double k, double vp0, double vs0, double rho0)
    {
        double t = angleDeg * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t), t2 = Math.Tan(t) * Math.Tan(t);
        return vp0 * rho0 * Math.Pow(vp / vp0, 1 + t2) * Math.Pow(vs / vs0, -8 * k * s2) * Math.Pow(rho / rho0, 1 - 4 * k * s2);
    }
}
