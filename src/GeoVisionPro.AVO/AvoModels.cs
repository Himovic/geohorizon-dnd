using System.Numerics;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.AVO;

/// <summary>Elastic layer: Vp, Vs (m/s), density (g/cc).</summary>
public readonly record struct Layer(double Vp, double Vs, double Rho);

public enum AvoClass { ClassI, ClassII, ClassIIp, ClassIII, ClassIV, Undefined }

/// <summary>Reflection coefficient equations for PP reflections at a planar interface between two isotropic elastic half-spaces.</summary>
public static class Reflectivity
{
    /// <summary>
    /// Exact P-P reflection coefficient from the Zoeppritz equations (Aki &amp; Richards, 1980, eq. 5.39 form) for an incident
    /// P-wave at angle θ (degrees) — returns the complex coefficient (complex beyond the critical angle).
    /// </summary>
    public static Complex ZoeppritzPP(Layer upper, Layer lower, double angleDeg)
    {
        double a1 = upper.Vp, b1 = upper.Vs, r1 = upper.Rho, a2 = lower.Vp, b2 = lower.Vs, r2 = lower.Rho;
        double th1 = angleDeg * Math.PI / 180;
        double p = Math.Sin(th1) / a1;
        Complex Cos(double v) => Complex.Sqrt(1 - (p * v) * (p * v));
        Complex ci1 = Cos(a1), ci2 = Cos(a2), cj1 = Cos(b1), cj2 = Cos(b2);
        Complex a = r2 * (1 - 2 * b2 * b2 * p * p) - r1 * (1 - 2 * b1 * b1 * p * p);
        Complex b = r2 * (1 - 2 * b2 * b2 * p * p) + 2 * r1 * b1 * b1 * p * p;
        Complex c = r1 * (1 - 2 * b1 * b1 * p * p) + 2 * r2 * b2 * b2 * p * p;
        Complex d = 2 * (r2 * b2 * b2 - r1 * b1 * b1);
        Complex E = b * ci1 / a1 + c * ci2 / a2;
        Complex F = b * cj1 / b1 + c * cj2 / b2;
        Complex G = a - d * ci1 / a1 * cj2 / b2;
        Complex H = a - d * ci2 / a2 * cj1 / b1;
        Complex D = E * F + G * H * p * p;
        Complex rpp = ((b * ci1 / a1 - c * ci2 / a2) * F - (a + d * ci1 / a1 * cj2 / b2) * H * p * p) / D;
        return rpp;
    }

    /// <summary>
    /// Aki–Richards linearised PP coefficient: R(θ) = ½(1 − 4p²β²)Δρ/ρ + Δα/(2cos²θ α) − 4p²β² Δβ/β, with averages across the interface.
    /// </summary>
    public static double AkiRichards(Layer u, Layer l, double angleDeg)
    {
        double vp = (u.Vp + l.Vp) / 2, vs = (u.Vs + l.Vs) / 2, rho = (u.Rho + l.Rho) / 2;
        double th1 = angleDeg * Math.PI / 180;
        double sinT2 = l.Vp / u.Vp * Math.Sin(th1);
        double th = sinT2 < 1 ? (th1 + Math.Asin(sinT2)) / 2 : th1;
        double p = Math.Sin(th1) / u.Vp;
        double dvp = l.Vp - u.Vp, dvs = l.Vs - u.Vs, drho = l.Rho - u.Rho;
        return 0.5 * (1 - 4 * p * p * vs * vs) * drho / rho + dvp / (2 * Math.Cos(th) * Math.Cos(th) * vp) - 4 * p * p * vs * vs * dvs / vs;
    }

