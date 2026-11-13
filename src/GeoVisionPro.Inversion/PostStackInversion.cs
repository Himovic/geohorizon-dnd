using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Inversion;

public enum InversionMethod { Recursive, BandLimited, ModelBased, SparseSpike }

/// <summary>Parameters controlling post-stack acoustic impedance inversion.</summary>
public sealed record InversionParameters
{
    public InversionMethod Method { get; init; } = InversionMethod.ModelBased;
    /// <summary>Scaler applied to seismic amplitudes so that they represent reflectivity × wavelet (from well tie / auto-estimate).</summary>
    public double SeismicScale { get; init; } = double.NaN;
    /// <summary>Weight of the low-frequency model in model-based inversion (larger = closer to model).</summary>
    public double ModelWeight { get; init; } = 0.05;
    /// <summary>Hard constraint: impedance limited to ±(percent) of the low-frequency model; 0 = unconstrained.</summary>
    public double ConstraintPercent { get; init; } = 30;
    public int Iterations { get; init; } = 40;
    /// <summary>Sparsity weight for sparse-spike inversion (relative to max |Wᵀs|).</summary>
    public double SparsityLambda { get; init; } = 0.1;
    /// <summary>Merge frequency (Hz) between model low frequencies and inverted band (recursive / band-limited / sparse-spike).</summary>
    public double MergeFrequency { get; init; } = 10;
    public double WindowStart { get; init; } = double.NaN;  // s
    public double WindowEnd { get; init; } = double.NaN;    // s

    public static IReadOnlyList<ParameterDescriptor> Descriptors { get; } =
    [
        Param.Choice("method", "Method", "ModelBased", Enum.GetNames<InversionMethod>()),
        Param.Num("scale", "Seismic scaler (NaN = auto)", double.NaN), Param.Num("modelWeight", "Model weight", 0.05, "", 0),
        Param.Num("constraint", "Hard constraint", 30, "%", 0, 100), Param.Int("iterations", "Iterations", 40, "", 1, 1000),
        Param.Num("lambda", "Sparsity", 0.1, "", 0, 10), Param.Num("merge", "Merge frequency", 10, "Hz", 0)
    ];
}

/// <summary>Result of inverting a single trace.</summary>
public sealed record TraceInversionResult(float[] Impedance, float[] Reflectivity, float[] SyntheticFit, double Correlation, double ResidualRms);

/// <summary>
/// Post-stack acoustic impedance inversion with a convolutional model s = W r, r_i ≈ ½ Δ ln Z_i (small-contrast approximation).
/// Methods: recursive (trace integration), band-limited (recursive + LF merge), model-based (regularised least squares on ln Z,
/// conjugate gradient, optional hard bounds) and sparse-spike (L1-regularised reflectivity by FISTA, LF merge).
/// </summary>
public sealed class PostStackInversion
{
    private readonly Wavelet _w;
    private readonly double _dt;

    public PostStackInversion(Wavelet wavelet, double dt)
    {
        _dt = dt;
        _w = Math.Abs(wavelet.SampleInterval - dt) > 1e-12 ? wavelet.Resample(dt) : wavelet;
    }

    public Wavelet Wavelet => _w;

    private float[] Convolve(ReadOnlySpan<float> r) => Dsp.ConvolveSame(r, _w.Samples, _w.CenterIndex);

    /// <summary>Adjoint of the centred convolution (correlation with the wavelet).</summary>
    private float[] Correlate(ReadOnlySpan<float> s)
    {
        var rev = _w.Samples.Reverse().ToArray();
        return Dsp.ConvolveSame(s, rev, _w.Length - 1 - _w.CenterIndex);
    }

    /// <summary>Reflectivity from ln Z: r_i = ½(L_i − L_{i−1}), r_0 = 0.</summary>
    public static float[] ReflectivityFromLogImpedance(ReadOnlySpan<double> l)
    {
        var r = new float[l.Length];
        for (int i = 1; i < l.Length; i++) r[i] = (float)(0.5 * (l[i] - l[i - 1]));
        return r;
    }

