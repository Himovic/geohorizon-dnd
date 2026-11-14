using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Controls;

public enum ViewerTool { Navigate, Pick, Draw, Erase, Measure }

/// <summary>Polyline overlay in section coordinates (trace index, fractional sample index); NaN samples break the line.</summary>
public sealed record SectionOverlay(string Name, Color Color, double Thickness, IReadOnlyList<(double Trace, double Sample)> Points, bool Dashed = false, bool ShowPoints = false);

/// <summary>Vertical marker (e.g. well track) with optional label and value curve.</summary>
public sealed record SectionMarker(string Label, double Trace, double SampleTop, double SampleBottom, Color Color);

/// <summary>
/// High-performance seismic section/slice display. X axis = trace (row of the data), Y axis = sample (column).
/// The image is rasterised by <see cref="SectionRenderer"/> at device resolution for the visible viewport only, so
/// zooming and panning on large sections stay responsive. Supports overlays, markers, crosshair, picking and drawing.
/// </summary>
public sealed class SeismicSectionControl : FrameworkElement
{
    private Array2D? _data;
    private SeismicDisplaySettings _settings = new();
    private (float, float)? _clip;
    private SectionViewport _vp;
    private WriteableBitmap? _image;
    private bool _dirty = true;
    private Point? _dragStart;
    private SectionViewport _dragVp;
    private Point _mouse;
    private readonly List<(double Trace, double Sample)> _draft = [];
    private (double Trace, double Sample)? _measureStart;

    public const double LeftMargin = 62, TopMargin = 26, RightMargin = 12, BottomMargin = 8;

    public SeismicSectionControl()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    public event EventHandler<(double Trace, double Sample, float Amplitude)>? PointerMoved;
    public event EventHandler<(double Trace, double Sample, MouseButton Button, ModifierKeys Modifiers, int ClickCount)>? PointerClicked;
    /// <summary>Raised when a drawn polyline is completed (double click) in Draw mode.</summary>
    public event EventHandler<IReadOnlyList<(double Trace, double Sample)>>? PolylineCompleted;
    public event EventHandler<(double Trace0, double Trace1)>? EraseRequested;
    public event EventHandler? ViewportChanged;

    public ViewerTool Tool { get; set; } = ViewerTool.Navigate;
    public List<SectionOverlay> Overlays { get; } = [];
    public List<SectionMarker> Markers { get; } = [];
    public (double Trace, double Sample)? Crosshair { get; set; }
    /// <summary>Label of trace index (e.g. crossline number).</summary>
    public Func<int, string> TraceLabel { get; set; } = i => i.ToString(CultureInfo.InvariantCulture);
    /// <summary>Label of sample index (e.g. time in ms).</summary>
    public Func<double, string> SampleLabel { get; set; } = s => s.ToString("0", CultureInfo.InvariantCulture);
    /// <summary>Sample-axis value for tick placement (e.g. time ms of a sample) and its inverse.</summary>
    public Func<double, double> SampleToAxis { get; set; } = s => s;
    public Func<double, double> AxisToSample { get; set; } = a => a;
    public string TraceAxisTitle { get; set; } = "Trace";
    public string SampleAxisTitle { get; set; } = "Sample";
    public string? Title { get; set; }
    public Color ColorBarHint { get; set; } = Colors.Transparent;

    public Array2D? Data => _data;
    public SectionViewport Viewport => _vp;
    public SeismicDisplaySettings Settings => _settings;

    public void SetData(Array2D? data, bool resetView)
    {
        bool sizeChanged = _data is null || data is null || data.Rows != _data.Rows || data.Cols != _data.Cols;
        _data = data;
        _clip = null;
        if (data is not null && (resetView || sizeChanged)) _vp = SectionViewport.Full(data);
        _dirty = true;
        InvalidateVisual();
    }

    public void SetSettings(SeismicDisplaySettings s, (float, float)? fixedClip = null)
    {
        _settings = s; _clip = fixedClip; _dirty = true; InvalidateVisual();
    }