    /// <summary>Shuey (1985) three-term: R(θ) = A + B sin²θ + C (tan²θ − sin²θ), with A, B, C from Aki–Richards.</summary>
    public static (double A, double B, double C) ShueyTerms(Layer u, Layer l)
    {
        double vp = (u.Vp + l.Vp) / 2, vs = (u.Vs + l.Vs) / 2, rho = (u.Rho + l.Rho) / 2;
        double dvp = l.Vp - u.Vp, dvs = l.Vs - u.Vs, drho = l.Rho - u.Rho;
        double A = 0.5 * (dvp / vp + drho / rho);
        double B = 0.5 * dvp / vp - 2 * (vs / vp) * (vs / vp) * (drho / rho + 2 * dvs / vs);
        double C = 0.5 * dvp / vp;
        return (A, B, C);
    }

    public static double Shuey(Layer u, Layer l, double angleDeg, bool threeTerm = true)
    {
        var (A, B, C) = ShueyTerms(u, l);
        double t = angleDeg * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t);
        return A + B * s2 + (threeTerm ? C * (Math.Tan(t) * Math.Tan(t) - s2) : 0);
    }

    /// <summary>Fatti et al. (1994): R(θ) = (1+tan²θ) ΔIp/2Ip − 8K sin²θ ΔIs/2Is − (tan²θ/2 − 2K sin²θ) Δρ/ρ, K = (Vs/Vp)².</summary>
    public static double Fatti(Layer u, Layer l, double angleDeg)
    {
        double ipU = u.Vp * u.Rho, ipL = l.Vp * l.Rho, isU = u.Vs * u.Rho, isL = l.Vs * l.Rho;
        double ip = (ipU + ipL) / 2, isv = (isU + isL) / 2, rho = (u.Rho + l.Rho) / 2;
        double k = Math.Pow((u.Vs + l.Vs) / (u.Vp + l.Vp), 2);
        double t = angleDeg * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t), t2 = Math.Tan(t) * Math.Tan(t);
        return (1 + t2) * (ipL - ipU) / (2 * ip) - 8 * k * s2 * (isL - isU) / (2 * isv) - (t2 / 2 - 2 * k * s2) * (l.Rho - u.Rho) / rho;
    }

    public static double CriticalAngle(Layer u, Layer l) => l.Vp > u.Vp ? Math.Asin(u.Vp / l.Vp) * 180 / Math.PI : double.NaN;
}

/// <summary>Result of intercept/gradient regression of amplitude versus sin²θ.</summary>
public sealed record InterceptGradient(double Intercept, double Gradient, double Curvature, double Correlation, int Count)
{
    /// <summary>Product A·B (positive in class III gas sands with negative A and B).</summary>
    public double Product => Intercept * Gradient;
    /// <summary>Approximate Poisson reflectivity A + B (Δσ indicator, Verm &amp; Hilterman).</summary>
    public double PoissonReflectivity => Intercept + Gradient;
}

public static class AvoAnalysis
{
    /// <summary>
    /// Least-squares fit of R(θ) = A + B sin²θ (two-term) or + C(tan²θ − sin²θ) (three-term) to amplitudes at angles (deg).
    /// </summary>
    public static InterceptGradient FitInterceptGradient(IReadOnlyList<double> anglesDeg, IReadOnlyList<double> amplitudes, bool threeTerm = false)
    {
        var rows = new List<double[]>(); var y = new List<double>();
        for (int i = 0; i < anglesDeg.Count; i++)
        {
            if (double.IsNaN(amplitudes[i])) continue;
            double t = anglesDeg[i] * Math.PI / 180, s2 = Math.Sin(t) * Math.Sin(t);
            rows.Add(threeTerm ? [1, s2, Math.Tan(t) * Math.Tan(t) - s2] : [1, s2]);
            y.Add(amplitudes[i]);
        }
        int m = threeTerm ? 3 : 2;
        if (rows.Count < m) return new InterceptGradient(double.NaN, double.NaN, double.NaN, double.NaN, rows.Count);
        var ata = new double[m, m]; var aty = new double[m];
        for (int r = 0; r < rows.Count; r++)
            for (int i = 0; i < m; i++)
            {
                aty[i] += rows[r][i] * y[r];
                for (int j = 0; j < m; j++) ata[i, j] += rows[r][i] * rows[r][j];
            }
        double[] c;
        try { c = LinearSolvers.SolveDense(ata, aty); } catch { return new InterceptGradient(double.NaN, double.NaN, double.NaN, double.NaN, rows.Count); }
        var pred = rows.Select(r => r.Select((v, i) => v * c[i]).Sum()).ToList();
        double corr = Statistics.PearsonCorrelation(pred, y);
        return new InterceptGradient(c[0], c[1], threeTerm ? c[2] : 0, corr, rows.Count);
    }

