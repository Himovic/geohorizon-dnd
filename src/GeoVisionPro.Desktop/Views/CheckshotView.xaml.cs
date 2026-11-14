using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class CheckshotView : UserControl
{
    private CheckshotViewModel? _vm;
    public CheckshotView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Updated -= Draw;
            _vm = DataContext as CheckshotViewModel;
            if (_vm is not null) { _vm.Updated += Draw; Draw(_vm, EventArgs.Empty); }
        };
    }

    private void Draw(object? s, EventArgs e)
    {
        if (_vm is null) return;
        ChartHelper.Prepare(TdPlot, "Time-depth", "TWT (ms)", "TVDSS (m)");
        ChartHelper.Prepare(VelPlot, "Velocities", "Velocity (m/s)", "TVDSS (m)");
        ChartHelper.Prepare(DriftPlot, "Sonic drift", "Drift (ms)", "TVDSS (m)");
        if (_vm.CheckshotModel is { } cs)
        {
            ChartHelper.Points(TdPlot, cs.Twt.Select(t => t * 1000).ToArray(), cs.TvdSs, "#FFB300", "Checkshots", 6);
            var iv = cs.IntervalVelocities();
            var ivx = iv.SelectMany(v => new[] { v.Velocity, v.Velocity }).ToArray();
            var ivy = iv.SelectMany(v => new[] { v.TopTvdSs, v.BaseTvdSs }).ToArray();
            if (ivx.Length > 1) ChartHelper.Line(VelPlot, ivx, ivy, "#3D8BDB", "Interval (checkshot)");
            ChartHelper.Line(VelPlot, cs.AverageVelocities(), cs.TvdSs, "#20B8A6", "Average");
            ChartHelper.Line(VelPlot, cs.RmsVelocities(), cs.TvdSs, "#C792EA", "RMS");
        }
        if (_vm.SonicModel is { } sm) ChartHelper.Line(TdPlot, sm.Twt.Select(t => t * 1000).ToArray(), sm.TvdSs, "#9AA6B8", "Integrated sonic", 1);
        if (_vm.Calibration is { } cal)
        {
            ChartHelper.Line(TdPlot, cal.Calibrated.Twt.Select(t => t * 1000).ToArray(), cal.Calibrated.TvdSs, "#3D8BDB", "Calibrated", 1.5f);
            if (cal.DriftPoints.Count > 0) ChartHelper.Line(DriftPlot, cal.DriftPoints.Select(d => d.Drift * 1000).ToArray(), cal.DriftPoints.Select(d => d.TvdSs).ToArray(), "#F07178", "Drift (checkshot − sonic)");
        }
        ChartHelper.Finish(TdPlot, true, true);
        ChartHelper.Finish(VelPlot, true, true);
        ChartHelper.Finish(DriftPlot, true, true);
    }
}
