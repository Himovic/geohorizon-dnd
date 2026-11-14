using System.Windows;
using ScottPlot;
using ScottPlot.WPF;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Controls;

/// <summary>Central place for ScottPlot usage (theming and common plot types) so the rest of the UI is insulated from its API.</summary>
public static class ChartHelper
{
    public static bool IsDark => (System.Windows.Application.Current?.TryFindResource("BackgroundColor") is System.Windows.Media.Color c) && c.R < 100;

    public static Color C(string hex) => Color.FromHex(hex);
    public static Color C(System.Windows.Media.Color c) => new(c.R, c.G, c.B, c.A);

    public static void Prepare(WpfPlot wp, string title = "", string xLabel = "", string yLabel = "")
    {
        var p = wp.Plot;
        p.Clear();
        if (IsDark)
        {
            p.FigureBackground.Color = C("#202633");
            p.DataBackground.Color = C("#10131A");
            p.Axes.Color(C("#9AA6B8"));
            p.Grid.MajorLineColor = C("#2A3242");
        }
        else
        {
            p.FigureBackground.Color = C("#FFFFFF");
            p.DataBackground.Color = C("#FFFFFF");
            p.Axes.Color(C("#334155"));
            p.Grid.MajorLineColor = C("#E2E8F0");
        }
        p.Title(title);
        p.XLabel(xLabel);
        p.YLabel(yLabel);
    }

    public static ScottPlot.Plottables.Scatter Line(WpfPlot wp, double[] x, double[] y, string color, string label = "", float width = 1.5f)
    {
        var s = wp.Plot.Add.ScatterLine(x, y);
        s.Color = C(color); s.LineWidth = width;
        if (!string.IsNullOrEmpty(label)) s.LegendText = label;
        return s;
    }

    public static ScottPlot.Plottables.Scatter Points(WpfPlot wp, double[] x, double[] y, string color, string label = "", float size = 4)
    {
        var s = wp.Plot.Add.ScatterPoints(x, y);
        s.Color = C(color); s.MarkerSize = size;
        if (!string.IsNullOrEmpty(label)) s.LegendText = label;
        return s;
    }

    /// <summary>Scatter points coloured by a third variable using a colour map (binned into 32 groups for performance).</summary>
    public static void ColoredPoints(WpfPlot wp, double[] x, double[] y, double[] z, ColorMap cmap, float size = 4)
    {
        var valid = Enumerable.Range(0, x.Length).Where(i => double.IsFinite(x[i]) && double.IsFinite(y[i]) && double.IsFinite(z[i])).ToArray();
        if (valid.Length == 0) return;
        double mn = valid.Min(i => z[i]), mx = valid.Max(i => z[i]);
        const int bins = 32;
        foreach (var g in valid.GroupBy(i => mx > mn ? Math.Min(bins - 1, (int)((z[i] - mn) / (mx - mn) * bins)) : 0))
        {
            var (r, gg, b) = cmap.Evaluate((g.Key + 0.5) / bins);
            var s = wp.Plot.Add.ScatterPoints(g.Select(i => x[i]).ToArray(), g.Select(i => y[i]).ToArray());
            s.Color = new Color(r, gg, b);
            s.MarkerSize = size;
        }
    }

    public static void VLine(WpfPlot wp, double x, string color) { var l = wp.Plot.Add.VerticalLine(x); l.Color = C(color); }
    public static void HLine(WpfPlot wp, double y, string color) { var l = wp.Plot.Add.HorizontalLine(y); l.Color = C(color); }

    public static void Finish(WpfPlot wp, bool legend = false, bool invertY = false)
    {
        wp.Plot.Axes.AutoScale();
        if (invertY) wp.Plot.Axes.InvertY();
        if (legend) wp.Plot.ShowLegend(); else wp.Plot.HideLegend();
        wp.Refresh();
    }

    /// <summary>Data coordinates under a WPF mouse position on the plot.</summary>
    public static (double X, double Y) ToData(WpfPlot wp, System.Windows.Point p)
    {
        float s = wp.DisplayScale;
        var c = wp.Plot.GetCoordinates(new Pixel((float)(p.X * s), (float)(p.Y * s)));
        return (c.X, c.Y);
    }

    public static void SavePng(WpfPlot wp, string path, int width = 1400, int height = 900) => wp.Plot.SavePng(path, width, height);
}
