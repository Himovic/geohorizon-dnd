using System.Numerics;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;

namespace GeoVisionPro.Attributes;

public enum AttributeScope { Trace, Neighborhood }

/// <summary>A seismic attribute computed from a trace (or a 3×3 trace neighbourhood) producing a trace of the same length.</summary>
public interface ISeismicAttribute
{
    string Id { get; }
    string DisplayName { get; }
    string Category { get; }
    string Unit { get; }
    string Description { get; }
    AttributeScope Scope { get; }
    IReadOnlyList<ParameterDescriptor> Parameters { get; }
    /// <summary>Trace scope: input trace → output trace (same length).</summary>
    void ComputeTrace(ReadOnlySpan<float> input, Span<float> output, ParameterSet p, double dt) => throw new NotSupportedException();
    /// <summary>Neighbourhood scope: traces[di+1, dj+1] for di,dj ∈ {-1,0,1} (null where missing); output for the centre trace.</summary>
    void ComputeNeighborhood(float[]?[,] traces, Span<float> output, ParameterSet p, double dt, double dxInline, double dxCrossline) => throw new NotSupportedException();
}

internal static class ComplexTrace
{
    /// <summary>Instantaneous frequency (Hz) from the analytic signal: f = (x h' − h x') / (2π (x² + h²)), central differences.</summary>
    public static float[] InstantaneousFrequency(ReadOnlySpan<float> x, double dt, int smoothSamples = 0)
    {
        var a = Dsp.AnalyticSignal(x);
        int n = a.Length;
        var f = new float[n];
        var num = new float[n];
        var den = new float[n];
        for (int i = 0; i < n; i++)
        {
            int i0 = Math.Max(0, i - 1), i1 = Math.Min(n - 1, i + 1);
            double dx = (a[i1].Real - a[i0].Real) / ((i1 - i0) * dt);
            double dh = (a[i1].Imaginary - a[i0].Imaginary) / ((i1 - i0) * dt);
            num[i] = (float)(a[i].Real * dh - a[i].Imaginary * dx);
            den[i] = (float)(2 * Math.PI * (a[i].Real * a[i].Real + a[i].Imaginary * a[i].Imaginary));
        }
        if (smoothSamples > 1)
        {
            // envelope-weighted smoothing (ratio of smoothed numerator and denominator) avoids singular spikes
            num = Dsp.MovingAverage(num, smoothSamples);
            den = Dsp.MovingAverage(den, smoothSamples);
        }
        for (int i = 0; i < n; i++) f[i] = den[i] > 1e-30 ? num[i] / den[i] : 0f;
        return f;
    }
}

public abstract class TraceAttribute : ISeismicAttribute
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Category { get; }
    public virtual string Unit => "";
    public abstract string Description { get; }
    public AttributeScope Scope => AttributeScope.Trace;
    public virtual IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
    public abstract void ComputeTrace(ReadOnlySpan<float> input, Span<float> output, ParameterSet p, double dt);
}

public sealed class EnvelopeAttribute : TraceAttribute
{
    public override string Id => "envelope";
    public override string DisplayName => "Envelope (Instantaneous Amplitude)";
    public override string Category => "Amplitude";
    public override string Description => "Magnitude of the analytic signal |x + iH[x]| (Taner et al., 1979).";
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        var a = Dsp.AnalyticSignal(x);
        for (int i = 0; i < a.Length; i++) y[i] = (float)a[i].Magnitude;
    }
}

public sealed class InstantaneousPhaseAttribute : TraceAttribute
{
    public override string Id => "instphase";
    public override string DisplayName => "Instantaneous Phase";
    public override string Category => "Phase";
    public override string Unit => "deg";
    public override string Description => "atan2(H[x], x) in degrees (-180..180); highlights event continuity independent of amplitude.";
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        var a = Dsp.AnalyticSignal(x);
        for (int i = 0; i < a.Length; i++) y[i] = (float)(Math.Atan2(a[i].Imaginary, a[i].Real) * 180 / Math.PI);
    }
}

