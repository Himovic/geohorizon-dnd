using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class RockPhysicsView : UserControl
{
    public RockPhysicsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not RockPhysicsViewModel vm) return;
            vm.Updated += (_, _) => Draw(vm);
            Draw(vm);
        };
    }

    private void Draw(RockPhysicsViewModel vm)
    {
        ChartHelper.Prepare(Plot, "Rock physics templates (brine-saturated)", "Porosity (v/v)", "Vp (m/s)");
        if (vm.WellPoints is { } wp)
        {
            var idx = Enumerable.Range(0, wp.Phi.Length).Where(i => double.IsFinite(wp.Phi[i]) && double.IsFinite(wp.Vp[i])).Where((_, k) => k % 3 == 0).ToArray();
            ChartHelper.Points(Plot, idx.Select(i => wp.Phi[i]).ToArray(), idx.Select(i => wp.Vp[i]).ToArray(), "#E8EDF4", vm.Well?.Name ?? "", 2);
        }
        try { foreach (var (name, phi, vp, color) in vm.Templates()) ChartHelper.Line(Plot, phi, vp, color, name, 2); }
        catch (Exception e) { vm.Report = e.Message; }
        ChartHelper.Finish(Plot, true);
    }
}
