using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;
using GeoVisionPro.WellTie;

namespace GeoVisionPro.Tests;

public class LasTests
{
    private const string Las20 = """
~VERSION INFORMATION
 VERS.                 2.0 :   CWLS LOG ASCII STANDARD -VERSION 2.0
 WRAP.                  NO :   ONE LINE PER DEPTH STEP
~WELL INFORMATION BLOCK
#MNEM.UNIT       DATA TYPE    INFORMATION
 STRT.M        1670.0000                :START DEPTH
 STOP.M        1669.7500                :STOP DEPTH
 STEP.M          -0.1250                :STEP
 NULL.          -999.25                 :NULL VALUE
 COMP.       ANY OIL COMPANY INC.       :COMPANY
 WELL.       AAAAA_2                    :WELL
 UWI .       100123401234W500           :UNIQUE WELL ID
 XCOORD.M    512345.5                   :X
 YCOORD.M    6712345.1                  :Y
 EKB .M      25.4                       :KB
~CURVE INFORMATION
 DEPT.M                      :  1  DEPTH
 DT  .US/M     60 520 32 00  :  2  SONIC TRANSIT TIME
 RHOB.K/M3     45 350 01 00  :  3  BULK DENSITY
 NPHI.V/V      42 890 00 00  :  4  NEUTRON POROSITY
 GR  .GAPI     45 310 01 00  :  5  GAMMA RAY
~PARAMETER INFORMATION
 BHT .DEGC   35.5000            :BOTTOM HOLE TEMPERATURE
~Other
     Note: This is a test file.
~A  DEPTH     DT       RHOB        NPHI     GR
1670.000   123.450 2550.000    0.450  -999.25
1669.875   123.450 2550.000    0.450   45.0
1669.750   123.450 2550.000    0.450   46.0
""";

    [Fact]
    public void Parse_Las20_HeaderCurvesNullsAndOrder()
    {
        var doc = LasReader.Parse(Las20);
        Assert.Equal("2.0", doc.Version);
        Assert.Equal(-999.25, doc.NullValue);
        Assert.Equal(4, doc.CurveData.Count);
        Assert.Equal(1669.75, doc.Depths[0], 6); // reversed to ascending
        var well = LasReader.ToWell(doc, "aaaa.las");
        Assert.Equal("AAAAA_2", well.Name);
        Assert.Equal("100123401234W500", well.Uwi);
        Assert.Equal(512345.5, well.SurfaceX, 3);
        Assert.Equal(25.4, well.KellyBushing, 3);
        var gr = well.FindCurve("GR")!;
        Assert.Equal(CurveFamily.GammaRay, gr.Family);
        Assert.True(float.IsNaN(gr.Values[2])); // null at 1670 (now last)
        Assert.Equal(CurveFamily.Sonic, well.FindCurve("DT")!.Family);
        Assert.Equal("K/M3", well.FindCurve("RHOB")!.Unit);
    }

    [Fact]
    public void Parse_WrappedLas()
    {
        const string wrapped = """
~V
 VERS. 2.0 :
 WRAP. YES :
~W
 NULL. -999.25 :
 WELL. W1 :
~C
 DEPT.M :
 A.X :
 B.X :
 C.X :
~A
100.0
 1 2
 3
100.5
 4 5 6
""";
        var doc = LasReader.Parse(wrapped);
        Assert.True(doc.Wrapped);
        Assert.Equal(2, doc.Depths.Length);
        Assert.Equal(6f, doc.CurveData[2][1]);
    }

    [Fact]
    public void Parse_Las30_LogAndTops()
    {
        const string las3 = """
~Version
VERS.  3.0 : CWLS LOG ASCII STANDARD - VERSION 3.0
WRAP.  NO :
DLM .  COMMA : DELIMITING CHARACTER
~Well
WELL. W3 : WELL NAME
NULL. -999.25 :
~Log_Definition
DEPT.M : depth {F}
GR  .GAPI : gamma {F}
~Log_Data | Log_Definition
1000.0, 50.0
1000.5, 55.0
~Tops_Definition
TOPN. : top name {S}
TOPT.M : top depth {F}
~Tops_Data | Tops_Definition
"Sand A", 1000.2
""";
        var doc = LasReader.Parse(las3);
        Assert.Equal(2, doc.Depths.Length);
        Assert.Equal(55f, doc.CurveData[0][1]);
        Assert.Single(doc.Tops);
        Assert.Equal("Sand A", doc.Tops[0].Name);
    }

