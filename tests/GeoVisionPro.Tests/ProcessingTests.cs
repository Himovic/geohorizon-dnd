using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.SeismicProcessing;

namespace GeoVisionPro.Tests;

public class ProcessingTests
{
    private static ProcessingContext Ctx(double dt = 0.004, double dx = 25, double[]? offsets = null) => new(dt, 0, dx, offsets);

    private static ParameterSet Defaults(string id) => ParameterSet.Defaults(ProcessingCatalog.Get(id).Parameters);

    [Fact]
    public void Catalog_AllOperationsHaveValidDefaults()
    {
        foreach (var op in ProcessingCatalog.All)
        {
            var p = ParameterSet.Defaults(op.Parameters);
            Assert.True(p.Validate(op.Parameters).IsValid, op.Id);
            Assert.False(string.IsNullOrWhiteSpace(op.Description));
        }
    }

    [Fact]
    public void ParameterSet_ValidatesRanges()
    {
        var op = ProcessingCatalog.Get("butterworth");
        var p = Defaults("butterworth").Set("order", 99);
        Assert.False(p.Validate(op.Parameters).IsValid);
        var json = Defaults("bandpass").ToJson();
        Assert.Equal(Defaults("bandpass")["f3"], ParameterSet.FromJson(json)["f3"]);
    }

    [Fact]
    public void Agc_NormalisesRms()
    {
        var t = Enumerable.Range(0, 500).Select(i => (float)(Math.Sin(i * 0.2) * Math.Exp(-i / 100.0) * 10)).ToArray();
        new AgcOperation().ProcessTrace(t, Defaults("agc").Set("window", 200), Ctx());
        Assert.InRange(Dsp.Rms(t.AsSpan(50, 100)), 0.8, 1.2);
        Assert.InRange(Dsp.Rms(t.AsSpan(350, 100)), 0.8, 1.2);
    }

    [Fact]
    public void SpikingDecon_CompressesMinimumPhaseWavelet()
    {
        // minimum-phase wavelet (exponentially damped) convolved with sparse reflectivity
        var w = Enumerable.Range(0, 30).Select(i => (float)(Math.Exp(-i / 5.0) * Math.Cos(i * 0.6))).ToArray();
        var rng = new Random(1);
        var r = new float[1000];
        for (int i = 0; i < r.Length; i += 1) r[i] = (float)(rng.NextDouble() < 0.1 ? rng.NextDouble() * 2 - 1 : 0);
        var x = Dsp.Convolve(r, w)[..1000];
        var before = Dsp.Correlation(x, r);
        new SpikingDeconvolutionOperation().ProcessTrace(x, Defaults("decon_spike").Set("length", 160).Set("prewhite", 0.1), Ctx());
        var after = Dsp.Correlation(x, r);
        Assert.True(after > before + 0.2 && after > 0.85, $"correlation with reflectivity {before:0.00} -> {after:0.00}");
    }

    [Fact]
    public void PredictiveDecon_AttenuatesPeriodicMultiples()
    {
        // primary spikes + water-layer reverberation with period 40 samples and reflection coefficient -0.6
        var x = new float[800];
        foreach (var p in new[] { 50, 300, 520 })
        {
            double a = 1;
            for (int k = p; k < 800; k += 40) { x[k] += (float)a; a *= -0.6; }
        }
        var acBefore = Dsp.AutoCorrelate(x, 45);
        new PredictiveDeconvolutionOperation().ProcessTrace(x, Defaults("decon_pred").Set("gap", 4 * 30).Set("length", 4 * 30).Set("prewhite", 0.1), Ctx());
        var acAfter = Dsp.AutoCorrelate(x, 45);
        Assert.True(Math.Abs(acAfter[40] / acAfter[0]) < 0.3 * Math.Abs(acBefore[40] / acBefore[0]));
    }