public sealed class CosinePhaseAttribute : TraceAttribute
{
    public override string Id => "cosphase";
    public override string DisplayName => "Cosine of Instantaneous Phase";
    public override string Category => "Phase";
    public override string Description => "cos(φ) = x / envelope; amplitude-normalised continuity display.";
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        var a = Dsp.AnalyticSignal(x);
        for (int i = 0; i < a.Length; i++) { double m = a[i].Magnitude; y[i] = m > 1e-30 ? (float)(a[i].Real / m) : 0f; }
    }
}

public sealed class InstantaneousFrequencyAttribute : TraceAttribute
{
    public override string Id => "instfreq";
    public override string DisplayName => "Instantaneous Frequency";
    public override string Category => "Frequency";
    public override string Unit => "Hz";
    public override string Description => "Time derivative of instantaneous phase / 2π, envelope-weighted smoothing.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Int("smooth", "Smoothing", 5, "samples", 1, 201)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt) =>
        ComplexTrace.InstantaneousFrequency(x, dt, p.GetInt("smooth", 5)).CopyTo(y);
}

public sealed class PhaseRotationAttribute : TraceAttribute
{
    public override string Id => "phaserot";
    public override string DisplayName => "Phase Rotation";
    public override string Category => "Phase";
    public override string Description => "Constant phase rotation y = x cosφ − H[x] sinφ (90° gives a quadrature trace).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("angle", "Rotation angle", 90, "deg", -180, 180)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt) =>
        Dsp.PhaseRotate(x, p.GetDouble("angle", 90)).CopyTo(y);
}

public enum WindowStatistic { Rms, MeanAbsolute, Maximum, Minimum, MaximumAbsolute, Energy, Mean }

public sealed class WindowedAmplitudeAttribute : TraceAttribute
{
    private readonly WindowStatistic _stat;
    public WindowedAmplitudeAttribute(WindowStatistic stat) => _stat = stat;
    public override string Id => "win_" + _stat.ToString().ToLowerInvariant();
    public override string DisplayName => _stat switch
    {
        WindowStatistic.Rms => "RMS Amplitude",
        WindowStatistic.MeanAbsolute => "Average Absolute Amplitude",
        WindowStatistic.Maximum => "Maximum Amplitude",
        WindowStatistic.Minimum => "Minimum Amplitude",
        WindowStatistic.MaximumAbsolute => "Maximum Absolute Amplitude",
        WindowStatistic.Energy => "Total Energy",
        _ => "Mean Amplitude"
    };
    public override string Category => "Amplitude";
    public override string Description => "Sliding-window statistic centred on each sample.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "Window length", 40, "ms", 1)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        int h = Math.Max(0, (int)Math.Round(p.GetDouble("window", 40) / 1000 / dt / 2));
        for (int i = 0; i < x.Length; i++)
            y[i] = Statistic(x.Slice(Math.Max(0, i - h), Math.Min(x.Length - 1, i + h) - Math.Max(0, i - h) + 1), _stat);
    }

    public static float Statistic(ReadOnlySpan<float> w, WindowStatistic stat)
    {
        if (w.Length == 0) return float.NaN;
        double acc = stat switch { WindowStatistic.Maximum or WindowStatistic.MaximumAbsolute => double.MinValue, WindowStatistic.Minimum => double.MaxValue, _ => 0 };
        int n = 0;
        foreach (var v in w)
        {
            if (float.IsNaN(v)) continue;
            n++;
            switch (stat)
            {
                case WindowStatistic.Rms: case WindowStatistic.Energy: acc += v * (double)v; break;
                case WindowStatistic.MeanAbsolute: acc += Math.Abs(v); break;
                case WindowStatistic.Mean: acc += v; break;
                case WindowStatistic.Maximum: acc = Math.Max(acc, v); break;
                case WindowStatistic.Minimum: acc = Math.Min(acc, v); break;
                case WindowStatistic.MaximumAbsolute: acc = Math.Max(acc, Math.Abs(v)); break;
            }
        }
        if (n == 0) return float.NaN;
        return stat switch
        {
            WindowStatistic.Rms => (float)Math.Sqrt(acc / n),
            WindowStatistic.MeanAbsolute or WindowStatistic.Mean => (float)(acc / n),
            _ => (float)acc
        };
    }
}