    /// <summary>
    /// Rutherford &amp; Williams (1989) classes extended by Castagna (1998), for the top of a sand: I (A &gt; 0.02, high impedance),
    /// II (|A| ≤ 0.02), IIp (II with polarity reversal: small positive A, negative B), III (A &lt; −0.02, B &lt; 0), IV (A &lt; −0.02, B &gt; 0).
    /// </summary>
    public static AvoClass Classify(double intercept, double gradient, double nearZero = 0.02)
    {
        if (double.IsNaN(intercept) || double.IsNaN(gradient)) return AvoClass.Undefined;
        if (intercept > nearZero) return AvoClass.ClassI;
        if (Math.Abs(intercept) <= nearZero) return intercept > 0 && gradient < 0 && intercept + gradient < 0 ? AvoClass.ClassIIp : AvoClass.ClassII;
        return gradient <= 0 ? AvoClass.ClassIII : AvoClass.ClassIV;
    }

    /// <summary>
    /// Smith &amp; Gidlow (1987) fluid factor ΔF = ΔVp/Vp − 1.16 (Vs/Vp) ΔVs/Vs from intercept/gradient via the mudrock line,
    /// computed here directly from the two-term approximation: F = A·w1 + B·w2 with background Vs/Vp.
    /// </summary>
    public static double FluidFactor(double intercept, double gradient, double vsOverVp = 0.5)
    {
        // With Gardner density (Δρ/ρ = ¼ ΔVp/Vp): A = 5/8 ΔVp/Vp, B = ½ΔVp/Vp − 4k(ΔVs/Vs) − ½k ΔVp/Vp (k = (Vs/Vp)²)
        double k = vsOverVp * vsOverVp;
        double dvpvp = intercept * 8 / 5;
        double dvsvs = (0.5 * dvpvp - 2 * k * 0.25 * dvpvp - gradient) / (4 * k);
        return dvpvp - 1.16 * vsOverVp * dvsvs;
    }

    /// <summary>
    /// Incidence angle (deg) at offset x and zero-offset time t0 for a straight-ray approximation through an RMS velocity:
    /// sin θ = (V_int / V_rms²) · x / sqrt(t0² + x²/V_rms²) (Walden, 1991).
    /// </summary>
    public static double OffsetToAngle(double offset, double t0, double vrms, double vint)
    {
        double tx = Math.Sqrt(t0 * t0 + offset * offset / (vrms * vrms));
        if (tx <= 0) return 0;
        double s = vint / (vrms * vrms) * offset / tx;
        return s >= 1 ? 90 : Math.Asin(s) * 180 / Math.PI;
    }

    /// <summary>Amplitude picked along a time across the traces of an (NMO-corrected) gather, with optional window maximum.</summary>
    public static double[] PickGatherAmplitudes(Array2D gather, double time, double dt, double t0, int halfWindowSamples = 0, bool peak = true)
    {
        var res = new double[gather.Rows];
        double idx = (time - t0) / dt;
        for (int r = 0; r < gather.Rows; r++)
        {
            var tr = gather.Row(r);
            if (halfWindowSamples <= 0) { res[r] = Dsp.SampleSinc(tr, idx); continue; }
            int c = (int)Math.Round(idx);
            double best = double.NaN;
            for (int k = Math.Max(0, c - halfWindowSamples); k <= Math.Min(tr.Length - 1, c + halfWindowSamples); k++)
                if (double.IsNaN(best) || (peak ? tr[k] > best : tr[k] < best)) best = tr[k];
            res[r] = best;
        }
        return res;
    }

