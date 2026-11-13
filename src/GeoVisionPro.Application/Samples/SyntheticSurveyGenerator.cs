using System.Globalization;
using System.Text;
using GeoVisionPro.AVO;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;
using GeoVisionPro.RockPhysics;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Application.Samples;

/// <summary>
/// Generates a physically consistent synthetic test survey from a layered earth model: a faulted anticline with a gas sand
/// at the crest. Outputs a post-stack SEG-Y (convolutional model + noise), pre-stack CMP gathers (hyperbolic moveout,
/// Zoeppritz amplitudes), wells with LAS logs consistent with the model, checkshots, tops and the true horizons.
/// Every product is derived from the same model, so the processing, tie, inversion and AVO workflows can be validated
/// against known answers.
/// </summary>
public sealed class SyntheticSurveyGenerator
{
    public sealed record LayerModel(string Name, double Vp, double Vs, double Rho, double Gr, double VpGradient = 0);

    public int Inlines { get; init; } = 60;
    public int Crosslines { get; init; } = 80;
    public int Samples { get; init; } = 376;          // 1.5 s at 4 ms
    public double Dt { get; init; } = 0.004;
    public double BinSize { get; init; } = 25;
    public double OriginX { get; init; } = 500_000;
    public double OriginY { get; init; } = 6_700_000;
    public double Azimuth { get; init; } = 30;        // degrees, rotation of the inline axis
    public double PeakFrequency { get; init; } = 30;
    public double NoiseLevel { get; init; } = 0.02;   // relative to RMS
    public int Seed { get; init; } = 42;
    public int FirstInline { get; init; } = 100;
    public int FirstCrossline { get; init; } = 200;

    /// <summary>Layers from the surface down; the gas sand (index 3) replaces the brine sand above the gas-water contact.</summary>
    public IReadOnlyList<LayerModel> Layers { get; init; } =
    [
        new("Overburden", 1900, 700, 2.10, 75, 0.6),
        new("Upper Shale", 2500, 1050, 2.28, 105, 0.4),
        new("Shale B", 2700, 1200, 2.33, 110, 0.3),
        new("Reservoir Sand", 3050, 1650, 2.24, 45, 0),
        new("Lower Shale", 2950, 1400, 2.40, 115, 0.3),
        new("Carbonate", 4300, 2350, 2.58, 25, 0),
    ];
    public LayerModel GasSand { get; } = new("Reservoir Sand (gas)", 2650, 1700, 2.08, 40);

    /// <summary>Base two-way times of horizons (tops of layers 1..5) at the survey edge.</summary>
    public double[] HorizonBaseTimes { get; init; } = [0.42, 0.62, 0.80, 0.86, 1.05];
    public double AnticlineAmplitude { get; init; } = 0.07;
    public double FaultThrow { get; init; } = 0.025;
    public double GasWaterContactTime { get; init; } = 0.775;

    public SurveyGeometry Geometry
    {
        get
        {
            double a = Azimuth * Math.PI / 180;
            // inline axis direction (increasing crossline moves along the inline): (sin a, cos a); inline step perpendicular
            var t = new GridTransform(FirstInline, FirstCrossline, OriginX, OriginY,
                BinSize * Math.Cos(a), BinSize * Math.Sin(a), -BinSize * Math.Sin(a), BinSize * Math.Cos(a));
            return new SurveyGeometry(new IndexRange(FirstInline, FirstInline + Inlines - 1, 1), new IndexRange(FirstCrossline, FirstCrossline + Crosslines - 1, 1),
                Samples, Dt, 0, t, DomainType.Time);
        }
    }

    /// <summary>Horizon time of surface k (0-based, top of layer k+1) at grid indices (i, j).</summary>
    public double HorizonTime(int k, double i, double j)
    {
        double ci = Inlines * 0.5, cj = Crosslines * 0.42, s = Math.Min(Inlines, Crosslines) * 0.32;
        double dome = AnticlineAmplitude * Math.Exp(-((i - ci) * (i - ci) + (j - cj) * (j - cj)) / (2 * s * s));
        double dip = 0.0006 * i + 0.0003 * j;
        double fault = j > FaultTrace(i) ? FaultThrow * (k + 1) / HorizonBaseTimes.Length : 0;
        double damp = 0.6 + 0.4 * k / Math.Max(1, HorizonBaseTimes.Length - 1);
        return HorizonBaseTimes[k] - dome * damp + dip + fault;
    }

    /// <summary>Fault trace position (crossline index) as a function of inline index: a slightly oblique planar fault.</summary>
    public double FaultTrace(double i) => Crosslines * 0.68 + 0.15 * (i - Inlines / 2.0);

