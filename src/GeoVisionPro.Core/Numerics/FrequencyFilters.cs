using System.Numerics;

namespace GeoVisionPro.Core.Numerics;

public enum FilterShape { Ormsby, Butterworth }

/// <summary>
/// Zero-phase frequency-domain filters. The filter response H(f) is real and non-negative, so the
/// filtered trace has no phase distortion. Traces are mirrored-padded to suppress wrap-around.
/// </summary>
public static class FrequencyFilters
{
    /// <summary>Trapezoidal (Ormsby) band-pass weight at |f|: 0 below f1, ramp f1-f2 (cosine), 1 in f2-f3, ramp f3-f4, 0 above f4.</summary>
    public static double OrmsbyResponse(double f, double f1, double f2, double f3, double f4)
    {
        f = Math.Abs(f);
        if (f < f1 || f > f4) return 0;
        if (f < f2) return f2 > f1 ? 0.5 - 0.5 * Math.Cos(Math.PI * (f - f1) / (f2 - f1)) : 1;
        if (f <= f3) return 1;
        return f4 > f3 ? 0.5 + 0.5 * Math.Cos(Math.PI * (f - f3) / (f4 - f3)) : 1;
    }

    /// <summary>Butterworth band-pass amplitude response |H| = HP(fLow) * LP(fHigh). Set fLow&lt;=0 for low-pass, fHigh&lt;=0 for high-pass.</summary>
    public static double ButterworthResponse(double f, double fLow, double fHigh, int order)
    {
        f = Math.Abs(f);
        double h = 1;
        if (fLow > 0) h *= f == 0 ? 0 : 1 / Math.Sqrt(1 + Math.Pow(fLow / f, 2 * order));
        if (fHigh > 0) h *= 1 / Math.Sqrt(1 + Math.Pow(f / fHigh, 2 * order));
        return h;
    }

    public static double NotchResponse(double f, double fNotch, double width)
    {
        f = Math.Abs(f);
        double d = Math.Abs(f - fNotch);
        if (d >= width) return 1;
        return 0.5 - 0.5 * Math.Cos(Math.PI * d / width);
    }

    /// <summary>Applies an arbitrary real zero-phase response H(f in Hz) to a trace in-place.</summary>
    public static void ApplyResponse(Span<float> trace, double dt, Func<double, double> response)
    {
        int n = trace.Length;
        if (n < 2) return;
        int pad = Math.Min(n - 1, Math.Max(16, n / 4));
        int total = n + 2 * pad;
        int nfft = Fft.NextPowerOfTwo(total);
        var c = new Complex[nfft];
        for (int i = 0; i < total; i++)
        {
            int src = i - pad;
            if (src < 0) src = -src;                    // mirror at start
            if (src >= n) src = 2 * (n - 1) - src;      // mirror at end
            src = Math.Clamp(src, 0, n - 1);
            c[i] = trace[src];
        }
        Fft.ForwardInPlace(c);
        for (int k = 0; k < nfft; k++) c[k] *= response(Fft.BinFrequency(k, nfft, dt));
        Fft.InverseInPlace(c);
        for (int i = 0; i < n; i++) trace[i] = (float)c[i + pad].Real;
    }

    public static void BandPass(Span<float> trace, double dt, double f1, double f2, double f3, double f4) =>
        ApplyResponse(trace, dt, f => OrmsbyResponse(f, f1, f2, f3, f4));

    public static void Butterworth(Span<float> trace, double dt, double fLow, double fHigh, int order) =>
        ApplyResponse(trace, dt, f => ButterworthResponse(f, fLow, fHigh, order));

    public static void Notch(Span<float> trace, double dt, double fNotch, double width) =>
        ApplyResponse(trace, dt, f => NotchResponse(f, fNotch, width));
}
