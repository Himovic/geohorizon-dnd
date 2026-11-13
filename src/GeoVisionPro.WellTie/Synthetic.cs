using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.WellTie;

/// <summary>Time-domain elastic logs at seismic sampling.</summary>
public sealed record TimeLogs(double StartTime, double SampleInterval, float[] Velocity, float[] Density, float[] Impedance)
{
    public int Count => Impedance.Length;
    public double TimeAt(int i) => StartTime + i * SampleInterval;
}

public sealed record SyntheticResult(TimeLogs Logs, float[] Reflectivity, float[] Synthetic, Wavelet Wavelet)
{
    public double StartTime => Logs.StartTime;
    public double SampleInterval => Logs.SampleInterval;
}

public static class SyntheticSeismogram
{
    /// <summary>
    /// Converts sonic and density logs to two-way time at sampling <paramref name="dt"/> using the time-depth model.
    /// Each output sample is the arithmetic average of all log samples whose time falls in [t - dt/2, t + dt/2)
    /// (anti-alias blocking); samples without log data are linearly interpolated, edges are trimmed.
    /// </summary>
    public static TimeLogs ToTime(LogCurve sonic, LogCurve density, TimeDepthModel tdr, WellTrajectory traj, double dt)
    {
        string su = string.IsNullOrEmpty(sonic.Unit) ? "us/ft" : sonic.Unit;
        var times = new List<double>(); var vel = new List<double>(); var rho = new List<double>();
        for (int i = 0; i < sonic.Count; i++)
        {
            double md = sonic.Depths[i];
            double v = UnitConverter.SlownessToVelocity(sonic.Values[i], su);
            double r = density.ValueAtDepth(md);
            if (density.Unit.Equals("kg/m3", StringComparison.OrdinalIgnoreCase)) r /= 1000;
            if (double.IsNaN(v) || double.IsNaN(r) || r <= 0) continue;
            times.Add(tdr.TimeAtMd(md, traj)); vel.Add(v); rho.Add(r);
        }
        if (times.Count < 4) throw new InvalidOperationException("Sonic and density logs have too few overlapping valid samples.");
        double t0 = Math.Ceiling(times.Min() / dt) * dt, t1 = Math.Floor(times.Max() / dt) * dt;
        int n = (int)Math.Round((t1 - t0) / dt) + 1;
        var sv = new double[n]; var sr = new double[n]; var cnt = new int[n];
        for (int k = 0; k < times.Count; k++)
        {
            int idx = (int)Math.Round((times[k] - t0) / dt);
            if (idx < 0 || idx >= n) continue;
            // average slowness (time-consistent) rather than velocity
            sv[idx] += 1 / vel[k]; sr[idx] += rho[k]; cnt[idx]++;
        }
        var V = new float[n]; var R = new float[n];
        for (int i = 0; i < n; i++)
        {
            V[i] = cnt[i] > 0 ? (float)(cnt[i] / sv[i]) : float.NaN;
            R[i] = cnt[i] > 0 ? (float)(sr[i] / cnt[i]) : float.NaN;
        }
        Dsp.FillGapsLinear(V); Dsp.FillGapsLinear(R);
        var ai = V.Select((v, i) => v * R[i]).ToArray();
        return new TimeLogs(t0, dt, V, R, ai);
    }

    /// <summary>Normal-incidence reflection coefficients r_i = (Z_{i+1} − Z_i)/(Z_{i+1} + Z_i), placed at sample i+1 (top of lower layer).</summary>
    public static float[] Reflectivity(ReadOnlySpan<float> impedance)
    {
        var r = new float[impedance.Length];
        for (int i = 0; i + 1 < impedance.Length; i++)
        {
            double a = impedance[i], b = impedance[i + 1];
            r[i + 1] = a + b > 0 && !double.IsNaN(a) && !double.IsNaN(b) ? (float)((b - a) / (b + a)) : 0f;
        }
        return r;
    }

