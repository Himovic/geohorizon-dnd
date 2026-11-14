using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Application.Workflows;
using GeoVisionPro.Attributes;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.SeismicProcessing;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>A configured processing step in the sequence editor.</summary>
public sealed partial class StepViewModel : ObservableObject
{
    public string OperationId { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public bool IsAttribute { get; }
    [ObservableProperty] private bool _enabled = true;
    public ObservableCollection<ParameterItemViewModel> Parameters { get; }
    public StepViewModel(IProcessingOperation op, IDictionary<string, string>? values = null)
    {
        OperationId = op.Id; DisplayName = op.DisplayName; Category = op.Category;
        Parameters = ParameterItemViewModel.From(op.Parameters, values);
        Description = op.Description;
    }
    public StepViewModel(ISeismicAttribute a, IDictionary<string, string>? values = null)
    {
        OperationId = a.Id; DisplayName = a.DisplayName; Category = a.Category; IsAttribute = true;
        Parameters = ParameterItemViewModel.From(a.Parameters, values);
        Description = a.Description;
    }
    public string Description { get; }
    public ParameterSet ToSet() => ParameterItemViewModel.ToSet(Parameters);
    public ProcessingStep ToStep() => new(OperationId, ToSet());
}

/// <summary>Processing sequence editor with live before/after preview, spectra and batch execution with provenance.</summary>
public sealed partial class ProcessingViewModel : DocumentViewModel
{
    public ObservableCollection<SeismicDataset> Inputs { get; } = [];
    public ObservableCollection<IProcessingOperation> Operations { get; } = [];
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    [ObservableProperty] private SeismicDataset? _input;
    [ObservableProperty] private IProcessingOperation? _selectedOperation;
    [ObservableProperty] private StepViewModel? _selectedStep;
    [ObservableProperty] private int _previewPosition;
    [ObservableProperty] private int _previewMin;
    [ObservableProperty] private int _previewMax;
    [ObservableProperty] private string _outputName = "Processed";
    [ObservableProperty] private string _previewInfo = "";
    public Array2D? Before { get; private set; }
    public Array2D? After { get; private set; }
    public double SampleInterval { get; private set; } = 0.004;
    public double StartTime { get; private set; }
    public event EventHandler? PreviewUpdated;
    public bool IsPreStack => Input?.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers;

    public ProcessingViewModel(MainViewModel main) : base(main)
    {
        Title = "Seismic Processing";
        foreach (var s in main.Session.Seismic) Inputs.Add(s);
        Input = main.SelectedItem as SeismicDataset ?? main.SelectedSeismic();
    }

    partial void OnInputChanged(SeismicDataset? value)
    {
        Operations.Clear();
        if (value is null) return;
        bool pre = IsPreStack;
        foreach (var op in ProcessingCatalog.All.Where(o => pre || o.Scope != OperationScope.Gather)) Operations.Add(op);
        var g = value.Geometry;
        PreviewMin = g.Inlines.First; PreviewMax = g.Inlines.Last;
        _previewPosition = g.Inlines.ValueAt(g.InlineCount / 2); OnPropertyChanged(nameof(PreviewPosition));
        if (pre) foreach (var st in Steps.ToList()) { }
        OutputName = $"{value.Name} processed";
        _ = UpdatePreview();
    }

    partial void OnPreviewPositionChanged(int value) => _ = UpdatePreview();

    [RelayCommand]
    private void AddStep()
    {
        if (SelectedOperation is null) return;
        var s = new StepViewModel(SelectedOperation);
        foreach (var p in s.Parameters) p.PropertyChanged += (_, _) => _ = UpdatePreview();
        s.PropertyChanged += (_, _) => _ = UpdatePreview();
        Steps.Add(s);
        SelectedStep = s;
        _ = UpdatePreview();
    }

    [RelayCommand] private void RemoveStep() { if (SelectedStep is not null) { Steps.Remove(SelectedStep); _ = UpdatePreview(); } }
    [RelayCommand] private void MoveUp() { if (SelectedStep is { } s && Steps.IndexOf(s) > 0) { Steps.Move(Steps.IndexOf(s), Steps.IndexOf(s) - 1); _ = UpdatePreview(); } }
    [RelayCommand] private void MoveDown() { if (SelectedStep is { } s && Steps.IndexOf(s) < Steps.Count - 1) { Steps.Move(Steps.IndexOf(s), Steps.IndexOf(s) + 1); _ = UpdatePreview(); } }

    private List<ProcessingStep> ActiveSteps() => Steps.Where(s => s.Enabled).Select(s => s.ToStep()).ToList();