    public void ResetView()
    {
        if (_data is null) return;
        _vp = SectionViewport.Full(_data); _dirty = true; InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetViewport(SectionViewport vp) { _vp = vp; _dirty = true; InvalidateVisual(); }

    public void Refresh() { _dirty = true; InvalidateVisual(); }
    public void RefreshOverlays() => InvalidateVisual();

    private Rect PlotRect => new(LeftMargin, TopMargin, Math.Max(1, ActualWidth - LeftMargin - RightMargin), Math.Max(1, ActualHeight - TopMargin - BottomMargin));

    public Point ToScreen(double trace, double sample)
    {
        var r = PlotRect;
        return new Point(r.Left + (trace - _vp.TraceStart) / _vp.TraceSpan * r.Width, r.Top + (sample - _vp.SampleStart) / _vp.SampleSpan * r.Height);
    }

    public (double Trace, double Sample) FromScreen(Point p)
    {
        var r = PlotRect;
        return (_vp.TraceStart + (p.X - r.Left) / r.Width * _vp.TraceSpan, _vp.SampleStart + (p.Y - r.Top) / r.Height * _vp.SampleSpan);
    }

    private static readonly Typeface Face = new("Segoe UI");
    private FormattedText Text(string s, double size, Brush b) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var bg = TryFindResource("ViewerBackgroundBrush") as Brush ?? Brushes.Black;
        var fg = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(TryFindResource("BackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var r = PlotRect;
        dc.DrawRectangle(bg, null, r);
        if (_data is null || _data.Rows == 0)
        {
            dc.DrawText(Text("No data", 13, fg), new Point(r.Left + 10, r.Top + 10));
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)(r.Width * dpi.DpiScaleX)), ph = Math.Max(1, (int)(r.Height * dpi.DpiScaleY));
        if (_dirty || _image is null || _image.PixelWidth != pw || _image.PixelHeight != ph)
        {
            _clip ??= SectionRenderer.ComputeClip(_settings.DisplayAgcSamples > 1 ? SectionRenderer.PrepareForDisplay(_data, _settings) : _data, _settings);
            var px = SectionRenderer.Render(_data, _settings, _vp, pw, ph, _settings.DisplayAgcSamples > 1 ? null : _clip);
            _image = BitmapUtil.ToBitmap(px, pw, ph, 96 * dpi.DpiScaleX);
            _dirty = false;
        }
        dc.PushClip(new RectangleGeometry(r));
        dc.DrawImage(_image, r);
        DrawOverlays(dc, r);
        dc.Pop();
        DrawAxes(dc, r, fg);
    }

    private void DrawOverlays(DrawingContext dc, Rect r)
    {
        foreach (var o in Overlays)
        {
            var pen = new Pen(new SolidColorBrush(o.Color), o.Thickness);
            if (o.Dashed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                double lastTrace = double.NaN;
                foreach (var (t, s) in o.Points)
                {
                    if (double.IsNaN(s) || double.IsNaN(t) || (!double.IsNaN(lastTrace) && Math.Abs(t - lastTrace) > 1.5 && !o.ShowPoints)) { open = false; lastTrace = t; if (double.IsNaN(s)) continue; }
                    var p = ToScreen(t, s);
                    if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                    else ctx.LineTo(p, true, false);
                    lastTrace = t;
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            if (o.ShowPoints)
                foreach (var (t, s) in o.Points)
                    if (!double.IsNaN(s)) dc.DrawEllipse(pen.Brush, null, ToScreen(t, s), 3, 3);
        }
        foreach (var m in Markers)
        {
            var b = new SolidColorBrush(m.Color);
            var top = ToScreen(m.Trace, m.SampleTop); var bot = ToScreen(m.Trace, m.SampleBottom);
            dc.DrawLine(new Pen(b, 2.5), top, bot);
            dc.DrawText(Text(m.Label, 11, b), new Point(top.X + 4, Math.Max(r.Top + 2, top.Y)));
        }
        if (_draft.Count > 0)
        {
            var pen = new Pen(Brushes.Yellow, 1.5) { DashStyle = DashStyles.Dot };
            for (int i = 1; i < _draft.Count; i++) dc.DrawLine(pen, ToScreen(_draft[i - 1].Trace, _draft[i - 1].Sample), ToScreen(_draft[i].Trace, _draft[i].Sample));
            dc.DrawLine(pen, ToScreen(_draft[^1].Trace, _draft[^1].Sample), _mouse);
            foreach (var p in _draft) dc.DrawEllipse(Brushes.Yellow, null, ToScreen(p.Trace, p.Sample), 3, 3);
        }
        if (_measureStart is { } ms)
        {
            var a = ToScreen(ms.Trace, ms.Sample);
            dc.DrawLine(new Pen(Brushes.Cyan, 1.2), a, _mouse);
            var (t1, s1) = FromScreen(_mouse);
            string txt = $"Δtraces {t1 - ms.Trace:0.0}, Δ{SampleAxisTitle} {SampleToAxis(s1) - SampleToAxis(ms.Sample):0.0}";
            dc.DrawText(Text(txt, 11, Brushes.Cyan), new Point(_mouse.X + 8, _mouse.Y - 16));
        }
        if (Crosshair is { } c)
        {
            var p = ToScreen(c.Trace, c.Sample);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 32, 184, 166)), 1) { DashStyle = DashStyles.Dash };
            dc.DrawLine(pen, new Point(p.X, r.Top), new Point(p.X, r.Bottom));
            dc.DrawLine(pen, new Point(r.Left, p.Y), new Point(r.Right, p.Y));
        }
    }