    /// <summary>Estimates the seismic scaler: least-squares ratio between the trace and the synthetic of the model.</summary>
    public double EstimateScale(ReadOnlySpan<float> seismic, ReadOnlySpan<float> modelImpedance)
    {
        var l = new double[modelImpedance.Length];
        for (int i = 0; i < l.Length; i++) l[i] = Math.Log(Math.Max(modelImpedance[i], 1e-6));
        var syn = Convolve(ReflectivityFromLogImpedance(l));
        double ss = 0, sm = 0;
        for (int i = 0; i < syn.Length; i++) { ss += seismic[i] * (double)syn[i]; sm += syn[i] * (double)syn[i]; }
        // the model is low-frequency so its synthetic is weak; use RMS ratio of band-limited content instead when tiny
        return sm > 0 && ss > 0 ? ss / sm : double.NaN;
    }

    /// <summary>Inverts one trace. <paramref name="model"/> is the low-frequency impedance model on the same samples.</summary>
    public TraceInversionResult InvertTrace(ReadOnlySpan<float> seismic, ReadOnlySpan<float> model, InversionParameters p, CancellationToken ct = default)
    {
        int n = seismic.Length;
        double scale = double.IsNaN(p.SeismicScale) || p.SeismicScale == 0 ? 1 : p.SeismicScale;
        var s = new float[n];
        for (int i = 0; i < n; i++) s[i] = float.IsNaN(seismic[i]) ? 0 : (float)(seismic[i] / scale);
        var l0 = new double[n];
        for (int i = 0; i < n; i++) l0[i] = Math.Log(Math.Max(model[i], 1e-6));
        double[] l = p.Method switch
        {
            InversionMethod.Recursive => Recursive(s, l0, mergeHz: 0),
            InversionMethod.BandLimited => Recursive(s, l0, p.MergeFrequency),
            InversionMethod.ModelBased => ModelBased(s, l0, p, ct),
            InversionMethod.SparseSpike => SparseSpike(s, l0, p, ct),
            _ => throw new NotSupportedException()
        };
        if (p.ConstraintPercent > 0)
        {
            double lim = Math.Log(1 + p.ConstraintPercent / 100);
            for (int i = 0; i < n; i++) l[i] = Math.Clamp(l[i], l0[i] - lim, l0[i] + lim);
        }
        var r = ReflectivityFromLogImpedance(l);
        var fit = Convolve(r);
        double corr = Dsp.Correlation(fit, s);
        double res = 0;
        for (int i = 0; i < n; i++) res += Math.Pow(fit[i] - s[i], 2);
        var z = l.Select(v => (float)Math.Exp(v)).ToArray();
        for (int i = 0; i < n; i++) fit[i] = (float)(fit[i] * scale);
        return new TraceInversionResult(z, r, fit, corr, Math.Sqrt(res / n) * scale);
    }

    /// <summary>
    /// Recursive inversion: the (deconvolved-by-assumption) trace is treated as reflectivity; ln Z = ln Z_0 + 2Σ r.
    /// When mergeHz &gt; 0 the integrated trace is high-passed and the model below mergeHz is added (band-limited inversion).
    /// </summary>
    private double[] Recursive(float[] s, double[] l0, double mergeHz)
    {
        int n = s.Length;
        // exact recursion Z_{i+1} = Z_i (1 + r)/(1 − r) in log form
        var integ = new float[n];
        double acc = 0;
        for (int i = 1; i < n; i++)
        {
            double r = Math.Clamp(s[i], -0.95, 0.95);
            acc += Math.Log((1 + r) / (1 - r));
            integ[i] = (float)acc;
        }
        var l = new double[n];
        if (mergeHz > 0)
        {
            FrequencyFilters.Butterworth(integ, _dt, mergeHz, 0, 4);
            var lowModel = l0.Select(v => (float)v).ToArray();
            FrequencyFilters.Butterworth(lowModel, _dt, 0, mergeHz, 4);
            for (int i = 0; i < n; i++) l[i] = lowModel[i] + integ[i];
        }
        else for (int i = 0; i < n; i++) l[i] = l0[0] + integ[i];
        return l;
    }

