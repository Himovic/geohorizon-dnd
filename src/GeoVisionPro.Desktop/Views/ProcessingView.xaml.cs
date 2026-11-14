using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.SeismicProcessing;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class ProcessingView : UserControl
{
    private ProcessingViewModel? _vm;
    public ProcessingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PreviewUpdated -= Draw;
            _vm = DataContext as ProcessingViewModel;
            if (_vm is not null) { _vm.PreviewUpdated += Draw; Draw(_vm, EventArgs.Empty); }
        };
        BeforeView.ViewportChanged += (_, _) => AfterView.SetViewport(BeforeView.Viewport);
        AfterView.ViewportChanged += (_, _) => BeforeView.SetViewport(AfterView.Viewport);
    }

    private void Draw(object? s, EventArgs e)
    {
        if (_vm?.Before is null || _vm.After is null) return;
        double dt = _vm.SampleInterval, t0 = _vm.StartTime;
        foreach (var v in new[] { BeforeView, AfterView })
        {
            v.SampleToAxis = x => (t0 + x * dt) * 1000; v.AxisToSample = a => (a / 1000 - t0) / dt;
            v.SampleLabel = x => ((t0 + x * dt) * 1000).ToString("0");
            v.SampleAxisTitle = "TWT (ms)"; v.TraceAxisTitle = _vm.IsPreStack ? "Offset index" : "Crossline index";
        }
        var settings = new SeismicDisplaySettings();
        var clip = SectionRenderer.ComputeClip(_vm.Before, settings);
        BeforeView.Title = "Before"; AfterView.Title = "After";
        BeforeView.SetSettings(settings, clip);
        // the output uses its own clip so amplitude-changing operations (AGC, whitening) remain visible
        AfterView.SetSettings(settings);
        BeforeView.SetData(_vm.Before, false);
        AfterView.SetData(_vm.After, false);
        var (f1, a1) = ProcessingEngine.AverageSpectrum(_vm.Before, dt);
        var (f2, a2) = ProcessingEngine.AverageSpectrum(_vm.After, dt);
        ChartHelper.Prepare(Spectrum, "", "Frequency (Hz)", "Amplitude (normalised)");
        if (a1.Length > 0) ChartHelper.Line(Spectrum, f1, a1.Select(v => v / Math.Max(1e-30, a1.Max())).ToArray(), "#9AA6B8", "Before");
        if (a2.Length > 0) ChartHelper.Line(Spectrum, f2, a2.Select(v => v / Math.Max(1e-30, a2.Max())).ToArray(), "#3D8BDB", "After");
        ChartHelper.Finish(Spectrum, true);
    }
}
