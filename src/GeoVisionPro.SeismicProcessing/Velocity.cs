using System.Globalization;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.SeismicProcessing;

/// <summary>
/// A 1D velocity function v(t) defined by (two-way time [s], velocity [m/s]) knots with linear interpolation
/// and constant extrapolation. Text format: "t1:v1, t2:v2, ..." (times in seconds).
/// </summary>
public sealed class VelocityFunction
{
    public double[] Times { get; }
    public double[] Velocities { get; }

    public VelocityFunction(double[] times, double[] velocities)
    {
        if (times.Length == 0 || times.Length != velocities.Length) throw new ArgumentException("Velocity function needs matching, non-empty knots.");
        var order = Enumerable.Range(0, times.Length).OrderBy(i => times[i]).ToArray();
        Times = order.Select(i => times[i]).ToArray();
        Velocities = order.Select(i => velocities[i]).ToArray();
        if (Velocities.Any(v => v <= 0)) throw new ArgumentException("Velocities must be positive.");
    }

    public static VelocityFunction Constant(double v) => new([0], [v]);

    public static VelocityFunction Parse(string text)
    {
        var t = new List<double>(); var v = new List<double>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split(':');
            if (kv.Length != 2) throw new FormatException($"Invalid velocity knot '{part}', expected time:velocity.");
            t.Add(double.Parse(kv[0], CultureInfo.InvariantCulture));
            v.Add(double.Parse(kv[1], CultureInfo.InvariantCulture));
        }
        return new VelocityFunction(t.ToArray(), v.ToArray());
    }

    public override string ToString() => string.Join(", ", Times.Zip(Velocities, (a, b) => $"{a.ToString("0.###", CultureInfo.InvariantCulture)}:{b.ToString("0", CultureInfo.InvariantCulture)}"));

    public double At(double t) => Times.Length == 1 ? Velocities[0] : Dsp.Interp(Times, Velocities, t, clamp: true);

    public double[] Sample(int n, double dt, double t0)
    {
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = At(t0 + i * dt);
        return r;
    }

    /// <summary>
    /// Dix conversion of RMS velocities (sampled at regular dt) to interval velocities:
    /// v_int,n² = (t_n v_n² - t_{n-1} v_{n-1}²)/(t_n - t_{n-1}). Non-physical values are clamped to the RMS value.
    /// </summary>
    public static double[] DixIntervalFromRms(double[] vrms, double dt, double t0)
    {
        int n = vrms.Length;
        var vint = new double[n];
        if (n == 0) return vint;
        vint[0] = vrms[0];
        for (int i = 1; i < n; i++)
        {
            double t1 = t0 + (i - 1) * dt, t2 = t0 + i * dt;
            double num = t2 * vrms[i] * vrms[i] - t1 * vrms[i - 1] * vrms[i - 1];
            double v2 = num / (t2 - t1);
            vint[i] = v2 > 0 ? Math.Sqrt(v2) : vrms[i];
        }
        return vint;
    }

    /// <summary>RMS velocity from interval velocity sampled at regular dt.</summary>
    public static double[] RmsFromInterval(double[] vint, double dt)
    {
        var vrms = new double[vint.Length];
        double sum = 0;
        for (int i = 0; i < vint.Length; i++)
        {
            sum += vint[i] * vint[i] * dt;
            vrms[i] = Math.Sqrt(sum / ((i + 1) * dt));
        }
        return vrms;
    }
}

/// <summary>Normal moveout correction, semblance velocity analysis and stacking of CMP gathers.</summary>
public static class Nmo
{
    /// <summary>
    /// Hyperbolic NMO correction: output sample t0 takes input at t(x) = sqrt(t0² + x²/v(t0)²) (sinc interpolation).
    /// Samples whose stretch (t - t0)/t0 exceeds <paramref name="stretchMute"/> are muted.
    /// </summary>
    public static void Correct(Array2D gather, double[] offsets, VelocityFunction vrms, double dt, double t0, double stretchMute = 0.5, bool inverse = false)
    {
        int ns = gather.Cols;
        var input = new float[ns];
        for (int r = 0; r < gather.Rows; r++)
        {
            var row = gather.Row(r);
            row.CopyTo(input);
            double x = offsets[r];
            for (int k = 0; k < ns; k++)
            {
                double tz = t0 + k * dt;
                double v = vrms.At(tz);
                if (!inverse)
                {
                    double tx = Math.Sqrt(tz * tz + x * x / (v * v));
                    double stretch = tz > 0 ? (tx - tz) / tz : double.PositiveInfinity;
                    if (x != 0 && stretch > stretchMute) { row[k] = 0; continue; }
                    double idx = (tx - t0) / dt;
                    row[k] = idx > ns - 1 ? 0 : Dsp.SampleSinc(input, idx);
                }
                else
                {
                    // inverse NMO: find t0 for given tx by fixed-point on t0 = sqrt(tx² - x²/v(t0)²)
                    double tx = tz, tt = tx;
                    for (int it = 0; it < 6; it++)
                    {
                        double vv = vrms.At(tt);
                        double s = tx * tx - x * x / (vv * vv);
                        tt = s > 0 ? Math.Sqrt(s) : 0;
                    }
                    double idx = (tt - t0) / dt;
                    row[k] = tt <= 0 || idx < 0 ? 0 : Dsp.SampleSinc(input, idx);
                }
            }
        }
    }

    /// <summary>
    /// Semblance velocity spectrum (Neidell &amp; Taner, 1971): S(t0,v) = Σ_window (Σ_x a)² / (N Σ_window Σ_x a²).
    /// Returns [velocity index, time sample] with values in [0,1].
    /// </summary>
    public static Array2D Semblance(Array2D gather, double[] offsets, double[] velocities, double dt, double t0, int windowSamples = 5,
        CancellationToken ct = default)
    {
        int ns = gather.Cols, nx = gather.Rows;
        var result = new Array2D(velocities.Length, ns);
        var num = new double[ns];
        var den = new double[ns];
        Parallel.For(0, velocities.Length, new ParallelOptions { CancellationToken = ct }, iv =>
        {
            var lnum = new double[ns];
            var lden = new double[ns];
            var cnt = new int[ns];
            double v = velocities[iv];
            for (int k = 0; k < ns; k++)
            {
                double tz = t0 + k * dt;
                double s = 0, s2 = 0; int live = 0;
                for (int r = 0; r < nx; r++)
                {
                    double tx = Math.Sqrt(tz * tz + offsets[r] * offsets[r] / (v * v));
                    double idx = (tx - t0) / dt;
                    if (idx > ns - 1) continue;
                    float a = Dsp.SampleLinear(gather.Row(r), idx);
                    if (float.IsNaN(a)) continue;
                    s += a; s2 += a * (double)a; live++;
                }
                lnum[k] = s * s; lden[k] = s2; cnt[k] = live;
            }
            int h = windowSamples / 2;
            var row = result.Row(iv);
            for (int k = 0; k < ns; k++)
            {
                double sn = 0, sd = 0; int nn = 0;
                for (int w = Math.Max(0, k - h); w <= Math.Min(ns - 1, k + h); w++) { sn += lnum[w]; sd += lden[w]; nn = Math.Max(nn, cnt[w]); }
                row[k] = sd > 0 && nn > 0 ? (float)(sn / (nn * sd)) : 0f;
            }
        });
        _ = num; _ = den;
        return result;
    }

    /// <summary>Automatic velocity picking: semblance maximum per time window, constrained to be monotonically non-decreasing within tolerance.</summary>
    public static VelocityFunction AutoPick(Array2D semblance, double[] velocities, double dt, double t0, int stepSamples = 25, double minSemblance = 0.1)
    {
        var times = new List<double>(); var vels = new List<double>();
        for (int k = stepSamples / 2; k < semblance.Cols; k += stepSamples)
        {
            int best = -1; float bv = (float)minSemblance;
            for (int iv = 0; iv < velocities.Length; iv++)
                if (semblance[iv, k] > bv) { bv = semblance[iv, k]; best = iv; }
            if (best < 0) continue;
            times.Add(t0 + k * dt); vels.Add(velocities[best]);
        }
        if (times.Count == 0) return VelocityFunction.Constant(velocities[velocities.Length / 2]);
        return new VelocityFunction(times.ToArray(), vels.ToArray());
    }

    /// <summary>Stacks an NMO-corrected gather, normalising by the live fold at each sample.</summary>
    public static float[] Stack(Array2D correctedGather)
    {
        int ns = correctedGather.Cols;
        var sum = new double[ns];
        var fold = new int[ns];
        for (int r = 0; r < correctedGather.Rows; r++)
        {
            var row = correctedGather.Row(r);
            for (int k = 0; k < ns; k++)
                if (row[k] != 0 && !float.IsNaN(row[k])) { sum[k] += row[k]; fold[k]++; }
        }
        var y = new float[ns];
        for (int k = 0; k < ns; k++) y[k] = fold[k] > 0 ? (float)(sum[k] / fold[k]) : 0f;
        return y;
    }
}
