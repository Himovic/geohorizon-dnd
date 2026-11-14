using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeoVisionPro.Desktop.Controls;

/// <summary>Conversions between renderer pixel buffers (BGRA32, alpha in the high byte) and WPF imaging types.</summary>
public static class BitmapUtil
{
    public static WriteableBitmap ToBitmap(uint[] pixels, int width, int height, double dpi = 96)
    {
        var wb = new WriteableBitmap(Math.Max(1, width), Math.Max(1, height), dpi, dpi, PixelFormats.Bgra32, null);
        if (width > 0 && height > 0) wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return wb;
    }

    /// <summary>PNG-encodes a pixel buffer (used for 3D textures and report figures).</summary>
    public static MemoryStream ToPngStream(uint[] pixels, int width, int height)
    {
        var bmp = ToBitmap(pixels, width, height);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Captures any visual as BGRA pixels (for reports and screenshot export).</summary>
    public static (uint[] Pixels, int Width, int Height) Capture(FrameworkElement element, double scale = 1.5)
    {
        int w = Math.Max(1, (int)(element.ActualWidth * scale)), h = Math.Max(1, (int)(element.ActualHeight * scale));
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        rtb.Render(dv);
        var px = new uint[w * h];
        rtb.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    public static void SavePng(FrameworkElement element, string path, double scale = 2)
    {
        var (px, w, h) = Capture(element, scale);
        using var s = ToPngStream(px, w, h);
        File.WriteAllBytes(path, s.ToArray());
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; }
        catch (NullReferenceException) { return fallback; }
    }

    public static Brush Brush(string hex) { var b = new SolidColorBrush(ParseColor(hex, Colors.Orange)); b.Freeze(); return b; }

    /// <summary>"Nice" tick step for an axis span (1, 2, 5 × 10^n).</summary>
    public static double NiceStep(double span, int targetTicks)
    {
        if (span <= 0 || double.IsNaN(span)) return 1;
        double raw = span / Math.Max(1, targetTicks);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / mag;
        return (n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10) * mag;
    }
}
