using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>Hierarchical project explorer (Surveys, Wells, Horizons, Faults, Interpretation, Processing, Attributes, Inversion, ...).</summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<ExplorerNode> Roots { get; } = [];
    [ObservableProperty] private ExplorerNode? _selectedNode;
    [ObservableProperty] private string _filter = "";

    public ExplorerViewModel(MainViewModel main) => _main = main;

    partial void OnSelectedNodeChanged(ExplorerNode? value) => _main.SelectedItem = value?.Item;
    partial void OnFilterChanged(string value) => Rebuild();

    private bool Match(string name) => string.IsNullOrWhiteSpace(Filter) || name.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    public void Rebuild()
    {
        Roots.Clear();
        var s = _main.Session;
        if (!s.IsOpen) return;
        var root = new ExplorerNode(s.Project.Name, "", s.Project) { IsExpanded = true };
        Roots.Add(root);
        var surveys = Folder(root, "Surveys", "");
        var vol = Folder(surveys, "Seismic Volumes", "");
        var lines = Folder(surveys, "Seismic Lines (2D)", "");
        var gathers = Folder(surveys, "Gathers", "");
        var processing = Folder(root, "Processing", "");
        var attributes = Folder(root, "Attributes", "");
        var inversion = Folder(root, "Inversion", "");
        foreach (var d in s.Seismic.Where(d => Match(d.Name)))
        {
            var parent = d.Kind switch
            {
                SeismicKind.PreStackGathers or SeismicKind.AngleGathers => gathers,
                SeismicKind.PostStack2D => lines,
                _ => d.Category switch { SeismicCategory.Processed => processing, SeismicCategory.Attribute => attributes, SeismicCategory.Inversion or SeismicCategory.Model => inversion, _ => vol }
            };
            var n = new ExplorerNode(d.Name, d.ValidationStatus == DataValidationStatus.Invalid ? "" : "", d)
            {
                Detail = $"{d.Kind}, {d.InlineFirst}-{d.InlineLast} × {d.CrosslineFirst}-{d.CrosslineLast}, {d.SampleCount} samples",
                DefaultCommand = new RelayCommand(() => OpenDefault(d))
            };
            if (d.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers)
                n.Actions.Add(new MenuAction("Open gathers", new RelayCommand(() => _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.Gather)))));
            else
            {
                n.Actions.Add(new MenuAction("Open inline", new RelayCommand(() => _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.Inline)))));
                n.Actions.Add(new MenuAction("Open crossline", new RelayCommand(() => _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.Crossline)))));
                n.Actions.Add(new MenuAction("Open time slice", new RelayCommand(() => _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.TimeSlice)))));
                n.Actions.Add(new MenuAction("Volume rendering (VTK)", new RelayCommand(() => _main.Open(new VtkVolumeViewModel(_main, d)))));
            }
            n.Actions.Add(new MenuAction("Export SEG-Y...", _main.ExportSelectedCommand));
            n.Actions.Add(new MenuAction("Delete", new RelayCommand(() => Delete(d))));
            parent.Children.Add(n);
        }
        var wells = Folder(root, "Wells", "");
        foreach (var w in s.Wells.Where(w => Match(w.Name)))
        {
            var wn = new ExplorerNode(w.Name, "", w) { Detail = $"{w.Curves.Count} curves, {w.Tops.Count} tops", DefaultCommand = new RelayCommand(() => OpenDefault(w)) };
            wn.Actions.Add(new MenuAction("Show logs", new RelayCommand(() => OpenDefault(w))));
            wn.Actions.Add(new MenuAction("Well tie", new RelayCommand(() => _main.Open(new WellTieViewModel(_main, w)))));
            wn.Actions.Add(new MenuAction("Export LAS...", _main.ExportSelectedCommand));
            wn.Actions.Add(new MenuAction("Delete", new RelayCommand(() => Delete(w))));
            var logs = new ExplorerNode("Logs", "");
            foreach (var c in w.Curves) logs.Children.Add(new ExplorerNode($"{c.Mnemonic} [{c.Unit}]{(c.IsComputed ? " (computed)" : "")}", "", c) { Detail = c.Source });
            var tops = new ExplorerNode("Tops", "");
            foreach (var t in w.Tops) tops.Children.Add(new ExplorerNode($"{t.Name} @ {t.MeasuredDepth:0.0}", "", t));
            var traj = new ExplorerNode(w.Deviation.Count > 0 ? $"Trajectory ({w.Deviation.Count} stations)" : "Trajectory (vertical)", "");
            var cs = new ExplorerNode("Checkshots / TDR", "");
            foreach (var c in w.Checkshots) cs.Children.Add(new ExplorerNode($"{c.Name}{(c.Id == w.ActiveTimeDepthId ? " (active)" : "")}", "", c) { Detail = $"{c.Source}, {c.Points.Count} points" });
            wn.Children.Add(logs); wn.Children.Add(tops); wn.Children.Add(traj); wn.Children.Add(cs);
            wells.Children.Add(wn);
        }
        var horizons = Folder(root, "Horizons", "");
        foreach (var h in s.Horizons.Where(h => Match(h.Name)))
        {
            var hn = new ExplorerNode(h.Name, "", h) { Detail = $"{h.Values.Count(v => !double.IsNaN(v)):N0} picks", DefaultCommand = new RelayCommand(() => OpenDefault(h)) };
            hn.Actions.Add(new MenuAction("Set as active horizon", new RelayCommand(() => _main.InterpretationControls.ActiveHorizon = h)));
            hn.Actions.Add(new MenuAction("Show on map", new RelayCommand(() => OpenDefault(h))));
            hn.Actions.Add(new MenuAction("Export...", _main.ExportSelectedCommand));
            hn.Actions.Add(new MenuAction("Delete", new RelayCommand(() => Delete(h))));
            horizons.Children.Add(hn);
        }
        var faults = Folder(root, "Faults", "");
        foreach (var f in s.Faults.Where(f => Match(f.Name)))
        {
            var fn = new ExplorerNode(f.Name, "", f) { Detail = $"{f.Sticks.Count} sticks", DefaultCommand = new RelayCommand(() => _main.Open(new Scene3DViewModel(_main))) };
            fn.Actions.Add(new MenuAction("Set as active fault", new RelayCommand(() => _main.InterpretationControls.ActiveFault = f)));
            fn.Actions.Add(new MenuAction("Delete", new RelayCommand(() => Delete(f))));
            faults.Children.Add(fn);
        }
        var interp = Folder(root, "Interpretation", "");
        foreach (var w in s.Wavelets) interp.Children.Add(new ExplorerNode($"Wavelet: {w.Name}", "", w) { Detail = w.Source });
        foreach (var t in s.WellTies) interp.Children.Add(new ExplorerNode($"Tie: {s.FindWell(t.WellId)?.Name} r={t.Correlation:0.00}", "", t));
        var cross = Folder(root, "Crossplots", "");
        foreach (var c in s.Crossplots) cross.Children.Add(new ExplorerNode(c.Name, "", c));
        var models = Folder(root, "Geological Models", "");
        foreach (var m in s.Models) models.Children.Add(new ExplorerNode(m.Name, "", m) { Detail = m.Description });
        var wf = Folder(root, "Workflows", "");
        foreach (var w in s.Workflows) wf.Children.Add(new ExplorerNode(w.Name, "", w) { DefaultCommand = new RelayCommand(() => _main.Open(new WorkflowViewModel(_main, w))) });
        var reports = Folder(root, "Reports", "");
        foreach (var r in s.Reports) reports.Children.Add(new ExplorerNode(r.Name, "", r) { DefaultCommand = new RelayCommand(() => { if (System.IO.File.Exists(r.Path)) Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true }); }) });
        var hist = Folder(root, $"History ({s.History.Count})", "");
        hist.Children.Clear();
        _ = hist;
    }

    private static ExplorerNode Folder(ExplorerNode parent, string name, string glyph)
    {
        var n = new ExplorerNode(name, glyph) { IsExpanded = true };
        parent.Children.Add(n);
        return n;
    }

    public void OpenDefault(object item)
    {
        switch (item)
        {
            case SeismicDataset d when d.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers: _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.Gather)); break;
            case SeismicDataset d: _main.Open(new SeismicViewerViewModel(_main, d, SectionKind.Inline)); break;
            case Well w: _main.Open(new WellLogViewModel(_main, w)); break;
            case Horizon h: var m = _main.Open(new MapViewModel(_main)); m.SelectedHorizon = h; break;
        }
    }

    [RelayCommand]
    private void Activate(ExplorerNode? n)
    {
        if (n?.DefaultCommand is { } c && c.CanExecute(null)) c.Execute(null);
    }

    private void Delete(object item)
    {
        if (!_main.Dialogs.Confirm($"Delete '{item}' from the project? This cannot be undone.")) return;
        _main.Session.Remove(item);
    }
}

