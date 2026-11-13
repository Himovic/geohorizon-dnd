using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Numerics;

namespace GeoVisionPro.SeismicProcessing;

internal static class P
{
    public static ParameterDescriptor Num(string key, string label, double def, string unit = "", double min = double.NegativeInfinity, double max = double.PositiveInfinity, string desc = "") => Param.Num(key, label, def, unit, min, max, desc);
    public static ParameterDescriptor Int(string key, string label, int def, string unit = "", double min = double.NegativeInfinity, double max = double.PositiveInfinity, string desc = "") => Param.Int(key, label, def, unit, min, max, desc);
    public static ParameterDescriptor Bool(string key, string label, bool def, string desc = "") => Param.Bool(key, label, def, desc);
    public static ParameterDescriptor Text(string key, string label, string def, string desc = "") => Param.Text(key, label, def, desc);
    public static ParameterDescriptor Choice(string key, string label, string def, string[] choices, string desc = "") => Param.Choice(key, label, def, choices, desc);
}

public sealed class AmplitudeScaleOperation : TraceOperation
{
    public override string Id => "scale";
    public override string DisplayName => "Amplitude Scaling";
    public override string Category => "Amplitude";
    public override string Description => "Multiplies every sample by a constant factor.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [P.Num("factor", "Scale factor", 1.0)];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        float f = (float)p.GetDouble("factor", 1);
        for (int i = 0; i < t.Length; i++) t[i] *= f;
    }
}

public sealed class PolarityReversalOperation : TraceOperation
{
    public override string Id => "polarity";
    public override string DisplayName => "Polarity Reversal";
    public override string Category => "Amplitude";
    public override string Description => "Reverses trace polarity (multiplies by -1).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        for (int i = 0; i < t.Length; i++) t[i] = -t[i];
    }
}

public sealed class DcRemovalOperation : TraceOperation
{
    public override string Id => "dc";
    public override string DisplayName => "DC Removal";
    public override string Category => "Basic";
    public override string Description => "Removes the trace mean (zero-frequency component).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) => Dsp.RemoveMean(t);
}

public sealed class DetrendOperation : TraceOperation
{
    public override string Id => "detrend";
    public override string DisplayName => "Linear Detrend";
    public override string Category => "Basic";
    public override string Description => "Removes the least-squares linear trend from each trace.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) => Dsp.Detrend(t);
}

public sealed class BandPassOperation : TraceOperation
{
    public override string Id => "bandpass";
    public override string DisplayName => "Band-pass Filter (Ormsby)";
    public override string Category => "Filtering";
    public override string Description => "Zero-phase trapezoidal band-pass filter with cosine-tapered corners f1-f2-f3-f4.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("f1", "Low cut", 5, "Hz", 0), P.Num("f2", "Low pass", 10, "Hz", 0),
        P.Num("f3", "High pass", 60, "Hz", 0), P.Num("f4", "High cut", 80, "Hz", 0)
    ];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) =>
        FrequencyFilters.BandPass(t, ctx.SampleInterval, p.GetDouble("f1"), p.GetDouble("f2"), p.GetDouble("f3"), p.GetDouble("f4"));
}

public sealed class ButterworthOperation : TraceOperation
{
    public override string Id => "butterworth";
    public override string DisplayName => "Low/High-pass Filter (Butterworth)";
    public override string Category => "Filtering";
    public override string Description => "Zero-phase Butterworth amplitude response. Set low cut to 0 for low-pass, high cut to 0 for high-pass.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("low", "Low cut (high-pass corner)", 0, "Hz", 0), P.Num("high", "High cut (low-pass corner)", 60, "Hz", 0),
        P.Int("order", "Order", 4, "", 1, 16)
    ];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) =>
        FrequencyFilters.Butterworth(t, ctx.SampleInterval, p.GetDouble("low"), p.GetDouble("high"), p.GetInt("order", 4));
}

public sealed class NotchOperation : TraceOperation
{
    public override string Id => "notch";
    public override string DisplayName => "Notch Filter";
    public override string Category => "Filtering";
    public override string Description => "Zero-phase notch with raised-cosine shoulders (e.g. power-line 50/60 Hz).";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [P.Num("freq", "Notch frequency", 50, "Hz", 0), P.Num("width", "Half width", 2, "Hz", 0.1)];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) =>
        FrequencyFilters.Notch(t, ctx.SampleInterval, p.GetDouble("freq"), p.GetDouble("width"));
}

/// <summary>RMS automatic gain control with a centred sliding window.</summary>
public sealed class AgcOperation : TraceOperation
{
    public override string Id => "agc";
    public override string DisplayName => "AGC (RMS)";
    public override string Category => "Amplitude";
    public override string Description => "Automatic gain control: each sample divided by the RMS amplitude in a centred window. Destroys relative amplitudes.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [P.Num("window", "Window length", 500, "ms", 1)];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx) =>
        Apply(t, (int)Math.Max(1, Math.Round(p.GetDouble("window", 500) / 1000 / ctx.SampleInterval)));

