using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GeoVisionPro.Desktop.Controls;

public enum TrackStyle { Line, Wiggle, Stems, Blocky }

/// <summary>A time-indexed series displayed in one track (optionally repeated to emulate a trace panel).</summary>
public sealed record TimeSeriesTrack(string Title, float[] Values, double StartTime, double SampleInterval, Color Color, TrackStyle Style = TrackStyle.Line,
    int Repeat = 1, double? Min = null, double? Max = null, float[][]? Panel = null);

/// <summary>
/// Synchronised time tracks for well ties: logs in time, reflectivity stems, synthetic and seismic wiggle panels sharing a
/// vertical time axis (TWT in ms, increasing downward). Time window can be zoomed with the wheel and panned by dragging.
/// </summary>
public sealed class TimeTracksControl : FrameworkElement
{
    private double _t0 = double.NaN, _t1 = double.NaN;
    private Point? _drag; private double _d0, _d1;
    public List<TimeSeriesTrack> Tracks { get; } = [];
    public List<(string Label, double Time, Color Color)> Markers { get; } = [];
    public double TrackWidth { get; set; } = 95;
    public (double Start, double End)? HighlightWindow { get; set; }
    public event EventHandler<double>? TimeClicked;
    private const double Header = 34, Axis = 50;

    public TimeTracksControl() { ClipToBounds = true; Focusable = true; }

    public void SetTimeRange(double t0, double t1) { _t0 = t0; _t1 = t1; InvalidateVisual(); }
    public void AutoRange()
    {
        if (Tracks.Count == 0) return;
        _t0 = Tracks.Min(t => t.StartTime);
        _t1 = Tracks.Max(t => t.StartTime + (Math.Max(t.Values.Length, t.Panel?.FirstOrDefault()?.Length ?? 0) - 1) * t.SampleInterval);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size s) => new(Axis + Tracks.Sum(t => TrackWidth * (t.Panel is null ? 1 : 1.6)) + 4, double.IsInfinity(s.Height) ? 400 : s.Height);