    public async Task UpdatePreview()
    {
        if (Input is null) return;
        try
        {
            var steps = ActiveSteps();
            var v = ProcessingEngine.Validate(steps, IsPreStack);
            if (!v.IsValid) { PreviewInfo = v.ToString(); return; }
            var ds = Input; int pos = PreviewPosition;
            var (before, after, g) = await Task.Run(() =>
            {
                if (IsPreStack)
                {
                    var gs = Main.Session.GetGathers(ds);
                    var gg = gs.Geometry;
                    int xl = gg.Crosslines.ValueAt(gg.CrosslineCount / 2);
                    var b = gs.ReadGather(pos, xl) ?? new Array2D(gs.Offsets.Length, gg.SampleCount);
                    var a = ProcessingEngine.ApplyToSection(b, steps, new ProcessingContext(gg.SampleInterval, gg.StartTime, 25, gs.Offsets, pos));
                    return (b, a, gg);
                }
                var vol = Main.Session.GetVolume(ds);
                var g2 = vol.Geometry;
                var sec = vol.ReadInline(pos);
                double dx = g2.Transform.CrosslineSpacing > 0 ? g2.Transform.CrosslineSpacing : 25;
                return (sec, ProcessingEngine.ApplyToSection(sec, steps, new ProcessingContext(g2.SampleInterval, g2.StartTime, dx, null, pos)), g2);
            });
            Before = before; After = after; SampleInterval = g.SampleInterval; StartTime = g.StartTime;
            PreviewInfo = $"{(IsPreStack ? "Gather at inline" : "Inline")} {pos}: RMS {Dsp.Rms(before.Data):G4} → {Dsp.Rms(after.Data):G4}; {steps.Count} step(s).";
            PreviewUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { PreviewInfo = e.Message; }
    }

    [RelayCommand]
    private async Task Run()
    {
        if (Input is null || Steps.Count == 0) return;
        var steps = ActiveSteps();
        var input = Input; var name = OutputName;
        SeismicDataset? result = null;
        await RunAsync($"Processing {input.Name}", async (p, ct) =>
            result = IsPreStack ? await Main.Processing.RunGatherSequenceAsync(input, steps, name, p, ct) : await Main.Processing.RunSequenceAsync(input, steps, name, p, ct));
        if (result is not null) Main.Open(new SeismicViewerViewModel(Main, result, IsPreStack ? SectionKind.Gather : SectionKind.Inline));
    }

    [RelayCommand]
    private void SaveAsWorkflow()
    {
        var name = Main.Dialogs.Prompt("Workflow name:", "Save workflow", "Processing workflow");
        if (string.IsNullOrWhiteSpace(name)) return;
        var wf = new WorkflowDefinition { Name = name, Steps = Steps.Select(s => new WorkflowStep { Kind = WorkflowStepKind.Processing, OperationId = s.OperationId, Parameters = s.ToSet(), Enabled = s.Enabled }).ToList() };
        Main.Workflows.Save(wf);
    }

    public override void OnProjectChanged()
    {
        var sel = Input;
        Inputs.Clear();
        foreach (var s in Main.Session.Seismic) Inputs.Add(s);
        if (sel is not null && Inputs.Contains(sel)) _input = sel; OnPropertyChanged(nameof(Input));
    }
}

/// <summary>Workflow manager: build, save, load, export/import and execute reusable processing/attribute workflows.</summary>
public sealed partial class WorkflowViewModel : DocumentViewModel
{
    public ObservableCollection<StepViewModel> Steps { get; } = [];
    public ObservableCollection<WorkflowDefinitionEntity> Saved { get; } = [];
    public ObservableCollection<SeismicDataset> Inputs { get; } = [];
    public IReadOnlyList<object> Catalog { get; }
    [ObservableProperty] private object? _selectedCatalogItem;
    [ObservableProperty] private StepViewModel? _selectedStep;
    [ObservableProperty] private string _name = "New workflow";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private SeismicDataset? _input;
    [ObservableProperty] private WorkflowDefinitionEntity? _selectedSaved;
    [ObservableProperty] private string _validation = "";
    private Guid? _entityId;

    public WorkflowViewModel(MainViewModel main, WorkflowDefinitionEntity? entity = null) : base(main)
    {
        Title = "Workflow Manager";
        Catalog = ProcessingCatalog.All.Cast<object>().Concat(AttributeCatalog.All).ToList();
        foreach (var s in main.Session.Workflows) Saved.Add(s);
        foreach (var s in main.Session.Seismic) Inputs.Add(s);
        Input = main.SelectedSeismic();
        if (entity is not null) Load(entity);
    }