    public static void Apply(Span<float> t, int windowSamples)
    {
        int n = t.Length, h = windowSamples / 2;
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + t[i] * (double)t[i];
        double globalRms = Math.Sqrt(cum[n] / Math.Max(1, n));
        double eps = globalRms * 1e-6 + 1e-30;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
            double rms = Math.Sqrt((cum[b + 1] - cum[a]) / (b - a + 1));
            t[i] = (float)(t[i] / (rms + eps));
        }
    }
}

public sealed class RmsNormalizationOperation : TraceOperation
{
    public override string Id => "rmsnorm";
    public override string DisplayName => "RMS Normalization";
    public override string Category => "Amplitude";
    public override string Description => "Scales each trace so that its RMS amplitude equals the target value.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [P.Num("target", "Target RMS", 1.0, "", 0)];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        float rms = Dsp.Rms(t);
        if (rms <= 0) return;
        float f = (float)(p.GetDouble("target", 1) / rms);
        for (int i = 0; i < t.Length; i++) t[i] *= f;
    }
}

public sealed class TraceNormalizationOperation : TraceOperation
{
    public override string Id => "tracenorm";
    public override string DisplayName => "Trace Normalization (peak)";
    public override string Category => "Amplitude";
    public override string Description => "Scales each trace to unit maximum absolute amplitude.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        float m = Dsp.MaxAbs(t);
        if (m <= 0) return;
        for (int i = 0; i < t.Length; i++) t[i] /= m;
    }
}

public sealed class TimeSmoothingOperation : TraceOperation
{
    public override string Id => "smooth";
    public override string DisplayName => "Smoothing (time)";
    public override string Category => "Filtering";
    public override string Description => "Centred moving-average or median filter along time.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [P.Int("length", "Window length", 5, "samples", 1, 1001), P.Choice("type", "Filter", "Mean", ["Mean", "Median"])];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        int len = p.GetInt("length", 5);
        var y = p.GetString("type") == "Median" ? Dsp.MedianFilter(t, len) : Dsp.MovingAverage(t, len);
        y.CopyTo(t);
    }
}

/// <summary>
/// Wiener–Levinson spiking deconvolution (Robinson &amp; Treitel): solves (R + εr0 I) f = δ for an inverse filter
/// of the minimum-phase wavelet estimated from the trace autocorrelation in a design window.
/// </summary>
public sealed class SpikingDeconvolutionOperation : TraceOperation
{
    public override string Id => "decon_spike";
    public override string DisplayName => "Spiking Deconvolution";
    public override string Category => "Deconvolution";
    public override string Description => "Wiener-Levinson minimum-phase spiking deconvolution with pre-whitening. Assumes white reflectivity and a minimum-phase wavelet.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("length", "Operator length", 120, "ms", 4), P.Num("prewhite", "Pre-whitening", 1.0, "%", 0, 100),
        P.Num("winStart", "Design window start", 0, "ms", 0), P.Num("winEnd", "Design window end (0=trace end)", 0, "ms", 0)
    ];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        int len = Math.Max(2, (int)Math.Round(p.GetDouble("length") / 1000 / ctx.SampleInterval));
        Deconvolution.Predictive(t, len, 1, p.GetDouble("prewhite") / 100, Window(t.Length, p, ctx));
    }

    internal static (int, int) Window(int n, ParameterSet p, ProcessingContext ctx)
    {
        int a = Math.Clamp((int)Math.Round((p.GetDouble("winStart", 0) / 1000 - ctx.StartTime) / ctx.SampleInterval), 0, n - 1);
        double we = p.GetDouble("winEnd", 0);
        int b = we <= 0 ? n - 1 : Math.Clamp((int)Math.Round((we / 1000 - ctx.StartTime) / ctx.SampleInterval), a + 1, n - 1);
        return (a, b);
    }
}

public sealed class PredictiveDeconvolutionOperation : TraceOperation
{
    public override string Id => "decon_pred";
    public override string DisplayName => "Predictive Deconvolution";
    public override string Category => "Deconvolution";
    public override string Description => "Gapped prediction-error filtering (Peacock & Treitel, 1969) for short-period multiple and reverberation attenuation.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("length", "Operator length", 160, "ms", 4), P.Num("gap", "Prediction gap", 24, "ms", 0),
        P.Num("prewhite", "Pre-whitening", 1.0, "%", 0, 100),
        P.Num("winStart", "Design window start", 0, "ms", 0), P.Num("winEnd", "Design window end (0=trace end)", 0, "ms", 0)
    ];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        int len = Math.Max(2, (int)Math.Round(p.GetDouble("length") / 1000 / ctx.SampleInterval));
        int gap = Math.Max(1, (int)Math.Round(p.GetDouble("gap") / 1000 / ctx.SampleInterval));
        Deconvolution.Predictive(t, len, gap, p.GetDouble("prewhite") / 100, SpikingDeconvolutionOperation.Window(t.Length, p, ctx));
    }
}

