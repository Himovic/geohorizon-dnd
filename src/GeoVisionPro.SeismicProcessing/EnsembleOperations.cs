using System.Numerics;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.SeismicProcessing;

internal static class ComplexLinear
{
    /// <summary>Gaussian elimination with partial pivoting for small dense complex systems.</summary>
    public static Complex[] Solve(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var x = (Complex[])b.Clone();
        for (int k = 0; k < n; k++)
        {
            int piv = k;
            for (int i = k + 1; i < n; i++) if (m[i, k].Magnitude > m[piv, k].Magnitude) piv = i;
            if (m[piv, k].Magnitude < 1e-300) return new Complex[n];
            if (piv != k)
            {
                for (int j = 0; j < n; j++) (m[k, j], m[piv, j]) = (m[piv, j], m[k, j]);
                (x[k], x[piv]) = (x[piv], x[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                var f = m[i, k] / m[k, k];
                if (f == Complex.Zero) continue;
                for (int j = k; j < n; j++) m[i, j] -= f * m[k, j];
                x[i] -= f * x[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            var s = x[i];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}

/// <summary>F-K (frequency-wavenumber) dip/velocity filter on a 2D section.</summary>
public sealed class FkFilterOperation : EnsembleOperation
{
    public override string Id => "fk";
    public override string DisplayName => "F-K Velocity Filter";
    public override string Category => "Noise Attenuation";
    public override string Description => "2D FFT fan filter: rejects (or passes) events with apparent velocity |f/k| below the cut-off, with a cosine taper. Requires regular trace spacing.";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("vcut", "Apparent velocity cut-off", 1500, "m/s", 1), P.Num("taper", "Taper width", 20, "%", 0, 100),
        P.Choice("mode", "Mode", "RejectSlow", ["RejectSlow", "PassSlow"])
    ];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        int ntr = data.Rows, ns = data.Cols;
        if (ntr < 4) return;
        int nk = Fft.NextPowerOfTwo(ntr * 2), nf = Fft.NextPowerOfTwo(ns * 2);
        var m = new float[ntr, ns];
        for (int r = 0; r < ntr; r++) for (int c = 0; c < ns; c++) m[r, c] = data[r, c];
        var spec = Fft.Forward2D(m, nk, nf);
        double vcut = p.GetDouble("vcut", 1500), taper = p.GetDouble("taper", 20) / 100;
        bool rejectSlow = p.GetString("mode", "RejectSlow") == "RejectSlow";
        double v1 = vcut * (1 - taper), v2 = vcut;
        for (int ik = 0; ik < nk; ik++)
        {
            double k = Math.Abs(Fft.BinFrequency(ik, nk, ctx.TraceSpacing));
            for (int jf = 0; jf < nf; jf++)
            {
                double f = Math.Abs(Fft.BinFrequency(jf, nf, ctx.SampleInterval));
                double va = k == 0 ? double.PositiveInfinity : f / k;
                double wFast = va >= v2 ? 1 : va <= v1 ? 0 : 0.5 - 0.5 * Math.Cos(Math.PI * (va - v1) / Math.Max(1e-9, v2 - v1));
                double w = rejectSlow ? wFast : 1 - wFast;
                spec[ik, jf] *= w;
            }
        }
        var outp = Fft.Inverse2DReal(spec, ntr, ns);
        for (int r = 0; r < ntr; r++) for (int c = 0; c < ns; c++) data[r, c] = outp[r, c];
    }
}

/// <summary>
/// F-X prediction filtering (Canales, 1984) for random noise attenuation: in overlapping time windows, each
/// frequency slice is predicted across traces by a complex Wiener filter (forward and backward, averaged).
/// </summary>
public sealed class FxDeconvolutionOperation : EnsembleOperation
{
    public override string Id => "fxdecon";
    public override string DisplayName => "F-X Deconvolution (random noise)";
    public override string Category => "Noise Attenuation";
    public override string Description => "Attenuates spatially random noise while preserving laterally predictable (linear within window) events.";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Int("filter", "Filter length", 4, "traces", 1, 20), P.Num("window", "Time window", 400, "ms", 20),
        P.Num("fmin", "Min frequency", 2, "Hz", 0), P.Num("fmax", "Max frequency", 90, "Hz", 1), P.Num("mu", "Damping", 1, "%", 0, 100)
    ];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        int ntr = data.Rows, ns = data.Cols;
        int L = p.GetInt("filter", 4);
        if (ntr < 2 * L + 2) return;
        int win = Math.Clamp((int)Math.Round(p.GetDouble("window", 400) / 1000 / ctx.SampleInterval), 8, ns);
        int hop = Math.Max(1, win / 2);
        var taper = Dsp.Hann(win);
        var output = new double[ntr * ns];
        var weight = new double[ns];
        int nfft = Fft.NextPowerOfTwo(win);
        double df = 1.0 / (nfft * ctx.SampleInterval);
        int kmin = Math.Max(1, (int)(p.GetDouble("fmin", 2) / df)), kmax = Math.Min(nfft / 2, (int)(p.GetDouble("fmax", 90) / df));
        double mu = p.GetDouble("mu", 1) / 100;
        for (int start = 0; start < ns; start += hop)
        {
            int len = Math.Min(win, ns - start);
            var specs = new Complex[ntr][];
            for (int r = 0; r < ntr; r++)
            {
                var c = new Complex[nfft];
                var row = data.Row(r);
                for (int i = 0; i < len; i++) c[i] = row[start + i] * taper[i];
                Fft.ForwardInPlace(c);
                specs[r] = c;
            }
            var s = new Complex[ntr];
            for (int k = kmin; k <= kmax; k++)
            {
                for (int r = 0; r < ntr; r++) s[r] = specs[r][k];
                var pred = PredictBothWays(s, L, mu);
                for (int r = 0; r < ntr; r++)
                {
                    specs[r][k] = pred[r];
                    if (k > 0 && k < nfft / 2) specs[r][nfft - k] = Complex.Conjugate(pred[r]);
                }
            }
            for (int k = 0; k <= nfft / 2; k++)
            {
                if (k >= kmin && k <= kmax) continue;
                for (int r = 0; r < ntr; r++) { specs[r][k] = Complex.Zero; if (k > 0 && k < nfft / 2) specs[r][nfft - k] = Complex.Zero; }
            }
            for (int r = 0; r < ntr; r++)
            {
                Fft.InverseInPlace(specs[r]);
                for (int i = 0; i < len; i++) output[r * ns + start + i] += specs[r][i].Real * taper[i];
            }
            for (int i = 0; i < len; i++) weight[start + i] += taper[i] * taper[i];
            if (start + win >= ns) break;
        }
        for (int r = 0; r < ntr; r++)
        {
            var row = data.Row(r);
            for (int i = 0; i < ns; i++) row[i] = weight[i] > 1e-6 ? (float)(output[r * ns + i] / weight[i]) : row[i];
        }
    }

    private static Complex[] PredictBothWays(Complex[] s, int L, double mu)
    {
        int n = s.Length;
        var fwd = Predict(s, L, mu);
        var rev = s.Reverse().ToArray();
        var bwd = Predict(rev, L, mu).Reverse().ToArray();
        var res = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            bool hasF = i >= L, hasB = i < n - L;
            res[i] = hasF && hasB ? 0.5 * (fwd[i] + bwd[i]) : hasF ? fwd[i] : hasB ? bwd[i] : s[i];
        }
        return res;
    }

    /// <summary>Least squares forward prediction s[i] ≈ Σ_l a_l s[i-l-1].</summary>
    private static Complex[] Predict(Complex[] s, int L, double mu)
    {
        int n = s.Length;
        var ata = new Complex[L, L];
        var atb = new Complex[L];
        for (int i = L; i < n; i++)
        {
            for (int a = 0; a < L; a++)
            {
                var ca = Complex.Conjugate(s[i - a - 1]);
                atb[a] += ca * s[i];
                for (int b = 0; b < L; b++) ata[a, b] += ca * s[i - b - 1];
            }
        }
        double tr = 0;
        for (int a = 0; a < L; a++) tr += ata[a, a].Real;
        for (int a = 0; a < L; a++) ata[a, a] += mu * tr / L + 1e-30;
        var f = ComplexLinear.Solve(ata, atb);
        var outp = (Complex[])s.Clone();
        for (int i = L; i < n; i++)
        {
            Complex v = Complex.Zero;
            for (int l = 0; l < L; l++) v += f[l] * s[i - l - 1];
            outp[i] = v;
        }
        return outp;
    }
}

/// <summary>Weighted running average across adjacent traces (structure-agnostic lateral smoothing).</summary>
public sealed class LateralMixOperation : EnsembleOperation
{
    public override string Id => "mix";
    public override string DisplayName => "Trace Mix (lateral smoothing)";
    public override string Category => "Noise Attenuation";
    public override string Description => "Triangular-weighted running average over neighbouring traces. Smears dipping events; use small widths.";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [P.Int("width", "Mix width", 3, "traces", 1, 51)];
    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        int h = p.GetInt("width", 3) / 2;
        if (h < 1) return;
        var src = data.Clone();
        for (int r = 0; r < data.Rows; r++)
        {
            var row = data.Row(r);
            for (int c = 0; c < data.Cols; c++)
            {
                double s = 0, w = 0;
                for (int d = -h; d <= h; d++)
                {
                    int rr = r + d;
                    if (rr < 0 || rr >= data.Rows) continue;
                    double wt = h + 1 - Math.Abs(d);
                    s += wt * src[rr, c]; w += wt;
                }
                row[c] = (float)(s / w);
            }
        }
    }
}

