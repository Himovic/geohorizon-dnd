using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.Visualization2D;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Desktop.ViewModels;

public enum SectionKind { Inline, Crossline, TimeSlice, Arbitrary, Gather }

/// <summary>
/// 2D seismic viewer: inline, crossline, time slice, arbitrary line or CMP gather of a dataset, with display controls,
/// horizon/fault/well overlays, linked cursor and interpretation tools (manual/seeded picking, fault sticks, erase).
/// </summary>
public sealed partial class SeismicViewerViewModel : DocumentViewModel
{
    public SeismicDataset Dataset { get; }
    public SectionKind Kind { get; }
    private readonly ISeismicVolume? _volume;
    private readonly IGatherDataset? _gathers;
    private ArbitraryPath? _path;
    private (int Inline, int Crossline)[] _locations = [];
    private readonly List<(double Trace, double Time)> _pickChain = [];
    private CancellationTokenSource? _loadCts;

    [ObservableProperty] private int _position;
    [ObservableProperty] private int _minimum;
    [ObservableProperty] private int _maximum;
    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private int _gatherCrossline;
    [ObservableProperty] private string _colorMapName = ColorMaps.Seismic.Name;
    [ObservableProperty] private SeismicDisplayMode _displayMode = SeismicDisplayMode.VariableDensity;
    [ObservableProperty] private double _clipPercentile = 99;
    [ObservableProperty] private double _gain = 1;
    [ObservableProperty] private bool _reversePolarity;
    [ObservableProperty] private double _agcWindowMs;
    [ObservableProperty] private bool _interpolate = true;
    [ObservableProperty] private bool _showHorizons = true;
    [ObservableProperty] private bool _showFaults = true;
    [ObservableProperty] private bool _showWells = true;
    [ObservableProperty] private ViewerTool _tool = ViewerTool.Navigate;
    [ObservableProperty] private string _readout = "";
    [ObservableProperty] private string _positionLabel = "";

    public string[] ColorMapNames { get; } = ColorMaps.All.Select(c => c.Name).ToArray();
    public SeismicDisplayMode[] DisplayModes { get; } = Enum.GetValues<SeismicDisplayMode>();
    public ViewerTool[] Tools { get; } = Enum.GetValues<ViewerTool>();
    public SurveyGeometry Geometry { get; }
    public InterpretationControlsViewModel Interp => Main.InterpretationControls;

    public Array2D? Section { get; private set; }
    public List<SectionOverlay> Overlays { get; } = [];
    public List<SectionMarker> Markers { get; } = [];
    public (double Trace, double Sample)? Crosshair { get; private set; }
    public double[] GatherOffsets { get; private set; } = [];

    /// <summary>Raised when the section data changed (view must re-render; bool = reset zoom).</summary>
    public event EventHandler<bool>? SectionChanged;
    public event EventHandler? OverlaysChanged;
    public event EventHandler? DisplayChanged;

    public SeismicViewerViewModel(MainViewModel main, SeismicDataset ds, SectionKind kind, ArbitraryPath? path = null) : base(main)
    {
        Dataset = ds; Kind = kind; _path = path;
        Geometry = ds.Geometry;
        if (kind == SectionKind.Gather) { _gathers = main.Session.GetGathers(ds); Geometry = _gathers.Geometry; GatherOffsets = _gathers.Offsets; }
        else _volume = main.Session.GetVolume(ds);
        if (ds.Category is SeismicCategory.Attribute or SeismicCategory.Inversion or SeismicCategory.Model)
        {
            ColorMapName = ds.Category == SeismicCategory.Attribute ? ColorMaps.Spectrum.Name : ColorMaps.Impedance.Name;
            _clipMode = ClipMode.MinMax;
        }
        var g = Geometry;
        switch (kind)
        {
            case SectionKind.Inline: Minimum = g.Inlines.First; Maximum = g.Inlines.Last; Step = g.Inlines.Step; _position = g.Inlines.ValueAt(g.InlineCount / 2); break;
            case SectionKind.Crossline: Minimum = g.Crosslines.First; Maximum = g.Crosslines.Last; Step = g.Crosslines.Step; _position = g.Crosslines.ValueAt(g.CrosslineCount / 2); break;
            case SectionKind.TimeSlice: Minimum = 0; Maximum = g.SampleCount - 1; Step = 1; _position = g.SampleCount / 2; break;
            case SectionKind.Gather:
                Minimum = g.Inlines.First; Maximum = g.Inlines.Last; Step = g.Inlines.Step; _position = g.Inlines.ValueAt(g.InlineCount / 2);
                _gatherCrossline = g.Crosslines.ValueAt(g.CrosslineCount / 2);
                break;
            case SectionKind.Arbitrary: Minimum = 0; Maximum = 0; break;
        }
        Title = $"{ds.Name} - {kind}";
        main.Cursor.Moved += OnCursorMoved;
        Interp.PropertyChanged += OnInterpChanged;
        _ = LoadAsync(true);
    }

