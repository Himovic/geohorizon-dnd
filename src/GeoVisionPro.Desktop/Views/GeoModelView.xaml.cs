using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class GeoModelView : UserControl
{
    public GeoModelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not GeoModelViewModel vm) return;
            vm.Updated += (_, _) => Draw(vm);
        };
    }

    private void Draw(GeoModelViewModel vm)
    {
        var map = vm.LayerMap();
        if (map is null) { LayerImage.Source = null; return; }
        var (mn, mx) = map.Range();
        var px = SectionRenderer.RenderMap(map, ColorMaps.Viridis, mn, mx, map.NJ, map.NI);
        LayerImage.Source = BitmapUtil.ToBitmap(px, map.NJ, map.NI);
    }
}