    [Fact]
    public void SpectralWhitening_FlattensSpectrumInBand()
    {
        var w = Wavelet.Ricker(20, 0.004, 0.3);
        var rng = new Random(3);
        var r = Enumerable.Range(0, 1000).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        var x = Dsp.ConvolveSame(r, w.Samples, w.CenterIndex);
        double Ratio(float[] t) { var (f, a) = Fft.AmplitudeSpectrum(t, 0.004, 1024); var sm = Dsp.MovingAverage(a.Select(v => (float)v).ToArray(), 15); return sm[(int)(50 / f[1])] / sm[(int)(20 / f[1])]; }
        double before = Ratio(x);
        new SpectralWhiteningOperation().ProcessTrace(x, Defaults("whiten"), Ctx());
        double after = Ratio(x);
        Assert.True(after > 2 * before && after > 0.5, $"50/20 Hz ratio {before:0.00} -> {after:0.00}");
    }

    [Fact]
    public void FkFilter_RemovesSlowLinearEvent()
    {
        int ntr = 64, ns = 256; double dt = 0.004, dx = 5;
        var sec = new Array2D(ntr, ns);
        var w = Wavelet.Ricker(25, dt, 0.1);
        for (int r = 0; r < ntr; r++)
        {
            int kFlat = 128, kSlow = 20 + (int)Math.Round(r * dx / 1000 / dt); // 1000 m/s coherent noise (unaliased)
            foreach (var (k, a) in new[] { (kFlat, 1.0), (kSlow, 1.0) })
                for (int j = 0; j < w.Length; j++) { int kk = k + j - w.CenterIndex; if (kk >= 0 && kk < ns) sec[r, kk] += (float)(a * w.Samples[j]); }
        }
        var before = sec.Clone();
        new FkFilterOperation().ProcessEnsemble(sec, Defaults("fk").Set("vcut", 1500), Ctx(dt, dx));
        // energy around the slow event at the far traces should drop while the flat event survives
        double slowBefore = 0, slowAfter = 0, flatBefore = 0, flatAfter = 0;
        for (int r = 20; r < 44; r++)
        {
            int kSlow = 20 + (int)Math.Round(r * dx / 1000 / dt);
            for (int k = kSlow - 3; k <= kSlow + 3; k++) { slowBefore += before[r, k] * before[r, k]; slowAfter += sec[r, k] * sec[r, k]; }
            for (int k = 125; k <= 131; k++) { flatBefore += before[r, k] * before[r, k]; flatAfter += sec[r, k] * sec[r, k]; }
        }
        Assert.True(slowAfter < 0.1 * slowBefore, $"slow event energy {slowBefore:0.00} -> {slowAfter:0.00}");
        Assert.True(flatAfter > 0.8 * flatBefore, $"flat event energy {flatBefore:0.00} -> {flatAfter:0.00}");
    }

    [Fact]
    public void FxDecon_ImprovesSignalToNoise()
    {
        int ntr = 40, ns = 300; double dt = 0.004;
        var w = Wavelet.Ricker(25, dt, 0.12);
        var clean = new Array2D(ntr, ns);
        for (int r = 0; r < ntr; r++) foreach (var k0 in new[] { 80 + r, 200 - r / 2 })
                for (int j = 0; j < w.Length; j++) { int k = k0 + j - w.CenterIndex; if (k >= 0 && k < ns) clean[r, k] += w.Samples[j]; }
        var rng = new Random(5);
        var noisy = clean.Clone();
        for (int i = 0; i < noisy.Data.Length; i++) noisy.Data[i] += (float)((rng.NextDouble() - 0.5) * 0.8);
        double ErrRms(Array2D a) => Math.Sqrt(a.Data.Select((v, i) => Math.Pow(v - clean.Data[i], 2)).Average());
        double before = ErrRms(noisy);
        new FxDeconvolutionOperation().ProcessEnsemble(noisy, Defaults("fxdecon"), Ctx(dt));
        double after = ErrRms(noisy);
        Assert.True(after < 0.7 * before, $"error RMS {before:0.000} -> {after:0.000}");
    }