    private (int Layer, bool Gas) LayerAt(double t, double i, double j)
    {
        int layer = 0;
        for (int k = 0; k < HorizonBaseTimes.Length; k++) if (t >= HorizonTime(k, i, j)) layer = k + 1;
        bool gas = layer == 3 && t < GasWaterContactTime && j <= FaultTrace(i);
        return (layer, gas);
    }

    public LayerModel Properties(double t, double i, double j, out double vp)
    {
        var (layer, gas) = LayerAt(t, i, j);
        var m = gas ? GasSand : Layers[layer];
        double top = layer == 0 ? 0 : HorizonTime(layer - 1, i, j);
        vp = m.Vp + m.VpGradient * (t - top) * 1000;
        return m;
    }

    /// <summary>Acoustic impedance trace on the survey time axis at fractional grid position.</summary>
    public float[] ImpedanceTrace(double i, double j)
    {
        var z = new float[Samples];
        for (int k = 0; k < Samples; k++)
        {
            var m = Properties(k * Dt, i, j, out var vp);
            z[k] = (float)(vp * m.Rho);
        }
        return z;
    }

    public Wavelet Wavelet => Wavelet.Ricker(PeakFrequency, Dt, 0.2);

    /// <summary>Noise-free post-stack trace (normal-incidence convolutional model, exact reflection coefficients).</summary>
    public float[] CleanTrace(double i, double j)
    {
        var z = ImpedanceTrace(i, j);
        var r = new float[Samples];
        for (int k = 1; k < Samples; k++) r[k] = (z[k] - z[k - 1]) / (z[k] + z[k - 1]);
        var w = Wavelet;
        return Dsp.ConvolveSame(r, w.Samples, w.CenterIndex);
    }

    public InMemoryVolume BuildVolume(bool addNoise = true)
    {
        var g = Geometry;
        var vol = new InMemoryVolume("Synthetic stack", g);
        var rng = new Random(Seed);
        float rmsRef = 0;
        for (int i = 0; i < Inlines; i++)
            for (int j = 0; j < Crosslines; j++)
            {
                var tr = CleanTrace(i, j);
                if (rmsRef == 0) rmsRef = Dsp.Rms(tr);
                if (addNoise && NoiseLevel > 0)
                {
                    var noise = new float[Samples];
                    for (int k = 0; k < Samples; k++) noise[k] = (float)(Gaussian(rng) * NoiseLevel * rmsRef * 3);
                    FrequencyFilters.BandPass(noise, Dt, 4, 8, 70, 90);
                    for (int k = 0; k < Samples; k++) tr[k] += noise[k];
                }
                vol.WriteTrace(FirstInline + i, FirstCrossline + j, tr);
            }
        return vol;
    }

    private static double Gaussian(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());

    /// <summary>True interpreted horizons (tops of layers 1..n).</summary>
    public List<Horizon> BuildHorizons()
    {
        var g = Geometry;
        string[] colors = ["#FFB300", "#20B8A6", "#E040FB", "#4FC3F7", "#FF7043"];
        var list = new List<Horizon>();
        for (int k = 0; k < HorizonBaseTimes.Length; k++)
        {
            var h = Horizon.CreateFor(g, $"Top {Layers[k + 1].Name}", colors[k % colors.Length]);
            for (int i = 0; i < Inlines; i++)
                for (int j = 0; j < Crosslines; j++)
                    h.SetValue(FirstInline + i, FirstCrossline + j, HorizonTime(k, i, j), PickSource.Imported);
            h.Description = "Synthetic model surface (ground truth)";
            list.Add(h);
        }
        return list;
    }

    /// <summary>Fault sticks along the model fault trace (survey coordinates; time in s).</summary>
    public Fault BuildFault()
    {
        var f = new Fault { Name = "Fault F1", Color = "#E53935", Domain = DomainType.Time };
        for (int i = 0; i < Inlines; i += 6)
        {
            var st = new FaultStick();
            for (double t = 0.3; t <= 1.25; t += 0.19)
                st.Points.Add(new Point3D(FirstInline + i, FirstCrossline + FaultTrace(i) + (t - 0.3) * 6, t));
            f.Sticks.Add(st);
        }
        return f;
    }