    public string CatalogName(object o) => o switch { IProcessingOperation p => $"[{p.Category}] {p.DisplayName}", ISeismicAttribute a => $"[Attribute] {a.DisplayName}", _ => "" };

    [RelayCommand]
    private void Add()
    {
        StepViewModel? s = SelectedCatalogItem switch { IProcessingOperation p => new StepViewModel(p), ISeismicAttribute a => new StepViewModel(a), _ => null };
        if (s is null) return;
        Steps.Add(s); SelectedStep = s; Validate();
    }

    [RelayCommand] private void Remove() { if (SelectedStep is not null) { Steps.Remove(SelectedStep); Validate(); } }
    [RelayCommand] private void Up() { if (SelectedStep is { } s && Steps.IndexOf(s) > 0) Steps.Move(Steps.IndexOf(s), Steps.IndexOf(s) - 1); }
    [RelayCommand] private void Down() { if (SelectedStep is { } s && Steps.IndexOf(s) < Steps.Count - 1) Steps.Move(Steps.IndexOf(s), Steps.IndexOf(s) + 1); }

    private WorkflowDefinition Build() => new()
    {
        Name = Name, Description = Description,
        Steps = Steps.Select(s => new WorkflowStep { Kind = s.IsAttribute ? WorkflowStepKind.Attribute : WorkflowStepKind.Processing, OperationId = s.OperationId, Parameters = s.ToSet(), Enabled = s.Enabled }).ToList()
    };

    private void Load(WorkflowDefinitionEntity e)
    {
        var wf = WorkflowDefinition.FromJson(e.Json);
        _entityId = e.Id; Name = wf.Name; Description = wf.Description;
        Steps.Clear();
        foreach (var s in wf.Steps)
        {
            try
            {
                var vm = s.Kind == WorkflowStepKind.Processing ? new StepViewModel(ProcessingCatalog.Get(s.OperationId), s.Parameters) : new StepViewModel(AttributeCatalog.Get(s.OperationId), s.Parameters);
                vm.Enabled = s.Enabled;
                Steps.Add(vm);
            }
            catch (KeyNotFoundException ex) { Main.Log.Warn("Workflow", ex.Message); }
        }
        Validate();
    }

    partial void OnSelectedSavedChanged(WorkflowDefinitionEntity? value) { if (value is not null) Load(value); }
    partial void OnInputChanged(SeismicDataset? value) => Validate();

    private void Validate()
    {
        var r = Build().Validate(Input?.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers);
        Validation = r.IsValid ? "Workflow is valid." : r.ToString();
    }

    [RelayCommand] private void Save() { var e = Main.Workflows.Save(Build(), _entityId); _entityId = e.Id; if (!Saved.Contains(e)) Saved.Add(e); Main.Log.Success("Workflow", $"'{Name}' saved to the project."); }

    [RelayCommand]
    private void ExportJson()
    {
        var p = Main.Dialogs.SaveFile("Export workflow", "Workflow JSON (*.gvwf.json)|*.gvwf.json|JSON (*.json)|*.json", Name + ".gvwf.json");
        if (p is not null) File.WriteAllText(p, Build().ToJson());
    }

    [RelayCommand]
    private void ImportJson()
    {
        var p = Main.Dialogs.OpenFile("Import workflow", "JSON (*.json)|*.json");
        if (p is null) return;
        Main.Safe(() => Load(new WorkflowDefinitionEntity { Id = Guid.NewGuid(), Name = Path.GetFileNameWithoutExtension(p), Json = File.ReadAllText(p) }));
        _entityId = null;
    }

    [RelayCommand]
    private async Task Execute()
    {
        if (Input is null) return;
        var wf = Build(); var input = Input;
        IReadOnlyList<SeismicDataset>? outs = null;
        await RunAsync($"Workflow {wf.Name}", async (p, ct) => outs = await Main.Workflows.ExecuteAsync(wf, input, p, ct));
        if (outs is { Count: > 0 }) Main.Open(new SeismicViewerViewModel(Main, outs[^1], outs[^1].Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers ? SectionKind.Gather : SectionKind.Inline));
    }
}

/// <summary>Interactive semblance velocity analysis on CMP gathers with picking, NMO preview and NMO-stack.</summary>
public sealed partial class VelocityAnalysisViewModel : DocumentViewModel
{
    public ObservableCollection<SeismicDataset> GatherSets { get; } = [];
    [ObservableProperty] private SeismicDataset? _gathers;
    [ObservableProperty] private int _inline;
    [ObservableProperty] private int _crossline;
    [ObservableProperty] private double _vMin = 1400;
    [ObservableProperty] private double _vMax = 4500;
    [ObservableProperty] private double _vStep = 50;
    [ObservableProperty] private string _velocityFunction = "";
    [ObservableProperty] private double _stretchMute = 50;
    [ObservableProperty] private string _info = "";
    public Array2D? Gather { get; private set; }
    public Array2D? Corrected { get; private set; }
    public Array2D? Semblance { get; private set; }
    public double[] Velocities { get; private set; } = [];
    public double[] Offsets { get; private set; } = [];
    public SurveyGeometry? Geometry { get; private set; }
    public List<(double Time, double Velocity)> Picks { get; } = [];
    public event EventHandler? Updated;