public static class Deconvolution
{
    /// <summary>
    /// Prediction-error filtering with gap α and operator length L (α = 1 gives spiking deconvolution).
    /// Normal equations: Σ_j f_j r(i-j) = r(i+α), i = 0..L-1. Output y(t) = x(t) - Σ f_j x(t-α-j). RMS preserved.
    /// </summary>
    public static double[] Predictive(Span<float> trace, int length, int gap, double prewhitening, (int Start, int End) window)
    {
        int n = trace.Length;
        var design = trace.Slice(window.Start, window.End - window.Start + 1);
        var r = Dsp.AutoCorrelate(design, length + gap);
        if (r[0] <= 0) return [];
        double rmsBefore = Dsp.Rms(trace);
        var rr = r.Take(length).ToArray();
        rr[0] *= 1 + prewhitening;
        var g = new double[length];
        for (int i = 0; i < length; i++) g[i] = r[i + gap];
        var f = Dsp.LevinsonSolve(rr, g);
        var x = trace.ToArray();
        for (int t = 0; t < n; t++)
        {
            double pred = 0;
            for (int j = 0; j < length; j++)
            {
                int k = t - gap - j;
                if (k < 0) break;
                pred += f[j] * x[k];
            }
            trace[t] = (float)(x[t] - pred);
        }
        double rmsAfter = Dsp.Rms(trace);
        if (rmsAfter > 0) { float s = (float)(rmsBefore / rmsAfter); for (int t = 0; t < n; t++) trace[t] *= s; }
        return f;
    }
}

/// <summary>Spectral whitening: flattens the amplitude spectrum within a band while preserving phase.</summary>
public sealed class SpectralWhiteningOperation : TraceOperation
{
    public override string Id => "whiten";
    public override string DisplayName => "Spectral Whitening";
    public override string Category => "Deconvolution";
    public override string Description => "Divides the amplitude spectrum by its smoothed version within an Ormsby band; phase is preserved and the trace RMS is restored.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Num("f1", "Low cut", 5, "Hz", 0), P.Num("f2", "Low pass", 10, "Hz", 0), P.Num("f3", "High pass", 60, "Hz", 0), P.Num("f4", "High cut", 80, "Hz", 0),
        P.Num("smooth", "Spectral smoothing", 10, "Hz", 0.1), P.Num("stab", "Stabilisation", 1, "%", 0, 100)
    ];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        int n = t.Length;
        if (n < 4) return;
        double rms0 = Dsp.Rms(t);
        int nfft = Fft.NextPowerOfTwo(2 * n);
        var spec = Fft.Forward(t, nfft);
        double df = 1.0 / (nfft * ctx.SampleInterval);
        int half = Math.Max(1, (int)Math.Round(p.GetDouble("smooth", 10) / df / 2));
        var amp = new float[nfft / 2 + 1];
        for (int k = 0; k <= nfft / 2; k++) amp[k] = (float)spec[k].Magnitude;
        var sm = Dsp.MovingAverage(amp, 2 * half + 1);
        double eps = sm.Max() * p.GetDouble("stab", 1) / 100 + 1e-30;
        double f1 = p.GetDouble("f1"), f2 = p.GetDouble("f2"), f3 = p.GetDouble("f3"), f4 = p.GetDouble("f4");
        for (int k = 0; k < nfft; k++)
        {
            int kk = k <= nfft / 2 ? k : nfft - k;
            double w = FrequencyFilters.OrmsbyResponse(kk * df, f1, f2, f3, f4) / (sm[kk] + eps);
            spec[k] *= w;
        }
        var y = Fft.InverseReal(spec, n);
        double rms1 = Dsp.Rms(y);
        float s = rms1 > 0 ? (float)(rms0 / rms1) : 0;
        for (int i = 0; i < n; i++) t[i] = y[i] * s;
    }
}

public sealed class TopMuteOperation : TraceOperation
{
    public override string Id => "topmute";
    public override string DisplayName => "Top / Bottom Mute";
    public override string Category => "Muting";
    public override string Description => "Zeroes samples above a top-mute time and below a bottom-mute time with cosine tapers.";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [P.Num("top", "Top mute time", 0, "ms", 0), P.Num("bottom", "Bottom mute time (0 = none)", 0, "ms", 0), P.Num("taper", "Taper length", 20, "ms", 0)];
    public override void ProcessTrace(Span<float> t, ParameterSet p, ProcessingContext ctx)
    {
        double dt = ctx.SampleInterval;
        int taper = (int)Math.Round(p.GetDouble("taper") / 1000 / dt);
        int top = (int)Math.Round((p.GetDouble("top") / 1000 - ctx.StartTime) / dt);
        double bottomMs = p.GetDouble("bottom");
        int bottom = bottomMs > 0 ? (int)Math.Round((bottomMs / 1000 - ctx.StartTime) / dt) : int.MaxValue;
        for (int i = 0; i < t.Length; i++)
        {
            double w = 1;
            if (i < top) w = 0;
            else if (i < top + taper) w = 0.5 - 0.5 * Math.Cos(Math.PI * (i - top) / Math.Max(1, taper));
            if (i > bottom) w = 0;
            else if (bottom != int.MaxValue && i > bottom - taper) w *= 0.5 - 0.5 * Math.Cos(Math.PI * (bottom - i) / Math.Max(1, taper));
            t[i] = (float)(t[i] * w);
        }
    }
}
