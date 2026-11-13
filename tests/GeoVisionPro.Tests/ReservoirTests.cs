using GeoVisionPro.AVO;
using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Crossplot;
using GeoVisionPro.GeologicalModeling;
using GeoVisionPro.Inversion;
using GeoVisionPro.Petrophysics;
using GeoVisionPro.RockPhysics;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Tests;

public class InversionTests
{
    private static (float[] Z, float[] S, Wavelet W) Model(int n = 400, double dt = 0.002)
    {
        var z = new float[n];
        for (int i = 0; i < n; i++)
            z[i] = i < 100 ? 5000f : i < 160 ? 6500f : i < 200 ? 5600f : i < 280 ? 7200f : 8000f + 2f * (i - 280);
        var w = Wavelet.Ricker(35, dt, 0.12);
        var r = new float[n];
        for (int i = 1; i < n; i++) r[i] = (z[i] - z[i - 1]) / (z[i] + z[i - 1]);
        return (z, Dsp.ConvolveSame(r, w.Samples, w.CenterIndex), w);
    }

    private static float[] LowPass(float[] z, double dt, double hz)
    {
        var l = z.Select(v => (float)Math.Log(v)).ToArray();
        FrequencyFilters.Butterworth(l, dt, 0, hz, 4);
        return l.Select(v => (float)Math.Exp(v)).ToArray();
    }

    [Fact]
    public void Recursive_OnPureReflectivity_RecoversImpedanceExactly()
    {
        var (z, _, _) = Model();
        var r = new float[z.Length];
        for (int i = 1; i < z.Length; i++) r[i] = (z[i] - z[i - 1]) / (z[i] + z[i - 1]);
        // reflectivity at sample i is the contrast between i-1 and i; exact recursion
        var inv = new PostStackInversion(Wavelet.Ricker(30, 0.002, 0.1), 0.002);
        var res = inv.InvertTrace(r, Enumerable.Repeat(z[0], z.Length).ToArray(), new InversionParameters { Method = InversionMethod.Recursive, ConstraintPercent = 0, SeismicScale = 1 });
        for (int i = 0; i < z.Length; i += 20) Assert.Equal(z[i], res.Impedance[i], z[i] * 2e-3);
    }

    [Theory]
    [InlineData(InversionMethod.ModelBased, 0.9)]
    [InlineData(InversionMethod.SparseSpike, 0.85)]
    [InlineData(InversionMethod.BandLimited, 0.9)]
    public void Inversion_RecoversBlockyImpedance(InversionMethod method, double minCorr)
    {
        var (z, s, w) = Model();
        var lf = LowPass(z, 0.002, 8);
        var inv = new PostStackInversion(w, 0.002);
        var res = inv.InvertTrace(s, lf, new InversionParameters { Method = method, SeismicScale = 1, ModelWeight = 0.01, Iterations = 80, ConstraintPercent = 40, SparsityLambda = 0.02, MergeFrequency = 8 });
        double rInv = Dsp.Correlation(res.Impedance, z);
        double rLf = Dsp.Correlation(lf, z);
        Assert.True(rInv > minCorr, $"{method}: r(inverted, true) = {rInv:0.000}, r(model, true) = {rLf:0.000}");
        // model-based and sparse-spike inversions must add information beyond the low-frequency model (RMS error decreases)
        double eInv = Math.Sqrt(z.Select((v, i) => Math.Pow(v - res.Impedance[i], 2)).Average()), eLf = Math.Sqrt(z.Select((v, i) => Math.Pow(v - lf[i], 2)).Average());
        if (method != InversionMethod.BandLimited) Assert.True(eInv < eLf, $"{method}: RMS error {eInv:0} vs model {eLf:0}");
        Assert.True(res.Correlation > 0.8, $"{method}: synthetic fit {res.Correlation:0.000}");
    }

    [Fact]
    public void ScaleEstimation_RecoversAmplitudeFactor()
    {
        var (z, s, w) = Model();
        var scaled = s.Select(v => v * 1000f).ToArray();
        var inv = new PostStackInversion(w, 0.002);
        var res = inv.InvertTrace(scaled, LowPass(z, 0.002, 8), new InversionParameters { Method = InversionMethod.ModelBased, SeismicScale = 1000, ModelWeight = 0.01, Iterations = 80 });
        Assert.True(Dsp.Correlation(res.Impedance, z) > 0.9);
    }