    /// <summary>
    /// Convolutional synthetic: s = r * w (wavelet centred), SEG normal polarity unless reversed. The coefficient stored at
    /// sample i+1 belongs to the interface between blocked samples i and i+1, i.e. at time t_i + Δt/2; the synthetic is
    /// therefore advanced by half a sample (band-limited sinc interpolation) so events are not biased Δt/2 late.
    /// </summary>
    public static SyntheticResult Generate(TimeLogs logs, Wavelet wavelet, bool reversePolarity = false)
    {
        var w = Math.Abs(wavelet.SampleInterval - logs.SampleInterval) > 1e-9 ? wavelet.Resample(logs.SampleInterval) : wavelet;
        var rc = Reflectivity(logs.Impedance);
        var raw = Dsp.ConvolveSame(rc, w.Samples, w.CenterIndex);
        var syn = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++) { var v = Dsp.SampleSinc(raw, i + 0.5); syn[i] = float.IsNaN(v) ? raw[i] : v; }
        if (reversePolarity) for (int i = 0; i < syn.Length; i++) syn[i] = -syn[i];
        return new SyntheticResult(logs, rc, syn, w);
    }

    /// <summary>
    /// Extracts the seismic trace along the borehole: for each output time, the trace at the borehole location at that
    /// time (from the trajectory and TDR) is sampled; optionally averages a (2r+1)² neighbourhood of traces.
    /// </summary>
    public static float[] ExtractTraceAlongWell(ISeismicVolume volume, WellTrajectory traj, TimeDepthModel tdr, double t0, double dt, int n, int radius = 0)
    {
        var g = volume.Geometry;
        var result = new float[n];
        var cache = new Dictionary<(int, int), float[]>();
        float[] Trace(int il, int xl)
        {
            if (!cache.TryGetValue((il, xl), out var tr)) { tr = new float[g.SampleCount]; volume.TryReadTrace(il, xl, tr); cache[(il, xl)] = tr; }
            return tr;
        }
        for (int k = 0; k < n; k++)
        {
            double t = t0 + k * dt;
            double md = tdr.MdAtTime(t, traj);
            var loc = traj.LocationAt(md);
            var (fi, fx) = g.Transform.ToGrid(loc.X, loc.Y);
            int ci = (int)Math.Round((fi - g.Inlines.First) / g.Inlines.Step), cj = (int)Math.Round((fx - g.Crosslines.First) / g.Crosslines.Step);
            double s = 0; int m = 0;
            for (int di = -radius; di <= radius; di++)
                for (int dj = -radius; dj <= radius; dj++)
                {
                    int ii = ci + di, jj = cj + dj;
                    if (ii < 0 || jj < 0 || ii >= g.InlineCount || jj >= g.CrosslineCount) continue;
                    var tr = Trace(g.Inlines.ValueAt(ii), g.Crosslines.ValueAt(jj));
                    float a = Dsp.SampleSinc(tr, g.SampleAt(t));
                    if (!float.IsNaN(a)) { s += a; m++; }
                }
            result[k] = m > 0 ? (float)(s / m) : float.NaN;
        }
        return result;
    }
}