    [Fact]
    public void Nmo_FlattensHyperbola_AndSemblancePeaksAtTrueVelocity()
    {
        double dt = 0.004, v = 2200, t0 = 0.8; int ns = 400;
        var offsets = Enumerable.Range(0, 24).Select(i => i * 100.0).ToArray();
        var w = Wavelet.Ricker(25, dt, 0.12);
        var g = new Array2D(offsets.Length, ns);
        for (int r = 0; r < offsets.Length; r++)
        {
            double tx = Math.Sqrt(t0 * t0 + offsets[r] * offsets[r] / (v * v));
            for (int j = 0; j < w.Length; j++) { double kk = tx / dt + j - w.CenterIndex; int k = (int)Math.Round(kk); if (k >= 0 && k < ns) g[r, k] += w.Samples[j]; }
        }
        var vels = Enumerable.Range(0, 41).Select(i => 1500.0 + i * 50).ToArray();
        var s = Nmo.Semblance(g, offsets, vels, dt, 0, 5);
        int k0 = (int)(t0 / dt);
        int best = Enumerable.Range(0, vels.Length).MaxBy(i => s[i, k0]);
        Assert.InRange(vels[best], 2100, 2300);
        Nmo.Correct(g, offsets, VelocityFunction.Constant(v), dt, 0, 0.6);
        for (int r = 0; r < offsets.Length; r++)
        {
            if (offsets[r] > 1600) continue; // beyond the stretch-mute zone the trace is (correctly) muted
            var row = g.Row(r).ToArray();
            int peak = Enumerable.Range(150, 100).MaxBy(k => row[k]);
            Assert.InRange(peak, k0 - 1, k0 + 1);
        }
        var stack = Nmo.Stack(g);
        Assert.InRange(Array.IndexOf(stack, stack.Max()), k0 - 1, k0 + 1);
    }

    [Fact]
    public void Dix_RoundTrip()
    {
        double[] vint = [1500, 1500, 2000, 2000, 2500, 2500, 3000];
        var vrms = VelocityFunction.RmsFromInterval(vint, 0.004);
        var back = VelocityFunction.DixIntervalFromRms(vrms, 0.004, 0.004);
        for (int i = 1; i < vint.Length; i++) Assert.Equal(vint[i], back[i], 0);
    }

    [Fact]
    public void Migration_CollapsesDiffraction()
    {
        int ntr = 101, ns = 300; double dt = 0.004, dx = 12.5, v = 2000, t0 = 0.6;
        var w = Wavelet.Ricker(25, dt, 0.12);
        var sec = new Array2D(ntr, ns);
        for (int r = 0; r < ntr; r++)
        {
            double x = (r - 50) * dx;
            double t = Math.Sqrt(t0 * t0 + 4 * x * x / (v * v));
            for (int j = 0; j < w.Length; j++) { int k = (int)Math.Round(t / dt) + j - w.CenterIndex; if (k >= 0 && k < ns) sec[r, k] += w.Samples[j]; }
        }
        double Focus(Array2D a)
        {
            double centre = 0, total = 0;
            for (int r = 0; r < ntr; r++) for (int k = 0; k < ns; k++) { double e = a[r, k] * a[r, k]; total += e; if (Math.Abs(r - 50) <= 3 && Math.Abs(k - 150) <= 8) centre += e; }
            return centre / total;
        }
        double before = Focus(sec);
        var kir = KirchhoffTimeMigrationOperation.Migrate(sec, dx, dt, 0, VelocityFunction.Constant(v), 2000, 80);
        var ps = PhaseShiftMigrationOperation.Migrate(sec, dx, dt, Enumerable.Repeat(v, ns).ToArray(), 100);
        Assert.True(Focus(kir) > 3 * before, $"Kirchhoff focus {before:0.000} -> {Focus(kir):0.000}");
        Assert.True(Focus(ps) > 3 * before, $"Phase-shift focus {before:0.000} -> {Focus(ps):0.000}");
    }