    /// <summary>
    /// Builds a vertical well at grid position (i, j) with logs sampled every 0.1524 m from the model. Depth conversion uses the
    /// model's interval velocities (SRD = MSL, KB = 25 m). Includes tops at layer boundaries and a checkshot survey.
    /// </summary>
    public Well BuildWell(string name, int i, int j, double logStartTvdss = 250, int checkshotEvery = 40)
    {
        var g = Geometry;
        var loc = g.MapLocation(FirstInline + i, FirstCrossline + j);
        const double kb = 25;
        var well = new Well { Name = name, Uwi = $"GVP-{name}", SurfaceX = loc.X, SurfaceY = loc.Y, KellyBushing = kb, GroundLevel = 0, DepthUnit = DepthUnit.Meters, Field = "Synthetic Field", Operator = "GeoVision Pro" };
        // time -> depth (TVDSS) by integrating interval velocity on a fine time grid
        double fine = 0.0002;
        var tList = new List<double> { 0 }; var zList = new List<double> { 0 };
        double z = 0;
        for (double t = fine; t <= Samples * Dt + 0.2; t += fine)
        {
            Properties(t - fine / 2, i, j, out var vp);
            z += vp * fine / 2;
            tList.Add(t); zList.Add(z);
        }
        var tArr = tList.ToArray(); var zArr = zList.ToArray();
        double TimeAt(double tvdss) => Dsp.Interp(zArr, tArr, tvdss, clamp: true);
        double td = Dsp.Interp(tArr, zArr, (Samples - 1) * Dt - 0.05, clamp: true);
        well.TotalDepth = td + kb;
        double step = 0.1524;
        int n = (int)((td - logStartTvdss) / step);
        var md = new double[n];
        float[] dt = new float[n], dts = new float[n], rhob = new float[n], gr = new float[n], nphi = new float[n], rt = new float[n], vpArr = new float[n];
        var rng = new Random(Seed + i * 1000 + j);
        for (int k = 0; k < n; k++)
        {
            double tvdss = logStartTvdss + k * step;
            md[k] = tvdss + kb;
            double t = TimeAt(tvdss);
            var m = Properties(t, i, j, out var vp);
            bool gas = ReferenceEquals(m, GasSand);
            double vs = m.Vs * vp / m.Vp;
            double noise = 1 + 0.01 * Gaussian(rng);
            dt[k] = (float)(304800.0 / (vp * noise));
            dts[k] = (float)(304800.0 / vs);
            rhob[k] = (float)(m.Rho + 0.008 * Gaussian(rng));
            gr[k] = (float)(m.Gr + 4 * Gaussian(rng));
            double phi = Math.Clamp((2.65 - m.Rho) / (2.65 - (gas ? 0.3 : 1.0)), 0.02, 0.38);
            nphi[k] = (float)(gas ? phi * 0.55 : m.Gr > 90 ? 0.32 + 0.02 * Gaussian(rng) : phi + 0.02);
            double sw = gas ? 0.25 : 1.0;
            rt[k] = (float)(m.Gr > 90 ? 2.5 : 0.05 / (Math.Pow(Math.Max(phi, 0.02), 2) * sw * sw));
            vpArr[k] = (float)vp;
        }
        LogCurve C(string mn, float[] v, string unit, CurveFamily f) => new() { WellId = well.Id, Name = mn, Mnemonic = mn, Unit = unit, Family = f, Depths = md, Values = v, Source = "Synthetic model" };
        well.Curves.AddRange([C("GR", gr, "gAPI", CurveFamily.GammaRay), C("RHOB", rhob, "g/cc", CurveFamily.Density), C("NPHI", nphi, "v/v", CurveFamily.NeutronPorosity),
            C("DT", dt, "us/ft", CurveFamily.Sonic), C("DTS", dts, "us/ft", CurveFamily.ShearSonic), C("RT", rt, "ohmm", CurveFamily.ResistivityDeep)]);
        for (int k = 0; k < HorizonBaseTimes.Length; k++)
        {
            double th = HorizonTime(k, i, j);
            double tvdss = Dsp.Interp(tArr, zArr, th, clamp: true);
            well.Tops.Add(new WellTop { WellId = well.Id, Name = Layers[k + 1].Name, MeasuredDepth = tvdss + kb, Source = "Synthetic model" });
        }
        var cs = new CheckshotSurvey { WellId = well.Id, Name = "Checkshot (synthetic)", Source = TimeDepthSource.Checkshot };
        for (int k = 0; k < n; k += checkshotEvery * 10)
        {
            double tvdss = logStartTvdss + k * step;
            cs.Points.Add(new TimeDepthPoint(tvdss + kb, TimeAt(tvdss) + 0.0005 * Gaussian(rng), tvdss));
        }
        // shallow checkshots above the log for the replacement interval
        for (double tvdss = 50; tvdss < logStartTvdss; tvdss += 100) cs.Points.Insert(0, new TimeDepthPoint(tvdss + kb, TimeAt(tvdss), tvdss));
        cs.Points.Sort((a, b) => a.MeasuredDepth.CompareTo(b.MeasuredDepth));
        well.Checkshots.Add(cs);
        well.ActiveTimeDepthId = cs.Id;
        well.ValidationStatus = DataValidationStatus.Valid;
        return well;
    }

