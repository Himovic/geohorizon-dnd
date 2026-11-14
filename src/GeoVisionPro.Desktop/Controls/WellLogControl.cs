using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GeoVisionPro.Domain;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Desktop.Controls;

/// <summary>A curve shown in a track with its own scale (Min left, Max right; reversed when Min &gt; Max).</summary>
public sealed record TrackCurve(LogCurve Curve, double Min, double Max, Color Color, bool Logarithmic = false, bool FillLeft = false, Color? FillColor = null);

public sealed class LogTrack
{
    public string Title { get; set; } = "";
    public double Width { get; set; } = 120;
    public List<TrackCurve> Curves { get; } = [];
    public bool Logarithmic { get; set; }
}

/// <summary>
/// Multi-track well log display (depth increasing downward) with independent curve scales, logarithmic resistivity tracks,
/// shading, formation tops and an optional depth shift used to flatten correlation panels on a marker.
/// </summary>
public sealed class WellLogControl : FrameworkElement
{
    private double _top = double.NaN, _bottom = double.NaN;
    private Point? _drag; private double _dragTop, _dragBottom;
    private double? _cursorDepth;
    public const double HeaderHeight = 54, DepthTrackWidth = 54;

    public Well? Well { get; private set; }
    public List<LogTrack> Tracks { get; } = [];
    /// <summary>Depth shift applied to the displayed depth (flattening): displayed = MD − Shift.</summary>
    public double FlattenShift { get; set; }
    public event EventHandler<(double Depth, string Readout)>? CursorMoved;
    public event EventHandler<double>? DepthRangeChanged;

    public WellLogControl() { ClipToBounds = true; Focusable = true; }

    public void SetWell(Well? w, bool autoTracks = true)
    {
        Well = w;
        Tracks.Clear();
        if (w is not null && autoTracks) BuildDefaultTracks(w);
        if (w is not null && w.Curves.Count > 0)
        {
            _top = w.Curves.Min(c => c.Depths.FirstOrDefault()) - FlattenShift;
            _bottom = w.Curves.Max(c => c.Depths.LastOrDefault()) - FlattenShift;
        }
        InvalidateMeasure(); InvalidateVisual();
    }

    public void SetDepthRange(double top, double bottom) { _top = top; _bottom = bottom; InvalidateVisual(); }
    public (double Top, double Bottom) DepthRange => (_top, _bottom);