    private ClipMode _clipMode = ClipMode.Percentile;

    public override (string Kind, string Argument)? WorkspaceKey => Kind is SectionKind.Arbitrary or SectionKind.Gather ? null : ("Section", $"{Dataset.Id}|{Kind}");

    public SeismicDisplaySettings DisplaySettings => new()
    {
        Mode = DisplayMode, ColorMap = ColorMaps.ByName(ColorMapName), ClipMode = _clipMode, ClipValue = ClipPercentile, Gain = Gain,
        ReversePolarity = ReversePolarity, DisplayAgcSamples = AgcWindowMs > 0 && Kind != SectionKind.TimeSlice ? (int)(AgcWindowMs / 1000 / Geometry.SampleInterval) : 0,
        Interpolate = Interpolate, WiggleColor = 0xFF000000, VariableAreaColor = 0xFF000000
    };

    partial void OnColorMapNameChanged(string value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnDisplayModeChanged(SeismicDisplayMode value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnClipPercentileChanged(double value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnGainChanged(double value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnReversePolarityChanged(bool value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnAgcWindowMsChanged(double value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnInterpolateChanged(bool value) => DisplayChanged?.Invoke(this, EventArgs.Empty);
    partial void OnShowHorizonsChanged(bool value) => RefreshOverlays();
    partial void OnShowFaultsChanged(bool value) => RefreshOverlays();
    partial void OnShowWellsChanged(bool value) => RefreshOverlays();
    partial void OnPositionChanged(int value) => _ = LoadAsync(false);
    partial void OnGatherCrosslineChanged(int value) => _ = LoadAsync(false);
    partial void OnToolChanged(ViewerTool value) => _pickChain.Clear();
    private void OnInterpChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(InterpretationControlsViewModel.ActiveHorizon) or nameof(InterpretationControlsViewModel.ActiveFault)) RefreshOverlays(); }

    [RelayCommand] private void Next() => Position = Math.Min(Maximum, Position + Step);
    [RelayCommand] private void Previous() => Position = Math.Max(Minimum, Position - Step);
    [RelayCommand] private void JumpForward() => Position = Math.Min(Maximum, Position + 10 * Step);
    [RelayCommand] private void JumpBack() => Position = Math.Max(Minimum, Position - 10 * Step);

    private async Task LoadAsync(bool resetView)
    {
        if (Kind != SectionKind.TimeSlice && Kind != SectionKind.Arbitrary && Step > 1 && (Position - Minimum) % Step != 0)
        {
            Position = Minimum + (int)Math.Round((Position - Minimum) / (double)Step) * Step;
            return;
        }
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var g = Geometry;
        int pos = Position, gx = GatherCrossline;
        try
        {
            var (section, locs) = await Task.Run(() => Read(pos, gx), cts.Token);
            if (cts.IsCancellationRequested) return;
            Section = section; _locations = locs;
            PositionLabel = Kind switch
            {
                SectionKind.Inline => $"Inline {pos}",
                SectionKind.Crossline => $"Crossline {pos}",
                SectionKind.TimeSlice => $"{(g.Domain == Core.Units.DomainType.Time ? "Time" : "Depth")} {g.TimeAt(pos) * (g.Domain == Core.Units.DomainType.Time ? 1000 : 1):0.#} {(g.Domain == Core.Units.DomainType.Time ? "ms" : "m")}",
                SectionKind.Gather => $"CDP {pos}/{gx}",
                _ => $"Arbitrary line ({locs.Length} traces)"
            };
            Title = $"{Dataset.Name} - {PositionLabel}";
            BuildOverlays();
            SectionChanged?.Invoke(this, resetView);
            OverlaysChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Main.Log.Error("Viewer", e.Message); }
    }

    private (Array2D, (int, int)[]) Read(int pos, int gx)
    {
        var g = Geometry;
        switch (Kind)
        {
            case SectionKind.Inline:
                return (_volume!.ReadInline(pos), Enumerable.Range(0, g.CrosslineCount).Select(j => (pos, g.Crosslines.ValueAt(j))).ToArray());
            case SectionKind.Crossline:
                return (_volume!.ReadCrossline(pos), Enumerable.Range(0, g.InlineCount).Select(i => (g.Inlines.ValueAt(i), pos)).ToArray());
            case SectionKind.TimeSlice:
            {
                // display rows = crosslines (X axis), columns = inlines (Y axis)
                var slice = _volume!.ReadTimeSlice(pos);
                var t = new Array2D(slice.Cols, slice.Rows);
                for (int i = 0; i < slice.Rows; i++) for (int j = 0; j < slice.Cols; j++) t[j, i] = slice[i, j];
                return (t, Enumerable.Range(0, g.CrosslineCount).Select(j => (g.Inlines.First, g.Crosslines.ValueAt(j))).ToArray());
            }
            case SectionKind.Gather:
            {
                var gather = _gathers!.ReadGather(pos, gx) ?? new Array2D(_gathers.Offsets.Length, g.SampleCount);
                return (gather, Enumerable.Range(0, gather.Rows).Select(_ => (pos, gx)).ToArray());
            }
            default:
            {
                var p = _path ?? new ArbitraryPath([(g.Inlines.First, g.Crosslines.First)]);
                var a = _volume!.ReadArbitrary(p);
                return (a, p.Points.Select(x => ((int)Math.Round(x.Inline), (int)Math.Round(x.Crossline))).ToArray());
            }
        }
    }

    // --- axis labels for the view
    public string TraceAxisTitle => Kind switch { SectionKind.Inline => "Crossline", SectionKind.Crossline => "Inline", SectionKind.TimeSlice => "Crossline", SectionKind.Gather => "Offset", _ => "Trace" };
    public string SampleAxisTitle => Kind == SectionKind.TimeSlice ? "Inline" : Geometry.Domain == Core.Units.DomainType.Time ? "TWT (ms)" : "Depth (m)";

    public string TraceLabel(int i) => Kind switch
    {
        SectionKind.Inline => Geometry.Crosslines.ValueAt(i).ToString(CultureInfo.InvariantCulture),
        SectionKind.Crossline => Geometry.Inlines.ValueAt(i).ToString(CultureInfo.InvariantCulture),
        SectionKind.TimeSlice => Geometry.Crosslines.ValueAt(i).ToString(CultureInfo.InvariantCulture),
        SectionKind.Gather => i < GatherOffsets.Length ? GatherOffsets[i].ToString("0", CultureInfo.InvariantCulture) : "",
        _ => i < _locations.Length ? $"{_locations[i].Inline}/{_locations[i].Crossline}" : ""
    };

    public double SampleToAxis(double s) => Kind == SectionKind.TimeSlice ? Geometry.Inlines.First + s * Geometry.Inlines.Step : Geometry.TimeAt(s) * (Geometry.Domain == Core.Units.DomainType.Time ? 1000 : 1);
    public double AxisToSample(double a) => Kind == SectionKind.TimeSlice ? (a - Geometry.Inlines.First) / Geometry.Inlines.Step : Geometry.SampleAt(a / (Geometry.Domain == Core.Units.DomainType.Time ? 1000 : 1));
    public string SampleLabel(double s) => SampleToAxis(s).ToString("0", CultureInfo.InvariantCulture);

    // --- overlays
    public void RefreshOverlays() { BuildOverlays(); OverlaysChanged?.Invoke(this, EventArgs.Empty); }

    private void BuildOverlays()
    {
        Overlays.Clear(); Markers.Clear();
        if (Section is null || !Main.Session.IsOpen) return;
        var g = Geometry;
        if (Kind is SectionKind.Inline or SectionKind.Crossline or SectionKind.Arbitrary)
        {
            if (ShowHorizons)
                foreach (var h in Main.Session.Horizons)
                {
                    var pts = _locations.Select((l, t) => ((double)t, g.SampleAt(h.GetValue(l.Inline, l.Crossline)))).ToList();
                    bool active = ReferenceEquals(h, Interp.ActiveHorizon);
                    Overlays.Add(new SectionOverlay(h.Name, BitmapUtil.ParseColor(h.Color, Colors.Orange), active ? 2.4 : 1.4, pts));
                }
            if (ShowFaults)
                foreach (var f in Main.Session.Faults)
                    foreach (var st in f.Sticks)
                    {
                        var pts = new List<(double, double)>();
                        foreach (var p in st.Points)
                        {
                            double tr = TraceOf(p.X, p.Y, out double dist);
                            if (dist <= Math.Max(g.Inlines.Step, g.Crosslines.Step) * 1.5) pts.Add((tr, g.SampleAt(p.Z)));
                        }
                        if (pts.Count >= 2) Overlays.Add(new SectionOverlay(f.Name, BitmapUtil.ParseColor(f.Color, Colors.Red), ReferenceEquals(f, Interp.ActiveFault) ? 2.5 : 1.6, pts, false, true));
                    }
            if (ShowWells && g.Domain == Core.Units.DomainType.Time)
                foreach (var w in Main.Session.Wells)
                {
                    var (fi, fx) = g.Transform.ToGrid(w.SurfaceX, w.SurfaceY);
                    double tr = TraceOf(fi, fx, out double dist);
                    if (dist > 3 * Math.Max(g.Inlines.Step, g.Crosslines.Step)) continue;
                    try
                    {
                        var tdr = Main.WellServices.GetTimeDepth(w);
                        var traj = WellTrajectory.Compute(w);
                        double mdTop = w.Curves.Count > 0 ? w.Curves.Min(c => c.Depths.FirstOrDefault()) : 0;
                        Markers.Add(new SectionMarker(w.Name, tr, g.SampleAt(tdr.TimeAtMd(mdTop, traj)), g.SampleAt(tdr.TimeAtMd(Math.Max(w.TotalDepth, mdTop + 1), traj)), Color.FromRgb(232, 237, 244)));
                        foreach (var top in w.Tops)
                            Overlays.Add(new SectionOverlay(top.Name, BitmapUtil.ParseColor(top.Color, Colors.Yellow), 3, [(tr - 1.5, g.SampleAt(tdr.TimeAtMd(top.MeasuredDepth, traj))), (tr + 1.5, g.SampleAt(tdr.TimeAtMd(top.MeasuredDepth, traj)))]));
                    }
                    catch (InvalidOperationException) { }
                }
        }
        else if (Kind == SectionKind.TimeSlice && ShowHorizons)
        {
            // horizon intersections with the slice as contour-like points
            double t = g.TimeAt(Position);
            foreach (var h in Main.Session.Horizons)
            {
                var pts = new List<(double, double)>();
                for (int i = 0; i < h.InlineCount; i++)
                    for (int j = 0; j + 1 < h.CrosslineCount; j++)
                    {
                        double a = h.Values[i * h.CrosslineCount + j], b = h.Values[i * h.CrosslineCount + j + 1];
                        if (double.IsNaN(a) || double.IsNaN(b) || (a - t) * (b - t) > 0) continue;
                        int il = h.InlineAt(i), xl = h.CrosslineAt(j);
                        if (!g.ContainsTrace(il, xl)) continue;
                        pts.Add((g.CrosslineIndex(xl) + (t - a) / (b - a + 1e-12), g.InlineIndex(il)));
                    }
                if (pts.Count > 0) Overlays.Add(new SectionOverlay(h.Name, BitmapUtil.ParseColor(h.Color, Colors.Orange), 1.5, pts, false, true));
            }
            if (ShowWells)
                foreach (var w in Main.Session.Wells)
                {
                    var (fi, fx) = g.Transform.ToGrid(w.SurfaceX, w.SurfaceY);
                    double tr = (fx - g.Crosslines.First) / g.Crosslines.Step, s = (fi - g.Inlines.First) / g.Inlines.Step;
                    if (tr < 0 || s < 0 || tr > g.CrosslineCount || s > g.InlineCount) continue;
                    Overlays.Add(new SectionOverlay(w.Name, Colors.White, 2, [(tr - 0.6, s), (tr + 0.6, s), (tr, s), (tr, s - 0.6), (tr, s + 0.6)], false, true));
                }
        }
    }

    /// <summary>Fractional trace index of a survey position on the current line and its distance (bins) from the line.</summary>
    private double TraceOf(double inline, double crossline, out double distance)
    {
        var g = Geometry;
        switch (Kind)
        {
            case SectionKind.Inline: distance = Math.Abs(inline - Position) / g.Inlines.Step; return (crossline - g.Crosslines.First) / g.Crosslines.Step;
            case SectionKind.Crossline: distance = Math.Abs(crossline - Position) / g.Crosslines.Step; return (inline - g.Inlines.First) / g.Inlines.Step;
            default:
                if (_path is null || _path.Points.Count == 0) { distance = double.MaxValue; return 0; }
                int best = 0; double bd = double.MaxValue;
                for (int k = 0; k < _path.Points.Count; k++)
                {
                    double d = Math.Pow((_path.Points[k].Inline - inline) / g.Inlines.Step, 2) + Math.Pow((_path.Points[k].Crossline - crossline) / g.Crosslines.Step, 2);
                    if (d < bd) { bd = d; best = k; }
                }
                distance = Math.Sqrt(bd);
                return best;
        }
    }

    // --- interaction from the view
    public void OnPointerMoved(double trace, double sample, float amplitude)
    {
        if (Section is null) return;
        int ti = Math.Clamp((int)Math.Round(trace), 0, Math.Max(0, _locations.Length - 1));
        var g = Geometry;
        if (Kind == SectionKind.TimeSlice)
        {
            int il = g.Inlines.First + (int)Math.Round(sample) * g.Inlines.Step, xl = g.Crosslines.ValueAt(Math.Clamp(ti, 0, g.CrosslineCount - 1));
            var m = g.MapLocation(il, xl);
            Readout = $"IL {il}  XL {xl}  X {m.X:0.0}  Y {m.Y:0.0}  amp {amplitude:G4}";
            Main.CursorText = Readout;
            Main.Cursor.Publish(m, g.TimeAt(Position), this);
            return;
        }
        if (_locations.Length == 0) return;
        var (lil, lxl) = _locations[ti];
        double time = g.TimeAt(sample);
        var map = g.MapLocation(lil, lxl);
        string tunit = g.Domain == Core.Units.DomainType.Time ? "ms" : "m";
        double tv = g.Domain == Core.Units.DomainType.Time ? time * 1000 : time;
        Readout = Kind == SectionKind.Gather
            ? $"Offset {TraceLabel(ti)}  t {tv:0.0} {tunit}  amp {amplitude:G4}"
            : $"IL {lil}  XL {lxl}  {(g.Domain == Core.Units.DomainType.Time ? "TWT" : "Z")} {tv:0.0} {tunit}  X {map.X:0.0}  Y {map.Y:0.0}  amp {amplitude:G4}";
        Main.CursorText = Readout;
        if (Kind != SectionKind.Gather) Main.Cursor.Publish(map, time, this);
    }

    private void OnCursorMoved(object? sender, (Point2D Map, double Time, object Source) e)
    {
        if (ReferenceEquals(e.Source, this) || Section is null || Kind == SectionKind.Gather) return;
        var g = Geometry;
        var (fi, fx) = g.Transform.ToGrid(e.Map.X, e.Map.Y);
        if (Kind == SectionKind.TimeSlice)
        {
            Crosshair = ((fx - g.Crosslines.First) / g.Crosslines.Step, (fi - g.Inlines.First) / g.Inlines.Step);
        }
        else
        {
            double tr = TraceOf(fi, fx, out double dist);
            Crosshair = dist <= 2 ? (tr, g.SampleAt(e.Time)) : (null as (double, double)?);
        }
        OverlaysChanged?.Invoke(this, EventArgs.Empty);
    }

    public async void OnPointerClicked(double trace, double sample, System.Windows.Input.MouseButton button, System.Windows.Input.ModifierKeys mods, int clicks)
    {
        if (Section is null || _locations.Length == 0) return;
        var g = Geometry;
        int ti = Math.Clamp((int)Math.Round(trace), 0, _locations.Length - 1);
        if (Kind == SectionKind.TimeSlice && clicks == 2)
        {
            int il = g.Inlines.First + (int)Math.Round(sample) * g.Inlines.Step;
            Main.Open(new SeismicViewerViewModel(Main, Dataset, SectionKind.Inline) { Position = il });
            return;
        }
        if (Tool != ViewerTool.Pick || Kind is SectionKind.TimeSlice or SectionKind.Gather) return;
        if (Interp.ActiveHorizon is not { } h) { Main.Dialogs.Info("Create or select an active horizon (Interpretation > New Horizon) before picking."); return; }
        if (button == System.Windows.Input.MouseButton.Right) { _pickChain.Clear(); Status = "Pick chain reset"; return; }
        double time = g.TimeAt(sample);
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) || mods.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            // seeded auto-tracking: Shift = full 3D flood fill, Ctrl = along this line only
            var (il, xl) = _locations[ti];
            bool lineOnly = mods.HasFlag(System.Windows.Input.ModifierKeys.Control);
            HorizonEdit? edit = null;
            var tracker = new HorizonAutoTracker(_volume!);
            var p = Interp.Tracking;
            await RunAsync($"Auto-track {h.Name}", (pr, ct) => Task.Run(() =>
                edit = tracker.Track(h, [(il, xl, time)], p, lineOnly && Kind == SectionKind.Inline ? Position : null, lineOnly && Kind == SectionKind.Crossline ? Position : null, pr, ct), ct));
            if (edit is not null) { Main.Undo.Push(new HorizonEditCommand(edit)); Main.RefreshViewers(); Status = $"{edit.Changes.Count:N0} nodes tracked"; }
            return;
        }
        _pickChain.Add((trace, time));
        var clicksForSegment = _pickChain.Count >= 2 ? _pickChain.Skip(_pickChain.Count - 2).ToList() : _pickChain.ToList();
        var e = ManualPicking.PickSegment(h, Section, _locations, clicksForSegment, g, Interp.SnapEvent, Interp.SnapWindow);
        Main.Undo.Push(new HorizonEditCommand(e));
        Main.RefreshViewers();
    }

    public void OnPolylineCompleted(IReadOnlyList<(double Trace, double Sample)> pts)
    {
        if (Kind is SectionKind.TimeSlice or SectionKind.Gather) return;
        if (Interp.ActiveFault is not { } f) { Main.Dialogs.Info("Create or select an active fault (Interpretation > New Fault) before drawing fault sticks."); return; }
        var g = Geometry;
        var stick = new FaultStick();
        foreach (var (t, s) in pts)
        {
            int ti = Math.Clamp((int)Math.Round(t), 0, _locations.Length - 1);
            var (il, xl) = _locations[ti];
            double fil = il, fxl = xl;
            if (Kind == SectionKind.Inline) fxl = g.Crosslines.First + t * g.Crosslines.Step;
            if (Kind == SectionKind.Crossline) fil = g.Inlines.First + t * g.Inlines.Step;
            stick.Points.Add(new Point3D(fil, fxl, g.TimeAt(s)));
        }
        f.Sticks.Add(stick);
        Main.Undo.Push(new Application.Services.DelegateCommand($"Add stick to {f.Name}", () => f.Sticks.Remove(stick), () => f.Sticks.Add(stick)));
        Main.RefreshViewers();
    }

    public void OnEraseRequested(double t0, double t1)
    {
        if (Interp.ActiveHorizon is not { } h || Kind is SectionKind.TimeSlice or SectionKind.Gather) return;
        var e = ManualPicking.Erase(h, _locations, (int)Math.Floor(t0), (int)Math.Ceiling(t1));
        Main.Undo.Push(new HorizonEditCommand(e));
        Main.RefreshViewers();
    }

    [RelayCommand]
    private void OpenOrthogonal()
    {
        if (Main.Cursor.Last is not { } c) return;
        var (fi, fx) = Geometry.Transform.ToGrid(c.Map.X, c.Map.Y);
        if (Kind == SectionKind.Inline)
            Main.Open(new SeismicViewerViewModel(Main, Dataset, SectionKind.Crossline) { Position = Geometry.Crosslines.First + (int)Math.Round((fx - Geometry.Crosslines.First) / Geometry.Crosslines.Step) * Geometry.Crosslines.Step });
        else
            Main.Open(new SeismicViewerViewModel(Main, Dataset, SectionKind.Inline) { Position = Geometry.Inlines.First + (int)Math.Round((fi - Geometry.Inlines.First) / Geometry.Inlines.Step) * Geometry.Inlines.Step });
    }

    [RelayCommand]
    private void ShowSpectrum()
    {
        if (Section is null) return;
        var (f, a) = SeismicProcessing.ProcessingEngine.AverageSpectrum(Section, Geometry.SampleInterval);
        Main.Open(new ChartViewModel(Main, $"Spectrum - {Title}", "Frequency (Hz)", "Amplitude", [("Average amplitude spectrum", f, a, "#3D8BDB")]));
    }

    public override void OnClosed()
    {
        Main.Cursor.Moved -= OnCursorMoved;
        Interp.PropertyChanged -= OnInterpChanged;
        _loadCts?.Cancel();
    }

    public override void OnProjectChanged() => RefreshOverlays();
}
