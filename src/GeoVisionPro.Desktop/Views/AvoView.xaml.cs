using System.Windows.Controls;
using System.Windows.Media;
using GeoVisionPro.AVO;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class AvoView : UserControl
{
    public AvoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not AvoViewModel vm) return;
            vm.Updated += (_, _) => Draw(vm);
            Draw(vm);
        };
    }

    private void Draw(AvoViewModel vm)
    {
        if (vm.Gather is not null && vm.Geometry is { } g)
        {
            GatherView.SampleToAxis = x => g.TimeAt(x) * 1000; GatherView.AxisToSample = a => g.SampleAt(a / 1000);
            GatherView.SampleLabel = x => (g.TimeAt(x) * 1000).ToString("0"); GatherView.SampleAxisTitle = "TWT (ms)";
            var ang = vm.Angles;
            GatherView.TraceLabel = i => i < ang.Length ? ang[i].ToString("0") + "°" : ""; GatherView.TraceAxisTitle = "Angle"; GatherView.Title = "NMO-corrected gather";
            GatherView.SetSettings(new SeismicDisplaySettings { Mode = SeismicDisplayMode.VariableDensityWiggle, ColorMap = ColorMaps.BlueWhiteRed });
            GatherView.SetData(vm.Gather, false);
            GatherView.Overlays.Clear();
            GatherView.Overlays.Add(new SectionOverlay("Pick", Colors.Yellow, 1.5, [(-0.5, g.SampleAt(vm.PickTimeMs / 1000)), (vm.Gather.Rows - 0.5, g.SampleAt(vm.PickTimeMs / 1000))], true));
            GatherView.RefreshOverlays();
        }
        ChartHelper.Prepare(AmpPlot, "Amplitude vs angle", "sin²θ", "Amplitude");
        if (vm.Angles.Length > 0)
        {
            var s2 = vm.Angles.Select(a => Math.Pow(Math.Sin(a * Math.PI / 180), 2)).ToArray();
            ChartHelper.Points(AmpPlot, s2, vm.Amplitudes, "#3D8BDB", "Picked", 7);
            if (vm.Fit is { } f && double.IsFinite(f.Intercept))
                ChartHelper.Line(AmpPlot, [0, s2.Max()], [f.Intercept, f.Intercept + f.Gradient * s2.Max()], "#FFB300", "A + B sin²θ");
        }
        ChartHelper.Finish(AmpPlot, true);
        var (u, l) = vm.ModelLayers();
        var angles = Enumerable.Range(0, (int)vm.MaxAngle + 1).Select(a => (double)a).ToArray();
        ChartHelper.Prepare(ModelPlot, "Model R(θ)", "Angle (°)", "Rpp");
        ChartHelper.Line(ModelPlot, angles, AvoModeling.Curve(u, l, angles, AvoModeling.Method.Zoeppritz), "#E8EDF4", "Zoeppritz", 2);
        ChartHelper.Line(ModelPlot, angles, AvoModeling.Curve(u, l, angles, AvoModeling.Method.AkiRichards), "#3D8BDB", "Aki-Richards");
        ChartHelper.Line(ModelPlot, angles, AvoModeling.Curve(u, l, angles, AvoModeling.Method.Shuey3), "#20B8A6", "Shuey 3-term");
        ChartHelper.Line(ModelPlot, angles, AvoModeling.Curve(u, l, angles, AvoModeling.Method.Shuey2), "#FFB300", "Shuey 2-term");
        ChartHelper.Finish(ModelPlot, true);
        ChartHelper.Prepare(IgPlot, "Intercept vs gradient", "A", "B");
        if (vm.HorizonIg.Count > 0) ChartHelper.Points(IgPlot, vm.HorizonIg.Select(p => p.A).ToArray(), vm.HorizonIg.Select(p => p.B).ToArray(), "#3D8BDB", "Gathers", 3);
        var (A, B, _) = Reflectivity.ShueyTerms(u, l);
        ChartHelper.Points(IgPlot, [A], [B], "#F07178", $"Model ({AvoAnalysis.Classify(A, B)})", 9);
        ChartHelper.HLine(IgPlot, 0, "#9AA6B8"); ChartHelper.VLine(IgPlot, 0, "#9AA6B8");
        ChartHelper.Finish(IgPlot, true);
    }
}