    /// <summary>
    /// CMP gathers over a sub-grid with hyperbolic moveout (RMS velocity from the local model), Zoeppritz PP amplitudes at the
    /// straight-ray incidence angle, and a stationary wavelet. Returned as an in-memory gather dataset with offsets in metres.
    /// </summary>
    public InMemoryGatherDataset BuildGathers(int inlineStep = 6, int crosslineStep = 8, double maxOffset = 2400, int nOffsets = 25)
    {
        var g = Geometry;
        var offsets = Enumerable.Range(0, nOffsets).Select(k => k * maxOffset / (nOffsets - 1)).ToArray();
        var ils = Enumerable.Range(0, Inlines).Where(i => i % inlineStep == 0).ToArray();
        var xls = Enumerable.Range(0, Crosslines).Where(j => j % crosslineStep == 0).ToArray();
        var sub = g with
        {
            Inlines = new IndexRange(FirstInline + ils[0], FirstInline + ils[^1], inlineStep),
            Crosslines = new IndexRange(FirstCrossline + xls[0], FirstCrossline + xls[^1], crosslineStep)
        };
        var ds = new InMemoryGatherDataset("Synthetic CMP gathers", sub, offsets, false);
        var w = Wavelet;
        foreach (var i in ils)
            foreach (var j in xls)
                ds.SetGather(FirstInline + i, FirstCrossline + j, ModelGather(i, j, offsets, w));
        return ds;
    }

    public Array2D ModelGather(double i, double j, double[] offsets, Wavelet w)
    {
        // interfaces and RMS velocity at each interface
        var interfaces = new List<(double T0, Layer Upper, Layer Lower, double Vrms, double Vint)>();
        double sumV2T = 0;
        Properties(0, i, j, out _);
        double prevT = 0;
        var tops = Enumerable.Range(0, HorizonBaseTimes.Length).Select(k => HorizonTime(k, i, j)).ToList();
        // include gas-water contact as an interface when gas present
        var (l3, _) = LayerAt(GasWaterContactTime - 1e-6, i, j);
        bool gasHere = LayerAt(tops[2] + 1e-4, i, j).Gas;
        if (gasHere && GasWaterContactTime < tops[3]) tops.Add(GasWaterContactTime);
        tops.Sort();
        _ = l3;
        foreach (var t in tops)
        {
            var mu = Properties(t - 1e-4, i, j, out var vpu);
            var ml = Properties(t + 1e-4, i, j, out var vpl);
            // accumulate RMS velocity with the interval above
            Properties((prevT + t) / 2, i, j, out var vmid);
            sumV2T += vmid * vmid * (t - prevT);
            prevT = t;
            double vrms = Math.Sqrt(sumV2T / t);
            interfaces.Add((t, new Layer(vpu, mu.Vs * vpu / mu.Vp, mu.Rho), new Layer(vpl, ml.Vs * vpl / ml.Vp, ml.Rho), vrms, vmid));
        }
        var gather = new Array2D(offsets.Length, Samples);
        for (int o = 0; o < offsets.Length; o++)
        {
            var spikes = new float[Samples];
            foreach (var (t0, up, lo, vrms, vint) in interfaces)
            {
                double tx = Math.Sqrt(t0 * t0 + offsets[o] * offsets[o] / (vrms * vrms));
                double ang = AvoAnalysis.OffsetToAngle(offsets[o], t0, vrms, up.Vp);
                if (ang >= Reflectivity.CriticalAngle(up, lo) - 2) continue;
                double r = Reflectivity.ZoeppritzPP(up, lo, ang).Real;
                double idx = tx / Dt;
                int k = (int)idx; double f = idx - k;
                if (k + 1 < Samples) { spikes[k] += (float)(r * (1 - f)); spikes[k + 1] += (float)(r * f); }
            }
            Dsp.ConvolveSame(spikes, w.Samples, w.CenterIndex).CopyTo(gather.Row(o));
            _ = interfaces;
        }
        return gather;
    }