/// <summary>Trace editing: kills listed traces and/or traces whose RMS is an outlier relative to the section median.</summary>
public sealed class TraceEditOperation : EnsembleOperation
{
    public override string Id => "traceedit";
    public override string DisplayName => "Trace Editing / Killing";
    public override string Category => "Basic";
    public override string Description => "Zeroes traces listed as 'inline:crossline' pairs (crossline index = trace position for gathers) and traces with RMS above k × median RMS or containing NaN/Inf.";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [P.Text("kill", "Traces to kill (il:xl;...)", ""), P.Num("rmsFactor", "Auto-kill RMS factor (0 = off)", 0, "× median", 0)];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var kills = new HashSet<int>();
        foreach (var pair in p.GetString("kill").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split(':');
            if (kv.Length == 2 && int.TryParse(kv[0], out var il) && int.TryParse(kv[1], out var xlIndex) && il == ctx.Inline) kills.Add(xlIndex);
        }
        double factor = p.GetDouble("rmsFactor", 0);
        var rms = Enumerable.Range(0, data.Rows).Select(r => (double)Dsp.Rms(data.Row(r))).ToArray();
        double median = Statistics.Percentile(rms.Where(v => v > 0), 50);
        for (int r = 0; r < data.Rows; r++)
        {
            var row = data.Row(r);
            bool bad = false;
            foreach (var v in row) if (!float.IsFinite(v)) { bad = true; break; }
            if (kills.Contains(r) || bad || (factor > 0 && median > 0 && rms[r] > factor * median)) row.Clear();
        }
    }
}