public sealed class SweetnessAttribute : TraceAttribute
{
    public override string Id => "sweetness";
    public override string DisplayName => "Sweetness";
    public override string Category => "Advanced";
    public override string Description => "Envelope / √(instantaneous frequency) (Hart, 2008); highlights bright, low-frequency (often sand-prone) intervals.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Int("smooth", "Frequency smoothing", 11, "samples", 1, 201)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        var a = Dsp.AnalyticSignal(x);
        var f = ComplexTrace.InstantaneousFrequency(x, dt, p.GetInt("smooth", 11));
        double fFloor = 1.0; // Hz, avoids division by ~0
        for (int i = 0; i < a.Length; i++) y[i] = (float)(a[i].Magnitude / Math.Sqrt(Math.Max(Math.Abs(f[i]), fFloor)));
    }
}

/// <summary>Relative acoustic impedance: running integral of the trace followed by low-cut filtering (removes integration drift).</summary>
public sealed class RelativeImpedanceAttribute : TraceAttribute
{
    public override string Id => "rai";
    public override string DisplayName => "Relative Acoustic Impedance";
    public override string Category => "Advanced";
    public override string Description => "Integrated trace with a low-cut (Butterworth) filter: a band-limited proxy of impedance contrasts. Relative values only (no low-frequency trend).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("lowcut", "Low cut", 5, "Hz", 0.1)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        double s = 0;
        for (int i = 0; i < x.Length; i++) { s += float.IsNaN(x[i]) ? 0 : x[i]; y[i] = (float)s; }
        FrequencyFilters.Butterworth(y, dt, p.GetDouble("lowcut", 5), 0, 4);
    }
}

/// <summary>Short-time Fourier transform spectral decomposition (Gaussian window): magnitude or phase at a chosen frequency.</summary>
public sealed class SpectralDecompositionAttribute : TraceAttribute
{
    private readonly bool _phase;
    public SpectralDecompositionAttribute(bool phase) => _phase = phase;
    public override string Id => _phase ? "specphase" : "specmag";
    public override string DisplayName => _phase ? "Spectral Phase (STFT)" : "Spectral Magnitude (STFT)";
    public override string Category => "Frequency";
    public override string Unit => _phase ? "deg" : "";
    public override string Description => "Gaussian-windowed Fourier coefficient at a single frequency, computed for every sample (time-frequency decomposition).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [Param.Num("freq", "Frequency", 30, "Hz", 0.1), Param.Num("window", "Window (Gaussian ±2σ)", 80, "ms", 4)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        double f = p.GetDouble("freq", 30);
        double sigma = p.GetDouble("window", 80) / 1000 / 4;
        int h = (int)Math.Ceiling(2 * sigma / dt);
        var g = new double[2 * h + 1];
        var cw = new System.Numerics.Complex[2 * h + 1];
        for (int k = -h; k <= h; k++)
        {
            double t = k * dt;
            g[k + h] = Math.Exp(-0.5 * t * t / (sigma * sigma));
            cw[k + h] = System.Numerics.Complex.FromPolarCoordinates(g[k + h], -2 * Math.PI * f * t);
        }
        double norm = g.Sum();
        for (int i = 0; i < x.Length; i++)
        {
            System.Numerics.Complex s = System.Numerics.Complex.Zero;
            for (int k = -h; k <= h; k++)
            {
                int j = i + k;
                if (j < 0 || j >= x.Length || float.IsNaN(x[j])) continue;
                s += x[j] * cw[k + h];
            }
            y[i] = _phase ? (float)(s.Phase * 180 / Math.PI) : (float)(2 * s.Magnitude / norm);
        }
    }
}

