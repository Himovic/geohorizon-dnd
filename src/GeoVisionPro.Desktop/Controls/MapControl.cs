using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GeoVisionPro.Core.Spatial;

namespace GeoVisionPro.Desktop.Controls;

public sealed record MapPolygon(string Name, IReadOnlyList<Point2D> Points, Color Stroke, bool Closed = true, double Thickness = 1.5);
public sealed record MapPoint(string Label, Point2D Location, Color Color);
/// <summary>A raster draped on the map: an image whose pixel (0,0) corner maps to C00, (w,0) to C10, (0,h) to C01.</summary>
public sealed record MapRaster(BitmapSource Image, Point2D C00, Point2D C10, Point2D C01, double Opacity = 1);

/// <summary>
/// Base map in projected coordinates (X east, Y north) with equal-aspect scaling, survey outlines, wells, rasters
/// (horizon/attribute maps) and polyline digitising (arbitrary lines, polygons).
/// </summary>
public sealed class MapControl : FrameworkElement
{
    private double _cx, _cy, _scale = 1; // map centre and pixels per map unit
    private Point? _drag; private double _dcx, _dcy;
    private readonly List<Point2D> _draft = [];
    private Point _mouse;

    public List<MapPolygon> Polygons { get; } = [];
    public List<MapPoint> Points { get; } = [];
    public List<MapRaster> Rasters { get; } = [];
    public bool DrawMode { get; set; }
    public Point2D? Highlight { get; set; }
    public event EventHandler<Point2D>? MapMoved;
    public event EventHandler<(Point2D Location, int ClickCount)>? MapClicked;
    public event EventHandler<IReadOnlyList<Point2D>>? PolylineCompleted;

    public MapControl() { ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross; }

    public void ZoomToFit()
    {
        var pts = Polygons.SelectMany(p => p.Points).Concat(Points.Select(p => p.Location)).ToList();
        if (pts.Count == 0 || ActualWidth <= 0) return;
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        _cx = (x0 + x1) / 2; _cy = (y0 + y1) / 2;
        _scale = Math.Min((ActualWidth - 60) / Math.Max(1, x1 - x0), (ActualHeight - 60) / Math.Max(1, y1 - y0));
        InvalidateVisual();
    }

    public Point ToScreen(Point2D p) => new(ActualWidth / 2 + (p.X - _cx) * _scale, ActualHeight / 2 - (p.Y - _cy) * _scale);
    public Point2D ToMap(Point s) => new(_cx + (s.X - ActualWidth / 2) / _scale, _cy - (s.Y - ActualHeight / 2) / _scale);

    private static readonly Typeface Face = new("Segoe UI");

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(TryFindResource("ViewerBackgroundBrush") as Brush ?? Brushes.Black, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var fg = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;
        foreach (var r in Rasters)
        {
            var o = ToScreen(r.C00); var ax = ToScreen(r.C10); var ay = ToScreen(r.C01);
            var m = new Matrix((ax.X - o.X) / r.Image.PixelWidth, (ax.Y - o.Y) / r.Image.PixelWidth, (ay.X - o.X) / r.Image.PixelHeight, (ay.Y - o.Y) / r.Image.PixelHeight, o.X, o.Y);
            dc.PushTransform(new MatrixTransform(m));
            dc.PushOpacity(r.Opacity);
            dc.DrawImage(r.Image, new Rect(0, 0, r.Image.PixelWidth, r.Image.PixelHeight));
            dc.Pop(); dc.Pop();
        }
        // grid
        double step = BitmapUtil.NiceStep(ActualWidth / Math.Max(_scale, 1e-12), 6);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 160, 170, 190)), 1);
        var tl = ToMap(new Point(0, 0)); var br = ToMap(new Point(ActualWidth, ActualHeight));
        for (double x = Math.Floor(tl.X / step) * step; x <= br.X; x += step)
        {
            var p = ToScreen(new Point2D(x, 0));
            dc.DrawLine(gridPen, new Point(p.X, 0), new Point(p.X, ActualHeight));
            dc.DrawText(new FormattedText(x.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, fg, 1.0), new Point(p.X + 2, ActualHeight - 14));
        }
        for (double y = Math.Floor(br.Y / step) * step; y <= tl.Y; y += step)
        {
            var p = ToScreen(new Point2D(0, y));
            dc.DrawLine(gridPen, new Point(0, p.Y), new Point(ActualWidth, p.Y));
            dc.DrawText(new FormattedText(y.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 9.5, fg, 1.0), new Point(2, p.Y - 13));
        }
        foreach (var poly in Polygons)
        {
            if (poly.Points.Count < 2) continue;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(ToScreen(poly.Points[0]), false, poly.Closed);
                foreach (var p in poly.Points.Skip(1)) c.LineTo(ToScreen(p), true, false);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(poly.Stroke), poly.Thickness), geo);
            var lp = ToScreen(poly.Points[0]);
            dc.DrawText(new FormattedText(poly.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, new SolidColorBrush(poly.Stroke), 1.0), new Point(lp.X + 3, lp.Y - 14));
        }
        foreach (var pt in Points)
        {
            var p = ToScreen(pt.Location);
            var b = new SolidColorBrush(pt.Color);
            dc.DrawEllipse(b, new Pen(Brushes.Black, 1), p, 4.5, 4.5);
            dc.DrawText(new FormattedText(pt.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, b, 1.0), new Point(p.X + 6, p.Y - 7));
        }
        if (Highlight is { } h) { var p = ToScreen(h); dc.DrawEllipse(null, new Pen(Brushes.Yellow, 2), p, 7, 7); }
        if (_draft.Count > 0)
        {
            var pen = new Pen(Brushes.Yellow, 1.5) { DashStyle = DashStyles.Dash };
            for (int i = 1; i < _draft.Count; i++) dc.DrawLine(pen, ToScreen(_draft[i - 1]), ToScreen(_draft[i]));
            dc.DrawLine(pen, ToScreen(_draft[^1]), _mouse);
        }
        // north arrow and scale
        dc.DrawText(new FormattedText("N ↑", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 13, fg, 1.0), new Point(ActualWidth - 34, 8));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var before = ToMap(e.GetPosition(this));
        _scale *= e.Delta > 0 ? 1.25 : 0.8;
        var after = ToMap(e.GetPosition(this));
        _cx += before.X - after.X; _cy += before.Y - after.Y;
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var m = ToMap(e.GetPosition(this));
        if (DrawMode && e.ChangedButton == MouseButton.Left)
        {
            if (e.ClickCount == 2) { _draft.Add(m); if (_draft.Count >= 2) PolylineCompleted?.Invoke(this, _draft.ToList()); _draft.Clear(); }
            else _draft.Add(m);
            InvalidateVisual();
            return;
        }
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _drag = e.GetPosition(this); _dcx = _cx; _dcy = _cy; CaptureMouse();
        }
        if (e.ChangedButton == MouseButton.Left) MapClicked?.Invoke(this, (m, e.ClickCount));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouse = e.GetPosition(this);
        if (_drag is { } d && IsMouseCaptured)
        {
            _cx = _dcx - (_mouse.X - d.X) / _scale; _cy = _dcy + (_mouse.Y - d.Y) / _scale;
            InvalidateVisual();
            return;
        }
        MapMoved?.Invoke(this, ToMap(_mouse));
        if (_draft.Count > 0) InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e) { _drag = null; if (IsMouseCaptured) ReleaseMouseCapture(); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Escape) { _draft.Clear(); InvalidateVisual(); } base.OnKeyDown(e); }
}
