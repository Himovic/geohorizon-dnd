using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Attributes;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.Visualization2D;
using GeoVisionPro.Visualization3D;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>Generic line/scatter chart document (spectra, QC curves).</summary>
public sealed partial class ChartViewModel : DocumentViewModel
{
    public string XLabel { get; }
    public string YLabel { get; }
    public IReadOnlyList<(string Name, double[] X, double[] Y, string Color)> Series { get; }
    public bool InvertY { get; init; }
    public ChartViewModel(MainViewModel main, string title, string x, string y, IReadOnlyList<(string, double[], double[], string)> series) : base(main)
    {
        Title = title; XLabel = x; YLabel = y; Series = series;
    }
}

/// <summary>Base map: survey outlines, wells, horizon/attribute maps, arbitrary line digitising.</summary>
public sealed partial class MapViewModel : DocumentViewModel
{
    public ObservableCollection<Horizon> Horizons { get; } = [];
    [ObservableProperty] private Horizon? _selectedHorizon;
    [ObservableProperty] private string _mapAttribute = "Time";
    [ObservableProperty] private string _colorMapName = ColorMaps.Spectrum.Name;
    [ObservableProperty] private bool _drawArbitrary;
    [ObservableProperty] private string _readout = "";
    [ObservableProperty] private string _legend = "";
    public string[] MapAttributes { get; } = ["Time", "Amplitude at horizon", "RMS amplitude (±20 ms)", "Dip angle", "Most-positive curvature", "Most-negative curvature", "Picking source"];
    public string[] ColorMapNames { get; } = ColorMaps.All.Select(c => c.Name).ToArray();
    public List<MapPolygon> Polygons { get; } = [];
    public List<MapPoint> Points { get; } = [];
    public List<MapRaster> Rasters { get; } = [];
    public event EventHandler? MapChanged;
    private Grid2D? _customGrid;
    private string? _customName;

    public MapViewModel(MainViewModel main) : base(main)
    {
        Title = "Base Map";
        OnProjectChanged();
    }

    public override (string Kind, string Argument)? WorkspaceKey => ("Map", "");

    public override void OnProjectChanged()
    {
        var sel = SelectedHorizon;
        Horizons.Clear();
        foreach (var h in Main.Session.Horizons) Horizons.Add(h);
        SelectedHorizon = sel is not null && Horizons.Contains(sel) ? sel : null;
        Rebuild();
    }

    partial void OnSelectedHorizonChanged(Horizon? value) { _customGrid = null; Rebuild(); }
    partial void OnMapAttributeChanged(string value) { _customGrid = null; Rebuild(); }
    partial void OnColorMapNameChanged(string value) => Rebuild();

    /// <summary>Displays an externally computed grid (e.g. horizon attribute extraction) aligned to a horizon.</summary>
    public void ShowGrid(Horizon geometryOf, Grid2D grid, string name)
    {
        SetProperty(ref _selectedHorizon, geometryOf, nameof(SelectedHorizon));
        _customGrid = grid; _customName = name;
        Rebuild();
    }

