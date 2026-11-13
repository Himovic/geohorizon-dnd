using GeoVisionPro.Attributes;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Tests;

public class InterpretationTests
{
    [Fact]
    public void Snap_FindsPeakAndZeroCrossing()
    {
        var t = Enumerable.Range(0, 100).Select(i => (float)Math.Sin(2 * Math.PI * i / 40.0)).ToArray(); // peak at 10, 50; zero-cross down at 20
        Assert.Equal(10, EventSnapping.Snap(t, 12.3, EventType.Peak, 5), 1);
        Assert.Equal(30, EventSnapping.Snap(t, 28, EventType.Trough, 5), 1);
        Assert.Equal(20, EventSnapping.Snap(t, 21, EventType.ZeroCrossingNegative, 5), 1);
        Assert.True(double.IsNaN(EventSnapping.Snap(t, 12, EventType.Trough, 2)));
    }

    [Fact]
    public void AutoTracker_FollowsSyntheticHorizon()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 24, Crosslines = 30, Samples = 320, NoiseLevel = 0.01 };
        using var vol = gen.BuildVolume();
        var truth = gen.BuildHorizons()[4]; // top of carbonate (strong positive reflection)
        var h = Horizon.CreateFor(vol.Geometry, "tracked", "#fff");
        int il = gen.FirstInline + 4, xl = gen.FirstCrossline + 4;
        var tracker = new HorizonAutoTracker(vol);
        var edit = tracker.Track(h, [(il, xl, truth.GetValue(il, xl))], new TrackingParameters { Event = EventType.Peak, MinCorrelation = 0.6, MaxJump = 3 });
        int good = 0, total = 0;
        for (int k = 0; k < h.Values.Length; k++)
        {
            if (double.IsNaN(h.Values[k])) continue;
            total++;
            if (Math.Abs(h.Values[k] - truth.Values[k]) < 0.008) good++;
        }
        Assert.True(total > 0.5 * h.Values.Length, $"coverage {total}/{h.Values.Length}");
        Assert.True(good > 0.95 * total, $"accuracy {good}/{total}");
        edit.Undo();
        Assert.All(h.Values, v => Assert.True(double.IsNaN(v)));
        edit.Redo();
        Assert.Equal(total, h.Values.Count(v => !double.IsNaN(v)));
    }

    [Fact]
    public void ManualPick_InterpolatesAndSnaps()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 6, Crosslines = 20, Samples = 300, NoiseLevel = 0 };
        using var vol = gen.BuildVolume(false);
        var g = vol.Geometry;
        int il = gen.FirstInline + 2;
        var section = vol.ReadInline(il);
        var locs = Enumerable.Range(0, g.CrosslineCount).Select(j => (il, g.Crosslines.ValueAt(j))).ToList();
        var truth = gen.BuildHorizons()[4];
        var h = Horizon.CreateFor(g, "m", "#fff");
        double t0 = truth.GetValue(il, g.Crosslines.First), t1 = truth.GetValue(il, g.Crosslines.Last);
        // user clicks every 4th trace, 5 ms off the event; snapping must bring every trace onto the peak
        var clicks = Enumerable.Range(0, g.CrosslineCount).Where(j => j % 4 == 0 || j == g.CrosslineCount - 1)
            .Select(j => ((double)j, truth.GetValue(il, g.Crosslines.ValueAt(j)) + 0.005)).ToList();
        ManualPicking.PickSegment(h, section, locs, clicks, g, EventType.Peak, 4);
        _ = t0; _ = t1;
        for (int j = 0; j < g.CrosslineCount; j++) Assert.True(Math.Abs(truth.GetValue(il, g.Crosslines.ValueAt(j)) - h.GetValue(il, g.Crosslines.ValueAt(j))) < 0.003, $"trace {j}");
        ManualPicking.Erase(h, locs, 0, 4);
        Assert.True(double.IsNaN(h.GetValue(il, g.Crosslines.First)));
    }

    [Theory]
    [InlineData(GriddingMethod.InverseDistance, 0.08)] // IDW flattens towards the local mean
    [InlineData(GriddingMethod.Laplace, 0.01)]
    [InlineData(GriddingMethod.MinimumCurvature, 0.005)]
    public void Gridding_FillsHolesInSmoothSurface(GriddingMethod method, double maxErr)
    {
        var full = new Grid2D(40, 50);
        for (int i = 0; i < 40; i++) for (int j = 0; j < 50; j++) full[i, j] = 1 + 0.01 * i + 0.005 * j + 0.0001 * i * j;
        var holes = full.Clone();
        for (int i = 10; i < 20; i++) for (int j = 15; j < 30; j++) holes[i, j] = double.NaN;
        var filled = Gridding.Interpolate(holes, method, 30, 2000);
        double worst = 0;
        for (int i = 10; i < 20; i++) for (int j = 15; j < 30; j++) worst = Math.Max(worst, Math.Abs(filled[i, j] - full[i, j]));
        Assert.True(worst < maxErr, $"{method}: max error {worst}");
    }

    [Fact]
    public void Smoothing_ReducesNoise()
    {
        var rng = new Random(1);
        var g = new Grid2D(30, 30);
        for (int k = 0; k < g.Values.Length; k++) g.Values[k] = 1 + 0.1 * (rng.NextDouble() - 0.5);
        var s = Gridding.Smooth(g, 2);
        double sdBefore = Statistics.Summarize(g.Values).StdDev, sdAfter = Statistics.Summarize(s.Values).StdDev;
        Assert.True(sdAfter < 0.4 * sdBefore);
    }

    [Fact]
    public void FaultSurface_And_Throw()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 40, Crosslines = 60, Samples = 300 };
        var fault = gen.BuildFault();
        var mesh = FaultModeling.BuildSurface(fault, 10);
        Assert.Equal(fault.Sticks.Count * 10, mesh.Vertices.Count);
        Assert.Equal((fault.Sticks.Count - 1) * 9 * 2, mesh.Triangles.Count);
        var horizon = gen.BuildHorizons()[3];
        var inter = FaultModeling.HorizonIntersection(fault, horizon);
        Assert.True(inter.Count >= fault.Sticks.Count - 1);
        var throws = FaultModeling.MeasureThrow(fault, horizon, 3);
        Assert.NotEmpty(throws);
        // model throw for horizon 3 (k=3) = FaultThrow * 4/5 = 20 ms plus small regional dip difference
        Assert.InRange(throws.Average(t => t.Throw), 0.012, 0.032);
    }

    [Fact]
    public void Flatten_PutsHorizonAtReferenceTime()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 4, Crosslines = 12, Samples = 300, NoiseLevel = 0 };
        using var vol = gen.BuildVolume(false);
        var g = vol.Geometry;
        int il = gen.FirstInline + 1;
        var sec = vol.ReadInline(il);
        var h = gen.BuildHorizons()[4];
        var locs = Enumerable.Range(0, g.CrosslineCount).Select(j => (il, g.Crosslines.ValueAt(j))).ToList();
        var flat = Flattening.Flatten(sec, locs, h, g, 0.6);
        int k = (int)Math.Round(0.6 / g.SampleInterval);
        for (int r = 0; r < flat.Rows; r++)
        {
            var row = flat.Row(r).ToArray();
            int peak = Enumerable.Range(k - 5, 11).MaxBy(x => row[x]);
            Assert.InRange(peak, k - 1, k + 1);
        }
    }

    [Fact]
    public void HorizonIo_XyzAndZmap_RoundTrip()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 10, Crosslines = 12, Samples = 200, Azimuth = 0 };
        var g = gen.Geometry;
        var h = gen.BuildHorizons()[0];
        var xyz = InterpretationIO.ExportXyz(h, g);
        var back = InterpretationIO.ImportXyz(xyz, g, "b", HorizonXyzColumns.MapXYZ);
        for (int k = 0; k < h.Values.Length; k++) Assert.True(Math.Abs(h.Values[k] - back.Values[k]) < 1e-6);
        var back2 = InterpretationIO.ImportXyz(xyz.Replace("#", "#"), g, "c", HorizonXyzColumns.MapXYZ);
        Assert.Equal(h.Values.Length, back2.Values.Count(v => !double.IsNaN(v)));
        var zmap = InterpretationIO.ExportZmap(h, g);
        var z = InterpretationIO.ImportZmap(zmap, g, "z");
        int defined = z.Values.Count(v => !double.IsNaN(v));
        Assert.True(defined > 0.9 * h.Values.Length);
        for (int k = 0; k < h.Values.Length; k++) if (!double.IsNaN(z.Values[k])) Assert.True(Math.Abs(h.Values[k] - z.Values[k]) < 1e-5);
    }

    [Fact]
    public void FaultIo_RoundTrip()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 30, Crosslines = 40 };
        var f = gen.BuildFault();
        var text = InterpretationIO.ExportFaultSticks(f, gen.Geometry);
        var back = InterpretationIO.ImportFaultSticks(text, gen.Geometry, "F", mapCoordinates: true);
        Assert.Equal(f.Sticks.Count, back.Sticks.Count);
        Assert.Equal(f.Sticks[0].Points[1].X, back.Sticks[0].Points[1].X, 2);
        Assert.Equal(f.Sticks[0].Points[1].Z, back.Sticks[0].Points[1].Z, 6);
    }
}