    private double Y(double t) => Header + (t - _t0) / Math.Max(1e-9, _t1 - _t0) * (ActualHeight - Header);
    private double T(double y) => _t0 + (y - Header) / Math.Max(1, ActualHeight - Header) * (_t1 - _t0);
    private static readonly Typeface Face = new("Segoe UI");
    private FormattedText Txt(string s, double size, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("ViewerBackgroundBrush") as Brush ?? Brushes.Black;
        var fg = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        var border = new Pen(TryFindResource("BorderBrush") as Brush ?? Brushes.Gray, 1);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Tracks.Count == 0 || double.IsNaN(_t0)) { dc.DrawText(Txt("No data", 12, fg), new Point(8, 8)); return; }
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        if (HighlightWindow is { } hw)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 61, 139, 219)), null, new Rect(Axis, Y(hw.Start), ActualWidth - Axis, Math.Max(0, Y(hw.End) - Y(hw.Start))));
        double step = BitmapUtil.NiceStep((_t1 - _t0) * 1000, Math.Max(2, (int)((ActualHeight - Header) / 45))) / 1000;
        for (double t = Math.Ceiling(_t0 / step) * step; t <= _t1; t += step)
        {
            double y = Y(t);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(35, 160, 170, 190)), 1), new Point(Axis, y), new Point(ActualWidth, y));
            dc.DrawText(Txt((t * 1000).ToString("0", CultureInfo.InvariantCulture), 10, fg), new Point(4, y - 7));
        }
        dc.DrawText(Txt("TWT ms", 9.5, fg), new Point(4, 4));
        double x = Axis;
        foreach (var tr in Tracks)
        {
            double w = TrackWidth * (tr.Panel is null ? 1 : 1.6);
            dc.DrawRectangle(null, border, new Rect(x, Header, w, Math.Max(0, ActualHeight - Header)));
            dc.DrawText(Txt(tr.Title, 10, new SolidColorBrush(tr.Color)), new Point(x + 3, 4));
            if (tr.Panel is { Length: > 0 } panel)
            {
                double amp = panel.SelectMany(p => p).Where(v => !float.IsNaN(v)).Select(Math.Abs).DefaultIfEmpty(1).Max();
                double tw = w / (panel.Length + 1);
                for (int k = 0; k < panel.Length; k++) DrawWiggle(dc, panel[k], tr.StartTime, tr.SampleInterval, x + tw * (k + 1), tw * 1.4 / Math.Max(1e-12, amp), tr.Color);
            }
            else
            {
                var vals = tr.Values.Where(v => !float.IsNaN(v)).ToArray();
                if (vals.Length > 0)
                {
                    double mn = tr.Min ?? vals.Min(), mx = tr.Max ?? vals.Max();
                    if (tr.Style is TrackStyle.Wiggle or TrackStyle.Stems) { double a = Math.Max(Math.Abs(mn), Math.Abs(mx)); mn = -a; mx = a; }
                    if (mx - mn < 1e-12) { mx = mn + 1; }
                    dc.DrawText(Txt($"{mn:G3}", 8.5, fg), new Point(x + 2, 18));
                    var mxT = Txt($"{mx:G3}", 8.5, fg);
                    dc.DrawText(mxT, new Point(x + w - mxT.Width - 2, 18));
                    if (tr.Style is TrackStyle.Wiggle)
                    {
                        double tw = w / (tr.Repeat + 1);
                        for (int k = 0; k < tr.Repeat; k++) DrawWiggle(dc, tr.Values, tr.StartTime, tr.SampleInterval, x + tw * (k + 1), tw * 1.4 / (mx), tr.Color);
                    }
                    else DrawSeries(dc, tr, x, w, mn, mx);
                }
            }
            x += w;
        }
        foreach (var (label, time, color) in Markers)
        {
            double y = Y(time);
            var b = new SolidColorBrush(color);
            dc.DrawLine(new Pen(b, 1) { DashStyle = DashStyles.Dash }, new Point(Axis, y), new Point(x, y));
            dc.DrawText(Txt(label, 9.5, b), new Point(Axis + 2, y - 13));
        }
        dc.Pop();
    }

    private void DrawSeries(DrawingContext dc, TimeSeriesTrack tr, double x0, double w, double mn, double mx)
    {
        var pen = new Pen(new SolidColorBrush(tr.Color), 1.1);
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            bool open = false;
            double zeroX = x0 + (0 - mn) / (mx - mn) * w;
            for (int i = 0; i < tr.Values.Length; i++)
            {
                double t = tr.StartTime + i * tr.SampleInterval;
                if (t < _t0 - tr.SampleInterval || t > _t1 + tr.SampleInterval) { open = false; continue; }
                float v = tr.Values[i];
                if (float.IsNaN(v)) { open = false; continue; }
                double xx = x0 + (v - mn) / (mx - mn) * w, y = Y(t);
                if (tr.Style == TrackStyle.Stems)
                {
                    if (v != 0) { c.BeginFigure(new Point(zeroX, y), false, false); c.LineTo(new Point(xx, y), true, false); }
                    continue;
                }
                if (tr.Style == TrackStyle.Blocky && open) c.LineTo(new Point(xx, Y(t - tr.SampleInterval / 2)), true, false);
                var p = new Point(xx, tr.Style == TrackStyle.Blocky ? Y(t - tr.SampleInterval / 2) : y);
                if (!open) { c.BeginFigure(p, false, false); open = true; } else c.LineTo(p, true, false);
            }
        }
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawWiggle(DrawingContext dc, float[] v, double t0, double dt, double xc, double scale, Color color)
    {
        var geo = new StreamGeometry(); var fill = new StreamGeometry();
        using (var c = geo.Open())
        using (var f = fill.Open())
        {
            bool open = false;
            for (int i = 0; i < v.Length; i++)
            {
                double t = t0 + i * dt;
                if (t < _t0 - dt || t > _t1 + dt || float.IsNaN(v[i])) { open = false; continue; }
                var p = new Point(xc + v[i] * scale, Y(t));
                if (!open) { c.BeginFigure(p, false, false); open = true; } else c.LineTo(p, true, false);
                if (v[i] > 0 && i + 1 < v.Length && !float.IsNaN(v[i + 1]))
                {
                    f.BeginFigure(new Point(xc, Y(t)), true, true);
                    f.LineTo(p, false, false);
                    f.LineTo(new Point(xc + Math.Max(0, v[i + 1]) * scale, Y(t + dt)), false, false);
                    f.LineTo(new Point(xc, Y(t + dt)), false, false);
                }
            }
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(170, color.R, color.G, color.B)), null, fill);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 0.9), geo);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (double.IsNaN(_t0)) return;
        double t = T(e.GetPosition(this).Y), f = e.Delta > 0 ? 0.8 : 1.25;
        _t0 = t - (t - _t0) * f; _t1 = t + (_t1 - t) * f;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { TimeClicked?.Invoke(this, T(e.GetPosition(this).Y)); return; }
        _drag = e.GetPosition(this); _d0 = _t0; _d1 = _t1; CaptureMouse();
    }
    protected override void OnMouseUp(MouseButtonEventArgs e) { _drag = null; if (IsMouseCaptured) ReleaseMouseCapture(); }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag is not { } d || !IsMouseCaptured) return;
        double dt = -(e.GetPosition(this).Y - d.Y) / Math.Max(1, ActualHeight - Header) * (_d1 - _d0);
        _t0 = _d0 + dt; _t1 = _d1 + dt;
        InvalidateVisual();
    }
}