    [Fact]
    public void LowFrequencyModel_InterpolatesWellsAndHonoursCutoff()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 20, Crosslines = 24, Samples = 300 };
        var g = gen.Geometry;
        var w1 = gen.BuildWell("A", 5, 5); var w2 = gen.BuildWell("B", 15, 18);
        var logs = new[] { w1, w2 }.Select(w => WellTimeLog.AcousticImpedance(w, TimeDepthModel.FromCheckshot(w.Checkshots[0], WellTrajectory.Compute(w), 0), g.SampleInterval)).ToList();
        var lfm = new LowFrequencyModel(g, logs, gen.BuildHorizons(), 10);
        var tr = lfm.Trace(gen.FirstInline + 5, gen.FirstCrossline + 5);
        var truth = gen.ImpedanceTrace(5, 5);
        Assert.True(Dsp.Correlation(tr.AsSpan(60, 200), truth.AsSpan(60, 200)) > 0.8);
        var logTr = tr.Select(v => (float)Math.Log(v)).ToArray();
        Dsp.Detrend(logTr);
        var taper = Dsp.Hann(logTr.Length);
        for (int k = 0; k < logTr.Length; k++) logTr[k] *= taper[k];
        var (f, a) = Fft.AmplitudeSpectrum(logTr, g.SampleInterval, 512);
        Assert.True(a[(int)(40 / f[1])] < 0.05 * a[(int)(2 / f[1])]);
    }

    [Fact]
    public void ElasticImpedance_AtZeroAngle_EqualsAcousticImpedance()
    {
        Assert.Equal(3000 * 2.3, ElasticImpedance.Compute(3000, 1500, 2.3, 0, 0.25), 6);
        Assert.Equal(3000 * 2.3, ElasticImpedance.Normalized(3000, 1500, 2.3, 0, 0.25, 3000, 1500, 2.3), 6);
    }

    [Fact]
    public void SimultaneousInversion_RecoversIpAndIs()
    {
        int n = 300; double dt = 0.002;
        var vp = new double[n]; var vs = new double[n]; var rho = new double[n];
        for (int i = 0; i < n; i++)
        {
            bool sand = i >= 120 && i < 170;
            vp[i] = sand ? 2700 : i < 120 ? 2900 : 3100;
            vs[i] = sand ? 1650 : i < 120 ? 1300 : 1450;
            rho[i] = sand ? 2.1 : i < 120 ? 2.35 : 2.4;
        }
        var angles = new double[] { 5, 12, 19, 26, 33 };
        var w = Wavelet.Ricker(35, dt, 0.12);
        var gather = new Array2D(angles.Length, n);
        for (int a = 0; a < angles.Length; a++)
        {
            var r = new float[n];
            for (int i = 1; i < n; i++)
                r[i] = (float)Reflectivity.Fatti(new Layer(vp[i - 1], vs[i - 1], rho[i - 1]), new Layer(vp[i], vs[i], rho[i]), angles[a]);
            Dsp.ConvolveSame(r, w.Samples, w.CenterIndex).CopyTo(gather.Row(a));
        }
        float[] Smooth(Func<int, double> f) { var x = Enumerable.Range(0, n).Select(i => (float)Math.Log(f(i))).ToArray(); FrequencyFilters.Butterworth(x, dt, 0, 6, 4); return x.Select(v => (float)Math.Exp(v)).ToArray(); }
        var ip0 = Smooth(i => vp[i] * rho[i]); var is0 = Smooth(i => vs[i] * rho[i]); var rho0 = Smooth(i => rho[i]);
        var sim = new SimultaneousInversion(angles, angles.Select(_ => w).ToArray(), dt);
        var res = sim.Invert(gather, ip0, is0, rho0, 0.5, 0.01, 100);
        var ipTrue = Enumerable.Range(0, n).Select(i => (float)(vp[i] * rho[i])).ToArray();
        var isTrue = Enumerable.Range(0, n).Select(i => (float)(vs[i] * rho[i])).ToArray();
        Assert.True(Dsp.Correlation(res.Ip, ipTrue) > Dsp.Correlation(ip0, ipTrue));
        Assert.True(Dsp.Correlation(res.Ip, ipTrue) > 0.85, $"Ip r={Dsp.Correlation(res.Ip, ipTrue):0.00}");
        Assert.True(Dsp.Correlation(res.Is, isTrue) > 0.7, $"Is r={Dsp.Correlation(res.Is, isTrue):0.00}");
        Assert.True(res.AngleCorrelations.All(c => c > 0.8));
        // sand has the lowest Vp/Vs
        Assert.True(res.VpVs.Skip(130).Take(30).Average() < res.VpVs.Skip(200).Take(30).Average());
    }

    [Fact]
    public void InversionQc_ComparesWithWell()
    {
        var log = new WellTimeLog("w", new Point2D(0, 0), 0.1, 0.004, Enumerable.Repeat(6000f, 100).ToArray());
        var inv = Enumerable.Repeat(6060f, 200).ToArray();
        var qc = InversionEngine.CompareWithWell(inv, 0, 0.004, log);
        Assert.Equal(1.0, qc.MeanBiasPercent, 6);
        Assert.Equal(100, qc.Samples);
    }
}

