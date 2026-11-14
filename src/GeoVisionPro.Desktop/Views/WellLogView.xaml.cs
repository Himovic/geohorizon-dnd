using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class WellLogView : UserControl
{
    private WellLogViewModel? _vm;
    private bool _syncing;

    public WellLogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.WellsChanged -= Build;
            _vm = DataContext as WellLogViewModel;
            if (_vm is not null) { _vm.WellsChanged += Build; Build(_vm, EventArgs.Empty); }
        };
    }

    private void Build(object? s, EventArgs e)
    {
        Panel.Children.Clear();
        if (_vm is null) return;
        var controls = new List<WellLogControl>();
        foreach (var (well, shift) in _vm.Displayed)
        {
            var c = new WellLogControl { FlattenShift = shift, Margin = new Thickness(0, 0, 8, 0) };
            c.SetWell(well);
            c.CursorMoved += (_, r) => _vm.Readout = $"{well.Name}  MD {r.Depth:0.00}  {r.Readout}";
            c.DepthRangeChanged += (sender, _) => Sync((WellLogControl)sender!, controls);
            controls.Add(c);
            Panel.Children.Add(c);
        }
        if (controls.Count > 1)
        {
            double top = controls.Min(c => c.DepthRange.Top), bottom = controls.Max(c => c.DepthRange.Bottom);
            foreach (var c in controls) c.SetDepthRange(top, bottom);
        }
    }

    private void Sync(WellLogControl source, List<WellLogControl> all)
    {
        if (_syncing) return;
        _syncing = true;
        var (t, b) = source.DepthRange;
        foreach (var c in all.Where(c => !ReferenceEquals(c, source))) c.SetDepthRange(t, b);
        _syncing = false;
    }
}
