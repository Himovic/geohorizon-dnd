using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.AVO;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Crossplot;
using GeoVisionPro.Domain;
using GeoVisionPro.GeologicalModeling;
using GeoVisionPro.Inversion;
using GeoVisionPro.RockPhysics;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.SeismicProcessing;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Desktop.ViewModels;

public sealed partial class Selectable<T> : ObservableObject
{
    public required T Item { get; init; }
    public required string Name { get; init; }
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>Post-stack acoustic impedance inversion workflow and pre-stack simultaneous inversion at a location.</summary>
public sealed partial class InversionViewModel : DocumentViewModel
{
    public ObservableCollection<SeismicDataset> Volumes { get; } = [];
    public ObservableCollection<SeismicDataset> GatherSets { get; } = [];
    public ObservableCollection<Selectable<Well>> Wells { get; } = [];
    public ObservableCollection<Selectable<Horizon>> Horizons { get; } = [];
    public ObservableCollection<WaveletEntity> ProjectWavelets { get; } = [];
    [ObservableProperty] private SeismicDataset? _seismic;
    [ObservableProperty] private WaveletEntity? _wavelet;
    [ObservableProperty] private double _rickerFrequency = 30;
    [ObservableProperty] private bool _useRicker;
    [ObservableProperty] private double _lfCutoff = 10;
    [ObservableProperty] private InversionMethod _method = InversionMethod.ModelBased;
    [ObservableProperty] private double _scale = double.NaN;
    [ObservableProperty] private double _modelWeight = 0.05;
    [ObservableProperty] private double _constraint = 30;
    [ObservableProperty] private int _iterations = 40;
    [ObservableProperty] private double _sparsity = 0.1;
    [ObservableProperty] private double _merge = 10;
    [ObservableProperty] private Selectable<Well>? _qcWell;
    [ObservableProperty] private string _qcText = "";
    [ObservableProperty] private string _outputName = "Acoustic impedance";
    // pre-stack
    [ObservableProperty] private SeismicDataset? _gathers;
    [ObservableProperty] private int _gInline;
    [ObservableProperty] private int _gCrossline;
    [ObservableProperty] private string _velocityFunction = "0:1800, 1.5:2800";
    [ObservableProperty] private string _prestackText = "";
    public InversionMethod[] Methods { get; } = Enum.GetValues<InversionMethod>();
    public (double[] T, double[] Inverted, double[] Well, double[] Model)? QcCurves { get; private set; }
    public ElasticInversionResult? Elastic { get; private set; }
    public double ElasticT0 { get; private set; }
    public double ElasticDt { get; private set; }
    public event EventHandler? Updated;

