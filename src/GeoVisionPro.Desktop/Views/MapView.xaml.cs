using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class MapView : UserControl
{
    private MapViewModel? _vm;
    private bool _fitted;

    public MapView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MapViewModel);
        Map.MapMoved += (_, p) => _vm?.OnMoved(p);
        Map.MapClicked += (_, e) => _vm?.OnClicked(e.Location, e.ClickCount);
        Map.PolylineCompleted += (_, pts) => _vm?.OnPolyline(pts);
        Map.SizeChanged += (_, _) => { if (!_fitted && Map.ActualWidth > 0) { Map.ZoomToFit(); _fitted = true; } };
    }

    private void Attach(MapViewModel? vm)
    {
        if (_vm is not null) { _vm.MapChanged -= OnChanged; _vm.PropertyChanged -= OnProp; }
        _vm = vm;
        if (vm is null) return;
        vm.MapChanged += OnChanged; vm.PropertyChanged += OnProp;
        OnChanged(vm, EventArgs.Empty);
    }

    private void OnProp(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapViewModel.DrawArbitrary) && _vm is not null) Map.DrawMode = _vm.DrawArbitrary;
    }

    private void OnChanged(object? s, EventArgs e)
    {
        if (_vm is null) return;
        Map.Polygons.Clear(); Map.Polygons.AddRange(_vm.Polygons);
        Map.Points.Clear(); Map.Points.AddRange(_vm.Points);
        Map.Rasters.Clear(); Map.Rasters.AddRange(_vm.Rasters);
        if (!_fitted && Map.ActualWidth > 0) { Map.ZoomToFit(); _fitted = true; }
        Map.InvalidateVisual();
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => Map.ZoomToFit();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var p = _vm?.Main.Dialogs.SaveFile("Export map", "PNG image (*.png)|*.png", "map.png");
        if (p is not null) BitmapUtil.SavePng(Map, p);
    }
}