    public void BuildDefaultTracks(Well w)
    {
        void Add(string title, double width, bool log, params (CurveFamily F, double Min, double Max, string Color, bool Fill)[] specs)
        {
            var t = new LogTrack { Title = title, Width = width, Logarithmic = log };
            foreach (var (f, mn, mx, col, fill) in specs)
                foreach (var c in w.Curves.Where(c => c.Family == f).Take(2))
                    t.Curves.Add(new TrackCurve(c, mn, mx, BitmapUtil.ParseColor(col, Colors.White), log, fill, fill ? Color.FromArgb(90, 230, 190, 60) : null));
            if (t.Curves.Count > 0) Tracks.Add(t);
        }
        Add("Lithology", 120, false, (CurveFamily.GammaRay, 0, 150, "#7BD88F", true), (CurveFamily.Caliper, 6, 16, "#9AA6B8", false), (CurveFamily.SpontaneousPotential, -100, 20, "#C792EA", false));
        Add("Resistivity", 120, true, (CurveFamily.ResistivityDeep, 0.2, 2000, "#F07178", false), (CurveFamily.ResistivityMedium, 0.2, 2000, "#FFCB6B", false), (CurveFamily.ResistivityShallow, 0.2, 2000, "#82AAFF", false));
        Add("Porosity", 130, false, (CurveFamily.Density, 1.95, 2.95, "#F07178", false), (CurveFamily.NeutronPorosity, 0.45, -0.15, "#82AAFF", false), (CurveFamily.PhotoElectric, 0, 10, "#C3E88D", false));
        Add("Sonic", 110, false, (CurveFamily.Sonic, 140, 40, "#89DDFF", false), (CurveFamily.ShearSonic, 300, 80, "#FFCB6B", false));
        Add("Elastic", 110, false, (CurveFamily.AcousticImpedance, 3000, 12000, "#F78C6C", false), (CurveFamily.VpVs, 1.4, 2.6, "#C792EA", false));
        Add("Petrophysics", 120, false, (CurveFamily.ShaleVolume, 0, 1, "#9AA6B8", false), (CurveFamily.EffectivePorosity, 0.5, 0, "#82AAFF", false), (CurveFamily.WaterSaturation, 1, 0, "#20B8A6", false));
        var others = w.Curves.Where(c => c.Family is CurveFamily.Unknown or CurveFamily.Other or CurveFamily.Lithology or CurveFamily.TotalPorosity or CurveFamily.PVelocity or CurveFamily.SVelocity).Take(3).ToList();
        if (others.Count > 0)
        {
            var t = new LogTrack { Title = "Other", Width = 110 };
            string[] cols = ["#E8EDF4", "#FFCB6B", "#89DDFF"];
            for (int i = 0; i < others.Count; i++)
            {
                var s = LogConditioning.Summarize(others[i]);
                t.Curves.Add(new TrackCurve(others[i], s.Min, s.Max, BitmapUtil.ParseColor(cols[i], Colors.White)));
            }
            Tracks.Add(t);
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(DepthTrackWidth + Tracks.Sum(t => t.Width) + 4, double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    private double DepthToY(double d) => HeaderHeight + (d - _top) / Math.Max(1e-9, _bottom - _top) * (ActualHeight - HeaderHeight);
    private double YToDepth(double y) => _top + (y - HeaderHeight) / Math.Max(1, ActualHeight - HeaderHeight) * (_bottom - _top);

    private static readonly Typeface Face = new("Segoe UI");
    private FormattedText T(string s, double size, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static double XFor(double v, TrackCurve c, double x0, double w)
    {
        if (double.IsNaN(v)) return double.NaN;
        double f = c.Logarithmic ? (Math.Log10(Math.Max(v, 1e-6)) - Math.Log10(c.Min)) / (Math.Log10(c.Max) - Math.Log10(c.Min)) : (v - c.Min) / (c.Max - c.Min);
        return x0 + Math.Clamp(f, -0.02, 1.02) * w;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("ViewerBackgroundBrush") as Brush ?? Brushes.Black;
        var fg = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        var text = TryFindResource("TextBrush") as Brush ?? Brushes.White;
        var border = new Pen(TryFindResource("BorderBrush") as Brush ?? Brushes.Gray, 1);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Well is null || double.IsNaN(_top)) { dc.DrawText(T("No well", 12, fg), new Point(8, 8)); return; }
        dc.DrawText(T(Well.Name, 12.5, text), new Point(4, 2));
        // depth track
        double step = BitmapUtil.NiceStep(_bottom - _top, Math.Max(2, (int)((ActualHeight - HeaderHeight) / 50)));
        for (double d = Math.Ceiling(_top / step) * step; d <= _bottom; d += step)
        {
            double y = DepthToY(d);
            dc.DrawLine(border, new Point(DepthTrackWidth - 6, y), new Point(DepthTrackWidth, y));
            dc.DrawText(T((d + FlattenShift).ToString("0", CultureInfo.InvariantCulture), 10, fg), new Point(4, y - 7));
        }
        double x = DepthTrackWidth;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        foreach (var track in Tracks)
        {
            var area = new Rect(x, HeaderHeight, track.Width, Math.Max(0, ActualHeight - HeaderHeight));
            dc.DrawRectangle(null, border, area);
            // grid lines
            for (double d = Math.Ceiling(_top / step) * step; d <= _bottom; d += step)
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(35, 160, 170, 190)), 1), new Point(x, DepthToY(d)), new Point(x + track.Width, DepthToY(d)));
            double hy = 18;
            foreach (var c in track.Curves)
            {
                var b = new SolidColorBrush(c.Color);
                dc.DrawText(T($"{c.Curve.Mnemonic} [{c.Curve.Unit}]", 9.5, b), new Point(x + 3, hy));
                dc.DrawText(T(c.Min.ToString("G3", CultureInfo.InvariantCulture), 8.5, fg), new Point(x + 2, hy + 11));
                var mx = T(c.Max.ToString("G3", CultureInfo.InvariantCulture), 8.5, fg);
                dc.DrawText(mx, new Point(x + track.Width - mx.Width - 2, hy + 11));
                hy += 0; // single header row per curve stacked by colour
                DrawCurve(dc, c, x, track.Width);
            }
            dc.DrawText(T(track.Title, 9, fg), new Point(x + 3, 4 + 0 * hy));
            x += track.Width;
        }
        // tops
        foreach (var top in Well.Tops)
        {
            double y = DepthToY(top.MeasuredDepth - FlattenShift);
            if (y < HeaderHeight || y > ActualHeight) continue;
            var b = BitmapUtil.Brush(top.Color);
            dc.DrawLine(new Pen(b, 1.4) { DashStyle = DashStyles.Dash }, new Point(DepthTrackWidth, y), new Point(x, y));
            dc.DrawText(T(top.Name, 10, b), new Point(x - 4 - T(top.Name, 10, b).Width, y - 14));
        }
        if (_cursorDepth is { } cd)
        {
            double y = DepthToY(cd);
            dc.DrawLine(new Pen(Brushes.Yellow, 0.8), new Point(DepthTrackWidth, y), new Point(x, y));
        }
        dc.Pop();
    }