    public InversionViewModel(MainViewModel main) : base(main)
    {
        Title = "Seismic Inversion";
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D && s.Category != SeismicCategory.Inversion)) Volumes.Add(s);
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers)) GatherSets.Add(s);
        foreach (var w in main.Session.Wells.Where(w => w.FindCurve(CurveFamily.Sonic) is not null && w.FindCurve(CurveFamily.Density) is not null)) Wells.Add(new Selectable<Well> { Item = w, Name = w.Name });
        foreach (var h in main.Session.Horizons) Horizons.Add(new Selectable<Horizon> { Item = h, Name = h.Name });
        foreach (var w in main.Session.Wavelets) ProjectWavelets.Add(w);
        Seismic = main.SelectedSeismic();
        Wavelet = ProjectWavelets.FirstOrDefault();
        UseRicker = Wavelet is null;
        QcWell = Wells.FirstOrDefault();
        Gathers = GatherSets.FirstOrDefault();
    }

    partial void OnGathersChanged(SeismicDataset? value)
    {
        if (value is null) return;
        var g = value.Geometry;
        GInline = g.Inlines.ValueAt(g.InlineCount / 2); GCrossline = g.Crosslines.ValueAt(g.CrosslineCount / 2);
    }

    private Wavelet CurrentWavelet(double dt) => !UseRicker && Wavelet is not null ? Wavelet.ToWavelet() : GeoVisionPro.Core.Numerics.Wavelet.Ricker(RickerFrequency, dt, 0.2);

    private InversionParameters Params() => new()
    {
        Method = Method, SeismicScale = Scale, ModelWeight = ModelWeight, ConstraintPercent = Constraint, Iterations = Iterations, SparsityLambda = Sparsity, MergeFrequency = Merge
    };

    private (List<WellTimeLog> Logs, LowFrequencyModel Model) BuildModel(ISeismicVolume vol)
    {
        var logs = Wells.Where(w => w.IsSelected).Select(w => WellTimeLog.AcousticImpedance(w.Item, Main.WellServices.GetTimeDepth(w.Item), vol.Geometry.SampleInterval)).ToList();
        if (logs.Count == 0) throw new InvalidOperationException("Select at least one well with sonic and density logs.");
        return (logs, new LowFrequencyModel(vol.Geometry, logs, Horizons.Where(h => h.IsSelected).Select(h => h.Item).ToList(), LfCutoff));
    }

    [RelayCommand]
    private async Task EstimateScale()
    {
        if (Seismic is null) return;
        try
        {
            var vol = Main.Session.GetVolume(Seismic);
            var (logs, _) = BuildModel(vol);
            double s = await Task.Run(() => InversionEngine.EstimateScaleFromWells(vol, logs, CurrentWavelet(vol.Geometry.SampleInterval)));
            Scale = Math.Round(s, 6);
            QcText = $"Seismic scaler from {logs.Count} well(s): {s:G5} (seismic amplitude per unit reflectivity × wavelet).";
        }
        catch (Exception e) { QcText = e.Message; }
    }

    [RelayCommand]
    private async Task QcAtWell()
    {
        if (Seismic is null || QcWell is null) return;
        try
        {
            var vol = Main.Session.GetVolume(Seismic);
            var g = vol.Geometry;
            var (logs, model) = BuildModel(vol);
            var log = WellTimeLog.AcousticImpedance(QcWell.Item, Main.WellServices.GetTimeDepth(QcWell.Item), g.SampleInterval);
            var nt = g.NearestTrace(log.Location) ?? throw new InvalidOperationException("Well is outside the survey.");
            var p = Params();
            if (double.IsNaN(p.SeismicScale)) p = p with { SeismicScale = InversionEngine.EstimateScaleFromWells(vol, logs, CurrentWavelet(g.SampleInterval)) };
            var w = CurrentWavelet(g.SampleInterval);
            var (res, lf) = await Task.Run(() =>
            {
                var lfm = model.Trace(nt.Inline, nt.Crossline);
                return (new PostStackInversion(w, g.SampleInterval).InvertTrace(vol.ReadTrace(nt.Inline, nt.Crossline), lfm, p), lfm);
            });
            var qc = InversionEngine.CompareWithWell(res.Impedance, g.StartTime, g.SampleInterval, log, 0);
            var t = Enumerable.Range(0, g.SampleCount).Select(k => g.TimeAt(k) * 1000).ToArray();
            QcCurves = (t, res.Impedance.Select(v => (double)v).ToArray(), Enumerable.Range(0, g.SampleCount).Select(k => g.TimeAt(k) >= log.FirstTime && g.TimeAt(k) <= log.LastTime ? log.ValueAt(g.TimeAt(k)) : double.NaN).ToArray(), lf.Select(v => (double)v).ToArray());
            QcText = $"QC at {QcWell.Name} (trace {nt.Inline}/{nt.Crossline}): r(inverted, well) = {qc.Correlation:0.000}, RMS error {qc.RmsError:0} ({qc.RelativeRmsError * 100:0.0}%), bias {qc.MeanBiasPercent:+0.0;-0.0}%; synthetic fit r = {res.Correlation:0.000}. Scaler {p.SeismicScale:G4}. Estimated values, not measurements.";
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { QcText = e.Message; }
    }

    [RelayCommand]
    private async Task BuildModelVolume()
    {
        if (Seismic is null) return;
        try
        {
            var vol = Main.Session.GetVolume(Seismic);
            var (_, model) = BuildModel(vol);
            var ds = Seismic;
            SeismicDataset? res = null;
            await RunAsync("Low-frequency model", async (p, ct) => res = await Main.Processing.MaterializeModelAsync(ds, model, $"LF model ({LfCutoff} Hz)", p, ct));
            if (res is not null) Main.Open(new SeismicViewerViewModel(Main, res, SectionKind.Inline));
        }
        catch (Exception e) { QcText = e.Message; }
    }

    [RelayCommand]
    private async Task Run()
    {
        if (Seismic is null) return;
        try
        {
            var vol = Main.Session.GetVolume(Seismic);
            var (logs, model) = BuildModel(vol);
            var p = Params();
            var w = CurrentWavelet(vol.Geometry.SampleInterval);
            if (double.IsNaN(p.SeismicScale)) p = p with { SeismicScale = InversionEngine.EstimateScaleFromWells(vol, logs, w) };
            var ds = Seismic; var name = OutputName;
            SeismicDataset? res = null;
            await RunAsync($"Inversion ({p.Method})", async (pr, ct) => res = await Main.Processing.InvertAsync(ds, model, w, p, name, pr, ct));
            if (res is not null) Main.Open(new SeismicViewerViewModel(Main, res, SectionKind.Inline));
        }
        catch (Exception e) { QcText = e.Message; }
    }

    /// <summary>Simultaneous inversion of one CMP gather (offset gathers are converted to angle by NMO + straight-ray angles).</summary>
    [RelayCommand]
    private async Task RunPrestackAtLocation()
    {
        if (Gathers is null) return;
        try
        {
            var gs = Main.Session.GetGathers(Gathers);
            var g = gs.Geometry;
            var gather = gs.ReadGather(GInline, GCrossline) ?? throw new InvalidOperationException("No gather at this location.");
            var vf = GeoVisionPro.SeismicProcessing.VelocityFunction.Parse(VelocityFunction);
            double[] angles;
            if (gs.IsAngleGather) angles = gs.Offsets;
            else
            {
                Nmo.Correct(gather, gs.Offsets, vf, g.SampleInterval, g.StartTime, 0.5);
                double tMid = g.TimeAt(g.SampleCount / 2);
                angles = gs.Offsets.Select(o => AvoAnalysis.OffsetToAngle(o, tMid, vf.At(tMid), vf.At(tMid))).ToArray();
            }
            // background models from wells (Ip, Is via Vs/Vp, density)
            var wells = Wells.Where(w => w.IsSelected).Select(w => w.Item).Where(w => w.FindCurve(CurveFamily.ShearSonic) is not null).ToList();
            if (wells.Count == 0) throw new InvalidOperationException("Pre-stack inversion needs at least one selected well with DTS (shear sonic).");
            var w0 = wells[0];
            var tdr = Main.WellServices.GetTimeDepth(w0);
            var vp = LogConditioning.VelocityFromSlowness(w0.FindCurve(CurveFamily.Sonic)!);
            var vs = LogConditioning.VelocityFromSlowness(w0.FindCurve(CurveFamily.ShearSonic)!, "VS");
            var rho = w0.FindCurve(CurveFamily.Density)!;
            var ipC = vp.CloneAs("IP", vp.Values.Select((v, i) => (float)(v * rho.ValueAtDepth(vp.Depths[i]))).ToArray(), "", CurveFamily.AcousticImpedance, "");
            var isC = vs.CloneAs("IS", vs.Values.Select((v, i) => (float)(v * rho.ValueAtDepth(vs.Depths[i]))).ToArray(), "", CurveFamily.ShearImpedance, "");
            float[] Bg(LogCurve c)
            {
                var l = WellTimeLog.FromCurve(w0, c, tdr, g.SampleInterval);
                var tr = Enumerable.Range(0, g.SampleCount).Select(k => (float)Math.Log(Math.Max(l.ValueAt(g.TimeAt(k)), 1e-6))).ToArray();
                FrequencyFilters.Butterworth(tr, g.SampleInterval, 0, LfCutoff, 4);
                return tr.Select(v => (float)Math.Exp(v)).ToArray();
            }
            var ip0 = Bg(ipC); var is0 = Bg(isC); var r0 = Bg(rho);
            double kvsvp = is0.Average() / ip0.Average();
            var wavelet = CurrentWavelet(g.SampleInterval);
            // scale gather to reflectivity units using near-angle RMS vs modelled near-angle synthetic
            var sim = new SimultaneousInversion(angles, angles.Select(_ => wavelet).ToArray(), g.SampleInterval);
            double sc = double.IsNaN(Scale) ? 1 : Scale;
            var scaled = gather.Clone();
            for (int i = 0; i < scaled.Data.Length; i++) scaled.Data[i] = (float)(scaled.Data[i] / sc);
            Elastic = await Task.Run(() => sim.Invert(scaled, ip0, is0, r0, kvsvp, ModelWeight, Math.Max(Iterations, 60)));
            ElasticT0 = g.StartTime; ElasticDt = g.SampleInterval;
            PrestackText = $"Angles {string.Join(", ", angles.Select(a => a.ToString("0", CultureInfo.InvariantCulture)))}°. Per-angle fit r: {string.Join(" ", Elastic.AngleCorrelations.Select(c => c.ToString("0.00", CultureInfo.InvariantCulture)))}. {Elastic.Notes}";
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { PrestackText = e.Message; }
    }
}

/// <summary>AVO/AVA: gather amplitude analysis, intercept-gradient, classification, modelling (Zoeppritz and approximations).</summary>
public sealed partial class AvoViewModel : DocumentViewModel
{
    public ObservableCollection<SeismicDataset> GatherSets { get; } = [];
    [ObservableProperty] private SeismicDataset? _gathers;
    [ObservableProperty] private int _inline;
    [ObservableProperty] private int _crossline;
    [ObservableProperty] private string _velocityFunction = "0:1800, 1.5:2800";
    [ObservableProperty] private double _pickTimeMs = 800;
    [ObservableProperty] private int _halfWindow = 2;
    [ObservableProperty] private bool _pickTrough;
    [ObservableProperty] private string _result = "";
    // modelling
    [ObservableProperty] private double _vp1 = 2438, _vs1 = 1006, _rho1 = 2.25, _vp2 = 2600, _vs2 = 1700, _rho2 = 1.95;
    [ObservableProperty] private double _maxAngle = 45;
    [ObservableProperty] private bool _substituteToGas;
    [ObservableProperty] private double _porosity2 = 0.25;
    public Array2D? Gather { get; private set; }
    public double[] Angles { get; private set; } = [];
    public double[] Amplitudes { get; private set; } = [];
    public InterceptGradient? Fit { get; private set; }
    public SurveyGeometry? Geometry { get; private set; }
    public List<(double A, double B)> HorizonIg { get; } = [];
    public event EventHandler? Updated;

    public AvoViewModel(MainViewModel main) : base(main)
    {
        Title = "AVO / AVA";
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers)) GatherSets.Add(s);
        Gathers = GatherSets.FirstOrDefault();
    }

    partial void OnGathersChanged(SeismicDataset? value)
    {
        if (value is null) return;
        var g = value.Geometry;
        _inline = g.Inlines.ValueAt(g.InlineCount / 2); _crossline = g.Crosslines.ValueAt(g.CrosslineCount / 2);
        OnPropertyChanged(nameof(Inline)); OnPropertyChanged(nameof(Crossline));
        Analyze();
    }

    [RelayCommand]
    private void Analyze()
    {
        if (Gathers is null) { Updated?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            var gs = Main.Session.GetGathers(Gathers);
            var g = Geometry = gs.Geometry;
            var gather = gs.ReadGather(Inline, Crossline) ?? throw new InvalidOperationException("No gather at this location.");
            var vf = SeismicProcessing.VelocityFunction.Parse(VelocityFunction);
            if (!gs.IsAngleGather) Nmo.Correct(gather, gs.Offsets, vf, g.SampleInterval, g.StartTime, 0.6);
            Gather = gather;
            double t = PickTimeMs / 1000;
            Angles = gs.IsAngleGather ? gs.Offsets : gs.Offsets.Select(o => AvoAnalysis.OffsetToAngle(o, t, vf.At(t), vf.At(t))).ToArray();
            Amplitudes = AvoAnalysis.PickGatherAmplitudes(gather, t, g.SampleInterval, g.StartTime, HalfWindow, !PickTrough);
            for (int i = 0; i < Amplitudes.Length; i++) if (Amplitudes[i] == 0) Amplitudes[i] = double.NaN; // muted
            Fit = AvoAnalysis.FitInterceptGradient(Angles, Amplitudes);
            var cls = AvoAnalysis.Classify(Fit.Intercept / Math.Max(1e-12, Math.Abs(Amplitudes.Where(double.IsFinite).DefaultIfEmpty(1).Max())) * 0.1, Fit.Gradient / Math.Max(1e-12, Math.Abs(Amplitudes.Where(double.IsFinite).DefaultIfEmpty(1).Max())) * 0.1);
            Result = $"Intercept A = {Fit.Intercept:G4}, gradient B = {Fit.Gradient:G4} (r = {Fit.Correlation:0.000}, n = {Fit.Count}); A·B = {Fit.Product:G4}. " +
                     $"Class (amplitude-normalised, indicative only): {cls}. Fluid factor (mudrock-based): {AvoAnalysis.FluidFactor(Fit.Intercept, Fit.Gradient):G4}. Angles via straight-ray from the velocity function.";
        }
        catch (Exception e) { Result = e.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Intercept/gradient pairs at the pick time across all gathers (AVO crossplot).</summary>
    [RelayCommand]
    private async Task CrossplotAll()
    {
        if (Gathers is null) return;
        var gs = Main.Session.GetGathers(Gathers);
        var vf = SeismicProcessing.VelocityFunction.Parse(VelocityFunction);
        double t = PickTimeMs / 1000; int hw = HalfWindow; bool peak = !PickTrough;
        HorizonIg.Clear();
        var res = new List<(double, double)>();
        await RunAsync("AVO crossplot", (p, ct) => Task.Run(() =>
        {
            var g = gs.Geometry;
            for (int i = 0; i < g.InlineCount; i++)
                for (int j = 0; j < g.CrosslineCount; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    var gather = gs.ReadGather(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j));
                    if (gather is null) continue;
                    if (!gs.IsAngleGather) Nmo.Correct(gather, gs.Offsets, vf, g.SampleInterval, g.StartTime, 0.6);
                    var ang = gs.IsAngleGather ? gs.Offsets : gs.Offsets.Select(o => AvoAnalysis.OffsetToAngle(o, t, vf.At(t), vf.At(t))).ToArray();
                    var amp = AvoAnalysis.PickGatherAmplitudes(gather, t, g.SampleInterval, g.StartTime, hw, peak).Select(a => a == 0 ? double.NaN : a).ToArray();
                    var f = AvoAnalysis.FitInterceptGradient(ang, amp);
                    if (double.IsFinite(f.Intercept)) res.Add((f.Intercept, f.Gradient));
                }
        }, ct));
        HorizonIg.AddRange(res);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public (Layer Upper, Layer Lower) ModelLayers()
    {
        var u = new Layer(Vp1, Vs1, Rho1);
        var l = new Layer(Vp2, Vs2, Rho2);
        if (SubstituteToGas)
        {
            var brine = RockPhysicsModels.BatzleWangBrine(70, 25, 50000);
            var e = RockPhysicsModels.FluidSubstitution(new ElasticProperties(Vp2, Vs2, Rho2), Porosity2, Constituent.Quartz, brine, RockPhysicsModels.MixFluids(0.2, brine, Constituent.Gas));
            l = new Layer(e.Vp, e.Vs, e.Density);
        }
        return (u, l);
    }

    [RelayCommand] private void Model() => Updated?.Invoke(this, EventArgs.Empty);
}

/// <summary>Interactive crossplots of well logs or seismic volumes with selection, statistics, clustering and facies classification.</summary>
public sealed partial class CrossplotViewModel : DocumentViewModel
{
    public ObservableCollection<Selectable<Well>> Wells { get; } = [];
    public ObservableCollection<string> CurveNames { get; } = [];
    public ObservableCollection<SeismicDataset> Volumes { get; } = [];
    [ObservableProperty] private string _source = "Well logs";
    [ObservableProperty] private string? _xCurve;
    [ObservableProperty] private string? _yCurve;
    [ObservableProperty] private string? _zCurve;
    [ObservableProperty] private SeismicDataset? _xVolume;
    [ObservableProperty] private SeismicDataset? _yVolume;
    [ObservableProperty] private SeismicDataset? _zVolume;
    [ObservableProperty] private int _inlineDecimation = 4;
    [ObservableProperty] private double _depthMin = double.NaN;
    [ObservableProperty] private double _depthMax = double.NaN;
    [ObservableProperty] private bool _showRegression = true;
    [ObservableProperty] private int _clusters = 3;
    [ObservableProperty] private string _faciesName = "Facies A";
    [ObservableProperty] private string _stats = "";
    [ObservableProperty] private bool _selecting;
    public string[] Sources { get; } = ["Well logs", "Seismic volumes"];
    public CrossplotData? Data { get; private set; }
    public int[]? Selection { get; private set; }
    public int[]? Labels { get; private set; }
    public string[]? LabelNames { get; private set; }
    public List<Point2D> Polygon { get; } = [];
    private readonly List<(string Facies, int[] Indices)> _training = [];
    public event EventHandler? Updated;

    public CrossplotViewModel(MainViewModel main) : base(main)
    {
        Title = "Crossplot";
        foreach (var w in main.Session.Wells) Wells.Add(new Selectable<Well> { Item = w, Name = w.Name });
        foreach (var n in main.Session.Wells.SelectMany(w => w.Curves).Select(c => c.Mnemonic).Distinct()) CurveNames.Add(n);
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PostStack3D)) Volumes.Add(s);
        XCurve = CurveNames.FirstOrDefault(c => c is "AI" or "IP") ?? CurveNames.FirstOrDefault(c => c == "RHOB") ?? CurveNames.FirstOrDefault();
        YCurve = CurveNames.FirstOrDefault(c => c == "NPHI") ?? CurveNames.Skip(1).FirstOrDefault();
        ZCurve = CurveNames.FirstOrDefault(c => c == "GR");
        XVolume = Volumes.FirstOrDefault(); YVolume = Volumes.Skip(1).FirstOrDefault() ?? XVolume;
    }

    [RelayCommand]
    private async Task Load()
    {
        Labels = null; Selection = null; Polygon.Clear(); _training.Clear();
        try
        {
            Data = Source == "Well logs" ? FromWells() : await Task.Run(FromVolumes);
            UpdateStats();
        }
        catch (Exception e) { Stats = e.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private CrossplotData FromWells()
    {
        var x = new List<double>(); var y = new List<double>(); var z = new List<double>(); var src = new List<string>(); var dep = new List<double>();
        foreach (var w in Wells.Where(w => w.IsSelected).Select(w => w.Item))
        {
            var cx = w.FindCurve(XCurve ?? ""); var cy = w.FindCurve(YCurve ?? ""); var cz = ZCurve is null ? null : w.FindCurve(ZCurve);
            if (cx is null || cy is null) continue;
            for (int i = 0; i < cx.Count; i++)
            {
                double d = cx.Depths[i];
                if (!double.IsNaN(DepthMin) && d < DepthMin || !double.IsNaN(DepthMax) && d > DepthMax) continue;
                x.Add(cx.Values[i]); y.Add(cy.ValueAtDepth(d)); z.Add(cz?.ValueAtDepth(d) ?? d); src.Add(w.Name); dep.Add(d);
            }
        }
        return new CrossplotData(x.ToArray(), y.ToArray(), z.ToArray(), src.ToArray(), dep.ToArray()) { XName = XCurve ?? "X", YName = YCurve ?? "Y", ZName = ZCurve ?? "Depth" };
    }

    private CrossplotData FromVolumes()
    {
        if (XVolume is null || YVolume is null) throw new InvalidOperationException("Select X and Y volumes.");
        var vx = Main.Session.GetVolume(XVolume); var vy = Main.Session.GetVolume(YVolume); var vz = ZVolume is null ? null : Main.Session.GetVolume(ZVolume);
        var g = vx.Geometry;
        if (vy.Geometry.InlineCount != g.InlineCount || vy.Geometry.SampleCount != g.SampleCount) throw new InvalidOperationException("Volumes must share the same geometry.");
        var x = new List<double>(); var y = new List<double>(); var z = new List<double>();
        int dec = Math.Max(1, InlineDecimation);
        for (int i = 0; i < g.InlineCount; i += dec)
        {
            int il = g.Inlines.ValueAt(i);
            var a = vx.ReadInline(il); var b = vy.ReadInline(il); var c = vz?.ReadInline(il);
            for (int k = 0; k < a.Data.Length; k += dec)
            {
                if (a.Data[k] == 0 && b.Data[k] == 0) continue;
                x.Add(a.Data[k]); y.Add(b.Data[k]); z.Add(c?.Data[k] ?? g.TimeAt(k % g.SampleCount) * 1000);
            }
        }
        return new CrossplotData(x.ToArray(), y.ToArray(), z.ToArray()) { XName = XVolume.Name, YName = YVolume.Name, ZName = ZVolume?.Name ?? "TWT ms" };
    }

    private void UpdateStats()
    {
        if (Data is null) return;
        var st = CrossplotAnalytics.Statistics(Data, Selection);
        Stats = $"{(Selection is null ? "All" : "Selection")}: n = {st.X.Count}; {Data.XName} mean {st.X.Mean:G4} sd {st.X.StdDev:G4}; {Data.YName} mean {st.Y.Mean:G4} sd {st.Y.StdDev:G4}; " +
                $"Pearson r = {st.Pearson:0.000}, Spearman ρ = {st.Spearman:0.000}; fit {Data.YName} = {st.Fit.Intercept:G4} + {st.Fit.Slope:G4}·{Data.XName} (R² {st.Fit.RSquared:0.000}).";
    }

    public void AddPolygonPoint(double x, double y) { Polygon.Add(new Point2D(x, y)); Updated?.Invoke(this, EventArgs.Empty); }

    [RelayCommand]
    private void ClosePolygon()
    {
        if (Data is null || Polygon.Count < 3) return;
        Selection = CrossplotAnalytics.SelectInPolygon(Data, Polygon);
        Selecting = false;
        UpdateStats();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void ClearSelection() { Selection = null; Polygon.Clear(); UpdateStats(); Updated?.Invoke(this, EventArgs.Empty); }

    [RelayCommand]
    private void Cluster()
    {
        if (Data is null) return;
        var idx = Data.ValidIndices();
        var res = KMeans.Cluster(idx.Select(i => new[] { Data.X[i], Data.Y[i] }).ToArray(), Clusters);
        Labels = new int[Data.Count];
        Array.Fill(Labels, -1);
        for (int k = 0; k < idx.Length; k++) Labels[idx[k]] = res.Labels[k];
        LabelNames = Enumerable.Range(0, Clusters).Select(c => $"Cluster {c + 1}").ToArray();
        Stats = $"k-means: {Clusters} clusters, inertia {res.Inertia:G4} (standardised), {res.Iterations} iterations. Centroids: " +
                string.Join("; ", res.Centroids.Select((c, i) => $"{i + 1}: ({c[0]:G4}, {c[1]:G4})"));
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AssignFacies()
    {
        if (Selection is null || Selection.Length == 0) { Main.Dialogs.Info("Select points with a polygon first."); return; }
        _training.RemoveAll(t => t.Facies == FaciesName);
        _training.Add((FaciesName, Selection));
        Stats = $"Training facies: {string.Join(", ", _training.Select(t => $"{t.Facies} ({t.Indices.Length})"))}. Define ≥ 2 facies then Classify.";
        Selection = null; Polygon.Clear();
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Classify()
    {
        if (Data is null || _training.Count < 2) { Main.Dialogs.Info("Assign at least two facies from polygon selections."); return; }
        var feats = _training.SelectMany(t => t.Indices.Select(i => new[] { Data.X[i], Data.Y[i] })).ToList();
        var labels = _training.SelectMany(t => t.Indices.Select(_ => t.Facies)).ToList();
        try
        {
            var clf = GaussianFaciesClassifier.Train(feats, labels);
            LabelNames = clf.Labels.ToArray();
            Labels = new int[Data.Count];
            int uncertain = 0;
            for (int i = 0; i < Data.Count; i++)
            {
                if (!double.IsFinite(Data.X[i]) || !double.IsFinite(Data.Y[i])) { Labels[i] = -1; continue; }
                var (lab, post) = clf.Classify([Data.X[i], Data.Y[i]]);
                Labels[i] = Array.IndexOf(LabelNames, lab);
                if (post.Max() < 0.8) uncertain++;
            }
            Stats = $"Gaussian (QDA) facies classification: {LabelNames.Length} facies; {uncertain} of {Data.Count} points with max posterior < 0.8 (uncertain).";
        }
        catch (Exception e) { Stats = e.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void WriteFaciesLogs()
    {
        if (Data?.Source is null || Data.Depth is null || Labels is null || LabelNames is null) { Main.Dialogs.Info("Classify well-log data first."); return; }
        foreach (var grp in Enumerable.Range(0, Data.Count).GroupBy(i => Data.Source[i]))
        {
            var well = Main.Session.Wells.First(w => w.Name == grp.Key);
            var depths = grp.Select(i => Data.Depth[i]).ToArray();
            var vals = grp.Select(i => (float)(Labels[i] < 0 ? float.NaN : Labels[i] + 1)).ToArray();
            Main.WellServices.AddCurve(well, new LogCurve { Name = "FACIES", Mnemonic = "FACIES", Unit = "", Family = CurveFamily.Lithology, Depths = depths, Values = vals, IsComputed = true, Source = "Crossplot classification: " + string.Join(", ", LabelNames.Select((n, k) => $"{k + 1}={n}")) });
        }
        Main.Log.Success("Crossplot", "FACIES curves written to the wells.");
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Data is null) return;
        var p = Main.Dialogs.SaveFile("Export crossplot data", "CSV (*.csv)|*.csv", "crossplot.csv");
        if (p is null) return;
        var sb = new StringBuilder($"{Data.XName},{Data.YName},{Data.ZName},Source,Depth,Label\n");
        for (int i = 0; i < Data.Count; i++)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{Data.X[i]},{Data.Y[i]},{Data.Z?[i]},{Data.Source?[i]},{Data.Depth?[i]},{(Labels is null || Labels[i] < 0 ? "" : LabelNames![Labels[i]])}"));
        File.WriteAllText(p, sb.ToString());
    }

    [RelayCommand]
    private void SaveDefinition()
    {
        var e = new CrossplotEntity { Name = $"{XCurve ?? XVolume?.Name} vs {YCurve ?? YVolume?.Name}", Json = JsonSerializer.Serialize(new { Source, XCurve, YCurve, ZCurve, X = XVolume?.Id, Y = YVolume?.Id, DepthMin, DepthMax }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }) };
        Main.Session.Add(e);
    }
}

/// <summary>Structural framework, stratigraphic grid and property modelling.</summary>
public sealed partial class GeoModelViewModel : DocumentViewModel
{
    public ObservableCollection<Selectable<Horizon>> Horizons { get; } = [];
    public ObservableCollection<string> CurveNames { get; } = [];
    [ObservableProperty] private int _layersPerZone = 5;
    [ObservableProperty] private int _decimation = 2;
    [ObservableProperty] private string? _property = "PHIE";
    [ObservableProperty] private PropertyMethod _method = PropertyMethod.OrdinaryKriging;
    [ObservableProperty] private VariogramModel _variogramModel = VariogramModel.Spherical;
    [ObservableProperty] private double _range = 1500;
    [ObservableProperty] private double _nugget = 0.05;
    [ObservableProperty] private int _displayLayer;
    [ObservableProperty] private string _report = "";
    public PropertyMethod[] Methods { get; } = Enum.GetValues<PropertyMethod>();
    public VariogramModel[] Models { get; } = Enum.GetValues<VariogramModel>();
    public StratigraphicGrid? Grid { get; private set; }
    public float[]? PropertyValues { get; private set; }
    public event EventHandler? Updated;

    public GeoModelViewModel(MainViewModel main) : base(main)
    {
        Title = "Geological Model";
        foreach (var h in main.Session.Horizons) Horizons.Add(new Selectable<Horizon> { Item = h, Name = h.Name });
        foreach (var n in main.Session.Wells.SelectMany(w => w.Curves).Select(c => c.Mnemonic).Distinct()) CurveNames.Add(n);
        if (!CurveNames.Contains(Property ?? "")) Property = CurveNames.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Build()
    {
        var hs = Horizons.Where(h => h.IsSelected).Select(h => h.Item).ToList();
        if (hs.Count < 2) { Report = "Select at least two horizons (top and base)."; return; }
        var survey = Main.Session.Seismic.FirstOrDefault(s => s.Id == hs[0].SurveyId) ?? Main.SelectedSeismic();
        if (survey is null) { Report = "No survey for the horizons."; return; }
        var g = survey.Geometry;
        try
        {
            StructuralFramework? fw = null; StratigraphicGrid? grid = null; float[]? prop = null; var notes = new List<string>();
            int layers = LayersPerZone, dec = Decimation; string? propName = Property; var method = Method;
            var vario = new Variogram(VariogramModel, Nugget, 1, Range);
            var wells = Main.Session.Wells.ToList();
            await RunAsync("Build geological model", (p, ct) => Task.Run(() =>
            {
                fw = new StructuralFramework(g);
                foreach (var h in hs) fw.AddSurface(h.Name, h.AsGrid().Clone());
                fw.Build();
                notes.AddRange(fw.Messages);
                var zones = Enumerable.Range(0, fw.Surfaces.Count - 1).Select(z => new Zone($"{fw.Surfaces[z].Name} - {fw.Surfaces[z + 1].Name}", z, z + 1, layers, LayeringStyle.Proportional)).ToList();
                grid = StratigraphicGrid.Build(fw, zones, dec);
                if (propName is not null)
                {
                    var samples = new List<(Point2D, double, double)>();
                    foreach (var w in wells)
                    {
                        var c = w.FindCurve(propName);
                        if (c is null) continue;
                        var tdr = Main.WellServices.GetTimeDepth(w);
                        var traj = WellTrajectory.Compute(w);
                        for (int i = 0; i < c.Count; i += 2) samples.Add((traj.LocationAt(c.Depths[i]), tdr.TimeAtMd(c.Depths[i], traj), c.Values[i]));
                    }
                    var data = PropertyModeling.Upscale(grid, samples);
                    notes.Add($"{propName}: {data.Count} upscaled cells from {wells.Count(w => w.FindCurve(propName) is not null)} wells.");
                    if (data.Count > 0) prop = PropertyModeling.Distribute(grid, data, method, vario, 2, p, ct);
                }
            }, ct));
            if (grid is null) return;
            Grid = grid; PropertyValues = prop;
            double binArea = g.Transform.InlineSpacing * g.Transform.CrosslineSpacing;
            var zoneVols = Enumerable.Range(0, fw!.Surfaces.Count - 1).Select(z => $"zone {z + 1} bulk {grid.ZoneBulkVolume(z, binArea) / 1e6:0.###} (time-s·km²)").ToList();
            Report = $"Grid {grid.NI}×{grid.NJ}×{grid.NK} ({grid.CellCount:N0} cells), decimation {dec}. {string.Join("; ", zoneVols)}." + Environment.NewLine + string.Join(Environment.NewLine, notes) +
                     (prop is null ? "" : $"{Environment.NewLine}{propName} model: mean {prop.Where(float.IsFinite).DefaultIfEmpty(float.NaN).Average():0.###} ({Method}). Values between wells are interpolated estimates.");
            Main.Session.Add(new GeologicalModelEntity { Name = $"Model {string.Join("/", hs.Select(h => h.Name))}", Description = $"{grid.NI}x{grid.NJ}x{grid.NK}, {propName} {Method}", Json = JsonSerializer.Serialize(new { Horizons = hs.Select(h => h.Id), layers, dec, propName, Method = Method.ToString(), VariogramModel = VariogramModel.ToString(), Range, Nugget }) });
            DisplayLayer = Math.Min(DisplayLayer, grid.NK - 1);
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Report = e.Message; }
    }

    partial void OnDisplayLayerChanged(int value) => Updated?.Invoke(this, EventArgs.Empty);

    public Grid2D? LayerMap()
    {
        if (Grid is null || PropertyValues is null) return null;
        int k = Math.Clamp(DisplayLayer, 0, Grid.NK - 1);
        var m = new Grid2D(Grid.NI, Grid.NJ);
        for (int i = 0; i < Grid.NI; i++) for (int j = 0; j < Grid.NJ; j++) m[i, j] = PropertyValues[Grid.CellIndex(i, j, k)];
        return m;
    }
}
