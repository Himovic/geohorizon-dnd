using System.Numerics;

namespace GeoVisionPro.Core.Numerics;

/// <summary>
/// General purpose 1D digital signal processing routines used throughout GeoVision Pro.
/// All routines are deterministic, allocation-explicit and unit-tested against analytical results.
/// </summary>
public static class Dsp
{
    /// <summary>Full linear convolution, length a+b-1.</summary>
    public static float[] Convolve(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || b.Length == 0) return [];
        int n = a.Length + b.Length - 1;
        if ((long)a.Length * b.Length > 1_000_000) return ConvolveFft(a, b);
        var y = new float[n];
        for (int i = 0; i < a.Length; i++)
        {
            float ai = a[i];
            if (ai == 0) continue;
            for (int j = 0; j < b.Length; j++) y[i + j] += ai * b[j];
        }
        return y;
    }

    public static float[] ConvolveFft(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = a.Length + b.Length - 1;
        int nfft = Fft.NextPowerOfTwo(n);
        var fa = Fft.Forward(a, nfft);
        var fb = Fft.Forward(b, nfft);
        for (int i = 0; i < nfft; i++) fa[i] *= fb[i];
        return Fft.InverseReal(fa, n);
    }

    /// <summary>
    /// Convolution with a wavelet whose time-zero sample is at index <paramref name="waveletCenter"/>;
    /// output has the same length as <paramref name="signal"/> ("same" mode, zero-phase alignment).
    /// </summary>
    public static float[] ConvolveSame(ReadOnlySpan<float> signal, ReadOnlySpan<float> wavelet, int waveletCenter)
    {
        var full = Convolve(signal, wavelet);
        var y = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            int k = i + waveletCenter;
            if (k >= 0 && k < full.Length) y[i] = full[k];
        }
        return y;
    }

    /// <summary>Cross-correlation r[lag] = sum_t a[t] * b[t + lag] for lag in [-maxLag, maxLag].</summary>
    public static double[] CrossCorrelate(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int maxLag)
    {
        var r = new double[2 * maxLag + 1];
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int t = 0; t < a.Length; t++)
            {
                int u = t + lag;
                if (u < 0 || u >= b.Length) continue;
                s += a[t] * (double)b[u];
            }
            r[lag + maxLag] = s;
        }
        return r;
    }

    /// <summary>Autocorrelation for lags 0..maxLag.</summary>
    public static double[] AutoCorrelate(ReadOnlySpan<float> a, int maxLag)
    {
        var r = new double[maxLag + 1];
        for (int lag = 0; lag <= maxLag; lag++)
        {
            double s = 0;
            for (int t = 0; t + lag < a.Length; t++) s += a[t] * (double)a[t + lag];
            r[lag] = s;
        }
        return r;
    }

    /// <summary>Normalized (Pearson) correlation coefficient between two equally long series, ignoring NaNs.</summary>
    public static double Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sa = 0, sb = 0; int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(a[i]) || float.IsNaN(b[i])) continue;
            sa += a[i]; sb += b[i]; m++;
        }
        if (m < 2) return double.NaN;
        double ma = sa / m, mb = sb / m, sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++)
        {
            if (float.IsNaN(a[i]) || float.IsNaN(b[i])) continue;
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        if (saa <= 0 || sbb <= 0) return 0;
        return sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>
    /// Analytic signal via the FFT Hilbert transform (Marple, 1999): the negative frequencies are zeroed
    /// and positive frequencies doubled. Real part equals the input; imaginary part is the Hilbert transform.
    /// </summary>
    public static Complex[] AnalyticSignal(ReadOnlySpan<float> x)
    {
        int n = x.Length;
        if (n == 0) return [];
        int nfft = Fft.NextPowerOfTwo(n * 2);
        var spec = Fft.Forward(x, nfft);
        spec[0] *= 1;
        for (int k = 1; k < nfft / 2; k++) spec[k] *= 2;
        // Nyquist bin kept as is (weight 1)
        for (int k = nfft / 2 + 1; k < nfft; k++) spec[k] = Complex.Zero;
        Fft.InverseInPlace(spec);
        var result = new Complex[n];
        Array.Copy(spec, result, n);
        return result;
    }

    public static float[] Hilbert(ReadOnlySpan<float> x)
    {
        var a = AnalyticSignal(x);
        var h = new float[a.Length];
        for (int i = 0; i < a.Length; i++) h[i] = (float)a[i].Imaginary;
        return h;
    }

    /// <summary>Constant phase rotation of a real signal by <paramref name="degrees"/>: y = x cos(φ) - H[x] sin(φ).</summary>
    public static float[] PhaseRotate(ReadOnlySpan<float> x, double degrees)
    {
        double phi = degrees * Math.PI / 180.0;
        double c = Math.Cos(phi), s = Math.Sin(phi);
        var h = Hilbert(x);
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++) y[i] = (float)(x[i] * c - h[i] * s);
        return y;
    }

    /// <summary>
    /// Levinson–Durbin recursion solving the symmetric Toeplitz system R f = g where R is built from
    /// autocorrelation r[0..n-1]. Returns filter f of length n.
    /// </summary>
    public static double[] LevinsonSolve(ReadOnlySpan<double> r, ReadOnlySpan<double> g)
    {
        int n = g.Length;
        if (r.Length < n) throw new ArgumentException("Autocorrelation shorter than system size.");
        var f = new double[n];
        if (r[0] == 0) return f;
        var a = new double[n];   // prediction-error filter, a[0] = 1
        var tmp = new double[n];
        a[0] = 1.0;
        double err = r[0];
        f[0] = g[0] / r[0];
        for (int k = 1; k < n; k++)
        {
            double delta = 0;
            for (int j = 0; j < k; j++) delta += a[j] * r[k - j];
            double refl = -delta / err;
            for (int j = 0; j <= k; j++) tmp[j] = a[j] + refl * a[k - j]; // a[k] == 0 before update
            Array.Copy(tmp, a, k + 1);
            err *= 1 - refl * refl;
            if (err <= 0) err = double.Epsilon;
            double eps = 0;
            for (int j = 0; j < k; j++) eps += f[j] * r[k - j];
            double q = (g[k] - eps) / err;
            for (int j = 0; j <= k; j++) f[j] += q * a[k - j];
        }
        return f;
    }

    /// <summary>Removes the mean of a trace (ignoring NaN).</summary>
    public static void RemoveMean(Span<float> x)
    {
        double s = 0; int n = 0;
        foreach (var v in x) if (!float.IsNaN(v)) { s += v; n++; }
        if (n == 0) return;
        float m = (float)(s / n);
        for (int i = 0; i < x.Length; i++) if (!float.IsNaN(x[i])) x[i] -= m;
    }

    /// <summary>Removes the least-squares linear trend.</summary>
    public static void Detrend(Span<float> x)
    {
        int n = x.Length;
        if (n < 2) return;
        double st = 0, sx = 0, stt = 0, stx = 0;
        for (int i = 0; i < n; i++) { st += i; sx += x[i]; stt += (double)i * i; stx += (double)i * x[i]; }
        double den = n * stt - st * st;
        if (den == 0) return;
        double b = (n * stx - st * sx) / den;
        double a = (sx - b * st) / n;
        for (int i = 0; i < n; i++) x[i] -= (float)(a + b * i);
    }

    public static float Rms(ReadOnlySpan<float> x)
    {
        double s = 0; int n = 0;
        foreach (var v in x) if (!float.IsNaN(v)) { s += v * (double)v; n++; }
        return n == 0 ? 0f : (float)Math.Sqrt(s / n);
    }

    public static float MaxAbs(ReadOnlySpan<float> x)
    {
        float m = 0;
        foreach (var v in x) { var a = Math.Abs(v); if (a > m) m = a; }
        return m;
    }

    /// <summary>Centred boxcar moving average of odd length window (edge-shrinking).</summary>
    public static float[] MovingAverage(ReadOnlySpan<float> x, int window)
    {
        int n = x.Length;
        var y = new float[n];
        if (window <= 1) { x.CopyTo(y); return y; }
        int h = window / 2;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + x[i];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
            y[i] = (float)((prefix[b + 1] - prefix[a]) / (b - a + 1));
        }
        return y;
    }

    /// <summary>Running median filter (edge-shrinking), robust to spikes.</summary>
    public static float[] MedianFilter(ReadOnlySpan<float> x, int window)
    {
        int n = x.Length, h = Math.Max(0, window / 2);
        var y = new float[n];
        var buf = new float[2 * h + 1];
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h), m = b - a + 1;
            for (int j = 0; j < m; j++) buf[j] = x[a + j];
            Array.Sort(buf, 0, m);
            y[i] = (m & 1) == 1 ? buf[m / 2] : 0.5f * (buf[m / 2 - 1] + buf[m / 2]);
        }
        return y;
    }

    /// <summary>Hann (raised cosine) taper of length n.</summary>
    public static float[] Hann(int n)
    {
        var w = new float[n];
        if (n == 1) { w[0] = 1; return w; }
        for (int i = 0; i < n; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        return w;
    }

    /// <summary>Linear interpolation of y(x) at xi (x ascending). Out-of-range returns NaN unless clamp.</summary>
    public static double Interp(ReadOnlySpan<double> x, ReadOnlySpan<double> y, double xi, bool clamp = false)
    {
        int n = x.Length;
        if (n == 0) return double.NaN;
        if (xi <= x[0]) return clamp || xi == x[0] ? y[0] : double.NaN;
        if (xi >= x[n - 1]) return clamp || xi == x[n - 1] ? y[n - 1] : double.NaN;
        int lo = 0, hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (x[mid] <= xi) lo = mid; else hi = mid;
        }
        double t = (xi - x[lo]) / (x[hi] - x[lo]);
        return y[lo] + t * (y[hi] - y[lo]);
    }

    /// <summary>Linearly interpolated sample of a regularly sampled trace at fractional index.</summary>
    public static float SampleLinear(ReadOnlySpan<float> trace, double index)
    {
        if (index < 0 || index > trace.Length - 1 || trace.Length == 0) return float.NaN;
        int i0 = (int)Math.Floor(index);
        if (i0 >= trace.Length - 1) return trace[^1];
        double t = index - i0;
        return (float)(trace[i0] * (1 - t) + trace[i0 + 1] * t);
    }

    /// <summary>Windowed-sinc (8-point, Hann) interpolation at fractional index.</summary>
    public static float SampleSinc(ReadOnlySpan<float> trace, double index, int halfWidth = 4)
    {
        int n = trace.Length;
        if (n == 0 || index < 0 || index > n - 1) return float.NaN;
        int i0 = (int)Math.Floor(index);
        double frac = index - i0;
        if (frac < 1e-9) return trace[i0];
        double sum = 0, wsum = 0;
        for (int k = -halfWidth + 1; k <= halfWidth; k++)
        {
            int j = i0 + k;
            if (j < 0 || j >= n) continue;
            double xx = k - frac;
            double sinc = Math.Abs(xx) < 1e-12 ? 1 : Math.Sin(Math.PI * xx) / (Math.PI * xx);
            double w = 0.5 + 0.5 * Math.Cos(Math.PI * xx / halfWidth);
            sum += trace[j] * sinc * w;
            wsum += sinc * w;
        }
        return wsum == 0 ? float.NaN : (float)(sum / wsum);
    }

    /// <summary>Fits a parabola through three samples around index i and returns the fractional extremum position.</summary>
    public static double ParabolicPeak(ReadOnlySpan<float> x, int i)
    {
        if (i <= 0 || i >= x.Length - 1) return i;
        double ym = x[i - 1], y0 = x[i], yp = x[i + 1];
        double den = ym - 2 * y0 + yp;
        if (Math.Abs(den) < 1e-20) return i;
        double d = 0.5 * (ym - yp) / den;
        return i + Math.Clamp(d, -0.5, 0.5);
    }

    /// <summary>Fills NaN gaps by linear interpolation; leading/trailing NaNs are held at the nearest valid value if extend.</summary>
    public static void FillGapsLinear(Span<float> x, bool extend = true, int maxGap = int.MaxValue)
    {
        int n = x.Length, i = 0, lastValid = -1;
        while (i < n)
        {
            if (!float.IsNaN(x[i])) { lastValid = i; i++; continue; }
            int start = i;
            while (i < n && float.IsNaN(x[i])) i++;
            int gap = i - start;
            if (lastValid >= 0 && i < n)
            {
                if (gap <= maxGap)
                    for (int k = start; k < i; k++)
                    {
                        float t = (k - lastValid) / (float)(i - lastValid);
                        x[k] = x[lastValid] * (1 - t) + x[i] * t;
                    }
            }
            else if (extend && gap <= maxGap)
            {
                if (lastValid < 0 && i < n) for (int k = start; k < i; k++) x[k] = x[i];
                else if (lastValid >= 0) for (int k = start; k < i; k++) x[k] = x[lastValid];
            }
        }
    }
}