    private void DrawAxes(DrawingContext dc, Rect r, Brush fg)
    {
        var pen = new Pen(fg, 1);
        dc.DrawRectangle(null, pen, r);
        // sample axis (left)
        double a0 = SampleToAxis(_vp.SampleStart), a1 = SampleToAxis(_vp.SampleEnd);
        double step = BitmapUtil.NiceStep(Math.Abs(a1 - a0), Math.Max(2, (int)(r.Height / 45)));
        double first = Math.Ceiling(Math.Min(a0, a1) / step) * step;
        for (double a = first; a <= Math.Max(a0, a1) + 1e-9; a += step)
        {
            double s = AxisToSample(a);
            var p = ToScreen(_vp.TraceStart, s);
            if (p.Y < r.Top - 1 || p.Y > r.Bottom + 1) continue;
            dc.DrawLine(pen, new Point(r.Left - 4, p.Y), new Point(r.Left, p.Y));
            var ft = Text(SampleLabel(s), 10.5, fg);
            dc.DrawText(ft, new Point(r.Left - 6 - ft.Width, p.Y - ft.Height / 2));
        }
        var title = Text(SampleAxisTitle, 10.5, fg);
        dc.PushTransform(new RotateTransform(-90, 10, r.Top + r.Height / 2));
        dc.DrawText(title, new Point(10 - title.Width / 2, r.Top + r.Height / 2 - title.Height / 2));
        dc.Pop();
        // trace axis (top)
        double tstep = BitmapUtil.NiceStep(_vp.TraceSpan, Math.Max(2, (int)(r.Width / 70)));
        double tf = Math.Ceiling(Math.Max(0, _vp.TraceStart) / tstep) * tstep;
        for (double t = tf; t <= _vp.TraceEnd; t += tstep)
        {
            int ti = (int)Math.Round(t);
            if (_data is null || ti < 0 || ti >= _data.Rows) continue;
            var p = ToScreen(ti, _vp.SampleStart);
            dc.DrawLine(pen, new Point(p.X, r.Top - 4), new Point(p.X, r.Top));
            var ft = Text(TraceLabel(ti), 10.5, fg);
            dc.DrawText(ft, new Point(p.X - ft.Width / 2, r.Top - 5 - ft.Height));
        }
        var tt = Text(Title is null ? TraceAxisTitle : $"{Title}   [{TraceAxisTitle}]", 10.5, TryFindResource("TextBrush") as Brush ?? fg);
        dc.DrawText(tt, new Point(2, 2));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_data is null) return;
        double f = e.Delta > 0 ? 0.8 : 1.25;
        var (t, s) = FromScreen(e.GetPosition(this));
        bool zoomTraces = !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool zoomSamples = !Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var v = _vp;
        if (zoomTraces) v = v with { TraceStart = t - (t - v.TraceStart) * f, TraceEnd = t + (v.TraceEnd - t) * f };
        if (zoomSamples) v = v with { SampleStart = s - (s - v.SampleStart) * f, SampleEnd = s + (v.SampleEnd - s) * f };
        if (v.TraceSpan < 2 || v.SampleSpan < 4) return;
        _vp = v; _dirty = true; InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (_data is null) return;
        var pos = e.GetPosition(this);
        if (!PlotRect.Contains(pos)) return;
        var (t, s) = FromScreen(pos);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && Tool == ViewerTool.Navigate))
        {
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Middle) { ResetView(); return; }
            _dragStart = pos; _dragVp = _vp; CaptureMouse();
            if (e.ChangedButton == MouseButton.Left) PointerClicked?.Invoke(this, (t, s, e.ChangedButton, Keyboard.Modifiers, e.ClickCount));
            return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            switch (Tool)
            {
                case ViewerTool.Draw:
                    if (e.ClickCount == 2) { if (_draft.Count >= 1) { _draft.Add((t, s)); PolylineCompleted?.Invoke(this, _draft.ToList()); } _draft.Clear(); }
                    else _draft.Add((t, s));
                    InvalidateVisual();
                    return;
                case ViewerTool.Measure:
                    _measureStart = _measureStart is null ? (t, s) : null;
                    InvalidateVisual();
                    return;
                case ViewerTool.Erase:
                    _dragStart = pos; CaptureMouse();
                    return;
            }
        }
        PointerClicked?.Invoke(this, (t, s, e.ChangedButton, Keyboard.Modifiers, e.ClickCount));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_data is null) return;
        if (_dragStart is { } d && IsMouseCaptured && Tool != ViewerTool.Erase)
        {
            var r = PlotRect;
            double dt = -(_mouse.X - d.X) / r.Width * _dragVp.TraceSpan, ds = -(_mouse.Y - d.Y) / r.Height * _dragVp.SampleSpan;
            _vp = _dragVp with { TraceStart = _dragVp.TraceStart + dt, TraceEnd = _dragVp.TraceEnd + dt, SampleStart = _dragVp.SampleStart + ds, SampleEnd = _dragVp.SampleEnd + ds };
            _dirty = true; InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var (t, s) = FromScreen(_mouse);
        int ti = (int)Math.Round(t), si = (int)Math.Round(s);
        float amp = ti >= 0 && ti < _data.Rows && si >= 0 && si < _data.Cols ? _data[ti, si] : float.NaN;
        PointerMoved?.Invoke(this, (t, s, amp));
        if (_draft.Count > 0 || _measureStart is not null) InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragStart is { } d && Tool == ViewerTool.Erase && e.ChangedButton == MouseButton.Left)
        {
            var (t0, _) = FromScreen(d); var (t1, _) = FromScreen(e.GetPosition(this));
            EraseRequested?.Invoke(this, (Math.Min(t0, t1), Math.Max(t0, t1)));
        }
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _draft.Clear(); _measureStart = null; InvalidateVisual(); }
        if (e.Key == Key.Home) ResetView();
        base.OnKeyDown(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { _dirty = true; base.OnRenderSizeChanged(sizeInfo); }
}