public class AttributeTests
{
    private static ParameterSet D(ISeismicAttribute a) => ParameterSet.Defaults(a.Parameters);

    [Fact]
    public void Catalog_AllAttributesRun()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 5, Crosslines = 6, Samples = 128 };
        using var vol = gen.BuildVolume();
        foreach (var a in AttributeCatalog.All)
        {
            var res = AttributeEngine.ComputeInline(vol, gen.FirstInline + 2, a, D(a));
            Assert.Equal(6, res.Rows);
            Assert.All(res.Data, v => Assert.False(float.IsInfinity(v), a.Id));
        }
    }

    [Fact]
    public void Envelope_OfModulatedSine_RecoversEnvelope()
    {
        double dt = 0.002;
        var x = Enumerable.Range(0, 1000).Select(i => (float)((1 + 0.5 * Math.Sin(2 * Math.PI * 2 * i * dt)) * Math.Cos(2 * Math.PI * 40 * i * dt))).ToArray();
        var y = new float[x.Length];
        new EnvelopeAttribute().ComputeTrace(x, y, new ParameterSet(), dt);
        for (int i = 200; i < 800; i += 25) Assert.True(Math.Abs(1 + 0.5 * Math.Sin(2 * Math.PI * 2 * i * dt) - y[i]) < 0.03);
    }

    [Fact]
    public void InstantaneousFrequency_OfSine()
    {
        double dt = 0.002;
        var x = Enumerable.Range(0, 1000).Select(i => (float)Math.Cos(2 * Math.PI * 35 * i * dt)).ToArray();
        var y = new float[x.Length];
        var a = new InstantaneousFrequencyAttribute();
        a.ComputeTrace(x, y, D(a), dt);
        for (int i = 200; i < 800; i += 50) Assert.InRange(y[i], 33, 37);
    }

    [Fact]
    public void SpectralDecomposition_PicksMatchingFrequency()
    {
        double dt = 0.002;
        var x = Enumerable.Range(0, 1000).Select(i => (float)(i < 500 ? Math.Cos(2 * Math.PI * 20 * i * dt) : Math.Cos(2 * Math.PI * 60 * i * dt))).ToArray();
        var a = new SpectralDecompositionAttribute(false);
        var y = new float[x.Length];
        a.ComputeTrace(x, y, D(a).Set("freq", 20), dt);
        Assert.True(y[250] > 0.8 && y[750] < 0.1, $"{y[250]} {y[750]}");
    }

    [Fact]
    public void Coherence_IsLowAtFault()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 20, Crosslines = 40, Samples = 300, NoiseLevel = 0, FaultThrow = 0.04 };
        using var vol = gen.BuildVolume(false);
        var a = new CoherenceAttribute();
        int i = 10, il = gen.FirstInline + i;
        var res = AttributeEngine.ComputeInline(vol, il, a, D(a));
        int k0 = (int)(gen.HorizonTime(4, i, (int)gen.FaultTrace(i)) / gen.Dt);
        int jf = (int)Math.Round(gen.FaultPosition(i, k0 * gen.Dt));
        int k = (int)(gen.HorizonTime(4, i, jf) / gen.Dt);
        double atFault = Enumerable.Range(k - 3, 7).Average(kk => Math.Min(res[jf, kk], res[jf - 1, kk]));
        int ka = (int)(gen.HorizonTime(4, i, jf - 6) / gen.Dt);
        double away = Enumerable.Range(ka - 3, 7).Average(kk => res[jf - 6, kk]);
        Assert.True(away > 0.85, $"dip-steered coherence on the steep flank {away:0.00}");
        Assert.True(atFault < away - 0.2, $"coherence at fault {atFault:0.00}, away {away:0.00}");
    }

    [Fact]
    public void Dip_OfPlaneWave()
    {
        // plane wave dipping along crossline: t shift 2 ms per trace (25 m) -> 0.08 ms/m
        int nx = 9, ns = 200; double dt = 0.002;
        var traces = new float[]?[3, 3];
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
            traces[a, b] = Enumerable.Range(0, ns).Select(k => (float)Math.Cos(2 * Math.PI * 25 * (k * dt - (b - 1) * 0.002))).ToArray();
        var y = new float[ns];
        var attr = new DipAzimuthAttribute();
        attr.ComputeNeighborhood(traces, y, D(attr).Set("output", "CrosslineDip"), dt, 25, 25);
        Assert.InRange(y[100], 0.07, 0.09);
        attr.ComputeNeighborhood(traces, y, D(attr).Set("output", "InlineDip"), dt, 25, 25);
        Assert.InRange(y[100], -0.005, 0.005);
        _ = nx;
    }

    [Fact]
    public void Curvature_OfParaboloid_MatchesAnalytic()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 21, Crosslines = 21 };
        var h = Horizon.CreateFor(gen.Geometry, "p", "#fff");
        h.Domain = Core.Units.DomainType.Depth;
        double dx = 25, k = 1e-4; // z = k/2 (x² + y²) -> curvature k at apex
        for (int i = 0; i < 21; i++) for (int j = 0; j < 21; j++)
        {
            double x = (j - 10) * dx, y = (i - 10) * dx;
            h.Values[i * 21 + j] = 1000 + 0.5 * k * (x * x + y * y);
        }
        var mean = HorizonCurvature.Compute(h, dx, dx, CurvatureType.Mean);
        var gauss = HorizonCurvature.Compute(h, dx, dx, CurvatureType.Gaussian);
        Assert.Equal(k, mean[10, 10], 9);
        Assert.Equal(k * k, gauss[10, 10], 12);
        Assert.Equal(0, HorizonCurvature.Compute(h, dx, dx, CurvatureType.DipAngle)[10, 10], 6);
    }

    [Fact]
    public void ChaosEigenvalues_OfDiagonalMatrix()
    {
        var (a, b, c) = ChaosAttribute.SymmetricEigenvalues3(3, 1, 2, 0, 0, 0);
        Assert.Equal((3.0, 2.0, 1.0), (a, b, c));
        var (x, y, z) = ChaosAttribute.SymmetricEigenvalues3(2, 2, 1, 1, 0, 0); // eigenvalues 3,1,1
        Assert.Equal(3, x, 9); Assert.Equal(1, y, 9); Assert.Equal(1, z, 9);
    }

    [Fact]
    public void HorizonExtraction_AmplitudeAndWindow()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 8, Crosslines = 10, Samples = 300, NoiseLevel = 0 };
        using var vol = gen.BuildVolume(false);
        var h = gen.BuildHorizons()[4];
        var amp = HorizonAttributeExtraction.Extract(vol, h, HorizonWindowMode.AtHorizon, WindowStatistic.Rms);
        Assert.All(amp.Values, v => Assert.True(v > 0)); // carbonate top = positive RC -> peak
        var rms = HorizonAttributeExtraction.Extract(vol, h, HorizonWindowMode.WindowAroundHorizon, WindowStatistic.Rms, 0.02, 0.02);
        Assert.All(rms.Values, v => Assert.True(v > 0));
    }

    [Fact]
    public void AttributeVolume_IsWrittenForEveryTrace()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 4, Crosslines = 5, Samples = 100 };
        using var vol = gen.BuildVolume();
        using var outv = new InMemoryVolume("o", vol.Geometry);
        AttributeEngine.ComputeVolume(vol, outv, new EnvelopeAttribute(), new ParameterSet());
        Assert.True(outv.ReadTrace(gen.FirstInline + 3, gen.FirstCrossline + 4).Max() > 0);
    }
}
