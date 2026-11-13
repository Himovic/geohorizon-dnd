using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Core.Units;

namespace GeoVisionPro.Tests;

public class CoreNumericsTests
{
    [Fact]
    public void Fft_RoundTrip_ReconstructsSignal()
    {
        var x = Enumerable.Range(0, 100).Select(i => (float)Math.Sin(i * 0.3) + 0.2f * i).ToArray();
        var spec = Fft.Forward(x, 128);
        var y = Fft.InverseReal(spec, 100);
        for (int i = 0; i < 100; i++) Assert.Equal(x[i], y[i], 3);
    }

    [Fact]
    public void Hilbert_OfCosine_IsSine()
    {
        double dt = 0.004, f = 25;
        var x = Enumerable.Range(0, 512).Select(i => (float)Math.Cos(2 * Math.PI * f * i * dt)).ToArray();
        var h = Dsp.Hilbert(x);
        for (int i = 100; i < 400; i++) Assert.True(Math.Abs(Math.Sin(2 * Math.PI * f * i * dt) - h[i]) < 0.02, $"sample {i}");
        var a = Dsp.AnalyticSignal(x);
        for (int i = 100; i < 400; i++) Assert.True(Math.Abs(1.0 - a[i].Magnitude) < 0.02);
    }

    [Fact]
    public void Levinson_MatchesDenseSolve()
    {
        double[] r = [4, 2, 1, 0.5, 0.2];
        double[] g = [1, 0, 0.5, 0, 1];
        var f = Dsp.LevinsonSolve(r, g);
        var m = new double[5, 5];
        for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) m[i, j] = r[Math.Abs(i - j)];
        var expected = LinearSolvers.SolveDense(m, g);
        for (int i = 0; i < 5; i++) Assert.Equal(expected[i], f[i], 9);
    }

    [Fact]
    public void ConvolveSame_CentresWavelet()
    {
        float[] x = [0, 0, 0, 1, 0, 0, 0];
        var w = Wavelet.Ricker(30, 0.004, 0.024);
        var y = Dsp.ConvolveSame(x, w.Samples, w.CenterIndex);
        Assert.Equal(3, Array.IndexOf(y, y.Max()));
        Assert.Equal(1.0, y[3], 5);
    }

    [Fact]
    public void BandPass_RemovesOutOfBandSine()
    {
        double dt = 0.002;
        var x = Enumerable.Range(0, 1000).Select(i => (float)(Math.Sin(2 * Math.PI * 30 * i * dt) + Math.Sin(2 * Math.PI * 150 * i * dt))).ToArray();
        FrequencyFilters.BandPass(x, dt, 10, 15, 50, 70);
        var (f, a) = Fft.AmplitudeSpectrum(x, dt, 2048);
        double a30 = a[(int)Math.Round(30 / f[1])], a150 = a[(int)Math.Round(150 / f[1])];
        Assert.True(a150 < a30 * 0.01, $"150 Hz should be attenuated ({a150} vs {a30})");
    }

    [Fact]
    public void PhaseRotation_By180_InvertsPolarity()
    {
        var w = Wavelet.Ricker(25, 0.002, 0.2);
        var r = Dsp.PhaseRotate(w.Samples, 180);
        for (int i = 20; i < w.Length - 20; i++) Assert.Equal(-w.Samples[i], r[i], 2);
    }

    [Fact]
    public void Wavelet_EstimateConstantPhase_RecoversRotation()
    {
        var w = Wavelet.Ricker(30, 0.002, 0.3).RotatePhase(-60);
        Assert.InRange(w.EstimateConstantPhase(), -64, -56);
    }

    [Fact]
    public void Ormsby_HasUnitPeakAndBandLimitedSpectrum()
    {
        var w = Wavelet.Ormsby(5, 10, 50, 70, 0.002, 0.4);
        Assert.Equal(1.0, w.Samples.Max(), 6);
        var (f, a) = w.AmplitudeSpectrum();
        double max = a.Max();
        int k100 = (int)Math.Round(100 / f[1]);
        Assert.True(a[k100] < 0.05 * max);
    }

    [Fact]
    public void LinearRegression_RecoversLine()
    {
        var x = Enumerable.Range(0, 50).Select(i => (double)i).ToArray();
        var y = x.Select(v => 3 + 2 * v).ToArray();
        var fit = Statistics.LinearRegression(x, y);
        Assert.Equal(3, fit.Intercept, 9);
        Assert.Equal(2, fit.Slope, 9);
        Assert.Equal(1, fit.R, 9);
    }

    [Fact]
    public void Statistics_Percentiles()
    {
        var s = Statistics.Summarize(Enumerable.Range(1, 101).Select(i => (double)i).Append(double.NaN));
        Assert.Equal(101, s.Count);
        Assert.Equal(1, s.MissingCount);
        Assert.Equal(51, s.P50, 9);
        Assert.Equal(11, s.P10, 9);
    }

    [Fact]
    public void ConjugateGradient_SolvesSpdSystem()
    {
        double[,] a = { { 4, 1, 0 }, { 1, 3, 1 }, { 0, 1, 2 } };
        double[] b = [1, 2, 3];
        var x = new double[3];
        LinearSolvers.ConjugateGradient((v, y) => { for (int i = 0; i < 3; i++) { y[i] = 0; for (int j = 0; j < 3; j++) y[i] += a[i, j] * v[j]; } }, b, x, 50, 1e-12);
        var exact = LinearSolvers.SolveDense(a, b);
        for (int i = 0; i < 3; i++) Assert.Equal(exact[i], x[i], 8);
    }

    [Fact]
    public void FillGaps_InterpolatesLinearly()
    {
        float[] v = [1, float.NaN, float.NaN, 4, float.NaN];
        Dsp.FillGapsLinear(v);
        Assert.Equal([1f, 2f, 3f, 4f, 4f], v);
    }

    [Fact]
    public void SincInterpolation_IsExactOnBandLimitedSignal()
    {
        var x = Enumerable.Range(0, 200).Select(i => (float)Math.Sin(0.2 * i)).ToArray();
        Assert.Equal(Math.Sin(0.2 * 100.5), Dsp.SampleSinc(x, 100.5), 2);
    }
}