    private void DrawCurve(DrawingContext dc, TrackCurve c, double x0, double w)
    {
        var d = c.Curve.Depths; var v = c.Curve.Values;
        if (d.Length == 0) return;
        int i0 = Math.Max(0, LowerBound(d, _top + FlattenShift) - 1), i1 = Math.Min(d.Length - 1, LowerBound(d, _bottom + FlattenShift) + 1);
        int stride = Math.Max(1, (i1 - i0) / Math.Max(200, (int)(ActualHeight * 2)));
        var geo = new StreamGeometry();
        var fill = c.FillLeft && c.FillColor is not null ? new StreamGeometry() : null;
        using (var ctx = geo.Open())
        {
            bool open = false;
            for (int i = i0; i <= i1; i += stride)
            {
                double xx = XFor(v[i], c, x0, w);
                if (double.IsNaN(xx)) { open = false; continue; }
                var p = new Point(xx, DepthToY(d[i] - FlattenShift));
                if (!open) { ctx.BeginFigure(p, false, false); open = true; } else ctx.LineTo(p, true, false);
            }
        }
        if (fill is not null)
        {
            using var f = fill.Open();
            bool open = false; Point last = default;
            for (int i = i0; i <= i1; i += stride)
            {
                double xx = XFor(v[i], c, x0, w);
                if (double.IsNaN(xx)) { if (open) { f.LineTo(new Point(x0, last.Y), false, false); open = false; } continue; }
                var p = new Point(xx, DepthToY(d[i] - FlattenShift));
                if (!open) { f.BeginFigure(new Point(x0, p.Y), true, true); open = true; }
                f.LineTo(p, false, false); last = p;
            }
            if (open) f.LineTo(new Point(x0, last.Y), false, false);
            dc.DrawGeometry(new SolidColorBrush(c.FillColor!.Value), null, fill);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(c.Color), 1.1), geo);
    }

    private static int LowerBound(double[] a, double v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (double.IsNaN(_top)) return;
        double d = YToDepth(e.GetPosition(this).Y);
        double f = e.Delta > 0 ? 0.8 : 1.25;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _top = d - (d - _top) * f; _bottom = d + (_bottom - d) * f; }
        else { double s = (_bottom - _top) * (e.Delta > 0 ? -0.1 : 0.1); _top += s; _bottom += s; }
        InvalidateVisual();
        DepthRangeChanged?.Invoke(this, _top);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e) { Focus(); _drag = e.GetPosition(this); _dragTop = _top; _dragBottom = _bottom; CaptureMouse(); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { _drag = null; if (IsMouseCaptured) ReleaseMouseCapture(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag is { } d && IsMouseCaptured)
        {
            double dd = -(p.Y - d.Y) / Math.Max(1, ActualHeight - HeaderHeight) * (_dragBottom - _dragTop);
            _top = _dragTop + dd; _bottom = _dragBottom + dd;
            InvalidateVisual();
            DepthRangeChanged?.Invoke(this, _top);
            return;
        }
        if (Well is null || p.Y < HeaderHeight) return;
        double depth = YToDepth(p.Y);
        _cursorDepth = depth;
        var md = depth + FlattenShift;
        var readout = string.Join("  ", Tracks.SelectMany(t => t.Curves).Select(c => $"{c.Curve.Mnemonic}={c.Curve.ValueAtDepth(md):G4}"));
        CursorMoved?.Invoke(this, (md, readout));
        InvalidateVisual();
    }
}
