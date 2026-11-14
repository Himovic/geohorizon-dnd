using System.Windows.Controls;
using System.Windows.Media;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class VelocityAnalysisView : UserControl
{
    private VelocityAnalysisViewModel? _vm;
    public VelocityAnalysisView()
    {
        InitializeComponent();
        SemblanceView.Tool = ViewerTool.Pick;
        SemblanceView.PointerClicked += (_, e) =>
        {
            if (_vm?.Geometry is null || _vm.Velocities.Length == 0 || e.Button != System.Windows.Input.MouseButton.Left) return;
            int iv = Math.Clamp((int)Math.Round(e.Trace), 0, _vm.Velocities.Length - 1);
            _vm.AddPick(_vm.Geometry.TimeAt(e.Sample), _vm.Velocities[iv]);
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Updated -= Draw;
            _vm = DataContext as VelocityAnalysisViewModel;
            if (_vm is not null) { _vm.Updated += Draw; Draw(_vm, EventArgs.Empty); }
        };
    }

    private void Draw(object? s, EventArgs e)
    {
        if (_vm?.Geometry is not { } g || _vm.Gather is null || _vm.Semblance is null) return;
        foreach (var v in new[] { GatherView, SemblanceView, NmoView })
        {
            v.SampleToAxis = x => g.TimeAt(x) * 1000; v.AxisToSample = a => g.SampleAt(a / 1000);
            v.SampleLabel = x => (g.TimeAt(x) * 1000).ToString("0"); v.SampleAxisTitle = "TWT (ms)";
        }
        var offs = _vm.Offsets; var vels = _vm.Velocities;
        GatherView.TraceLabel = i => i < offs.Length ? offs[i].ToString("0") : ""; GatherView.TraceAxisTitle = "Offset (m)"; GatherView.Title = "CMP gather";
        NmoView.TraceLabel = GatherView.TraceLabel; NmoView.TraceAxisTitle = "Offset (m)"; NmoView.Title = "NMO corrected";
        SemblanceView.TraceLabel = i => i < vels.Length ? vels[i].ToString("0") : ""; SemblanceView.TraceAxisTitle = "Velocity (m/s)"; SemblanceView.Title = "Semblance";
        var wig = new SeismicDisplaySettings { Mode = SeismicDisplayMode.VariableDensityWiggle, ColorMap = ColorMaps.Greyscale };
        GatherView.SetSettings(wig); NmoView.SetSettings(wig);
        SemblanceView.SetSettings(new SeismicDisplaySettings { ColorMap = ColorMaps.Spectrum, ClipMode = ClipMode.MinMax }, (0f, 1f));
        GatherView.SetData(_vm.Gather, false);
        NmoView.SetData(_vm.Corrected, false);
        SemblanceView.SetData(_vm.Semblance, false);
        SemblanceView.Overlays.Clear();
        if (_vm.Picks.Count > 0)
        {
            double v0 = vels[0], dv = vels.Length > 1 ? vels[1] - vels[0] : 1;
            SemblanceView.Overlays.Add(new SectionOverlay("Picks", Colors.White, 2, _vm.Picks.Select(p => ((p.Velocity - v0) / dv, g.SampleAt(p.Time))).ToList(), false, true));
        }
        SemblanceView.RefreshOverlays();
    }
}
