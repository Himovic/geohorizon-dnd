using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GeoVisionPro.Crossplot;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class CrossplotView : UserControl
{
    private CrossplotViewModel? _vm;
    private static readonly string[] Palette = ["#3D8BDB", "#20B8A6", "#FFB300", "#F07178", "#C792EA", "#AED581", "#FF7043", "#4FC3F7"];

    public CrossplotView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) { _vm.Updated -= Draw; _vm.PropertyChanged -= OnProp; }
            _vm = DataContext as CrossplotViewModel;
            if (_vm is not null) { _vm.Updated += Draw; _vm.PropertyChanged += OnProp; Draw(_vm, EventArgs.Empty); }
        };
        Plot.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_vm is null || !_vm.Selecting) return;
            var (x, y) = ChartHelper.ToData(Plot, e.GetPosition(Plot));
            _vm.AddPolygonPoint(x, y);
            e.Handled = true;
        };
    }

    private void OnProp(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CrossplotViewModel.Selecting) && _vm is not null)
        {
            if (_vm.Selecting) Plot.UserInputProcessor.Disable(); else Plot.UserInputProcessor.Enable();
        }
        if (e.PropertyName == nameof(CrossplotViewModel.ShowRegression)) Draw(_vm, EventArgs.Empty);
    }

    private void Draw(object? s, EventArgs e)
    {
        if (_vm?.Data is not { } d) { ChartHelper.Prepare(Plot); Plot.Refresh(); return; }
        ChartHelper.Prepare(Plot, $"{d.YName} vs {d.XName}", d.XName, d.YName);
        if (_vm.Labels is { } labels && _vm.LabelNames is { } names)
        {
            for (int c = 0; c < names.Length; c++)
            {
                var idx = Enumerable.Range(0, d.Count).Where(i => labels[i] == c).ToArray();
                if (idx.Length > 0) ChartHelper.Points(Plot, idx.Select(i => d.X[i]).ToArray(), idx.Select(i => d.Y[i]).ToArray(), Palette[c % Palette.Length], names[c], 3);
            }
        }
        else if (d.Z is not null) ChartHelper.ColoredPoints(Plot, d.X, d.Y, d.Z, ColorMaps.Spectrum, 3);
        else ChartHelper.Points(Plot, d.X, d.Y, "#3D8BDB", "", 3);
        if (_vm.Selection is { Length: > 0 } sel)
            ChartHelper.Points(Plot, sel.Select(i => d.X[i]).ToArray(), sel.Select(i => d.Y[i]).ToArray(), "#FFFFFF", "Selection", 5);
        if (_vm.Polygon.Count > 0)
            ChartHelper.Line(Plot, _vm.Polygon.Select(p => p.X).Append(_vm.Polygon[0].X).ToArray(), _vm.Polygon.Select(p => p.Y).Append(_vm.Polygon[0].Y).ToArray(), "#FFFF00", "", 1.5f);
        if (_vm.ShowRegression)
        {
            var st = CrossplotAnalytics.Statistics(d, _vm.Selection);
            var xs = d.ValidIndices().Select(i => d.X[i]).ToArray();
            if (xs.Length > 1 && double.IsFinite(st.Fit.Slope))
                ChartHelper.Line(Plot, [xs.Min(), xs.Max()], [st.Fit.Evaluate(xs.Min()), st.Fit.Evaluate(xs.Max())], "#F07178", $"Fit (R² {st.Fit.RSquared:0.00})", 2);
        }
        ChartHelper.Finish(Plot, true);
    }

    private void Png_Click(object sender, RoutedEventArgs e)
    {
        var p = _vm?.Main.Dialogs.SaveFile("Export crossplot", "PNG (*.png)|*.png", "crossplot.png");
        if (p is not null) ChartHelper.SavePng(Plot, p);
    }
}