/// <summary>Reflection-based property grid for the selected object (editable primitive properties).</summary>
public sealed partial class PropertiesViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<PropertyRow> Rows { get; } = [];
    [ObservableProperty] private string _header = "No selection";
    public PropertiesViewModel(MainViewModel main) => _main = main;

    private static readonly HashSet<string> Hidden = ["Values", "Depths", "Sources", "Samples", "Curves", "Tops", "Checkshots", "Deviation", "Sticks", "Points", "Geometry", "Json", "ImportSettingsJson", "ParametersJson"];

    public void Show(object? item)
    {
        Rows.Clear();
        if (item is null) { Header = "No selection"; return; }
        Header = $"{item.GetType().Name}: {item}";
        foreach (var p in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name == "Name" ? 0 : 1))
        {
            if (Hidden.Contains(p.Name) || p.GetIndexParameters().Length > 0) continue;
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(decimal))) continue;
            object? v;
            try { v = p.GetValue(item); } catch (TargetInvocationException) { continue; }
            string s = v switch { double d => d.ToString("G10", CultureInfo.InvariantCulture), float f => f.ToString("G7", CultureInfo.InvariantCulture), null => "", _ => v.ToString() ?? "" };
            bool editable = p.CanWrite && p.SetMethod!.IsPublic && t != typeof(Guid) && t != typeof(DateTime) && p.Name != "Id";
            Rows.Add(new PropertyRow(p.DeclaringType?.Name ?? "", p.Name, s, editable ? val => Set(item, p, t, val) : null));
        }
        if (item is SeismicDataset ds && ds.ValidationMessages is { Length: > 0 } vm) Rows.Add(new PropertyRow("QC", "Validation", vm.Replace(Environment.NewLine, " | "), null));
        if (item is LogCurve c)
        {
            var st = WellLog.LogConditioning.Summarize(c);
            Rows.Add(new PropertyRow("QC", "Statistics", st.ToString(), null));
        }
    }

    private void Set(object item, PropertyInfo p, Type t, string text)
    {
        try
        {
            object? value = t.IsEnum ? Enum.Parse(t, text, true) : t == typeof(string) ? text : Convert.ChangeType(text, t, CultureInfo.InvariantCulture);
            p.SetValue(item, value);
            _main.Session.MarkDirty(item);
            if (p.Name is "Name" or "Color") _main.Explorer.Rebuild();
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            _main.Log.Warn("Properties", $"Invalid value for {p.Name}: '{text}'");
        }
    }
}