public class SpatialAndUnitsTests
{
    [Fact]
    public void GridTransform_RoundTrip_AndControlPointFit()
    {
        var t = new GridTransform(100, 200, 500000, 6700000, 21.65, 12.5, -12.5, 21.65);
        var p = t.ToMap(130, 260);
        var (il, xl) = t.ToGrid(p.X, p.Y);
        Assert.Equal(130, il, 6);
        Assert.Equal(260, xl, 6);
        var pts = new List<(double, double, double, double)>();
        foreach (var (a, b) in new[] { (100, 200), (150, 200), (100, 300), (170, 260) })
        {
            var m = t.ToMap(a, b);
            pts.Add((a, b, m.X, m.Y));
        }
        var fit = GridTransform.FromControlPoints(pts);
        var q = fit.ToMap(130, 260);
        Assert.Equal(p.X, q.X, 4);
        Assert.Equal(p.Y, q.Y, 4);
        Assert.Equal(25, t.InlineSpacing, 1);
    }

    [Fact]
    public void Polygon_ContainsAndArea()
    {
        var sq = new List<Point2D> { new(0, 0), new(10, 0), new(10, 10), new(0, 10) };
        Assert.True(Polygon2D.Contains(sq, new Point2D(5, 5)));
        Assert.False(Polygon2D.Contains(sq, new Point2D(15, 5)));
        Assert.Equal(100, Polygon2D.Area(sq), 9);
    }

    [Fact]
    public void Delaunay_TriangulatesGrid()
    {
        var pts = new List<Point2D>();
        for (int i = 0; i < 5; i++) for (int j = 0; j < 5; j++) pts.Add(new Point2D(i + 0.001 * j * i, j));
        var tris = Delaunay.Triangulate(pts);
        Assert.Equal(2 * 4 * 4, tris.Count);
    }

    [Theory]
    [InlineData(100, "us/ft", "us/m", 328.084)]
    [InlineData(2.65, "g/cc", "kg/m3", 2650)]
    [InlineData(1000, "ft", "m", 304.8)]
    public void Units_Convert(double v, string from, string to, double expected) => Assert.Equal(expected, UnitConverter.Convert(v, from, to), 2);

    [Fact]
    public void Slowness_ToVelocity()
    {
        Assert.Equal(3048, UnitConverter.SlownessToVelocity(100, "us/ft"), 3);
        Assert.Throws<ArgumentException>(() => UnitConverter.Convert(1, "m", "g/cc"));
    }

    [Fact]
    public void IndexRange_Behaviour()
    {
        var r = new IndexRange(100, 200, 2);
        Assert.Equal(51, r.Count);
        Assert.True(r.Contains(150));
        Assert.False(r.Contains(151));
        Assert.Equal(25, r.IndexOf(150));
    }
}
