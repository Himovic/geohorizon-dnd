using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GeoVisionPro.NativeEngine;

namespace GeoVisionPro.Desktop.Controls;

/// <summary>
/// Hosts the native VTK render window (gvvtk) inside WPF through a Win32 child window. Used for GPU ray-cast volume
/// rendering of seismic volumes. Only active when gvvtk.dll (built with VTK 9) is deployed next to the application.
/// </summary>
public sealed partial class VtkHost : HwndHost
{
    private IntPtr _hwnd;
    public VtkScene? Scene { get; private set; }
    public event EventHandler? SceneReady;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hwnd = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight),
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (VtkScene.IsAvailable)
        {
            Scene = new VtkScene(_hwnd, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight));
            Dispatcher.BeginInvoke(() => SceneReady?.Invoke(this, EventArgs.Empty));
        }
        return new HandleRef(this, _hwnd);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (Scene is null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Scene.Resize((int)(sizeInfo.NewSize.Width * dpi.DpiScaleX), (int)(sizeInfo.NewSize.Height * dpi.DpiScaleY));
        Scene.Render();
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Scene?.Dispose();
        Scene = null;
        DestroyWindow(hwnd.Handle);
    }
}