    public void Rebuild()
    {
        Polygons.Clear(); Points.Clear(); Rasters.Clear();
        if (!Main.Session.IsOpen) return;
        foreach (var s in Main.Session.Seismic.Where(s => s.Kind != SeismicKind.PostStack2D).GroupBy(s => (s.InlineFirst, s.InlineLast, s.CrosslineFirst, s.CrosslineLast, s.TransformX0)).Select(g => g.First()))
            Polygons.Add(new MapPolygon(s.Name, s.Geometry.MapOutline(), Color.FromRgb(61, 139, 219)));
        foreach (var w in Main.Session.Wells)
        {
            Points.Add(new MapPoint(w.Name, new Point2D(w.SurfaceX, w.SurfaceY), Color.FromRgb(232, 237, 244)));
            if (w.Deviation.Count > 1) Polygons.Add(new MapPolygon("", WellTrajectory.Compute(w).Sample(50).Select(p => new Point2D(p.X, p.Y)).ToList(), Color.FromRgb(200, 200, 200), false, 1));
        }
        if (SelectedHorizon is { } h && (Main.Session.Seismic.FirstOrDefault(s => s.Id == h.SurveyId) ?? Main.SelectedSeismic()) is { } survey)
        {
            try
            {
                var grid = _customGrid ?? ComputeAttribute(h, survey);
                var (mn, mx) = grid.Range();
                if (!double.IsNaN(mn))
                {
                    var cmap = ColorMaps.ByName(ColorMapName);
                    int w = h.CrosslineCount, ht = h.InlineCount;
                    var px = new uint[w * ht];
                    for (int i = 0; i < ht; i++) for (int j = 0; j < w; j++) { double v = grid[i, j]; px[i * w + j] = double.IsNaN(v) ? 0 : cmap.Lookup((v - mn) / Math.Max(1e-30, mx - mn)); }
                    var g = survey.Geometry;
                    var bmp = BitmapUtil.ToBitmap(px, w, ht);
                    bmp.Freeze();
                    // pixel corners: half-bin outside the first/last node
                    var c00 = g.MapLocation(h.InlineFirst - 0.5 * h.InlineStep, h.CrosslineFirst - 0.5 * h.CrosslineStep);
                    var c10 = g.MapLocation(h.InlineFirst - 0.5 * h.InlineStep, h.CrosslineAt(w - 1) + 0.5 * h.CrosslineStep);
                    var c01 = g.MapLocation(h.InlineAt(ht - 1) + 0.5 * h.InlineStep, h.CrosslineFirst - 0.5 * h.CrosslineStep);
                    Rasters.Add(new MapRaster(bmp, c00, c10, c01, 0.92));
                    Legend = $"{_customName ?? MapAttribute} of {h.Name}: {Fmt.N(mn, "G5")} … {Fmt.N(mx, "G5")}";
                }
            }
            catch (Exception e) { Main.Log.Warn("Map", e.Message); }
        }
        else Legend = "";
        MapChanged?.Invoke(this, EventArgs.Empty);
    }

    private Grid2D ComputeAttribute(Horizon h, SeismicDataset survey)
    {
        var g = survey.Geometry;
        switch (MapAttribute)
        {
            case "Amplitude at horizon": return HorizonAttributeExtraction.Extract(Main.Session.GetVolume(survey), h, HorizonWindowMode.AtHorizon, WindowStatistic.Rms);
            case "RMS amplitude (±20 ms)": return HorizonAttributeExtraction.Extract(Main.Session.GetVolume(survey), h, HorizonWindowMode.WindowAroundHorizon, WindowStatistic.Rms, 0.02, 0.02);
            case "Dip angle": return HorizonCurvature.Compute(h, g.Transform.InlineSpacing, g.Transform.CrosslineSpacing, CurvatureType.DipAngle);
            case "Most-positive curvature": return HorizonCurvature.Compute(h, g.Transform.InlineSpacing, g.Transform.CrosslineSpacing, CurvatureType.MostPositive);
            case "Most-negative curvature": return HorizonCurvature.Compute(h, g.Transform.InlineSpacing, g.Transform.CrosslineSpacing, CurvatureType.MostNegative);
            case "Picking source": return new Grid2D(h.InlineCount, h.CrosslineCount, h.Sources.Select(s => s == 0 ? double.NaN : s).ToArray());
            default: return h.AsGrid().Clone();
        }
    }

