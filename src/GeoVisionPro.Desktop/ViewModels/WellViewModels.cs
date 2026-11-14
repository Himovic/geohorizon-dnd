using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Domain;
using GeoVisionPro.Petrophysics;
using GeoVisionPro.RockPhysics;
using GeoVisionPro.WellLog;
using GeoVisionPro.WellTie;

namespace GeoVisionPro.Desktop.ViewModels;

public sealed partial class WellSelection : ObservableObject
{
    public required Well Well { get; init; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Multi-well log display and correlation panel with flattening on a formation top.</summary>
public sealed partial class WellLogViewModel : DocumentViewModel
{
    public ObservableCollection<WellSelection> Wells { get; } = [];
    public ObservableCollection<string> TopNames { get; } = [];
    [ObservableProperty] private string? _flattenOn;
    [ObservableProperty] private string _readout = "";
    public event EventHandler? WellsChanged;

    public WellLogViewModel(MainViewModel main, Well? initial = null) : base(main)
    {
        Title = initial is null ? "Well Logs" : $"Well Logs - {initial.Name}";
        Load(initial);
    }

    public override (string Kind, string Argument)? WorkspaceKey => ("WellLogs", "");

    private void Load(Well? initial)
    {
        Wells.Clear(); TopNames.Clear();
        foreach (var w in Main.Session.Wells)
        {
            var ws = new WellSelection { Well = w, IsSelected = initial is null ? Main.Session.Wells.Count <= 4 : ReferenceEquals(w, initial) };
            ws.PropertyChanged += (_, _) => WellsChanged?.Invoke(this, EventArgs.Empty);
            Wells.Add(ws);
        }
        TopNames.Add("(none)");
        foreach (var n in Main.Session.Wells.SelectMany(w => w.Tops).Select(t => t.Name).Distinct()) TopNames.Add(n);
        FlattenOn ??= "(none)";
        WellsChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFlattenOnChanged(string? value) => WellsChanged?.Invoke(this, EventArgs.Empty);
    public override void OnProjectChanged() { var sel = Wells.Where(w => w.IsSelected).Select(w => w.Well.Id).ToHashSet(); Load(null); foreach (var w in Wells) w.IsSelected = sel.Contains(w.Well.Id) || (sel.Count == 0 && w.IsSelected); }

    public IEnumerable<(Well Well, double Shift)> Displayed => Wells.Where(w => w.IsSelected).Select(w =>
    {
        var top = FlattenOn is null or "(none)" ? null : w.Well.Tops.FirstOrDefault(t => t.Name == FlattenOn);
        return (w.Well, top?.MeasuredDepth ?? 0);
    });
}

/// <summary>Checkshot QC and sonic calibration (drift curve) for a well.</summary>
public sealed partial class CheckshotViewModel : DocumentViewModel
{
    public ObservableCollection<Well> Wells { get; } = [];
    public ObservableCollection<CheckshotSurvey> Surveys { get; } = [];
    [ObservableProperty] private Well? _well;
    [ObservableProperty] private CheckshotSurvey? _survey;
    [ObservableProperty] private string _report = "";
    public TimeDepthModel? CheckshotModel { get; private set; }
    public CheckshotCalibrationResult? Calibration { get; private set; }
    public TimeDepthModel? SonicModel { get; private set; }
    public event EventHandler? Updated;

    public CheckshotViewModel(MainViewModel main) : base(main)
    {
        Title = "Checkshots & Time-Depth";
        foreach (var w in main.Session.Wells) Wells.Add(w);
        Well = main.SelectedWell();
    }

    partial void OnWellChanged(Well? value)
    {
        Surveys.Clear();
        if (value is null) return;
        foreach (var s in value.Checkshots) Surveys.Add(s);
        Survey = value.ActiveTimeDepth ?? Surveys.FirstOrDefault();
    }

    partial void OnSurveyChanged(CheckshotSurvey? value) => Compute(false);

    private void Compute(bool store)
    {
        Calibration = null; SonicModel = null; CheckshotModel = null;
        if (Well is null || Survey is null || Survey.Points.Count < 2) { Report = "Select a well with a checkshot or TDR survey (≥ 2 points)."; Updated?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            var traj = WellTrajectory.Compute(Well);
            CheckshotModel = TimeDepthModel.FromCheckshot(Survey, traj, Main.Session.Project.SeismicReferenceDatum);
            var qc = CheckshotCalibration.QualityControl(CheckshotModel);
            var lines = new List<string>
            {
                $"Survey '{Survey.Name}' ({Survey.Source}): {Survey.Points.Count} points, TWT {CheckshotModel.Twt[0] * 1000:0.0}-{CheckshotModel.Twt[^1] * 1000:0.0} ms.",
                $"Replacement velocity from shallowest point: {CheckshotCalibration.ReplacementVelocity(CheckshotModel):0} m/s."
            };
            lines.AddRange(qc.Select(q => "QC: " + q));
            if (Well.FindCurve(CurveFamily.Sonic) is not null)
            {
                Calibration = Main.WellServices.CalibrateSonic(Well, Survey, store);
                SonicModel = TimeDepthModel.FromSonic(Well.FindCurve(CurveFamily.Sonic)!, traj, Main.Session.Project.SeismicReferenceDatum, CheckshotCalibration.ReplacementVelocity(CheckshotModel), (CheckshotModel.TvdSs[0], CheckshotModel.Twt[0]));
                lines.Add($"Integrated sonic drift: RMS {Calibration.RmsResidualBefore * 1000:0.00} ms before, {Calibration.RmsResidualAfter * 1000:0.00} ms after drift correction.");
                if (store) lines.Add("Calibrated TDR and drift-corrected sonic stored in the well.");
            }
            else lines.Add("No sonic log: calibration unavailable.");
            Report = string.Join(Environment.NewLine, lines);
        }
        catch (Exception e) { Report = e.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void CalibrateAndStore() => Compute(true);

    [RelayCommand]
    private void SetActive()
    {
        if (Well is null || Survey is null) return;
        Well.ActiveTimeDepthId = Survey.Id;
        Main.Session.MarkDirty(Well);
        Main.Log.Info("Time-depth", $"{Well.Name}: active time-depth relation set to '{Survey.Name}'.");
        Main.RefreshViewers();
    }

    [RelayCommand]
    private void Import()
    {
        if (Well is null) return;
        var f = Main.Dialogs.OpenFile($"Import checkshots for {Well.Name}", Services.DialogService.TextFilter);
        if (f is null) return;
        Main.Safe(() => { var s = Main.WellImport.ImportCheckshots(f, Well); Surveys.Add(s); Survey = s; });
    }
}

/// <summary>Interactive well-to-seismic tie.</summary>
public sealed partial class WellTieViewModel : DocumentViewModel
{
    public ObservableCollection<Well> Wells { get; } = [];
    public ObservableCollection<SeismicDataset> Volumes { get; } = [];
    public ObservableCollection<WaveletEntity> ProjectWavelets { get; } = [];
    [ObservableProperty] private Well? _well;
    [ObservableProperty] private SeismicDataset? _seismic;
    [ObservableProperty] private string _waveletSource = "Ricker";
    [ObservableProperty] private double _rickerFrequency = 30;
    [ObservableProperty] private WaveletEntity? _projectWavelet;
    [ObservableProperty] private double _bulkShiftMs;
    [ObservableProperty] private double _phase;
    [ObservableProperty] private bool _reversePolarity;
    [ObservableProperty] private int _traceRadius = 1;
    [ObservableProperty] private double _windowStartMs = double.NaN;
    [ObservableProperty] private double _windowEndMs = double.NaN;
    [ObservableProperty] private double _waveletLengthMs = 200;
    [ObservableProperty] private string _quality = "";
    [ObservableProperty] private bool _activateTdr = true;
    public string[] WaveletSources { get; } = ["Ricker", "Project wavelet", "Statistical (extracted)", "Deterministic (least-squares)"];
    public WellTieSession? Session { get; private set; }
    public event EventHandler? Updated;
    private Wavelet? _extracted;

    public WellTieViewModel(MainViewModel main, Well? well = null) : base(main)
    {
        Title = "Well Tie";
        foreach (var w in main.Session.Wells) Wells.Add(w);
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D)) Volumes.Add(s);
        foreach (var w in main.Session.Wavelets) ProjectWavelets.Add(w);
        _seismic = main.SelectedSeismic();
        ProjectWavelet = ProjectWavelets.FirstOrDefault();
        Well = well ?? main.SelectedWell();
    }

    partial void OnWellChanged(Well? value) { _extracted = null; Title = $"Well Tie - {value?.Name}"; _ = Rebuild(); }
    partial void OnSeismicChanged(SeismicDataset? value) { _extracted = null; _ = Rebuild(); }
    partial void OnWaveletSourceChanged(string value) { _extracted = null; _ = Rebuild(); }
    partial void OnRickerFrequencyChanged(double value) => _ = Rebuild();
    partial void OnProjectWaveletChanged(WaveletEntity? value) => _ = Rebuild();
    partial void OnBulkShiftMsChanged(double value) => Recompute();
    partial void OnPhaseChanged(double value) => Recompute();
    partial void OnReversePolarityChanged(bool value) => Recompute();
    partial void OnTraceRadiusChanged(int value) => Recompute();
    partial void OnWindowStartMsChanged(double value) => Recompute();
    partial void OnWindowEndMsChanged(double value) => Recompute();

    private Wavelet CurrentWavelet(double dt)
    {
        switch (WaveletSource)
        {
            case "Project wavelet" when ProjectWavelet is not null: return ProjectWavelet.ToWavelet();
            case "Statistical (extracted)" or "Deterministic (least-squares)" when _extracted is not null: return _extracted;
            default: return Wavelet.Ricker(RickerFrequency, dt, WaveletLengthMs / 1000);
        }
    }

    private async Task Rebuild()
    {
        Session = null;
        if (Well is null || Seismic is null) { Quality = "Select a well and a seismic volume."; Updated?.Invoke(this, EventArgs.Empty); return; }
        var sonic = Well.FindCurve(CurveFamily.Sonic); var rhob = Well.FindCurve(CurveFamily.Density);
        if (sonic is null || rhob is null) { Quality = $"{Well.Name}: sonic (DT) and density (RHOB) logs are required."; Updated?.Invoke(this, EventArgs.Empty); return; }
        try
        {
            var vol = Main.Session.GetVolume(Seismic);
            var tdr = Main.WellServices.GetTimeDepth(Well);
            var dt = vol.Geometry.SampleInterval;
            if (WaveletSource is "Statistical (extracted)" or "Deterministic (least-squares)" && _extracted is null)
            {
                var tmp = new WellTieSession(Well, sonic, rhob, vol, tdr, Wavelet.Ricker(30, dt, 0.2));
                await Task.Run(tmp.Compute);
                _extracted = WaveletSource.StartsWith("Statistical")
                    ? WaveletEstimation.Statistical([tmp.SeismicTrace.Select(v => float.IsNaN(v) ? 0f : v).ToArray()], dt, 0, tmp.SeismicTrace.Length - 1, WaveletLengthMs / 1000)
                    : WaveletEstimation.LeastSquares(tmp.TiedReflectivity, tmp.SeismicTrace, dt, WaveletLengthMs / 1000).Wavelet;
            }
            Session = new WellTieSession(Well, sonic, rhob, vol, tdr, CurrentWavelet(dt));
            Recompute();
        }
        catch (Exception e) { Quality = e.Message; Updated?.Invoke(this, EventArgs.Empty); }
    }

    private void Recompute()
    {
        if (Session is null) return;
        Session.Parameters = Session.Parameters with
        {
            BulkShift = BulkShiftMs / 1000, PhaseRotation = Phase, ReversePolarity = ReversePolarity, TraceRadius = TraceRadius,
            WindowStart = double.IsNaN(WindowStartMs) ? double.NaN : WindowStartMs / 1000, WindowEnd = double.IsNaN(WindowEndMs) ? double.NaN : WindowEndMs / 1000
        };
        try
        {
            Session.Compute();
            var q = Session.Quality!;
            Quality = $"Correlation r = {q.Correlation:0.000} ({q.Rating}); best lag {q.BestLag * 1000:+0.0;-0.0} ms (r = {q.BestLagCorrelation:0.000}); NRMS error {q.NormalizedRmsError:0.00}. Wavelet: {Session.Wavelet.Name}";
        }
        catch (Exception e) { Quality = e.Message; }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task OptimizeShift()
    {
        if (Session is null) return;
        double s = 0;
        await Task.Run(() => s = Session.OptimizeBulkShift(0.08));
        _bulkShiftMs = s * 1000; OnPropertyChanged(nameof(BulkShiftMs));
        Recompute();
    }

    [RelayCommand]
    private async Task OptimizePhase()
    {
        if (Session is null) return;
        double p = 0;
        await Task.Run(() => p = Session.OptimizePhase());
        _phase = p; OnPropertyChanged(nameof(Phase));
        Recompute();
    }

    [RelayCommand]
    private async Task StretchSqueeze()
    {
        if (Session is null) return;
        await Task.Run(() => Session.AutoStretchSqueeze());
        Updated?.Invoke(this, EventArgs.Empty);
        var q = Session.Quality!;
        Quality = $"After stretch/squeeze ({Session.Parameters.Knots.Count} knots): r = {q.Correlation:0.000} ({q.Rating}).";
    }

    [RelayCommand]
    private void ClearStretch()
    {
        if (Session is null) return;
        Session.Parameters = Session.Parameters with { Knots = [] };
        Recompute();
    }

    [RelayCommand]
    private void ReExtract() { _extracted = null; _ = Rebuild(); }

    [RelayCommand]
    private void SaveTie()
    {
        if (Session is null || Seismic is null) return;
        Main.WellServices.SaveTie(Session, Seismic, ActivateTdr);
        Main.RefreshViewers();
    }

    [RelayCommand]
    private void SaveWavelet()
    {
        if (Session is null) return;
        var w = WaveletEntity.From(Session.Wavelet.RotatePhase(Phase), $"Tie {Well?.Name}");
        w.Name = Main.Dialogs.Prompt("Wavelet name:", "Save wavelet", $"{Session.Wavelet.Name} ({Well?.Name})") ?? w.Name;
        Main.Session.Add(w);
        ProjectWavelets.Add(w);
    }
}

/// <summary>Deterministic petrophysical evaluation of a well.</summary>
public sealed partial class PetrophysicsViewModel : DocumentViewModel
{
    public ObservableCollection<Well> Wells { get; } = [];
    [ObservableProperty] private Well? _well;
    [ObservableProperty] private double _grClean = 30;
    [ObservableProperty] private double _grShale = 120;
    [ObservableProperty] private VshaleMethod _vshMethod = VshaleMethod.Linear;
    [ObservableProperty] private PorosityMethod _phiMethod = PorosityMethod.NeutronDensity;
    [ObservableProperty] private SaturationMethod _swMethod = SaturationMethod.Archie;
    [ObservableProperty] private double _rhoMatrix = 2.65;
    [ObservableProperty] private double _rhoFluid = 1.0;
    [ObservableProperty] private double _rhoShale = 2.45;
    [ObservableProperty] private double _rw = 0.05;
    [ObservableProperty] private double _rsh = 2.0;
    [ObservableProperty] private double _a = 1;
    [ObservableProperty] private double _m = 2;
    [ObservableProperty] private double _n = 2;
    [ObservableProperty] private double _vshCut = 0.4;
    [ObservableProperty] private double _phiCut = 0.08;
    [ObservableProperty] private double _swCut = 0.6;
    [ObservableProperty] private double _top = double.NaN;
    [ObservableProperty] private double _base = double.NaN;
    [ObservableProperty] private string _summary = "";
    public VshaleMethod[] VshMethods { get; } = Enum.GetValues<VshaleMethod>();
    public PorosityMethod[] PhiMethods { get; } = Enum.GetValues<PorosityMethod>();
    public SaturationMethod[] SwMethods { get; } = Enum.GetValues<SaturationMethod>();
    public event EventHandler? Evaluated;

    public PetrophysicsViewModel(MainViewModel main) : base(main)
    {
        Title = "Petrophysics";
        foreach (var w in main.Session.Wells) Wells.Add(w);
        Well = main.SelectedWell();
    }

    partial void OnWellChanged(Well? value)
    {
        if (value?.FindCurve(CurveFamily.GammaRay) is { } gr)
        {
            var s = LogConditioning.Summarize(gr);
            GrClean = Math.Round(s.P10); GrShale = Math.Round(s.P90);
        }
        Evaluated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Evaluate()
    {
        if (Well is null) return;
        try
        {
            var p = new PetroParameters
            {
                GrClean = GrClean, GrShale = GrShale, VshMethod = VshMethod, PhiMethod = PhiMethod, SwMethod = SwMethod, RhoMatrix = RhoMatrix, RhoFluid = RhoFluid,
                RhoShale = RhoShale, Rw = Rw, Rsh = Rsh, A = A, M = M, N = N, VshCutoff = VshCut, PhiCutoff = PhiCut, SwCutoff = SwCut
            };
            var r = Main.WellServices.Evaluate(Well, p, double.IsNaN(Top) ? double.NegativeInfinity : Top, double.IsNaN(Base) ? double.PositiveInfinity : Base, true);
            var s = r.Summary;
            Summary = $"Interval {s.Top:0.0}-{s.Base:0.0}: gross {s.Gross:0.0}, net reservoir {s.NetReservoir:0.0}, net pay {s.NetPay:0.0} (N/G {s.NetToGross:0.00}).\n" +
                      $"Pay averages: PHIE {Fmt.N(s.AveragePhi, "0.000")}, SW {Fmt.N(s.AverageSw, "0.000")}; HC pore thickness {s.HydrocarbonPoreThickness:0.00}.\n" + string.Join("\n", r.Notes);
            Evaluated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Summary = e.Message; }
    }
}

/// <summary>Rock physics: Gassmann fluid substitution on logs and model templates.</summary>
public sealed partial class RockPhysicsViewModel : DocumentViewModel
{
    public ObservableCollection<Well> Wells { get; } = [];
    [ObservableProperty] private Well? _well;
    [ObservableProperty] private string _targetFluid = "Gas";
    [ObservableProperty] private double _targetSw = 0.2;
    [ObservableProperty] private double _temperature = 70;
    [ObservableProperty] private double _pressure = 25;
    [ObservableProperty] private double _salinity = 50000;
    [ObservableProperty] private double _clayFraction = 0.1;
    [ObservableProperty] private double _criticalPorosity = 0.4;
    [ObservableProperty] private double _coordination = 9;
    [ObservableProperty] private double _effPressure = 20;
    [ObservableProperty] private string _report = "";
    public string[] Fluids { get; } = ["Gas", "Oil", "Brine"];
    public event EventHandler? Updated;
    public (double[] Phi, double[] Vp)? WellPoints { get; private set; }

    public RockPhysicsViewModel(MainViewModel main) : base(main)
    {
        Title = "Rock Physics";
        foreach (var w in main.Session.Wells) Wells.Add(w);
        Well = main.SelectedWell();
    }

    partial void OnWellChanged(Well? value) => UpdateTemplates();

    public Constituent Mineral => RockPhysicsModels.MixMinerals([(Constituent.Quartz, 1 - ClayFraction), (Constituent.Clay, ClayFraction)]);

    /// <summary>Model curves Vp(φ) for brine-saturated rock: Wyllie, soft-sand, contact-cement, HS upper bound.</summary>
    public IReadOnlyList<(string Name, double[] Phi, double[] Vp, string Color)> Templates()
    {
        var min = Mineral; var brine = RockPhysicsModels.BatzleWangBrine(Temperature, Pressure, Salinity);
        var phis = Enumerable.Range(1, 39).Select(i => i * 0.01).Where(p => p < CriticalPorosity).ToArray();
        double[] Sat(Func<double, (double K, double Mu)> dry) => phis.Select(p =>
        {
            var (kd, md) = dry(p);
            double ks = RockPhysicsModels.GassmannSaturated(kd, min.BulkModulus, brine.BulkModulus, p);
            double rho = (1 - p) * min.Density + p * brine.Density;
            return ElasticProperties.FromModuli(ks, md, rho).Vp;
        }).ToArray();
        return
        [
            ("Wyllie time-average", phis, phis.Select(p => RockPhysicsModels.WyllieVelocity(p, ElasticProperties.FromModuli(min.BulkModulus, min.ShearModulus, min.Density).Vp, Math.Sqrt(brine.BulkModulus * 1e6 / brine.Density))).ToArray(), "#9AA6B8"),
            ("Soft sand (friable)", phis, Sat(p => RockPhysicsModels.SoftSand(p, EffPressure, CriticalPorosity, Coordination, min.BulkModulus, min.ShearModulus)), "#20B8A6"),
            ("Contact cement", phis, Sat(p => RockPhysicsModels.ContactCement(p, CriticalPorosity, Coordination, min.BulkModulus, min.ShearModulus, Constituent.Quartz.BulkModulus, Constituent.Quartz.ShearModulus)), "#FFB300"),
            ("Hashin-Shtrikman upper (brine)", phis, phis.Select(p =>
            {
                var hs = RockPhysicsModels.HashinShtrikman(min.BulkModulus, min.ShearModulus, brine.BulkModulus, 0, 1 - p);
                return ElasticProperties.FromModuli(hs.KHigh, hs.MuHigh, (1 - p) * min.Density + p * brine.Density).Vp;
            }).ToArray(), "#3D8BDB"),
        ];
    }

    private void UpdateTemplates()
    {
        WellPoints = null;
        if (Well?.FindCurve(CurveFamily.Sonic) is { } dt && (Well.FindCurve(CurveFamily.EffectivePorosity) ?? Well.FindCurve(CurveFamily.TotalPorosity)) is { } phi)
        {
            var d = dt.Depths;
            var vp = dt.Values.Select(v => Core.Units.UnitConverter.SlownessToVelocity(v, string.IsNullOrEmpty(dt.Unit) ? "us/ft" : dt.Unit)).ToArray();
            var ph = d.Select(phi.ValueAtDepth).ToArray();
            WellPoints = (ph, vp);
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand] private void Refresh() => UpdateTemplates();

    [RelayCommand]
    private void Substitute()
    {
        if (Well is null) return;
        var dt = Well.FindCurve(CurveFamily.Sonic); var dts = Well.FindCurve(CurveFamily.ShearSonic); var rhob = Well.FindCurve(CurveFamily.Density);
        var phi = Well.FindCurve(CurveFamily.EffectivePorosity) ?? Well.FindCurve(CurveFamily.TotalPorosity);
        var vsh = Well.FindCurve(CurveFamily.ShaleVolume);
        if (dt is null || dts is null || rhob is null || phi is null) { Report = "Requires DT, DTS, RHOB and a porosity curve (run Petrophysics first to compute PHIE)."; return; }
        var brine = RockPhysicsModels.BatzleWangBrine(Temperature, Pressure, Salinity);
        var hc = TargetFluid switch { "Gas" => Constituent.Gas, "Oil" => Constituent.Oil, _ => brine };
        var fluid2 = RockPhysicsModels.MixFluids(TargetFluid == "Brine" ? 1 : TargetSw, brine, hc);
        int n = dt.Count;
        var vpOut = new float[n]; var vsOut = new float[n]; var rhoOut = new float[n];
        int changed = 0;
        for (int i = 0; i < n; i++)
        {
            double md = dt.Depths[i];
            double vp = Core.Units.UnitConverter.SlownessToVelocity(dt.Values[i], string.IsNullOrEmpty(dt.Unit) ? "us/ft" : dt.Unit);
            double vs = Core.Units.UnitConverter.SlownessToVelocity(dts.ValueAtDepth(md), string.IsNullOrEmpty(dts.Unit) ? "us/ft" : dts.Unit);
            double rho = rhob.ValueAtDepth(md), p = phi.ValueAtDepth(md);
            double clay = vsh?.ValueAtDepth(md) ?? ClayFraction;
            if (double.IsNaN(vp) || double.IsNaN(vs) || double.IsNaN(rho) || double.IsNaN(p) || p < 0.03) { vpOut[i] = (float)vp; vsOut[i] = (float)vs; rhoOut[i] = (float)rho; continue; }
            var min = RockPhysicsModels.MixMinerals([(Constituent.Quartz, 1 - Math.Clamp(clay, 0, 1)), (Constituent.Clay, Math.Clamp(clay, 0, 1))]);
            var res = RockPhysicsModels.FluidSubstitution(new ElasticProperties(vp, vs, rho), p, min, brine, fluid2);
            vpOut[i] = (float)res.Vp; vsOut[i] = (float)res.Vs; rhoOut[i] = (float)res.Density;
            changed++;
        }
        string suffix = "_" + TargetFluid.ToUpperInvariant()[..1] + "FS";
        Main.WellServices.AddCurve(Well, dt.CloneAs("VP" + suffix, vpOut, "m/s", CurveFamily.PVelocity, $"Gassmann: in-situ assumed brine → {TargetFluid} Sw={TargetSw}"));
        Main.WellServices.AddCurve(Well, dt.CloneAs("VS" + suffix, vsOut, "m/s", CurveFamily.SVelocity, "Gassmann (shear modulus unchanged)"));
        Main.WellServices.AddCurve(Well, dt.CloneAs("RHOB" + suffix, rhoOut, "g/cc", CurveFamily.Other, "Gassmann density"));
        Report = $"Fluid substitution brine → {TargetFluid} (Sw {TargetSw}) applied on {changed} samples (φ ≥ 0.03). Brine (Batzle-Wang): K = {brine.BulkModulus:0.00} GPa, ρ = {brine.Density:0.000} g/cc. " +
                 "Assumptions: in-situ fluid is brine, isotropic homogeneous mineral (quartz/clay VRH from VSH), low-frequency Gassmann.";
    }
}