/// <summary>Hyperbolic normal-moveout correction of CMP gathers.</summary>
public sealed class NmoCorrectionOperation : EnsembleOperation
{
    public override string Id => "nmo";
    public override string DisplayName => "NMO Correction";
    public override string Category => "Pre-stack";
    public override string Description => "Hyperbolic NMO with RMS velocity function v(t) and stretch mute. Inverse NMO restores moveout.";
    public override OperationScope Scope => OperationScope.Gather;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Text("velocity", "RMS velocity t(s):v(m/s)", "0:1500, 1.0:2200, 2.0:3000"), P.Num("stretch", "Stretch mute", 50, "%", 0, 1000),
        P.Bool("inverse", "Inverse NMO", false)
    ];
    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var offsets = ctx.Offsets ?? throw new InvalidOperationException("NMO requires gather offsets.");
        Nmo.Correct(data, offsets, VelocityFunction.Parse(p.GetString("velocity")), ctx.SampleInterval, ctx.StartTime,
            p.GetDouble("stretch", 50) / 100, p.GetBool("inverse"));
    }
}

public sealed class OffsetMuteOperation : EnsembleOperation
{
    public override string Id => "offsetmute";
    public override string DisplayName => "Offset-dependent Mute";
    public override string Category => "Pre-stack";
    public override string Description => "Linear first-break mute t_mute(x) = t0 + |x| / v with cosine taper.";
    public override OperationScope Scope => OperationScope.Gather;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [P.Num("t0", "Intercept time", 0, "ms", 0), P.Num("velocity", "Mute velocity", 1800, "m/s", 1), P.Num("taper", "Taper", 20, "ms", 0)];
    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var offsets = ctx.Offsets ?? throw new InvalidOperationException("Offset mute requires gather offsets.");
        double t0 = p.GetDouble("t0") / 1000, v = p.GetDouble("velocity", 1800), taper = p.GetDouble("taper") / 1000;
        for (int r = 0; r < data.Rows; r++)
        {
            double tm = t0 + Math.Abs(offsets[r]) / v;
            var row = data.Row(r);
            for (int k = 0; k < data.Cols; k++)
            {
                double t = ctx.StartTime + k * ctx.SampleInterval;
                double w = t < tm ? 0 : t < tm + taper ? 0.5 - 0.5 * Math.Cos(Math.PI * (t - tm) / Math.Max(taper, 1e-9)) : 1;
                row[k] = (float)(row[k] * w);
            }
        }
    }
}