public sealed partial class MessagesViewModel : ObservableObject
{
    public ObservableCollection<LogMessage> Items { get; } = [];
    [ObservableProperty] private bool _showInfo = true;
    public MessagesViewModel(MainViewModel main)
    {
        foreach (var m in main.Log.Messages) Items.Add(m);
        main.Log.MessageLogged += (_, m) => MainViewModel.OnUi(() =>
        {
            Items.Add(m);
            if (Items.Count > 5000) Items.RemoveAt(0);
            main.StatusText = m.Text;
        });
    }
    [RelayCommand] private void Clear() => Items.Clear();
    public IEnumerable<LogMessage> Problems => Items.Where(i => i.Level is MessageLevel.Warning or MessageLevel.Error);
}

public sealed partial class TaskItemViewModel : ObservableObject
{
    public BackgroundTask Task { get; }
    public TaskItemViewModel(BackgroundTask t) { Task = t; }
    public string Name => Task.Name;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _elapsed = "";
    public void Update() { Progress = Task.Progress * 100; Message = Task.Message; Status = Task.Status.ToString(); Elapsed = $"{Task.Elapsed.TotalSeconds:0.0} s"; }
    [RelayCommand] private void Cancel() => Task.Cancel();
}

public sealed partial class TasksViewModel : ObservableObject
{
    public ObservableCollection<TaskItemViewModel> Items { get; } = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "No background tasks";
    private readonly MainViewModel _main;
    public TasksViewModel(MainViewModel main)
    {
        _main = main;
        main.Tasks.TaskChanged += (_, t) => MainViewModel.OnUi(() =>
        {
            var vm = Items.FirstOrDefault(i => i.Task.Id == t.Id);
            if (vm is null) { vm = new TaskItemViewModel(t); Items.Insert(0, vm); }
            vm.Update();
            IsBusy = main.Tasks.IsBusy;
            var running = Items.FirstOrDefault(i => i.Task.Status == BackgroundTaskStatus.Running);
            Summary = running is null ? "Idle" : $"{running.Name}: {running.Message} ({running.Progress:0}%)";
        });
    }
    [RelayCommand] private void ClearFinished() { _main.Tasks.ClearFinished(); foreach (var i in Items.Where(i => i.Task.Status != BackgroundTaskStatus.Running).ToList()) Items.Remove(i); }
}