    [Fact]
    public void Radon_RemovesParabolicMultiple_KeepsFlatPrimary()
    {
        double dt = 0.004; int ns = 300;
        var offsets = Enumerable.Range(0, 30).Select(i => i * 80.0).ToArray();
        double xmax = offsets[^1];
        var w = Wavelet.Ricker(25, dt, 0.12);
        var g = new Array2D(offsets.Length, ns);
        for (int r = 0; r < offsets.Length; r++)
        {
            foreach (var (t, q) in new[] { (0.5, 0.0), (0.7, 0.12) }) // flat primary and parabolic multiple (120 ms at far offset)
            {
                double tt = t + q * Math.Pow(offsets[r] / xmax, 2);
                for (int j = 0; j < w.Length; j++) { int k = (int)Math.Round(tt / dt) + j - w.CenterIndex; if (k >= 0 && k < ns) g[r, k] += w.Samples[j]; }
            }
        }
        var before = g.Clone();
        new ParabolicRadonOperation().ProcessEnsemble(g, Defaults("radon").Set("qcut", 40).Set("qmin", -40).Set("qmax", 200), Ctx(dt, 25, offsets));
        double E(Array2D a, Func<int, double> tf) { double s = 0; for (int r = 0; r < offsets.Length; r++) { int k = (int)Math.Round(tf(r) / dt); for (int d = -4; d <= 4; d++) s += a[r, k + d] * a[r, k + d]; } return s; }
        double multBefore = E(before, r => 0.7 + 0.12 * Math.Pow(offsets[r] / xmax, 2)), multAfter = E(g, r => 0.7 + 0.12 * Math.Pow(offsets[r] / xmax, 2));
        double primBefore = E(before, _ => 0.5), primAfter = E(g, _ => 0.5);
        Assert.True(multAfter < 0.2 * multBefore, $"multiple {multBefore:0.00} -> {multAfter:0.00}");
        Assert.True(primAfter > 0.7 * primBefore, $"primary {primBefore:0.00} -> {primAfter:0.00}");
    }

    [Fact]
    public void Engine_RunsSequenceOnVolume()
    {
        using var input = TestData.Ramp(6, 8, 64);
        using var output = new InMemoryVolume("out", input.Geometry);
        ProcessingEngine.RunVolume(input, output, [new ProcessingStep("dc", new ParameterSet()), new ProcessingStep("scale", Defaults("scale").Set("factor", 2.0))]);
        var t = output.ReadTrace(12, 3);
        Assert.Equal(0, t.Average(), 3);
        var src = input.ReadTrace(12, 3); var mean = src.Average();
        Assert.Equal((src[10] - mean) * 2, t[10], 2);
    }

    [Fact]
    public void Engine_RejectsGatherOperationOnPostStack()
    {
        using var input = TestData.Ramp();
        using var output = new InMemoryVolume("out", input.Geometry);
        Assert.Throws<InvalidOperationException>(() => ProcessingEngine.RunVolume(input, output, [new ProcessingStep("nmo", Defaults("nmo"))]));
    }

    [Fact]
    public void NmoStack_OfSyntheticGathers_MatchesZeroOffsetModel()
    {
        var gen = TestData.SmallGenerator();
        using var gathers = gen.BuildGathers(6, 8, 1500, 16);
        using var stack = new InMemoryVolume("stack", gathers.Geometry);
        // RMS velocity at the gather location
        int il = gathers.Geometry.Inlines.First, xl = gathers.Geometry.Crosslines.First;
        var vf = VelocityFunction.Parse(gen.RmsVelocityFunction(il - gen.FirstInline, xl - gen.FirstCrossline));
        ProcessingEngine.NmoStack(gathers, stack, vf, 0.5);
        var tr = stack.ReadTrace(il, xl);
        var zeroOffset = gathers.ReadGather(il, xl)!.Row(0).ToArray();
        Assert.True(Dsp.Correlation(tr, zeroOffset) > 0.8, $"stack vs zero-offset correlation {Dsp.Correlation(tr, zeroOffset):0.00}");
    }
}