    [Fact]
    public void Write_Read_RoundTrip()
    {
        var gen = TestData.SmallGenerator();
        var w = gen.BuildWell("RT-1", 5, 5);
        var text = LasWriter.Write(w);
        var back = LasReader.ToWell(LasReader.Parse(text), "rt.las");
        Assert.Equal(w.Name, back.Name);
        Assert.Equal(w.Curves.Count, back.Curves.Count);
        var a = w.FindCurve("DT")!; var b = back.FindCurve("DT")!;
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i += 97) Assert.Equal(a.Values[i], b.Values[i], 3);
        Assert.Equal(w.SurfaceX, back.SurfaceX, 1);
    }
}

public class WellLogTests
{
    [Fact]
    public void MinimumCurvature_VerticalAndSlantedWells()
    {
        var w = new Well { Name = "V", TotalDepth = 1000, KellyBushing = 20, SurfaceX = 100, SurfaceY = 200 };
        var t = WellTrajectory.Compute(w);
        Assert.Equal(500, t.TvdAt(500), 6);
        Assert.Equal(480, t.TvdSsAt(500), 6);
        // constant 30° inclination towards east from surface: TVD = MD cos30, East = MD sin30
        w.Deviation = [new(0, 30, 90), new(1000, 30, 90)];
        t = WellTrajectory.Compute(w);
        Assert.Equal(1000 * Math.Cos(Math.PI / 6), t.TvdAt(1000), 3);
        Assert.Equal(100 + 1000 * Math.Sin(Math.PI / 6), t.LocationAt(1000).X, 3);
        Assert.Equal(200, t.LocationAt(1000).Y, 3);
        Assert.Equal(1000, t.MdAtTvdSs(t.TvdSsAt(1000)), 3);
    }

    [Fact]
    public void MinimumCurvature_BuildSection_MatchesArcGeometry()
    {
        // build from 0 to 90° over a quarter circle of radius R: displacement = R, TVD = R
        double R = 1000;
        var w = new Well { Name = "B", TotalDepth = Math.PI / 2 * R };
        w.Deviation = [new(0, 0, 0), new(Math.PI / 2 * R, 90, 0)];
        var t = WellTrajectory.Compute(w);
        Assert.Equal(R, t.Stations[^1].Tvd, 3);
        Assert.Equal(R, t.Stations[^1].North, 3);
    }

    [Fact]
    public void Despike_RemovesIsolatedSpike()
    {
        var c = new LogCurve { Mnemonic = "GR", Depths = Enumerable.Range(0, 100).Select(i => (double)i).ToArray(), Values = Enumerable.Repeat(50f, 100).ToArray() };
        c.Values[40] = 500;
        var d = LogConditioning.Despike(c);
        Assert.Equal(50f, d.Values[40]);
    }

    [Fact]
    public void Normalize_MapsPercentiles()
    {
        var c = new LogCurve { Mnemonic = "GR", Depths = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray(), Values = Enumerable.Range(0, 1000).Select(i => (float)(i / 10.0)).ToArray() };
        var n = LogConditioning.Normalize(c, 20, 120);
        var s = LogConditioning.Summarize(n);
        Assert.Equal(20, Statistics.Percentile(n.Values.Select(v => (double)v), 5), 1);
        Assert.Equal(120, Statistics.Percentile(n.Values.Select(v => (double)v), 95), 1);
        Assert.Equal(1000, s.Count);
    }

    [Fact]
    public void DepthShift_IsFoundByCorrelation()
    {
        var rng = new Random(2);
        var depths = Enumerable.Range(0, 2000).Select(i => i * 0.5).ToArray();
        var vals = Dsp.MovingAverage(Enumerable.Range(0, 2000).Select(_ => (float)rng.NextDouble()).ToArray(), 7);
        var a = new LogCurve { Mnemonic = "A", Depths = depths, Values = vals };
        var b = LogConditioning.DepthShift(a, 3.0);
        var (shift, r) = LogConditioning.FindDepthShift(a, b, 6, 0.5);
        Assert.Equal(-3.0, shift, 6); // shift to apply to b to match a
        Assert.True(r > 0.95);
    }