    public void OnMoved(Point2D p)
    {
        var s = Main.SelectedSeismic();
        string grid = "";
        if (s is not null)
        {
            var (il, xl) = s.Geometry.Transform.ToGrid(p.X, p.Y);
            grid = $"  IL {il:0.0}  XL {xl:0.0}";
            if (SelectedHorizon is { } h)
            {
                double v = (_customGrid ?? null) is { } cg && h.TryIndex((int)Math.Round(il), (int)Math.Round(xl), out var i, out var j) ? cg[i, j] : h.GetValue((int)Math.Round(il), (int)Math.Round(xl));
                grid += $"  {(_customName ?? SelectedHorizon.Name)}: {Fmt.N(v * (_customGrid is null && MapAttribute == "Time" ? 1000 : 1), "G5")}";
            }
        }
        Readout = $"X {p.X:0.0}  Y {p.Y:0.0}{grid}";
        Main.CursorText = Readout;
        Main.Cursor.Publish(p, Main.Cursor.Last?.Time ?? 0, this);
    }

    public void OnClicked(Point2D p, int clicks)
    {
        if (clicks < 2 || Main.SelectedSeismic() is not { } s) return;
        var nt = s.Geometry.NearestTrace(p);
        if (nt is null) return;
        Main.Open(new SeismicViewerViewModel(Main, s, SectionKind.Inline) { Position = nt.Value.Inline });
    }

    public void OnPolyline(IReadOnlyList<Point2D> pts)
    {
        DrawArbitrary = false;
        if (Main.SelectedSeismic() is not { } s) return;
        var g = s.Geometry;
        var verts = pts.Select(p => g.Transform.ToGrid(p.X, p.Y)).Select(v => (v.Inline, v.Crossline)).ToList();
        var path = ArbitraryPath.FromVertices(verts, 1.0);
        var inside = path.Points.Where(q => q.Inline >= Math.Min(g.Inlines.First, g.Inlines.Last) && q.Inline <= Math.Max(g.Inlines.First, g.Inlines.Last)
            && q.Crossline >= Math.Min(g.Crosslines.First, g.Crosslines.Last) && q.Crossline <= Math.Max(g.Crosslines.First, g.Crosslines.Last)).ToList();
        if (inside.Count < 2) { Main.Dialogs.Info("The arbitrary line does not cross the survey."); return; }
        Main.Open(new SeismicViewerViewModel(Main, s, SectionKind.Arbitrary, new ArbitraryPath(inside)));
    }
}

/// <summary>One togglable object of the 3D scene.</summary>
public sealed partial class SceneItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    [ObservableProperty] private bool _visible = true;
    public List<SceneMesh> Meshes { get; } = [];
    public List<ScenePolyline> Lines { get; } = [];
    public bool Transparent { get; init; }
}

/// <summary>
/// Interactive 3D scene: inline/crossline/time slices with seismic textures, horizon and fault surfaces, well paths,
/// survey frame and a 2D-texture-stack volume rendering, with vertical exaggeration and per-object visibility.
/// </summary>
public sealed partial class Scene3DViewModel : DocumentViewModel
{
    public ObservableCollection<SceneItem> Items { get; } = [];
    public ObservableCollection<SeismicDataset> Volumes { get; } = [];
    [ObservableProperty] private SeismicDataset? _volume;
    [ObservableProperty] private int _inline;
    [ObservableProperty] private int _crossline;
    [ObservableProperty] private int _timeSample;
    [ObservableProperty] private bool _showInline = true;
    [ObservableProperty] private bool _showCrossline = true;
    [ObservableProperty] private bool _showTimeSlice;
    [ObservableProperty] private double _verticalExaggeration = 2;
    [ObservableProperty] private string _colorMapName = ColorMaps.Seismic.Name;
    [ObservableProperty] private int _volumeSlices = 48;
    [ObservableProperty] private double _volumeOpacity = 0.5;
    [ObservableProperty] private bool _orthographic;
    public string[] ColorMapNames { get; } = ColorMaps.All.Select(c => c.Name).ToArray();
    public event EventHandler? SceneChanged;
    public event EventHandler? CameraModeChanged;
    public SceneScale? Scale { get; private set; }