    /// <summary>
    /// Model-based inversion: minimise ||G L − s||² + μ||L − L0||² with G = W·D (D: ½ first difference). Normal equations
    /// (GᵀG + μI) L = Gᵀs + μL0 solved by conjugate gradients starting from L0. μ scales with ||G||² estimate.
    /// </summary>
    private double[] ModelBased(float[] s, double[] l0, InversionParameters p, CancellationToken ct)
    {
        int n = s.Length;
        double wEnergy = _w.Samples.Sum(v => v * (double)v);
        double mu = p.ModelWeight * wEnergy * 0.5;
        void G(double[] x, float[] outp)
        {
            var r = ReflectivityFromLogImpedance(x);
            Convolve(r).CopyTo(outp, 0);
        }
        void Gt(float[] y, double[] outp)
        {
            var c = Correlate(y);
            // Dᵀ of r_i = ½(x_i − x_{i−1}): (Dᵀc)_j = ½(c_j − c_{j+1})
            for (int j = 0; j < n; j++) outp[j] = 0.5 * ((j >= 1 ? c[j] : 0) - (j + 1 < n ? c[j + 1] : 0));
        }
        var tmp = new float[n];
        var tmp2 = new double[n];
        void ApplyA(double[] x, double[] y)
        {
            G(x, tmp);
            Gt(tmp, tmp2);
            for (int i = 0; i < n; i++) y[i] = tmp2[i] + mu * x[i];
        }
        var b = new double[n];
        Gt(s, b);
        for (int i = 0; i < n; i++) b[i] += mu * l0[i];
        var x0 = (double[])l0.Clone();
        LinearSolvers.ConjugateGradient(ApplyA, b, x0, p.Iterations, 1e-7, ct);
        return x0;
    }

    /// <summary>
    /// Sparse-spike inversion: minimise ½||W r − s||² + λ||r||₁ with FISTA (Beck &amp; Teboulle, 2009); ln Z from integrated
    /// reflectivity, with frequencies below the merge frequency replaced by the low-frequency model.
    /// </summary>
    private double[] SparseSpike(float[] s, double[] l0, InversionParameters p, CancellationToken ct)
    {
        int n = s.Length;
        // Lipschitz constant of WᵀW = max |W(f)|²
        var (_, amp) = Fft.AmplitudeSpectrum(_w.Samples, _dt, Fft.NextPowerOfTwo(Math.Max(n, _w.Length) * 2));
        double L = amp.Max(a => a * a);
        if (L <= 0) return l0;
        var wts = Correlate(s);
        double lambda = p.SparsityLambda * wts.Max(v => Math.Abs(v));
        var x = new float[n]; var y = new float[n]; var xOld = new float[n];
        double tk = 1;
        for (int it = 0; it < Math.Max(10, p.Iterations * 5); it++)
        {
            if ((it & 31) == 0) ct.ThrowIfCancellationRequested();
            var res = Convolve(y);
            for (int i = 0; i < n; i++) res[i] -= s[i];
            var grad = Correlate(res);
            Array.Copy(x, xOld, n);
            for (int i = 0; i < n; i++)
            {
                double v = y[i] - grad[i] / L;
                double th = lambda / L;
                x[i] = (float)(Math.Sign(v) * Math.Max(Math.Abs(v) - th, 0));
            }
            double tNext = (1 + Math.Sqrt(1 + 4 * tk * tk)) / 2;
            for (int i = 0; i < n; i++) y[i] = (float)(x[i] + (tk - 1) / tNext * (x[i] - xOld[i]));
            tk = tNext;
        }
        // de-bias amplitude: least-squares scale of the sparse reflectivity
        var fit = Convolve(x);
        double num = 0, den = 0;
        for (int i = 0; i < n; i++) { num += fit[i] * (double)s[i]; den += fit[i] * (double)fit[i]; }
        double sc = den > 0 ? num / den : 1;
        for (int i = 0; i < n; i++) x[i] = (float)(x[i] * sc);
        return Recursive(x, l0, p.MergeFrequency);
    }
}

/// <summary>QC metrics comparing an inverted impedance trace with a well impedance log over their overlap.</summary>
public sealed record InversionQc(double Correlation, double RmsError, double RelativeRmsError, double MeanBiasPercent, int Samples);

