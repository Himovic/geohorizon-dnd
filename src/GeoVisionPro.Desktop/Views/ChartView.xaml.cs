using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class ChartView : UserControl
{
    public ChartView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Draw();
    }

    private void Draw()
    {
        if (DataContext is not ChartViewModel vm) return;
        ChartHelper.Prepare(Plot, vm.Title, vm.XLabel, vm.YLabel);
        foreach (var (name, x, y, color) in vm.Series) ChartHelper.Line(Plot, x, y, color, name);
        ChartHelper.Finish(Plot, vm.Series.Count > 1, vm.InvertY);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        var p = vm.Main.Dialogs.SaveFile("Export chart", "PNG image (*.png)|*.png", "chart.png");
        if (p is not null) ChartHelper.SavePng(Plot, p);
    }

    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        var p = vm.Main.Dialogs.SaveFile("Export data", "CSV (*.csv)|*.csv", "chart.csv");
        if (p is null) return;
        var sb = new StringBuilder();
        foreach (var (name, x, y, _) in vm.Series)
        {
            sb.AppendLine($"# {name}");
            sb.AppendLine($"{vm.XLabel},{vm.YLabel}");
            for (int i = 0; i < x.Length; i++) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{x[i]},{y[i]}"));
        }
        File.WriteAllText(p, sb.ToString());
    }
}