/// <summary>Dominant (peak) frequency of the local amplitude spectrum in a sliding window.</summary>
public sealed class DominantFrequencyAttribute : TraceAttribute
{
    public override string Id => "domfreq";
    public override string DisplayName => "Dominant Frequency";
    public override string Category => "Frequency";
    public override string Unit => "Hz";
    public override string Description => "Frequency of the maximum of the Hann-windowed local amplitude spectrum.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Param.Num("window", "Window length", 100, "ms", 8)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        int w = Math.Max(8, (int)Math.Round(p.GetDouble("window", 100) / 1000 / dt));
        int h = w / 2;
        int nfft = Fft.NextPowerOfTwo(Math.Max(64, 4 * w));
        var taper = Dsp.Hann(2 * h + 1);
        var buf = new float[2 * h + 1];
        for (int i = 0; i < x.Length; i++)
        {
            for (int k = -h; k <= h; k++) { int j = i + k; buf[k + h] = j >= 0 && j < x.Length && !float.IsNaN(x[j]) ? x[j] * taper[k + h] : 0; }
            var spec = Fft.Forward(buf, nfft);
            int best = 1; double bv = 0;
            for (int k = 1; k <= nfft / 2; k++) { double m = spec[k].Magnitude; if (m > bv) { bv = m; best = k; } }
            y[i] = (float)(best / (nfft * dt));
        }
    }
}

/// <summary>Frequency attenuation: slope of ln(amplitude) versus frequency between f1 and f2 in a sliding window (more negative = stronger high-frequency loss).</summary>
public sealed class AttenuationAttribute : TraceAttribute
{
    public override string Id => "attenuation";
    public override string DisplayName => "Frequency Attenuation (spectral slope)";
    public override string Category => "Frequency";
    public override string Unit => "1/Hz";
    public override string Description => "Least-squares slope of ln|A(f)| over [f1,f2] in a Hann window; a relative attenuation indicator, not a calibrated Q estimate.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [Param.Num("window", "Window length", 120, "ms", 8), Param.Num("f1", "Lower frequency", 15, "Hz", 0), Param.Num("f2", "Upper frequency", 60, "Hz", 1)];
    public override void ComputeTrace(ReadOnlySpan<float> x, Span<float> y, ParameterSet p, double dt)
    {
        int w = Math.Max(8, (int)Math.Round(p.GetDouble("window", 120) / 1000 / dt));
        int h = w / 2;
        int nfft = Fft.NextPowerOfTwo(Math.Max(64, 2 * w));
        double df = 1 / (nfft * dt);
        int k1 = Math.Max(1, (int)(p.GetDouble("f1", 15) / df)), k2 = Math.Min(nfft / 2, (int)(p.GetDouble("f2", 60) / df));
        var taper = Dsp.Hann(2 * h + 1);
        var buf = new float[2 * h + 1];
        var fx = new List<double>(); var fy = new List<double>();
        for (int i = 0; i < x.Length; i++)
        {
            for (int k = -h; k <= h; k++) { int j = i + k; buf[k + h] = j >= 0 && j < x.Length && !float.IsNaN(x[j]) ? x[j] * taper[k + h] : 0; }
            var spec = Fft.Forward(buf, nfft);
            fx.Clear(); fy.Clear();
            for (int k = k1; k <= k2; k++) { double m = spec[k].Magnitude; if (m > 0) { fx.Add(k * df); fy.Add(Math.Log(m)); } }
            y[i] = fx.Count >= 2 ? (float)Statistics.LinearRegression(fx, fy).Slope : 0f;
        }
    }
}