    /// <summary>
    /// Computes intercept and gradient traces for an NMO-corrected angle gather (per-sample least squares), producing A and B arrays.
    /// </summary>
    public static (float[] A, float[] B) InterceptGradientTraces(Array2D angleGather, double[] anglesDeg)
    {
        int ns = angleGather.Cols;
        var a = new float[ns]; var b = new float[ns];
        var s2 = anglesDeg.Select(t => Math.Pow(Math.Sin(t * Math.PI / 180), 2)).ToArray();
        for (int k = 0; k < ns; k++)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
            for (int r = 0; r < angleGather.Rows; r++)
            {
                float v = angleGather[r, k];
                if (float.IsNaN(v) || v == 0) continue;
                sx += s2[r]; sy += v; sxx += s2[r] * s2[r]; sxy += s2[r] * v; n++;
            }
            if (n < 2) continue;
            double den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-15) continue;
            double slope = (n * sxy - sx * sy) / den;
            b[k] = (float)slope; a[k] = (float)((sy - slope * sx) / n);
        }
        return (a, b);
    }
}

/// <summary>Synthetic AVO gather modelling from a layered (blocky) earth model.</summary>
public static class AvoModeling
{
    public enum Method { Zoeppritz, AkiRichards, Shuey3, Shuey2, Fatti }

    /// <summary>
    /// Builds an angle gather [angle, sample]: reflectivity at each interface computed with the selected method and
    /// convolved with the wavelet. Interface times (s) give the position of each boundary between consecutive layers.
    /// </summary>
    public static Array2D AngleGather(IReadOnlyList<Layer> layers, IReadOnlyList<double> interfaceTimes, double[] anglesDeg, Wavelet wavelet,
        int samples, double dt, double t0, Method method = Method.Zoeppritz)
    {
        if (interfaceTimes.Count != layers.Count - 1) throw new ArgumentException("Need one interface time per layer boundary.");
        var w = Math.Abs(wavelet.SampleInterval - dt) > 1e-12 ? wavelet.Resample(dt) : wavelet;
        var g = new Array2D(anglesDeg.Length, samples);
        for (int a = 0; a < anglesDeg.Length; a++)
        {
            var rc = new float[samples];
            for (int i = 0; i < interfaceTimes.Count; i++)
            {
                double r = Coefficient(layers[i], layers[i + 1], anglesDeg[a], method);
                double idx = (interfaceTimes[i] - t0) / dt;
                int k = (int)Math.Floor(idx);
                double frac = idx - k;
                if (k >= 0 && k < samples) rc[k] += (float)(r * (1 - frac));
                if (k + 1 >= 0 && k + 1 < samples) rc[k + 1] += (float)(r * frac);
            }
            Dsp.ConvolveSame(rc, w.Samples, w.CenterIndex).CopyTo(g.Row(a));
        }
        return g;
    }

    public static double Coefficient(Layer u, Layer l, double angle, Method method) => method switch
    {
        Method.Zoeppritz => Reflectivity.ZoeppritzPP(u, l, angle).Real,
        Method.AkiRichards => Reflectivity.AkiRichards(u, l, angle),
        Method.Shuey3 => Reflectivity.Shuey(u, l, angle, true),
        Method.Shuey2 => Reflectivity.Shuey(u, l, angle, false),
        _ => Reflectivity.Fatti(u, l, angle)
    };

    /// <summary>R(θ) curves for a single interface for plotting.</summary>
    public static double[] Curve(Layer u, Layer l, IReadOnlyList<double> angles, Method method) => angles.Select(a => Coefficient(u, l, a, method)).ToArray();
}