    public Scene3DViewModel(MainViewModel main) : base(main)
    {
        Title = "3D View";
        foreach (var s in main.Session.Seismic.Where(s => s.Kind is SeismicKind.PostStack3D)) Volumes.Add(s);
        _volume = main.SelectedSeismic() ?? Volumes.FirstOrDefault();
        if (_volume is { } v)
        {
            var g = v.Geometry;
            _inline = g.Inlines.ValueAt(g.InlineCount / 2); _crossline = g.Crosslines.ValueAt(g.CrosslineCount / 2); _timeSample = g.SampleCount / 2;
        }
        _ = BuildAsync();
    }

    public override (string Kind, string Argument)? WorkspaceKey => ("Scene3D", "");
    public SurveyGeometry? Geometry => Volume?.Geometry;

    partial void OnVolumeChanged(SeismicDataset? value) => _ = BuildAsync();
    partial void OnInlineChanged(int value) => _ = UpdateSlicesAsync();
    partial void OnCrosslineChanged(int value) => _ = UpdateSlicesAsync();
    partial void OnTimeSampleChanged(int value) => _ = UpdateSlicesAsync();
    partial void OnShowInlineChanged(bool value) => _ = UpdateSlicesAsync();
    partial void OnShowCrosslineChanged(bool value) => _ = UpdateSlicesAsync();
    partial void OnShowTimeSliceChanged(bool value) => _ = UpdateSlicesAsync();
    partial void OnColorMapNameChanged(string value) => _ = UpdateSlicesAsync();
    partial void OnVerticalExaggerationChanged(double value) => _ = BuildAsync();
    partial void OnOrthographicChanged(bool value) => CameraModeChanged?.Invoke(this, EventArgs.Empty);

    public override void OnProjectChanged() => _ = BuildAsync();