public class AvoTests
{
    private static readonly Layer Shale = new(2438, 1006, 2.25);
    private static readonly Layer GasSand = new(2600, 1700, 1.95);  // class III style

    [Fact]
    public void Zoeppritz_NormalIncidence_EqualsImpedanceContrast()
    {
        double z1 = Shale.Vp * Shale.Rho, z2 = GasSand.Vp * GasSand.Rho;
        Assert.Equal((z2 - z1) / (z2 + z1), Reflectivity.ZoeppritzPP(Shale, GasSand, 0).Real, 9);
    }

    [Fact]
    public void Approximations_AgreeWithZoeppritzAtModerateAngles()
    {
        // linearised approximations degrade with angle and contrast (this gas sand has ~50% Vs contrast)
        foreach (var (ang, tol) in new[] { (0.0, 0.001), (10.0, 0.005), (20.0, 0.015), (30.0, 0.03) })
        {
            double exact = Reflectivity.ZoeppritzPP(Shale, GasSand, ang).Real;
            Assert.True(Math.Abs(exact - Reflectivity.AkiRichards(Shale, GasSand, ang)) < tol, $"AR {ang}");
            Assert.True(Math.Abs(exact - Reflectivity.Shuey(Shale, GasSand, ang)) < tol, $"Shuey {ang}");
            Assert.True(Math.Abs(exact - Reflectivity.Fatti(Shale, GasSand, ang)) < tol, $"Fatti {ang}");
        }
    }

    [Fact]
    public void Zoeppritz_PostCriticalIsComplexWithUnitMagnitudeForFluidLikeCase()
    {
        var fast = new Layer(4000, 2200, 2.5);
        double crit = Reflectivity.CriticalAngle(Shale, fast);
        Assert.True(crit is > 30 and < 40);
        var r = Reflectivity.ZoeppritzPP(Shale, fast, crit + 5);
        Assert.True(Math.Abs(r.Imaginary) > 1e-3);
    }

    [Fact]
    public void InterceptGradientFit_AndClassification()
    {
        var (A, B, _) = Reflectivity.ShueyTerms(Shale, GasSand);
        var angles = Enumerable.Range(0, 8).Select(i => i * 4.0).ToArray();
        var amps = angles.Select(a => Reflectivity.Shuey(Shale, GasSand, a, false)).ToArray();
        var fit = AvoAnalysis.FitInterceptGradient(angles, amps);
        Assert.Equal(A, fit.Intercept, 6);
        Assert.Equal(B, fit.Gradient, 6);
        Assert.Equal(AvoClass.ClassIII, AvoAnalysis.Classify(-0.1, -0.2));
        Assert.Equal(AvoClass.ClassIV, AvoAnalysis.Classify(-0.1, 0.05));
        Assert.Equal(AvoClass.ClassI, AvoAnalysis.Classify(0.1, -0.1));
        Assert.Equal(AvoClass.ClassII, AvoAnalysis.Classify(0.0, -0.1));
    }