    public VelocityAnalysisViewModel(MainViewModel main) : base(main)
    {
        Title = "Velocity Analysis";
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PreStackGathers)) GatherSets.Add(s);
        Gathers = GatherSets.FirstOrDefault();
    }

    partial void OnGathersChanged(SeismicDataset? value)
    {
        if (value is null) return;
        var g = value.Geometry;
        _inline = g.Inlines.ValueAt(g.InlineCount / 2); _crossline = g.Crosslines.ValueAt(g.CrosslineCount / 2);
        OnPropertyChanged(nameof(Inline)); OnPropertyChanged(nameof(Crossline));
        _ = Compute();
    }

    [RelayCommand]
    private async Task Compute()
    {
        if (Gathers is null) return;
        var gs = Main.Session.GetGathers(Gathers);
        Geometry = gs.Geometry; Offsets = gs.Offsets;
        var gather = gs.ReadGather(Inline, Crossline);
        if (gather is null) { Info = $"No gather at {Inline}/{Crossline}."; return; }
        Velocities = Enumerable.Range(0, (int)((VMax - VMin) / VStep) + 1).Select(i => VMin + i * VStep).ToArray();
        var g = Geometry;
        Gather = gather;
        Semblance = await Task.Run(() => Nmo.Semblance(gather, Offsets, Velocities, g.SampleInterval, g.StartTime, 7));
        ApplyNmo();
    }