    private SceneItem Item(string name, string kind, bool transparent = false)
    {
        var existing = Items.FirstOrDefault(i => i.Name == name && i.Kind == kind);
        bool vis = existing?.Visible ?? true;
        if (existing is not null) Items.Remove(existing);
        var it = new SceneItem { Name = name, Kind = kind, Visible = vis, Transparent = transparent };
        it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SceneItem.Visible)) SceneChanged?.Invoke(this, EventArgs.Empty); };
        Items.Add(it);
        return it;
    }

    public async Task BuildAsync()
    {
        if (Volume is null) { Items.Clear(); SceneChanged?.Invoke(this, EventArgs.Empty); return; }
        var g = Volume.Geometry;
        Scale = new SceneScale(g.Transform.X0, g.Transform.Y0, 1500, VerticalExaggeration);
        var s = Scale;
        var keep = Items.Where(i => i.Kind is "Slice" or "Volume").ToList();
        Items.Clear();
        foreach (var k in keep) Items.Add(k);
        var frame = Item("Survey frame", "Frame");
        frame.Lines.AddRange(MeshFactory.SurveyOutline(g, s, 0xFF3D8BDB));
        foreach (var h in Main.Session.Horizons.Where(h => h.InlineCount == g.InlineCount && h.CrosslineCount == g.CrosslineCount))
        {
            var it = Item(h.Name, "Horizon");
            var cmap = ColorMaps.Viridis;
            it.Meshes.Add(await Task.Run(() => MeshFactory.HorizonSurface(h, g, s, cmap, Math.Max(1, Math.Max(h.InlineCount, h.CrosslineCount) / 300))));
        }
        foreach (var f in Main.Session.Faults)
        {
            var mesh = FaultModeling.BuildSurface(f);
            if (mesh.Triangles.Count == 0) continue;
            var col = BitmapUtil.ParseColor(f.Color, Colors.Red);
            var it = Item(f.Name, "Fault");
            it.Meshes.Add(MeshFactory.FaultSurface(mesh, g, s, 0xFF000000u | ((uint)col.R << 16) | ((uint)col.G << 8) | col.B, f.Domain == Core.Units.DomainType.Time));
        }
        foreach (var w in Main.Session.Wells)
        {
            try
            {
                var traj = WellTrajectory.Compute(w);
                var tdr = Main.WellServices.GetTimeDepth(w);
                var pts = new List<Point3D>();
                for (double md = traj.Stations[0].MeasuredDepth; md <= traj.Stations[^1].MeasuredDepth; md += 20)
                {
                    var loc = traj.LocationAt(md);
                    pts.Add(s.ToWorld(loc.X, loc.Y, g.Domain == Core.Units.DomainType.Time ? tdr.TimeAtMd(md, traj) : traj.TvdSsAt(md), g.Domain == Core.Units.DomainType.Time));
                }
                var it = Item(w.Name, "Well");
                it.Lines.Add(new ScenePolyline(pts, 0xFFE8EDF4, 3, w.Name));
                foreach (var top in w.Tops)
                {
                    var loc = traj.LocationAt(top.MeasuredDepth);
                    double z = g.Domain == Core.Units.DomainType.Time ? tdr.TimeAtMd(top.MeasuredDepth, traj) : traj.TvdSsAt(top.MeasuredDepth);
                    var c = s.ToWorld(loc.X, loc.Y, z, g.Domain == Core.Units.DomainType.Time);
                    it.Lines.Add(new ScenePolyline([c + new Point3D(-60, 0, 0), c + new Point3D(60, 0, 0)], 0xFFFFD54F, 4, top.Name));
                }
            }
            catch (InvalidOperationException) { }
        }
        await UpdateSlicesAsync();
    }

    public async Task UpdateSlicesAsync()
    {
        if (Volume is null || Scale is null) return;
        var g = Volume.Geometry; var s = Scale;
        var vol = Main.Session.GetVolume(Volume);
        var settings = new SeismicDisplaySettings { ColorMap = ColorMaps.ByName(ColorMapName) };
        var stats = (Volume.AmplitudeMin, Volume.AmplitudeMax);
        int il = Inline, xl = Crossline, ts = TimeSample;
        bool si = ShowInline, sx = ShowCrossline, st = ShowTimeSlice;
        var meshes = await Task.Run(() =>
        {
            var list = new List<SceneMesh>();
            (float, float) clip = default;
            if (si && g.Inlines.Contains(il))
            {
                var sec = vol.ReadInline(il);
                clip = SectionRenderer.ComputeClip(sec, settings);
                var px = SectionRenderer.Render(sec, settings, SectionViewport.Full(sec), Math.Min(1024, sec.Rows * 2), Math.Min(1024, sec.Cols), clip);
                var c = MeshFactory.InlineCorners(g, il, s);
                list.Add(MeshFactory.TexturedQuad(c.TL, c.TR, c.BR, c.BL, px, Math.Min(1024, sec.Rows * 2), Math.Min(1024, sec.Cols)));
            }
            if (sx && g.Crosslines.Contains(xl))
            {
                var sec = vol.ReadCrossline(xl);
                if (clip == default) clip = SectionRenderer.ComputeClip(sec, settings);
                var px = SectionRenderer.Render(sec, settings, SectionViewport.Full(sec), Math.Min(1024, sec.Rows * 2), Math.Min(1024, sec.Cols), clip);
                var c = MeshFactory.CrosslineCorners(g, xl, s);
                list.Add(MeshFactory.TexturedQuad(c.TL, c.TR, c.BR, c.BL, px, Math.Min(1024, sec.Rows * 2), Math.Min(1024, sec.Cols)));
            }
            if (st)
            {
                var slice = vol.ReadTimeSlice(ts); // [il, xl]
                var t = new Array2D(slice.Cols, slice.Rows);
                for (int i = 0; i < slice.Rows; i++) for (int j = 0; j < slice.Cols; j++) t[j, i] = slice[i, j];
                if (clip == default) clip = SectionRenderer.ComputeClip(t, settings);
                int w = Math.Min(1024, t.Rows * 2), h = Math.Min(1024, t.Cols * 2);
                var px = SectionRenderer.Render(t, settings, SectionViewport.Full(t), w, h, clip);
                var c = MeshFactory.TimeSliceCorners(g, g.TimeAt(ts), s);
                list.Add(MeshFactory.TexturedQuad(c.TL, c.TR, c.BR, c.BL, px, w, h));
            }
            return list;
        });
        var it = Item("Seismic slices", "Slice");
        it.Meshes.AddRange(meshes);
        _ = stats;
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task BuildVolumeRendering()
    {
        if (Volume is null || Scale is null) return;
        var vol = Main.Session.GetVolume(Volume);
        var tf = new TransferFunction { ColorMap = ColorMaps.ByName(ColorMapName), Min = -Volume.AmplitudeRms * 3, Max = Volume.AmplitudeRms * 3, GlobalOpacity = VolumeOpacity };
        IReadOnlyList<SceneMesh>? stack = null;
        var s = Scale;
        await RunAsync("Volume rendering", (p, ct) => Task.Run(() => stack = MeshFactory.VolumeSliceStack(vol, tf, s, VolumeSlices, 256, ct), ct));
        if (stack is null) return;
        var it = Item("Volume rendering", "Volume", transparent: true);
        it.Meshes.AddRange(stack);
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RemoveVolumeRendering()
    {
        foreach (var i in Items.Where(i => i.Kind == "Volume").ToList()) Items.Remove(i);
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ZoomExtentsRequested;
    [RelayCommand] private void ZoomExtents() => ZoomExtentsRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>GPU ray-cast volume rendering through the native VTK bridge (when deployed).</summary>
public sealed partial class VtkVolumeViewModel : DocumentViewModel
{
    public SeismicDataset Dataset { get; }
    [ObservableProperty] private int _maxDimension = 256;
    [ObservableProperty] private double _opacity = 0.35;
    [ObservableProperty] private bool _parallelProjection;
    public bool IsAvailable => GeoVisionPro.NativeEngine.VtkScene.IsAvailable;
    public string AvailabilityMessage => IsAvailable ? "VTK renderer active (GPU ray casting)." :
        "The native VTK bridge (gvvtk.dll) is not installed. Build native/GeoVisionPro.NativeEngine with VTK 9 (see BUILD_INSTRUCTIONS.md) to enable ray-cast volume rendering. The 3D View provides slice-stack volume rendering without VTK.";
    public VtkVolumeViewModel(MainViewModel main, SeismicDataset ds) : base(main) { Dataset = ds; Title = $"Volume (VTK) - {ds.Name}"; }

    /// <summary>Decimated volume (x = crossline fastest, y = inline, z = sample) for the GPU, with spacing in world units.</summary>
    public (float[] Data, int Nx, int Ny, int Nz, double Sx, double Sy, double Sz) Prepare(CancellationToken ct)
    {
        var v = Main.Session.GetVolume(Dataset);
        var g = v.Geometry;
        int di = Math.Max(1, (int)Math.Ceiling(g.InlineCount / (double)MaxDimension)), dj = Math.Max(1, (int)Math.Ceiling(g.CrosslineCount / (double)MaxDimension)), dk = Math.Max(1, (int)Math.Ceiling(g.SampleCount / (double)MaxDimension));
        int nx = (g.CrosslineCount + dj - 1) / dj, ny = (g.InlineCount + di - 1) / di, nz = (g.SampleCount + dk - 1) / dk;
        var data = new float[(long)nx * ny * nz];
        var tr = new float[g.SampleCount];
        for (int y = 0; y < ny; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < nx; x++)
            {
                v.TryReadTrace(g.Inlines.ValueAt(y * di), g.Crosslines.ValueAt(x * dj), tr);
                for (int z = 0; z < nz; z++) data[((long)(nz - 1 - z) * ny + y) * nx + x] = tr[z * dk];
            }
        }
        return (data, nx, ny, nz, g.Transform.CrosslineSpacing * dj, g.Transform.InlineSpacing * di, g.SampleInterval * dk * 1500 * 2);
    }
}