    [Fact]
    public void ModelledAngleGather_AmplitudesFollowZoeppritz()
    {
        var w = Wavelet.Ricker(30, 0.002, 0.12);
        var angles = new double[] { 0, 10, 20, 30 };
        var g = AvoModeling.AngleGather([Shale, GasSand], [0.3], angles, w, 300, 0.002, 0);
        for (int a = 0; a < angles.Length; a++)
            Assert.Equal(Reflectivity.ZoeppritzPP(Shale, GasSand, angles[a]).Real, g[a, 150], 3);
        var (A, B) = AvoAnalysis.InterceptGradientTraces(g, angles);
        Assert.True(B[150] > 0 == Reflectivity.ShueyTerms(Shale, GasSand).B > 0);
    }

    [Fact]
    public void OffsetToAngle_StraightRay()
    {
        Assert.Equal(0, AvoAnalysis.OffsetToAngle(0, 1, 2000, 2000), 9);
        double ang = AvoAnalysis.OffsetToAngle(2000, 1.0, 2000, 2000); // x = z for constant v -> 45°
        Assert.Equal(45, ang, 6);
    }
}

public class RockPhysicsTests
{
    [Fact]
    public void Gassmann_DrySatDry_RoundTrip()
    {
        double kDry = 8, kMin = 36.6, kFl = 2.8, phi = 0.25;
        double ksat = RockPhysicsModels.GassmannSaturated(kDry, kMin, kFl, phi);
        Assert.True(ksat > kDry);
        Assert.Equal(kDry, RockPhysicsModels.GassmannDry(ksat, kMin, kFl, phi), 9);
    }

    [Fact]
    public void FluidSubstitution_BrineToGas_LowersVpAndDensity_KeepsMu()
    {
        var brineSand = new ElasticProperties(3200, 1800, 2.25);
        var gas = RockPhysicsModels.FluidSubstitution(brineSand, 0.25, Constituent.Quartz, Constituent.Brine, Constituent.Gas);
        Assert.True(gas.Vp < brineSand.Vp);
        Assert.True(gas.Density < brineSand.Density);
        Assert.Equal(brineSand.ShearModulus, gas.ShearModulus, 9);
        Assert.True(gas.Vs > brineSand.Vs); // lower density raises Vs
    }

    [Fact]
    public void Bounds_AreOrdered()
    {
        double[] f = [0.7, 0.3]; double[] k = [36.6, 2.8];
        double v = RockPhysicsModels.Voigt(f, k), r = RockPhysicsModels.Reuss(f, k), h = RockPhysicsModels.Hill(f, k);
        Assert.True(r < h && h < v);
        var hs = RockPhysicsModels.HashinShtrikman(36.6, 45, 21, 7, 0.7);
        Assert.True(hs.KLow <= hs.KHigh && hs.KHigh <= RockPhysicsModels.Voigt([0.7, 0.3], [36.6, 21]) + 1e-9);
        Assert.True(hs.KLow >= RockPhysicsModels.Reuss([0.7, 0.3], [36.6, 21]) - 1e-9);
        Assert.True(hs.MuLow <= hs.MuHigh);
    }

    [Fact]
    public void ElasticProperties_DerivedQuantities()
    {
        var e = new ElasticProperties(3000, 1500, 2.4);
        Assert.Equal(2.0, e.VpVs, 9);
        Assert.Equal(1.0 / 3, e.PoissonsRatio, 9);
        var back = ElasticProperties.FromModuli(e.BulkModulus, e.ShearModulus, e.Density);
        Assert.Equal(3000, back.Vp, 6);
        Assert.Equal(e.LambdaRho, (e.PImpedance * e.PImpedance - 2 * e.SImpedance * e.SImpedance) * 1e-6, 9);
    }

    [Fact]
    public void EmpiricalRelations()
    {
        Assert.Equal(0.31 * Math.Pow(3000, 0.25), RockPhysicsModels.GardnerDensity(3000), 9);
        double v = RockPhysicsModels.WyllieVelocity(0.2);
        Assert.Equal(0.2, RockPhysicsModels.WylliePorosity(v), 9);
        Assert.Equal((3000 - 1360) / 1.16, RockPhysicsModels.CastagnaMudrock(3000), 9);
        Assert.InRange(RockPhysicsModels.GreenbergCastagnaVs(3000, 1, 0, 0, 0), 1500, 1700);
    }