    public void AddPick(double time, double velocity)
    {
        Picks.RemoveAll(p => Math.Abs(p.Time - time) < 0.02);
        Picks.Add((time, velocity));
        Picks.Sort((a, b) => a.Time.CompareTo(b.Time));
        VelocityFunction = string.Join(", ", Picks.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Time:0.###}:{p.Velocity:0}")));
        ApplyNmo();
    }

    [RelayCommand]
    private void AutoPick()
    {
        if (Semblance is null || Geometry is null) return;
        var vf = Nmo.AutoPick(Semblance, Velocities, Geometry.SampleInterval, Geometry.StartTime, 25, 0.15);
        Picks.Clear();
        Picks.AddRange(vf.Times.Zip(vf.Velocities));
        VelocityFunction = vf.ToString();
        ApplyNmo();
    }

    [RelayCommand] private void ClearPicks() { Picks.Clear(); VelocityFunction = ""; ApplyNmo(); }

    private void ApplyNmo()
    {
        if (Gather is null || Geometry is null) return;
        Corrected = Gather.Clone();
        if (!string.IsNullOrWhiteSpace(VelocityFunction))
        {
            try { Nmo.Correct(Corrected, Offsets, SeismicProcessing.VelocityFunction.Parse(VelocityFunction), Geometry.SampleInterval, Geometry.StartTime, StretchMute / 100); Info = $"{Picks.Count} picks."; }
            catch (FormatException e) { Info = e.Message; }
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task Stack()
    {
        if (Gathers is null || string.IsNullOrWhiteSpace(VelocityFunction)) { Main.Dialogs.Info("Pick a velocity function first."); return; }
        var ds = Gathers; var vf = VelocityFunction; double sm = StretchMute / 100;
        SeismicDataset? res = null;
        await RunAsync("NMO + stack", async (p, ct) => res = await Main.Processing.NmoStackAsync(ds, vf, sm, null, $"{ds.Name} stack", p, ct));
        if (res is not null) Main.Open(new SeismicViewerViewModel(Main, res, SectionKind.Inline));
    }
}

/// <summary>Attribute computation: parameter configuration, inline preview, volume generation and horizon maps.</summary>
public sealed partial class AttributeViewModel : DocumentViewModel
{
    public ObservableCollection<SeismicDataset> Inputs { get; } = [];
    public IReadOnlyList<ISeismicAttribute> Attributes { get; } = AttributeCatalog.All;
    public ObservableCollection<Horizon> Horizons { get; } = [];
    [ObservableProperty] private SeismicDataset? _input;
    [ObservableProperty] private ISeismicAttribute? _attribute;
    [ObservableProperty] private ObservableCollection<ParameterItemViewModel> _parameters = [];
    [ObservableProperty] private int _previewInline;
    [ObservableProperty] private int _minInline;
    [ObservableProperty] private int _maxInline;
    [ObservableProperty] private Horizon? _horizon;
    [ObservableProperty] private HorizonWindowMode _windowMode = HorizonWindowMode.WindowAroundHorizon;
    [ObservableProperty] private WindowStatistic _statistic = WindowStatistic.Rms;
    [ObservableProperty] private double _aboveMs = 20;
    [ObservableProperty] private double _belowMs = 20;
    [ObservableProperty] private Horizon? _baseHorizon;
    [ObservableProperty] private string _info = "";
    public HorizonWindowMode[] WindowModes { get; } = Enum.GetValues<HorizonWindowMode>();
    public WindowStatistic[] Statistics { get; } = Enum.GetValues<WindowStatistic>();
    public Array2D? Preview { get; private set; }
    public SurveyGeometry? Geometry => Input?.Geometry;
    public event EventHandler? PreviewUpdated;

    public AttributeViewModel(MainViewModel main) : base(main)
    {
        Title = "Seismic Attributes";
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D)) Inputs.Add(s);
        foreach (var h in main.Session.Horizons) Horizons.Add(h);
        Horizon = Horizons.FirstOrDefault();
        Input = main.SelectedSeismic();
        Attribute = Attributes[0];
    }

    partial void OnInputChanged(SeismicDataset? value)
    {
        if (value is null) return;
        var g = value.Geometry;
        MinInline = g.Inlines.First; MaxInline = g.Inlines.Last;
        _previewInline = g.Inlines.ValueAt(g.InlineCount / 2); OnPropertyChanged(nameof(PreviewInline));
        _ = UpdatePreview();
    }

    partial void OnAttributeChanged(ISeismicAttribute? value)
    {
        Parameters = value is null ? [] : ParameterItemViewModel.From(value.Parameters);
        foreach (var p in Parameters) p.PropertyChanged += (_, _) => _ = UpdatePreview();
        _ = UpdatePreview();
    }

    partial void OnPreviewInlineChanged(int value) => _ = UpdatePreview();

    private async Task UpdatePreview()
    {
        if (Input is null || Attribute is null) return;
        try
        {
            var vol = Main.Session.GetVolume(Input);
            var p = ParameterItemViewModel.ToSet(Parameters);
            var v = p.Validate(Attribute.Parameters);
            if (!v.IsValid) { Info = v.ToString(); return; }
            var a = Attribute; int il = PreviewInline;
            if (!vol.Geometry.Inlines.Contains(il)) return;
            Preview = await Task.Run(() => AttributeEngine.ComputeInline(vol, il, a, p));
            Info = $"{a.DisplayName} ({a.Unit}): {a.Description}";
            PreviewUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Info = e.Message; }
    }

    [RelayCommand]
    private async Task ComputeVolume()
    {
        if (Input is null || Attribute is null) return;
        var input = Input; var a = Attribute; var p = ParameterItemViewModel.ToSet(Parameters);
        SeismicDataset? res = null;
        await RunAsync($"Attribute {a.DisplayName}", async (pr, ct) => res = await Main.Processing.ComputeAttributeAsync(input, a, p, $"{input.Name} [{a.DisplayName}]", pr, ct));
        if (res is not null) Main.Open(new SeismicViewerViewModel(Main, res, SectionKind.Inline));
    }

    [RelayCommand]
    private async Task ExtractMap()
    {
        if (Input is null || Horizon is null) return;
        var vol = Main.Session.GetVolume(Input); var h = Horizon; var mode = WindowMode; var stat = Statistic;
        double above = AboveMs / 1000, below = BelowMs / 1000; var bh = BaseHorizon;
        Grid2D? grid = null;
        await RunAsync($"Horizon map {h.Name}", (pr, ct) => Task.Run(() => grid = HorizonAttributeExtraction.Extract(vol, h, mode, stat, above, below, bh, pr, ct), ct));
        if (grid is null) return;
        var map = Main.Open(new MapViewModel(Main));
        map.ShowGrid(h, grid, $"{stat} of {Input.Name} ({mode})");
    }
}
