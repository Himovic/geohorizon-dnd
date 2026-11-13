namespace GeoVisionPro.Core.Numerics;

/// <summary>
/// A sampled seismic wavelet. <see cref="CenterIndex"/> is the sample corresponding to time zero.
/// Polarity convention (SEG normal): a positive reflection coefficient (increase in impedance)
/// convolved with a zero-phase wavelet yields a positive central peak.
/// </summary>
public sealed class Wavelet
{
    public string Name { get; set; }
    public float[] Samples { get; }
    public double SampleInterval { get; }   // seconds
    public int CenterIndex { get; }

    public Wavelet(string name, float[] samples, double sampleInterval, int centerIndex)
    {
        if (sampleInterval <= 0) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        Name = name; Samples = samples; SampleInterval = sampleInterval; CenterIndex = centerIndex;
    }

    public int Length => Samples.Length;
    public double StartTime => -CenterIndex * SampleInterval;
    public double TimeAt(int i) => (i - CenterIndex) * SampleInterval;

    /// <summary>Ricker (Mexican hat) wavelet: w(t) = (1 - 2π²f²t²) exp(-π²f²t²).</summary>
    public static Wavelet Ricker(double peakFrequency, double dt, double length)
    {
        int half = (int)Math.Round(length / 2 / dt);
        var s = new float[2 * half + 1];
        for (int i = -half; i <= half; i++)
        {
            double t = i * dt;
            double a = Math.PI * Math.PI * peakFrequency * peakFrequency * t * t;
            s[i + half] = (float)((1 - 2 * a) * Math.Exp(-a));
        }
        return new Wavelet($"Ricker {peakFrequency:0.#} Hz", s, dt, half);
    }

    /// <summary>Zero-phase Ormsby (trapezoidal spectrum f1-f2-f3-f4) wavelet, normalized to unit peak.</summary>
    public static Wavelet Ormsby(double f1, double f2, double f3, double f4, double dt, double length)
    {
        if (!(f1 <= f2 && f2 < f3 && f3 <= f4)) throw new ArgumentException("Ormsby corner frequencies must satisfy f1<=f2<f3<=f4.");
        int half = (int)Math.Round(length / 2 / dt);
        var s = new float[2 * half + 1];
        static double Term(double f, double t) => Math.Abs(t) < 1e-12 ? Math.PI * f * f : Math.Pow(Math.Sin(Math.PI * f * t), 2) / (Math.PI * t * t);
        double maxV = 0;
        for (int i = -half; i <= half; i++)
        {
            double t = i * dt;
            double v = (Term(f4, t) - Term(f3, t)) / Math.Max(f4 - f3, 1e-9)
                     - (Term(f2, t) - Term(f1, t)) / Math.Max(f2 - f1, 1e-9);
            s[i + half] = (float)v;
            maxV = Math.Max(maxV, Math.Abs(v));
        }
        if (maxV > 0) for (int i = 0; i < s.Length; i++) s[i] = (float)(s[i] / maxV);
        return new Wavelet($"Ormsby {f1}-{f2}-{f3}-{f4} Hz", s, dt, half);
    }

    /// <summary>Returns a constant-phase rotated copy.</summary>
    public Wavelet RotatePhase(double degrees)
    {
        var rotated = Dsp.PhaseRotate(Samples, degrees);
        return new Wavelet($"{Name} ({degrees:+0;-0}°)", rotated, SampleInterval, CenterIndex);
    }

    public Wavelet Scaled(double factor)
    {
        var s = Samples.Select(v => (float)(v * factor)).ToArray();
        return new Wavelet(Name, s, SampleInterval, CenterIndex);
    }

    public Wavelet NormalizedToPeak()
    {
        float m = Dsp.MaxAbs(Samples);
        return m > 0 ? Scaled(1.0 / m) : this;
    }

    /// <summary>Resamples to a new interval using windowed-sinc interpolation, keeping t=0 on a sample.</summary>
    public Wavelet Resample(double newDt)
    {
        if (Math.Abs(newDt - SampleInterval) < 1e-12) return this;
        int halfBefore = (int)Math.Floor(CenterIndex * SampleInterval / newDt);
        int halfAfter = (int)Math.Floor((Length - 1 - CenterIndex) * SampleInterval / newDt);
        var s = new float[halfBefore + halfAfter + 1];
        for (int i = -halfBefore; i <= halfAfter; i++)
        {
            double idx = CenterIndex + i * newDt / SampleInterval;
            s[i + halfBefore] = Dsp.SampleSinc(Samples, idx);
        }
        return new Wavelet(Name, s, newDt, halfBefore);
    }

    /// <summary>Estimates the constant phase (degrees) that best rotates this wavelet to zero phase.</summary>
    public double EstimateConstantPhase()
    {
        double best = 0, bestVal = double.MinValue;
        for (int deg = -180; deg < 180; deg++)
        {
            var r = Dsp.PhaseRotate(Samples, -deg);
            double v = r[CenterIndex];
            if (v > bestVal) { bestVal = v; best = deg; }
        }
        return best;
    }

    public (double[] Frequencies, double[] Amplitudes) AmplitudeSpectrum() => Fft.AmplitudeSpectrum(Samples, SampleInterval, Fft.NextPowerOfTwo(Math.Max(256, Length)));
}
