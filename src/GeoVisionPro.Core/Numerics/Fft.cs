using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace GeoVisionPro.Core.Numerics;

/// <summary>
/// Thin, convention-fixing wrapper over Math.NET Numerics FFTs.
/// Convention: forward transform is unscaled, inverse transform is scaled by 1/N
/// (identical to MATLAB/NumPy), so Inverse(Forward(x)) == x.
/// </summary>
public static class Fft
{
    public static int NextPowerOfTwo(int n)
    {
        if (n < 1) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static Complex[] Forward(ReadOnlySpan<float> real, int nfft)
    {
        var c = new Complex[nfft];
        int n = Math.Min(real.Length, nfft);
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0);
        Fourier.Forward(c, FourierOptions.Matlab);
        return c;
    }

    public static Complex[] Forward(ReadOnlySpan<double> real, int nfft)
    {
        var c = new Complex[nfft];
        int n = Math.Min(real.Length, nfft);
        for (int i = 0; i < n; i++) c[i] = new Complex(real[i], 0);
        Fourier.Forward(c, FourierOptions.Matlab);
        return c;
    }

    public static void ForwardInPlace(Complex[] data) => Fourier.Forward(data, FourierOptions.Matlab);

    public static void InverseInPlace(Complex[] data) => Fourier.Inverse(data, FourierOptions.Matlab);

    /// <summary>Inverse transform returning the real part of the first <paramref name="length"/> samples.</summary>
    public static float[] InverseReal(Complex[] spectrum, int length)
    {
        var c = (Complex[])spectrum.Clone();
        Fourier.Inverse(c, FourierOptions.Matlab);
        var r = new float[length];
        for (int i = 0; i < length && i < c.Length; i++) r[i] = (float)c[i].Real;
        return r;
    }

    /// <summary>Frequency (Hz) of FFT bin k for an nfft-point transform sampled at dt seconds. Negative for upper half.</summary>
    public static double BinFrequency(int k, int nfft, double dt)
    {
        double df = 1.0 / (nfft * dt);
        return k <= nfft / 2 ? k * df : (k - nfft) * df;
    }

    /// <summary>One-sided amplitude spectrum (bins 0..nfft/2).</summary>
    public static (double[] Frequencies, double[] Amplitudes) AmplitudeSpectrum(ReadOnlySpan<float> trace, double dt, int? nfftOverride = null)
    {
        int nfft = nfftOverride ?? NextPowerOfTwo(Math.Max(trace.Length, 2));
        var spec = Forward(trace, nfft);
        int nh = nfft / 2 + 1;
        var f = new double[nh];
        var a = new double[nh];
        for (int k = 0; k < nh; k++)
        {
            f[k] = k / (nfft * dt);
            a[k] = spec[k].Magnitude;
        }
        return (f, a);
    }

    /// <summary>2D FFT of a real matrix [rows, cols], zero-padded to (nr, nc).</summary>
    public static Complex[,] Forward2D(float[,] data, int nr, int nc)
    {
        int r0 = data.GetLength(0), c0 = data.GetLength(1);
        var result = new Complex[nr, nc];
        var row = new Complex[nc];
        for (int r = 0; r < nr; r++)
        {
            Array.Clear(row);
            if (r < r0) for (int c = 0; c < Math.Min(c0, nc); c++) row[c] = data[r, c];
            Fourier.Forward(row, FourierOptions.Matlab);
            for (int c = 0; c < nc; c++) result[r, c] = row[c];
        }
        var col = new Complex[nr];
        for (int c = 0; c < nc; c++)
        {
            for (int r = 0; r < nr; r++) col[r] = result[r, c];
            Fourier.Forward(col, FourierOptions.Matlab);
            for (int r = 0; r < nr; r++) result[r, c] = col[r];
        }
        return result;
    }

    public static float[,] Inverse2DReal(Complex[,] spec, int rows, int cols)
    {
        int nr = spec.GetLength(0), nc = spec.GetLength(1);
        var work = (Complex[,])spec.Clone();
        var col = new Complex[nr];
        for (int c = 0; c < nc; c++)
        {
            for (int r = 0; r < nr; r++) col[r] = work[r, c];
            Fourier.Inverse(col, FourierOptions.Matlab);
            for (int r = 0; r < nr; r++) work[r, c] = col[r];
        }
        var row = new Complex[nc];
        var output = new float[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < nc; c++) row[c] = work[r, c];
            Fourier.Inverse(row, FourierOptions.Matlab);
            for (int c = 0; c < cols; c++) output[r, c] = (float)row[c].Real;
        }
        return output;
    }
}