/// <summary>Wavelet estimation methods.</summary>
public static class WaveletEstimation
{
    /// <summary>
    /// Statistical wavelet: amplitude spectrum from the average autocorrelation of the traces within a window
    /// (white-reflectivity assumption), with a user-defined constant phase. Hann-tapered, unit peak.
    /// </summary>
    public static Wavelet Statistical(IReadOnlyList<float[]> traces, double dt, int windowStart, int windowEnd, double length, double phaseDegrees = 0)
    {
        int half = (int)Math.Round(length / 2 / dt);
        var acc = new double[half + 1];
        int used = 0;
        foreach (var tr in traces)
        {
            int a = Math.Clamp(windowStart, 0, tr.Length - 1), b = Math.Clamp(windowEnd, a + 1, tr.Length - 1);
            var seg = tr.AsSpan(a, b - a + 1).ToArray();
            for (int i = 0; i < seg.Length; i++) if (float.IsNaN(seg[i])) seg[i] = 0;
            var taper = Dsp.Hann(seg.Length);
            for (int i = 0; i < seg.Length; i++) seg[i] *= taper[i];
            var r = Dsp.AutoCorrelate(seg, half);
            if (r[0] <= 0) continue;
            for (int i = 0; i <= half; i++) acc[i] += r[i] / r[0];
            used++;
        }
        if (used == 0) throw new InvalidOperationException("No live traces in the wavelet extraction window.");
        // symmetric autocorrelation -> power spectrum -> amplitude spectrum (zero phase)
        int nfft = Fft.NextPowerOfTwo(4 * half + 2);
        var c = new System.Numerics.Complex[nfft];
        var tap = Dsp.Hann(2 * half + 1);
        for (int i = 0; i <= half; i++)
        {
            double v = acc[i] / used * tap[half + i];
            c[i] = v;
            if (i > 0) c[nfft - i] = v;
        }
        Fft.ForwardInPlace(c);
        for (int k = 0; k < nfft; k++) c[k] = Math.Sqrt(Math.Max(0, c[k].Real));
        Fft.InverseInPlace(c);
        var s = new float[2 * half + 1];
        for (int i = -half; i <= half; i++) s[i + half] = (float)(c[(i + nfft) % nfft].Real * tap[i + half]);
        var w = new Wavelet("Statistical", s, dt, half).NormalizedToPeak();
        if (phaseDegrees != 0) w = w.RotatePhase(phaseDegrees).NormalizedToPeak();
        w.Name = $"Statistical ({phaseDegrees:0}°)";
        return w;
    }

    /// <summary>
    /// Deterministic (well-based) least-squares wavelet: minimises ||s − r * w||² + λ||w||² for a wavelet of the given
    /// length centred on t = 0. The normal equations are Toeplitz (reflectivity autocorrelation) and solved by Levinson
    /// recursion. Returns the wavelet (with estimated amplitude and phase) and the prediction correlation.
    /// </summary>
    public static (Wavelet Wavelet, double Correlation) LeastSquares(ReadOnlySpan<float> reflectivity, ReadOnlySpan<float> seismic, double dt, double length, double damping = 0.01)
    {
        int half = (int)Math.Round(length / 2 / dt);
        int L = 2 * half + 1;
        int n = Math.Min(reflectivity.Length, seismic.Length);
        var r = reflectivity[..n].ToArray(); var s = seismic[..n].ToArray();
        for (int i = 0; i < n; i++) { if (float.IsNaN(r[i])) r[i] = 0; if (float.IsNaN(s[i])) s[i] = 0; }
        var ac = Dsp.AutoCorrelate(r, L);
        ac[0] *= 1 + damping;
        // s(t) = Σ_j w_j r(t - j + half): cross-correlation g_j = Σ_t s(t) r(t - j + half)
        var g = new double[L];
        for (int j = 0; j < L; j++)
        {
            double sum = 0;
            for (int t = 0; t < n; t++)
            {
                int k = t - j + half;
                if (k >= 0 && k < n) sum += s[t] * (double)r[k];
            }
            g[j] = sum;
        }
        var w = Dsp.LevinsonSolve(ac, g).Select(v => (float)v).ToArray();
        var taper = Dsp.Hann(L);
        // gentle taper on outer quarter only
        for (int i = 0; i < L; i++)
        {
            double rel = Math.Abs(i - half) / (double)Math.Max(1, half);
            if (rel > 0.75) w[i] *= (float)(0.5 + 0.5 * Math.Cos(Math.PI * (rel - 0.75) / 0.25));
        }
        _ = taper;
        var pred = Dsp.ConvolveSame(r, w, half);
        double corr = Dsp.Correlation(pred, s);
        return (new Wavelet("Least-squares (well)", w, dt, half), corr);
    }
}