/// <summary>Active interpretation objects and picking parameters shared by all seismic viewers.</summary>
public sealed partial class InterpretationControlsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<Horizon> Horizons { get; } = [];
    public ObservableCollection<Fault> Faults { get; } = [];
    [ObservableProperty] private Horizon? _activeHorizon;
    [ObservableProperty] private Fault? _activeFault;
    [ObservableProperty] private EventType _snapEvent = EventType.Peak;
    [ObservableProperty] private int _snapWindow = 4;
    [ObservableProperty] private double _minCorrelation = 0.75;
    [ObservableProperty] private double _maxJump = 3;
    [ObservableProperty] private bool _overwrite;
    public EventType[] EventTypes { get; } = Enum.GetValues<EventType>();

    public InterpretationControlsViewModel(MainViewModel main) => _main = main;

    public TrackingParameters Tracking => new() { Event = SnapEvent, SearchWindow = SnapWindow, MinCorrelation = MinCorrelation, MaxJump = MaxJump, Overwrite = Overwrite };

    public void Refresh()
    {
        var h = ActiveHorizon; var f = ActiveFault;
        Horizons.Clear(); Faults.Clear();
        if (!_main.Session.IsOpen) { ActiveHorizon = null; ActiveFault = null; return; }
        foreach (var x in _main.Session.Horizons) Horizons.Add(x);
        foreach (var x in _main.Session.Faults) Faults.Add(x);
        ActiveHorizon = h is not null && Horizons.Contains(h) ? h : Horizons.FirstOrDefault();
        ActiveFault = f is not null && Faults.Contains(f) ? f : Faults.FirstOrDefault();
    }

    [RelayCommand]
    private async Task Interpolate(string method)
    {
        if (ActiveHorizon is not { } h) return;
        var m = Enum.Parse<GriddingMethod>(method);
        GeoVisionPro.Core.Primitives.Grid2D? res = null;
        await _main.Tasks.Run($"Interpolate {h.Name}", (p, ct) => Task.Run(() => res = Gridding.Interpolate(h.AsGrid().Clone(), m, 60, 600, 1e-6, ct), ct)).Task!;
        if (res is null) return;
        var edit = Gridding.ApplyToHorizon(h, res, PickSource.Interpolated, onlyUndefined: true, $"Interpolate {h.Name} ({m})");
        _main.Undo.Push(new HorizonEditCommand(edit));
        _main.RefreshViewers();
    }

    [RelayCommand]
    private void Smooth()
    {
        if (ActiveHorizon is not { } h) return;
        var edit = Gridding.ApplyToHorizon(h, Gridding.Smooth(h.AsGrid(), 2), PickSource.Interpolated, onlyUndefined: false, $"Smooth {h.Name}");
        _main.Undo.Push(new HorizonEditCommand(edit));
        _main.RefreshViewers();
    }

    [RelayCommand]
    private void ClearHorizon()
    {
        if (ActiveHorizon is not { } h || !_main.Dialogs.Confirm($"Delete all picks of '{h.Name}'?")) return;
        var edit = new HorizonEdit(h, "Clear horizon");
        for (int k = 0; k < h.Values.Length; k++) if (!double.IsNaN(h.Values[k])) edit.Set(k, double.NaN, PickSource.None);
        _main.Undo.Push(new HorizonEditCommand(edit));
        _main.RefreshViewers();
    }
}

public sealed class HorizonEditCommand(HorizonEdit edit) : IUndoableCommand
{
    public string Description => edit.Description;
    public void Undo() => edit.Undo();
    public void Redo() => edit.Redo();
}

/// <summary>Tabular data manager: surveys, wells, horizons and faults with metadata and validation status.</summary>
public sealed partial class DataManagerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    public ObservableCollection<SeismicDataset> Surveys { get; } = [];
    public ObservableCollection<Well> Wells { get; } = [];
    public ObservableCollection<Horizon> Horizons { get; } = [];
    public ObservableCollection<Fault> Faults { get; } = [];
    [ObservableProperty] private object? _selected;
    public DataManagerViewModel(MainViewModel main) => _main = main;
    partial void OnSelectedChanged(object? value) { if (value is not null) _main.SelectedItem = value; }

    public void Refresh()
    {
        Surveys.Clear(); Wells.Clear(); Horizons.Clear(); Faults.Clear();
        if (!_main.Session.IsOpen) return;
        foreach (var s in _main.Session.Seismic) Surveys.Add(s);
        foreach (var w in _main.Session.Wells) Wells.Add(w);
        foreach (var h in _main.Session.Horizons) Horizons.Add(h);
        foreach (var f in _main.Session.Faults) Faults.Add(f);
    }
}
