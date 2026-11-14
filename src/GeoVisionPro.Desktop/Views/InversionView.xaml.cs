using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class InversionView : UserControl
{
    public InversionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not InversionViewModel vm) return;
            vm.Updated += (_, _) => Draw(vm);
            Draw(vm);
        };
    }

    private void Draw(InversionViewModel vm)
    {
        ChartHelper.Prepare(QcPlot, "Impedance at QC well", "AI (m/s·g/cc)", "TWT (ms)");
        if (vm.QcCurves is { } q)
        {
            ChartHelper.Line(QcPlot, q.Well, q.T, "#9AA6B8", "Well (blocked)", 1);
            ChartHelper.Line(QcPlot, q.Model, q.T, "#20B8A6", "LF model", 1.5f);
            ChartHelper.Line(QcPlot, q.Inverted, q.T, "#3D8BDB", "Inverted (estimate)", 1.8f);
        }
        ChartHelper.Finish(QcPlot, true, true);
        ChartHelper.Prepare(ElasticPlot, "Simultaneous inversion (estimates)", "Normalised value", "TWT (ms)");
        if (vm.Elastic is { } e)
        {
            var t = Enumerable.Range(0, e.Ip.Length).Select(k => (vm.ElasticT0 + k * vm.ElasticDt) * 1000).ToArray();
            double[] N(float[] v) { double m = v.Where(float.IsFinite).DefaultIfEmpty(1).Average(); return v.Select(x => x / m).ToArray(); }
            ChartHelper.Line(ElasticPlot, N(e.Ip), t, "#3D8BDB", "Ip");
            ChartHelper.Line(ElasticPlot, N(e.Is), t, "#20B8A6", "Is");
            ChartHelper.Line(ElasticPlot, N(e.Density), t, "#F07178", "ρ");
            ChartHelper.Line(ElasticPlot, N(e.VpVs), t, "#FFB300", "Vp/Vs");
        }
        ChartHelper.Finish(ElasticPlot, true, true);
    }
}
