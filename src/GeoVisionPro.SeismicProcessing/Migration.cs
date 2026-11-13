using System.Numerics;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.SeismicProcessing;

/// <summary>
/// 2D post-stack Kirchhoff time migration (diffraction summation) applied inline by inline:
/// image(x0, τ) = Σ_x w · ρ[d](x, t), t = sqrt(τ² + 4(x - x0)²/v_rms(τ)²), w = (τ/t)/sqrt(t), with the
/// half-derivative (ρ) pre-filter √(-iω). Exploding-reflector model; amplitudes are normalised to the input RMS.
/// </summary>
public sealed class KirchhoffTimeMigrationOperation : EnsembleOperation
{
    public override string Id => "kirchhoff";
    public override string DisplayName => "Kirchhoff Post-stack Time Migration (2D)";
    public override string Category => "Migration";
    public override string Description => "Diffraction-summation migration of each inline with an RMS velocity function, aperture and dip limit. 2D only (no crossline migration).";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Text("velocity", "RMS velocity t(s):v(m/s)", "0:1800, 2.0:2800"), P.Num("aperture", "Half aperture", 1500, "m", 10),
        P.Num("maxDip", "Maximum dip angle", 60, "deg", 1, 89)
    ];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var vf = VelocityFunction.Parse(p.GetString("velocity"));
        var result = Migrate(data, ctx.TraceSpacing, ctx.SampleInterval, ctx.StartTime, vf, p.GetDouble("aperture", 1500), p.GetDouble("maxDip", 60), ctx.CancellationToken);
        Array.Copy(result.Data, data.Data, data.Data.Length);
    }

    public static Array2D Migrate(Array2D section, double dx, double dt, double t0, VelocityFunction vrms, double aperture, double maxDipDeg, CancellationToken ct = default)
    {
        int ntr = section.Rows, ns = section.Cols;
        // half-derivative pre-filter
        var filtered = new Array2D(ntr, ns);
        int nfft = Fft.NextPowerOfTwo(2 * ns);
        var halfDeriv = new Complex[nfft];
        for (int k = 0; k < nfft; k++)
        {
            double f = Fft.BinFrequency(k, nfft, dt);
            double mag = Math.Sqrt(Math.Abs(2 * Math.PI * f));
            halfDeriv[k] = f == 0 ? Complex.Zero : Complex.FromPolarCoordinates(mag, f > 0 ? -Math.PI / 4 : Math.PI / 4);
        }
        for (int r = 0; r < ntr; r++)
        {
            var s = Fft.Forward(section.Row(r), nfft);
            for (int k = 0; k < nfft; k++) s[k] *= halfDeriv[k];
            Fft.InverseReal(s, ns).CopyTo(filtered.Row(r));
        }
        var v = vrms.Sample(ns, dt, t0);
        double tanMax = Math.Tan(maxDipDeg * Math.PI / 180);
        var output = new Array2D(ntr, ns);
        Parallel.For(0, ntr, new ParallelOptions { CancellationToken = ct }, ix0 =>
        {
            var outRow = output.Row(ix0);
            for (int k = 0; k < ns; k++)
            {
                double tau = t0 + k * dt;
                if (tau <= 0) continue;
                double vk = v[k];
                double halfAp = Math.Min(aperture, tanMax * vk * tau / 2);
                int nAp = (int)(halfAp / dx);
                double sum = 0;
                for (int ix = Math.Max(0, ix0 - nAp); ix <= Math.Min(ntr - 1, ix0 + nAp); ix++)
                {
                    double h = (ix - ix0) * dx;
                    double t = Math.Sqrt(tau * tau + 4 * h * h / (vk * vk));
                    double idx = (t - t0) / dt;
                    if (idx > ns - 1) continue;
                    float a = Dsp.SampleLinear(filtered.Row(ix), idx);
                    sum += a * (tau / t) / Math.Sqrt(t);
                }
                outRow[k] = (float)sum;
            }
        });
        double rmsIn = Dsp.Rms(section.Data), rmsOut = Dsp.Rms(output.Data);
        if (rmsOut > 0) { float sc = (float)(rmsIn / rmsOut); for (int i = 0; i < output.Data.Length; i++) output.Data[i] *= sc; }
        return output;
    }
}