/// <summary>
/// Parabolic Radon multiple attenuation (Hampson, 1986): least-squares frequency-domain transform
/// d(x,ω) = Σ_q m(q,ω) exp(-iωq(x/x_ref)²); events with residual moveout q &gt; q_cut (multiples, after NMO) are modelled and subtracted.
/// </summary>
public sealed class ParabolicRadonOperation : EnsembleOperation
{
    public override string Id => "radon";
    public override string DisplayName => "Parabolic Radon Demultiple";
    public override string Category => "Pre-stack";
    public override string Description => "Apply after NMO correction. Models residual parabolic moveout and subtracts the multiple region (q > q_cut).";
    public override OperationScope Scope => OperationScope.Gather;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("qmin", "Min moveout at far offset", -50, "ms"), P.Num("qmax", "Max moveout at far offset", 400, "ms"),
        P.Int("nq", "Number of q values", 80, "", 4, 400), P.Num("qcut", "Multiple cut-off", 30, "ms"),
        P.Num("damping", "Damping", 1, "%", 0, 100), P.Num("fmax", "Max frequency", 90, "Hz", 1)
    ];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var offsets = ctx.Offsets ?? throw new InvalidOperationException("Radon requires gather offsets.");
        var mult = ModelMultiples(data, offsets, p, ctx);
        for (int i = 0; i < data.Data.Length; i++) data.Data[i] -= mult.Data[i];
    }

    public static Array2D ModelMultiples(Array2D data, double[] offsets, ParameterSet p, ProcessingContext ctx)
    {
        int nx = data.Rows, ns = data.Cols;
        double xref = offsets.Max(o => Math.Abs(o));
        if (xref <= 0) return new Array2D(nx, ns);
        double qmin = p.GetDouble("qmin", -50) / 1000, qmax = p.GetDouble("qmax", 400) / 1000, qcut = p.GetDouble("qcut", 30) / 1000;
        int nq = p.GetInt("nq", 80);
        var q = Enumerable.Range(0, nq).Select(i => qmin + i * (qmax - qmin) / Math.Max(1, nq - 1)).ToArray();
        double damp = p.GetDouble("damping", 1) / 100;
        int nfft = Fft.NextPowerOfTwo(ns * 2);
        var spec = new Complex[nx][];
        for (int r = 0; r < nx; r++) spec[r] = Fft.Forward(data.Row(r), nfft);
        var outSpec = new Complex[nx][];
        for (int r = 0; r < nx; r++) outSpec[r] = new Complex[nfft];
        double df = 1.0 / (nfft * ctx.SampleInterval);
        int kmax = Math.Min(nfft / 2, (int)(p.GetDouble("fmax", 90) / df));
        var x2 = offsets.Select(o => (o / xref) * (o / xref)).ToArray();
        Parallel.For(1, kmax + 1, k =>
        {
            double w = 2 * Math.PI * k * df;
            var Lm = new Complex[nx, nq];
            for (int ix = 0; ix < nx; ix++)
                for (int iq = 0; iq < nq; iq++)
                    Lm[ix, iq] = Complex.FromPolarCoordinates(1, -w * q[iq] * x2[ix]);
            var lhl = new Complex[nq, nq];
            var lhd = new Complex[nq];
            for (int a = 0; a < nq; a++)
            {
                for (int ix = 0; ix < nx; ix++) lhd[a] += Complex.Conjugate(Lm[ix, a]) * spec[ix][k];
                for (int b = a; b < nq; b++)
                {
                    Complex s = Complex.Zero;
                    for (int ix = 0; ix < nx; ix++) s += Complex.Conjugate(Lm[ix, a]) * Lm[ix, b];
                    lhl[a, b] = s; lhl[b, a] = Complex.Conjugate(s);
                }
            }
            double tr = 0;
            for (int a = 0; a < nq; a++) tr += lhl[a, a].Real;
            for (int a = 0; a < nq; a++) lhl[a, a] += damp * tr / nq + 1e-12;
            var m = ComplexLinear.Solve(lhl, lhd);
            for (int ix = 0; ix < nx; ix++)
            {
                Complex s = Complex.Zero;
                for (int iq = 0; iq < nq; iq++) if (q[iq] > qcut) s += Lm[ix, iq] * m[iq];
                outSpec[ix][k] = s;
                if (k < nfft / 2) outSpec[ix][nfft - k] = Complex.Conjugate(s);
            }
        });
        var result = new Array2D(nx, ns);
        for (int ix = 0; ix < nx; ix++) Fft.InverseReal(outSpec[ix], ns).CopyTo(result.Row(ix));
        return result;
    }
}