    [Fact]
    public void AcousticImpedance_FromSonicAndDensity()
    {
        var d = new[] { 1000.0, 1001.0 };
        var dt = new LogCurve { Mnemonic = "DT", Unit = "us/ft", Depths = d, Values = [100f, 100f] };
        var rho = new LogCurve { Mnemonic = "RHOB", Unit = "g/cc", Depths = d, Values = [2.5f, 2.5f] };
        var ai = LogConditioning.AcousticImpedance(dt, rho);
        Assert.Equal(3048 * 2.5, ai.Values[0], 1);
    }

    [Fact]
    public void AsciiImports_TopsDeviationCheckshots()
    {
        var tops = AsciiWellImport.ReadTops("Well,Top,MD\nW1,Sand A,1200.5\nW1,Shale B,1350\n");
        Assert.Equal(2, tops.Count);
        Assert.Equal("Sand A", tops[0].Name);
        var dev = AsciiWellImport.ReadDeviation("MD INC AZI\n0 0 0\n500 10 45\n1000 20 50\n");
        Assert.Equal(3, dev.Count);
        var cs = AsciiWellImport.ReadTimeDepth("MD,TWT_ms\n500,500\n1000,900\n");
        Assert.Equal(0.9, cs[1].TwoWayTime, 9);
    }
}

public class CheckshotTests
{
    [Fact]
    public void TimeDepth_ConstantVelocity_IsLinear()
    {
        // V = 2000 m/s: TWT = 2 z / V
        var m = new TimeDepthModel([0, 1000, 2000], [0, 1.0, 2.0], 0, "test");
        Assert.Equal(0.5, m.TimeAtTvdSs(500), 9);
        Assert.Equal(1500, m.TvdSsAtTime(1.5), 9);
        Assert.Equal(2.5, m.TimeAtTvdSs(2500), 9); // extrapolation with last interval velocity
        Assert.All(m.IntervalVelocities(), iv => Assert.Equal(2000, iv.Velocity, 6));
        Assert.Equal(2000, m.AverageVelocities()[2], 6);
        Assert.Equal(2000, m.RmsVelocities()[2], 6);
    }

    [Fact]
    public void SonicIntegration_MatchesAnalyticTime()
    {
        var w = new Well { Name = "S", TotalDepth = 3000, KellyBushing = 0 };
        var depths = Enumerable.Range(0, 2001).Select(i => 1000.0 + i).ToArray();
        // V = 2500 m/s -> DT = 304800/2500 us/ft
        var dt = new LogCurve { Mnemonic = "DT", Unit = "us/ft", Depths = depths, Values = depths.Select(_ => (float)(304800.0 / 2500)).ToArray() };
        var traj = WellTrajectory.Compute(w);
        var m = TimeDepthModel.FromSonic(dt, traj, 0, 2000);
        Assert.Equal(1.0, m.TimeAtTvdSs(1000), 6);              // 2*1000/2000 replacement
        Assert.Equal(1.0 + 2 * 1000 / 2500.0, m.TimeAtTvdSs(2000), 4);
    }

    [Fact]
    public void Calibration_HonoursCheckshots()
    {
        var gen = TestData.SmallGenerator();
        var w = gen.BuildWell("C-1", 6, 6);
        var traj = WellTrajectory.Compute(w);
        var cs = TimeDepthModel.FromCheckshot(w.Checkshots[0], traj, 0);
        // sonic integrated with a wrong replacement velocity (drift)
        var sm = TimeDepthModel.FromSonic(w.FindCurve("DT")!, traj, 0, 1500);
        var res = CheckshotCalibration.Calibrate(sm, cs, w.FindCurve("DT"), traj);
        Assert.True(res.RmsResidualBefore > 0.01);
        Assert.True(res.RmsResidualAfter < 0.001, $"after {res.RmsResidualAfter}");
        Assert.NotNull(res.CorrectedSonic);
        Assert.Empty(CheckshotCalibration.QualityControl(cs, 1300, 7000));
    }
}