    /// <summary>RMS velocity function (t:v) at grid position (for NMO of the synthetic gathers).</summary>
    public string RmsVelocityFunction(double i, double j)
    {
        var sb = new StringBuilder();
        double sum = 0;
        for (int k = 1; k <= Samples; k += 25)
        {
            sum = 0;
            for (int q = 0; q < k; q++) { Properties((q + 0.5) * Dt, i, j, out var v); sum += v * v * Dt; }
            double t = k * Dt;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{t:0.###}:{Math.Sqrt(sum / t):0}"));
        }
        return sb.ToString();
    }

    /// <summary>Writes the complete sample dataset to a folder: SEG-Y stack and gathers, LAS wells, checkshots, tops, horizons and fault sticks.</summary>
    public IReadOnlyList<string> WriteSampleFiles(string folder)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        var g = Geometry;
        using (var vol = BuildVolume())
        {
            var p = Path.Combine(folder, "synthetic_stack.sgy");
            SegyWriter.WriteVolume(p, vol, SegySampleFormat.IbmFloat32,
                ["GeoVision Pro synthetic test survey (faulted anticline with gas sand)", $"{Inlines} inlines x {Crosslines} crosslines, {Samples} samples @ {Dt * 1000} ms",
                 $"Ricker {PeakFrequency} Hz, SEG normal polarity (positive RC = peak)", "Bytes 189/193 inline/crossline, 181/185 CDP X/Y, scalar -100", "SEG-Y REV1 IBM float"]);
            files.Add(p);
        }
        using (var gathers = BuildGathers())
        {
            var p = Path.Combine(folder, "synthetic_gathers.sgy");
            SegyWriter.WriteGathers(p, gathers);
            files.Add(p);
            File.WriteAllText(Path.Combine(folder, "rms_velocity_center.txt"), RmsVelocityFunction(Inlines / 2.0, Crosslines * 0.42));
        }
        var wells = new[] { BuildWell("GV-1", Inlines / 2, (int)(Crosslines * 0.42)), BuildWell("GV-2", Inlines / 4, Crosslines / 6), BuildWell("GV-3", 3 * Inlines / 4, (int)(Crosslines * 0.85)) };
        var ci = CultureInfo.InvariantCulture;
        var tops = new StringBuilder("Well,Top,MD\n");
        foreach (var w in wells)
        {
            var p = Path.Combine(folder, $"{w.Name}.las");
            LasWriter.WriteFile(p, w);
            files.Add(p);
            var cs = new StringBuilder("MD,TVDSS,TWT_ms\n");
            foreach (var pt in w.Checkshots[0].Points) cs.AppendLine(string.Create(ci, $"{pt.MeasuredDepth:0.00},{pt.TvdSs:0.00},{pt.TwoWayTime * 1000:0.000}"));
            var cp = Path.Combine(folder, $"{w.Name}_checkshot.csv");
            File.WriteAllText(cp, cs.ToString());
            files.Add(cp);
            foreach (var t in w.Tops) tops.AppendLine(string.Create(ci, $"{w.Name},{t.Name},{t.MeasuredDepth:0.00}"));
        }
        File.WriteAllText(Path.Combine(folder, "well_tops.csv"), tops.ToString());
        files.Add(Path.Combine(folder, "well_tops.csv"));
        foreach (var h in BuildHorizons())
        {
            var p = Path.Combine(folder, $"{h.Name.Replace(' ', '_')}.xyz");
            File.WriteAllText(p, Interpretation.InterpretationIO.ExportXyz(h, g));
            files.Add(p);
        }
        var fp = Path.Combine(folder, "fault_F1_sticks.txt");
        File.WriteAllText(fp, Interpretation.InterpretationIO.ExportFaultSticks(BuildFault(), g));
        files.Add(fp);
        File.WriteAllText(Path.Combine(folder, "README.txt"),
            "GeoVision Pro synthetic sample data (generated by SyntheticSurveyGenerator).\n" +
            "synthetic_stack.sgy  : post-stack time volume, IBM float, inline/crossline at bytes 189/193, coordinates at 181/185 (scalar -100).\n" +
            "synthetic_gathers.sgy: NMO-uncorrected CMP gathers, offsets at bytes 37-40 (m).\n" +
            "GV-*.las             : wells (GR, RHOB, NPHI, DT, DTS, RT) consistent with the seismic model; KB = 25 m, SRD = MSL.\n" +
            "GV-*_checkshot.csv   : MD, TVDSS (m), TWT (ms).\n" +
            "*.xyz                : true horizons (X Y INLINE CROSSLINE TWT_ms).\n" +
            "fault_F1_sticks.txt  : fault sticks (X Y INLINE CROSSLINE TWT_ms STICK).\n" +
            "rms_velocity_center.txt: RMS velocity function t(s):v(m/s) at the anticline crest.\n");
        files.Add(Path.Combine(folder, "README.txt"));
        return files;
    }
}