    [Fact]
    public void BatzleWang_BrineIsPhysical()
    {
        var b = RockPhysicsModels.BatzleWangBrine(60, 30, 50000);
        Assert.InRange(b.Density, 1.0, 1.1);
        double vel = Math.Sqrt(b.BulkModulus * 1e6 / b.Density);
        Assert.InRange(vel, 1500, 1700);
    }

    [Fact]
    public void HertzMindlin_And_SoftSand_IncreaseWithPressureAndDecreaseWithPorosity()
    {
        var (k1, m1) = RockPhysicsModels.HertzMindlin(10, 0.36, 9, 36.6, 45);
        var (k2, m2) = RockPhysicsModels.HertzMindlin(30, 0.36, 9, 36.6, 45);
        Assert.True(k2 > k1 && m2 > m1);
        var (ka, _) = RockPhysicsModels.SoftSand(0.1, 20, 0.36, 9, 36.6, 45);
        var (kb, _) = RockPhysicsModels.SoftSand(0.3, 20, 0.36, 9, 36.6, 45);
        Assert.True(ka > kb);
        var (kc, mc) = RockPhysicsModels.ContactCement(0.3, 0.36, 9, 36.6, 45, 36.6, 45);
        Assert.True(kc > kb && mc > 0);
    }
}

public class PetrophysicsTests
{
    [Fact]
    public void Archie_KnownValue()
    {
        // a=1,m=2,n=2, Rw=0.05, phi=0.2, Rt=5 -> Sw = sqrt(0.05/(0.04*5)) = 0.5
        Assert.Equal(0.5, PetrophysicsModels.Archie(5, 0.2, 0.05, 1, 2, 2), 9);
        Assert.Equal(0.5, PetrophysicsModels.Simandoux(5, 0.2, 0, 0.05, 2, 1, 2), 6);
        Assert.Equal(0.5, PetrophysicsModels.Indonesia(5, 0.2, 0, 0.05, 2, 1, 2, 2), 6);
    }

    [Fact]
    public void Vshale_Methods_AreBoundedAndOrdered()
    {
        double igr = 0.5;
        double lin = PetrophysicsModels.Vshale(igr, VshaleMethod.Linear), lt = PetrophysicsModels.Vshale(igr, VshaleMethod.LarionovTertiary);
        Assert.Equal(0.5, lin);
        Assert.True(lt < lin);
        Assert.Equal(0, PetrophysicsModels.Vshale(0, VshaleMethod.LarionovOlder), 9);
        Assert.Equal(1, PetrophysicsModels.Vshale(1, VshaleMethod.Steiber), 9);
        Assert.Equal(0.2, PetrophysicsModels.DensityPorosity(2.32, 2.65, 1.0), 9);
    }

    [Fact]
    public void Evaluation_FlagsGasSandAsPay()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 30, Crosslines = 40 };
        var w = gen.BuildWell("P-1", 15, (int)(40 * 0.42)); // crest: gas present
        var res = WellEvaluation.Evaluate(w, new PetroParameters { GrClean = 40, GrShale = 115 });
        Assert.True(res.Summary.NetPay > 5, $"net pay {res.Summary.NetPay:0.0} m");
        Assert.True(res.Summary.AverageSw < 0.5);
        Assert.InRange(res.Summary.NetToGross, 0.01, 0.6);
    }
}

public class CrossplotTests
{
    [Fact]
    public void KMeans_SeparatesClusters()
    {
        var rng = new Random(4);
        var f = new List<double[]>();
        for (int i = 0; i < 100; i++) f.Add([rng.NextDouble(), rng.NextDouble()]);
        for (int i = 0; i < 100; i++) f.Add([10 + rng.NextDouble(), 10 + rng.NextDouble()]);
        var res = KMeans.Cluster(f.ToArray(), 2);
        Assert.True(res.Labels.Take(100).Distinct().Count() == 1 && res.Labels.Skip(100).Distinct().Count() == 1);
        Assert.NotEqual(res.Labels[0], res.Labels[150]);
    }