public static class InversionEngine
{
    /// <summary>
    /// Estimates a global seismic scaler from traces at well locations: ratio of seismic RMS to RMS of the synthetic
    /// computed from the (unfiltered) well impedance with the wavelet.
    /// </summary>
    public static double EstimateScaleFromWells(ISeismicVolume seismic, IReadOnlyList<WellTimeLog> wells, Wavelet wavelet)
    {
        var g = seismic.Geometry;
        var w = Math.Abs(wavelet.SampleInterval - g.SampleInterval) > 1e-12 ? wavelet.Resample(g.SampleInterval) : wavelet;
        double num = 0, den = 0;
        foreach (var wl in wells)
        {
            var nt = g.NearestTrace(wl.Location);
            if (nt is null) continue;
            var tr = seismic.ReadTrace(nt.Value.Inline, nt.Value.Crossline);
            var l = new double[g.SampleCount];
            for (int k = 0; k < l.Length; k++) l[k] = Math.Log(Math.Max(wl.ValueAt(g.TimeAt(k)), 1e-6));
            var syn = Dsp.ConvolveSame(PostStackInversion.ReflectivityFromLogImpedance(l), w.Samples, w.CenterIndex);
            int a = Math.Max(0, (int)Math.Ceiling(g.SampleAt(wl.FirstTime))), b = Math.Min(g.SampleCount - 1, (int)Math.Floor(g.SampleAt(wl.LastTime)));
            for (int k = a; k <= b; k++) { num += tr[k] * (double)syn[k]; den += syn[k] * (double)syn[k]; }
        }
        return den > 0 && num > 0 ? num / den : double.NaN;
    }

    public static void RunVolume(ISeismicVolume seismic, LowFrequencyModel model, Wavelet wavelet, InversionParameters p, IWritableSeismicVolume output,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = seismic.Geometry;
        var inv = new PostStackInversion(wavelet, g.SampleInterval);
        int done = 0;
        var lockObj = new object();
        Parallel.For(0, g.InlineCount, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            int il = g.Inlines.ValueAt(i);
            var section = seismic.ReadInline(il);
            var results = new float[g.CrosslineCount][];
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                int xl = g.Crosslines.ValueAt(j);
                var row = section.Row(j);
                if (Dsp.MaxAbs(row) == 0) continue;
                var lf = model.Trace(il, xl);
                results[j] = inv.InvertTrace(row, lf, p, ct).Impedance;
            }
            lock (lockObj)
            {
                for (int j = 0; j < g.CrosslineCount; j++) if (results[j] is { } r) output.WriteTrace(il, g.Crosslines.ValueAt(j), r);
                done++;
                progress?.Report(ProgressInfo.Of(done, g.InlineCount, $"Inverted inline {il}"));
            }
        });
        output.Flush();
    }

    /// <summary>Compares inversion and well impedance (both in time) over the well's time range.</summary>
    public static InversionQc CompareWithWell(float[] inverted, double startTime, double dt, WellTimeLog well, double highCutHz = 0)
    {
        var wellOnGrid = new float[inverted.Length];
        for (int k = 0; k < inverted.Length; k++)
        {
            double t = startTime + k * dt;
            wellOnGrid[k] = t >= well.FirstTime && t <= well.LastTime ? (float)well.ValueAt(t) : float.NaN;
        }
        if (highCutHz > 0)
        {
            var tmp = wellOnGrid.Select(v => float.IsNaN(v) ? 0f : v).ToArray();
            FrequencyFilters.Butterworth(tmp, dt, 0, highCutHz, 4);
            for (int k = 0; k < tmp.Length; k++) if (!float.IsNaN(wellOnGrid[k])) wellOnGrid[k] = tmp[k];
        }
        double se = 0, sw = 0, bias = 0; int n = 0;
        var a = new List<float>(); var b = new List<float>();
        for (int k = 0; k < inverted.Length; k++)
        {
            if (float.IsNaN(wellOnGrid[k]) || float.IsNaN(inverted[k])) continue;
            double d = inverted[k] - wellOnGrid[k];
            se += d * d; sw += wellOnGrid[k] * (double)wellOnGrid[k]; bias += d / wellOnGrid[k]; n++;
            a.Add(inverted[k]); b.Add(wellOnGrid[k]);
        }
        if (n < 2) return new InversionQc(double.NaN, double.NaN, double.NaN, double.NaN, n);
        return new InversionQc(Dsp.Correlation(a.ToArray(), b.ToArray()), Math.Sqrt(se / n), Math.Sqrt(se / sw), 100 * bias / n, n);
    }
}