/// <summary>
/// Gazdag (1978) phase-shift post-stack time migration for a vertically varying velocity: the wavefield
/// P(k_x, ω) is downward continued in two-way vertical time with exp(iωΔτ√(1 - (v_int k_x / 2ω)²)) and imaged at t = 0.
/// Exact for v(τ) media; evanescent energy is discarded.
/// </summary>
public sealed class PhaseShiftMigrationOperation : EnsembleOperation
{
    public override string Id => "phaseshift";
    public override string DisplayName => "Phase-shift Post-stack Time Migration (2D)";
    public override string Category => "Migration";
    public override string Description => "Frequency-wavenumber migration exact for laterally invariant velocity; interval velocity derived from the RMS function by Dix conversion.";
    public override OperationScope Scope => OperationScope.Section;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        P.Text("velocity", "RMS velocity t(s):v(m/s)", "0:1800, 2.0:2800"), P.Num("fmax", "Maximum frequency", 100, "Hz", 1)
    ];

    public override void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        var vf = VelocityFunction.Parse(p.GetString("velocity"));
        var vrms = vf.Sample(data.Cols, ctx.SampleInterval, ctx.StartTime);
        var vint = VelocityFunction.DixIntervalFromRms(vrms, ctx.SampleInterval, ctx.StartTime);
        var result = Migrate(data, ctx.TraceSpacing, ctx.SampleInterval, vint, p.GetDouble("fmax", 100), ctx.CancellationToken);
        Array.Copy(result.Data, data.Data, data.Data.Length);
    }

    public static Array2D Migrate(Array2D section, double dx, double dt, double[] vInterval, double fmax, CancellationToken ct = default)
    {
        int ntr = section.Rows, ns = section.Cols;
        int nk = Fft.NextPowerOfTwo(2 * ntr), nf = Fft.NextPowerOfTwo(2 * ns);
        var m = new float[ntr, ns];
        for (int r = 0; r < ntr; r++) for (int c = 0; c < ns; c++) m[r, c] = section[r, c];
        var spec = Fft.Forward2D(m, nk, nf);   // [k, f]
        double df = 1.0 / (nf * dt);
        int fmaxBin = Math.Min(nf / 2, (int)(fmax / df));
        var image = new Complex[nk, ns];
        Parallel.For(0, nk, new ParallelOptions { CancellationToken = ct }, ik =>
        {
            double kx = Fft.BinFrequency(ik, nk, dx); // cycles/m
            var p = new Complex[nf];
            for (int jf = 0; jf < nf; jf++) p[jf] = spec[ik, jf];
            for (int it = 0; it < ns; it++)
            {
                // image at current depth step = sum over frequencies (inverse FT at t = 0)
                Complex sum = Complex.Zero;
                for (int jf = 0; jf < nf; jf++) sum += p[jf];
                image[ik, it] = sum / nf;
                double v = vInterval[it];
                for (int jf = 0; jf < nf; jf++)
                {
                    double f = Fft.BinFrequency(jf, nf, dt);
                    if (Math.Abs(f) > fmaxBin * df || f == 0) { p[jf] = Complex.Zero; continue; }
                    double arg = 1 - Math.Pow(v * kx / (2 * f), 2);
                    if (arg <= 0) { p[jf] = Complex.Zero; continue; }
                    double phase = 2 * Math.PI * f * dt * Math.Sqrt(arg);
                    p[jf] *= Complex.FromPolarCoordinates(1, phase);
                }
            }
        });
        // inverse FFT over k for each time sample
        var output = new Array2D(ntr, ns);
        var col = new Complex[nk];
        for (int it = 0; it < ns; it++)
        {
            for (int ik = 0; ik < nk; ik++) col[ik] = image[ik, it];
            Fft.InverseInPlace(col);
            for (int r = 0; r < ntr; r++) output[r, it] = (float)col[r].Real;
        }
        return output;
    }
}