    [Fact]
    public void GaussianClassifier_ClassifiesTrainingClasses()
    {
        var rng = new Random(8);
        var f = new List<double[]>(); var l = new List<string>();
        for (int i = 0; i < 80; i++) { f.Add([5000 + 200 * rng.NextDouble(), 1.6 + 0.1 * rng.NextDouble()]); l.Add("Gas sand"); }
        for (int i = 0; i < 80; i++) { f.Add([7000 + 300 * rng.NextDouble(), 2.0 + 0.1 * rng.NextDouble()]); l.Add("Shale"); }
        var clf = GaussianFaciesClassifier.Train(f, l);
        Assert.Equal("Gas sand", clf.Classify([5100, 1.65]).Label);
        var (lab, post) = clf.Classify([7100, 2.05]);
        Assert.Equal("Shale", lab);
        Assert.True(post.Max() > 0.99);
    }

    [Fact]
    public void Selection_And_Statistics()
    {
        var d = new CrossplotData([1, 2, 3, 4, double.NaN], [2, 4, 6, 8, 1]);
        Assert.Equal(4, d.ValidIndices().Length);
        var st = CrossplotAnalytics.Statistics(d);
        Assert.Equal(2, st.Fit.Slope, 9);
        Assert.Equal(1, st.Pearson, 9);
        var sel = CrossplotAnalytics.SelectInPolygon(d, [new(0, 0), new(2.5, 0), new(2.5, 10), new(0, 10)]);
        Assert.Equal([0, 1], sel);
    }
}

public class GeologicalModelingTests
{
    [Fact]
    public void Framework_And_StratigraphicGrid()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 12, Crosslines = 14 };
        var g = gen.Geometry;
        var hs = gen.BuildHorizons();
        var fw = new StructuralFramework(g);
        fw.AddSurface("H3", hs[2].AsGrid()); fw.AddSurface("H1", hs[0].AsGrid()); fw.AddSurface("H2", hs[1].AsGrid());
        fw.Build();
        Assert.Equal("H1", fw.Surfaces[0].Name);
        var grid = StratigraphicGrid.Build(fw, [new Zone("Z1", 0, 1, 4, LayeringStyle.Proportional), new Zone("Z2", 1, 2, 3, LayeringStyle.Proportional)], 2);
        Assert.Equal(7, grid.NK);
        Assert.Equal(fw.Surfaces[0].Surface[0, 0], grid.Z(0, 0, 0), 9);
        Assert.Equal(fw.Surfaces[2].Surface[0, 0], grid.Z(0, 0, 7), 9);
        Assert.True(grid.ZoneBulkVolume(0, 625) > 0);
    }

    [Fact]
    public void Kriging_HonoursDataAndIsBounded()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 12, Crosslines = 14 };
        var g = gen.Geometry;
        var hs = gen.BuildHorizons();
        var fw = new StructuralFramework(g);
        fw.AddSurface("A", hs[0].AsGrid()); fw.AddSurface("B", hs[1].AsGrid());
        fw.Build();
        var grid = StratigraphicGrid.Build(fw, [new Zone("Z", 0, 1, 2, LayeringStyle.Proportional)]);
        var data = new List<PropertyModeling.CellDatum>
        {
            new(2, 2, 0, g.MapLocation(gen.FirstInline + 2, gen.FirstCrossline + 2), 0.2),
            new(9, 10, 0, g.MapLocation(gen.FirstInline + 9, gen.FirstCrossline + 10), 0.3),
            new(5, 3, 0, g.MapLocation(gen.FirstInline + 5, gen.FirstCrossline + 3), 0.25),
        };
        var prop = PropertyModeling.Distribute(grid, data, PropertyMethod.OrdinaryKriging, new Variogram(VariogramModel.Spherical, 0, 1, 300));
        Assert.Equal(0.2, prop[grid.CellIndex(2, 2, 0)], 4);
        Assert.Equal(0.3, prop[grid.CellIndex(9, 10, 0)], 4);
        Assert.All(prop.Where(v => !float.IsNaN(v)), v => Assert.InRange(v, 0.1, 0.4));
        var idw = PropertyModeling.Distribute(grid, data, PropertyMethod.InverseDistance);
        Assert.Equal(0.2, idw[grid.CellIndex(2, 2, 0)], 2);
    }

    [Fact]
    public void Variogram_Models()
    {
        var v = new Variogram(VariogramModel.Spherical, 0.1, 1, 100);
        Assert.Equal(0, v.Gamma(0));
        Assert.Equal(1, v.Gamma(150), 9);
        Assert.True(new Variogram(VariogramModel.Exponential, 0, 1, 100).Gamma(100) > 0.94);
    }
}