public class WellTieTests
{
    private static (SyntheticSurveyGeneratorAlias Gen, Well Well, InMemoryVolume Vol) Setup()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 14, Crosslines = 18, Samples = 320, NoiseLevel = 0 };
        var well = gen.BuildWell("T-1", 7, 7);
        var vol = gen.BuildVolume(false);
        return (gen, well, vol);
    }

    [Fact]
    public void Synthetic_AtTrueTdr_CorrelatesWithSeismic()
    {
        var (gen, well, vol) = Setup();
        var traj = WellTrajectory.Compute(well);
        var tdr = TimeDepthModel.FromCheckshot(well.Checkshots[0], traj, 0);
        var tie = new WellTieSession(well, well.FindCurve("DT")!, well.FindCurve("RHOB")!, vol, tdr, gen.Wavelet);
        tie.Parameters = tie.Parameters with { TraceRadius = 0 };
        tie.Compute();
        Assert.True(tie.Quality!.Correlation > 0.85, $"r = {tie.Quality.Correlation:0.000}");
        // Reflectivity convention: impedance increase gives a positive RC
        Assert.True(tie.Synthetic!.Reflectivity.Max() > 0.05);
    }

    [Fact]
    public void BulkShift_IsRecovered()
    {
        var (gen, well, vol) = Setup();
        var traj = WellTrajectory.Compute(well);
        var tdr = TimeDepthModel.FromCheckshot(well.Checkshots[0], traj, 0);
        // shift the TDR by +16 ms; the optimizer must find ~ -16 ms
        var shifted = new TimeDepthModel(tdr.TvdSs, tdr.Twt.Select(t => t + 0.016).ToArray(), 0, "shifted");
        var tie = new WellTieSession(well, well.FindCurve("DT")!, well.FindCurve("RHOB")!, vol, shifted, gen.Wavelet) { Parameters = new() { TraceRadius = 0 } };
        tie.Compute();
        double r0 = tie.Quality!.Correlation;
        double s = tie.OptimizeBulkShift(0.04);
        Assert.InRange(s, -0.0201, -0.0119);
        Assert.True(tie.Quality.Correlation > r0);
    }

    [Fact]
    public void PhaseRotation_IsRecovered()
    {
        var (gen, well, vol) = Setup();
        var traj = WellTrajectory.Compute(well);
        // exact model time-depth relation: isolates the phase from time-alignment errors (phase and time shifts trade off)
        var (z, t) = gen.TrueTimeDepth(7, 7);
        var tdr = new TimeDepthModel(z, t, 0, "exact");
        var tie = new WellTieSession(well, well.FindCurve("DT")!, well.FindCurve("RHOB")!, vol, tdr, gen.Wavelet.RotatePhase(45)) { Parameters = new() { TraceRadius = 0 } };
        double ph = tie.OptimizePhase();
        Assert.InRange(ph, -55, -35);
    }

    [Fact]
    public void LeastSquaresWavelet_RecoversTrueWavelet()
    {
        var rng = new Random(9);
        var r = Enumerable.Range(0, 800).Select(_ => (float)(rng.NextDouble() < 0.2 ? rng.NextDouble() - 0.5 : 0)).ToArray();
        var w = Wavelet.Ricker(30, 0.002, 0.12);
        var s = Dsp.ConvolveSame(r, w.Samples, w.CenterIndex);
        var (est, corr) = WaveletEstimation.LeastSquares(r, s, 0.002, 0.12, 0.001);
        Assert.True(corr > 0.99);
        Assert.True(Dsp.Correlation(est.Samples, w.Samples) > 0.98);
    }

    [Fact]
    public void StatisticalWavelet_HasCorrectBandwidth()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 8, Crosslines = 8, Samples = 400, NoiseLevel = 0, PeakFrequency = 25 };
        using var vol = gen.BuildVolume(false);
        var traces = new List<float[]>();
        for (int i = 0; i < 8; i++) traces.Add(vol.ReadTrace(gen.FirstInline + i, gen.FirstCrossline + i));
        var w = WaveletEstimation.Statistical(traces, gen.Dt, 50, 350, 0.2);
        var (f, a) = w.AmplitudeSpectrum();
        double peak = f[Array.IndexOf(a, a.Max())];
        Assert.InRange(peak, 15, 40);
        Assert.Equal(w.CenterIndex, Array.IndexOf(w.Samples, w.Samples.Max()));
    }

    [Fact]
    public void Reflectivity_PlacementAndSign()
    {
        var rc = SyntheticSeismogram.Reflectivity([1000f, 1000f, 3000f, 3000f]);
        Assert.Equal(0.5f, rc[2], 6);
        Assert.Equal(0f, rc[1]);
    }
}
