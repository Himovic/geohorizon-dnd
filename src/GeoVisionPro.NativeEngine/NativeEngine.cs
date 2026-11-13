using System.Runtime.InteropServices;
using GeoVisionPro.Core.Numerics;

namespace GeoVisionPro.NativeEngine;

/// <summary>
/// Entry point to the optional native C++ engine (gvnative). Every function has a managed reference implementation that is
/// used automatically when the native library is not present or fails to load, so results never depend on its availability.
/// </summary>
public static partial class NativeCompute
{
    private const string Lib = "gvnative";
    private static readonly Lazy<bool> _available = new(Probe);

    /// <summary>True when the native library is loaded (set GEOVISION_DISABLE_NATIVE=1 to force managed code).</summary>
    public static bool IsAvailable => _available.Value;
    public static string Status => IsAvailable ? $"Native engine v{gv_version() / 10000.0:0.00}, OpenMP {(gv_has_openmp() == 1 ? "on" : "off")}" : "Managed fallback (native engine not loaded)";

    private static bool Probe()
    {
        if (Environment.GetEnvironmentVariable("GEOVISION_DISABLE_NATIVE") == "1") return false;
        try
        {
            if (!NativeLibrary.TryLoad(Lib, typeof(NativeCompute).Assembly, null, out var h)) return false;
            NativeLibrary.Free(h);
            return gv_version() >= 800;
        }
        catch (Exception) { return false; }
    }

    [LibraryImport(Lib)] private static partial int gv_version();
    [LibraryImport(Lib)] private static partial int gv_has_openmp();
    [LibraryImport(Lib)] private static unsafe partial void gv_ibm_to_ieee(byte* src, float* dst, long n, int bigEndian);
    [LibraryImport(Lib)] private static unsafe partial void gv_agc_rms(float* trace, int n, int window);
    [LibraryImport(Lib)] private static unsafe partial void gv_convolve_same(float* x, int nx, float* w, int nw, int center, float* output);
    [LibraryImport(Lib)] private static unsafe partial void gv_semblance(float* gather, int ntraces, int ns, double* offsets, double* velocities, int nv, double dt, double t0, int window, float* output);

    public static unsafe void IbmToIeee(ReadOnlySpan<byte> src, Span<float> dst, bool bigEndian)
    {
        if (src.Length < dst.Length * 4) throw new ArgumentException("Source too short.");
        if (IsAvailable)
        {
            fixed (byte* s = src) fixed (float* d = dst) gv_ibm_to_ieee(s, d, dst.Length, bigEndian ? 1 : 0);
            return;
        }
        Seismic_Decode(src, dst, bigEndian);
    }

    private static void Seismic_Decode(ReadOnlySpan<byte> src, Span<float> dst, bool bigEndian)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            uint w = bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(src.Slice(4 * i, 4)) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(4 * i, 4));
            uint mant = w & 0x00FFFFFF;
            if (mant == 0) { dst[i] = 0; continue; }
            int e = (int)((w >> 24) & 0x7F);
            double v = mant * Math.Pow(16, e - 64) / 16777216.0;
            dst[i] = (float)((w & 0x80000000) != 0 ? -v : v);
        }
    }

    public static unsafe void AgcRms(Span<float> trace, int window)
    {
        if (IsAvailable) { fixed (float* t = trace) gv_agc_rms(t, trace.Length, window); return; }
        int n = trace.Length, h = window / 2;
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + trace[i] * (double)trace[i];
        double eps = Math.Sqrt(cum[n] / Math.Max(1, n)) * 1e-6 + 1e-30;
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
            trace[i] = (float)(trace[i] / (Math.Sqrt((cum[b + 1] - cum[a]) / (b - a + 1)) + eps));
        }
    }

    public static unsafe float[] ConvolveSame(ReadOnlySpan<float> x, ReadOnlySpan<float> w, int center)
    {
        var y = new float[x.Length];
        if (IsAvailable && x.Length > 0 && w.Length > 0)
        {
            fixed (float* px = x) fixed (float* pw = w) fixed (float* py = y) gv_convolve_same(px, x.Length, pw, w.Length, center, py);
            return y;
        }
        return Dsp.ConvolveSame(x, w, center);
    }

    /// <summary>Semblance spectrum [velocity, sample] of a gather [trace, sample].</summary>
    public static unsafe float[] Semblance(float[] gather, int ntraces, int ns, double[] offsets, double[] velocities, double dt, double t0, int window)
    {
        var res = new float[velocities.Length * ns];
        if (IsAvailable)
        {
            fixed (float* g = gather) fixed (double* o = offsets) fixed (double* v = velocities) fixed (float* r = res)
                gv_semblance(g, ntraces, ns, o, v, velocities.Length, dt, t0, window, r);
            return res;
        }
        var a = new Core.Primitives.Array2D(ntraces, ns, gather);
        var s = SeismicProcessingFallback.Semblance(a, offsets, velocities, dt, t0, window);
        return s;
    }
}

internal static class SeismicProcessingFallback
{
    public static float[] Semblance(Core.Primitives.Array2D gather, double[] offsets, double[] velocities, double dt, double t0, int window)
    {
        int ns = gather.Cols, nx = gather.Rows;
        var res = new float[velocities.Length * ns];
        for (int iv = 0; iv < velocities.Length; iv++)
        {
            var num = new double[ns]; var den = new double[ns]; var cnt = new int[ns];
            double v = velocities[iv];
            for (int k = 0; k < ns; k++)
            {
                double tz = t0 + k * dt, s = 0, s2 = 0; int live = 0;
                for (int r = 0; r < nx; r++)
                {
                    double idx = (Math.Sqrt(tz * tz + offsets[r] * offsets[r] / (v * v)) - t0) / dt;
                    if (idx > ns - 1) continue;
                    float a = Dsp.SampleLinear(gather.Row(r), idx);
                    if (float.IsNaN(a)) continue;
                    s += a; s2 += a * (double)a; live++;
                }
                num[k] = s * s; den[k] = s2; cnt[k] = live;
            }
            int h = window / 2;
            for (int k = 0; k < ns; k++)
            {
                double sn = 0, sd = 0; int nn = 0;
                for (int w = Math.Max(0, k - h); w <= Math.Min(ns - 1, k + h); w++) { sn += num[w]; sd += den[w]; nn = Math.Max(nn, cnt[w]); }
                res[iv * ns + k] = sd > 0 && nn > 0 ? (float)(sn / (nn * sd)) : 0f;
            }
        }
        return res;
    }
}

/// <summary>Managed handle to the native VTK rendering bridge (gvvtk). Only usable when the library is present.</summary>
public sealed partial class VtkScene : IDisposable
{
    private const string Lib = "gvvtk";
    private IntPtr _handle;

    public static bool IsAvailable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("GEOVISION_DISABLE_VTK") == "1") return false;
            try { if (!NativeLibrary.TryLoad(Lib, typeof(VtkScene).Assembly, null, out var h)) return false; NativeLibrary.Free(h); return true; }
            catch { return false; }
        }
    }

    [LibraryImport(Lib)] private static partial IntPtr gvtk_create(IntPtr parent, int w, int h, int offscreen);
    [LibraryImport(Lib)] private static partial void gvtk_destroy(IntPtr s);
    [LibraryImport(Lib)] private static partial void gvtk_resize(IntPtr s, int w, int h);
    [LibraryImport(Lib)] private static partial void gvtk_set_background(IntPtr s, double r, double g, double b);
    [LibraryImport(Lib)] private static unsafe partial int gvtk_set_volume(IntPtr s, float* data, int nx, int ny, int nz, double sx, double sy, double sz, double ox, double oy, double oz);
    [LibraryImport(Lib)] private static unsafe partial void gvtk_set_transfer_function(IntPtr s, double* points, int n);
    [LibraryImport(Lib)] private static partial void gvtk_set_volume_visible(IntPtr s, int visible);
    [LibraryImport(Lib)] private static unsafe partial int gvtk_add_mesh(IntPtr s, float* xyz, int nverts, int* tris, int ntris, byte* rgba, double r, double g, double b, double opacity);
    [LibraryImport(Lib)] private static unsafe partial int gvtk_add_polyline(IntPtr s, float* xyz, int npts, double r, double g, double b, double width);
    [LibraryImport(Lib)] private static partial void gvtk_remove_actor(IntPtr s, int id);
    [LibraryImport(Lib)] private static partial void gvtk_clear(IntPtr s);
    [LibraryImport(Lib)] private static partial void gvtk_set_parallel_projection(IntPtr s, int on);
    [LibraryImport(Lib)] private static partial void gvtk_reset_camera(IntPtr s);
    [LibraryImport(Lib)] private static partial void gvtk_render(IntPtr s);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial int gvtk_screenshot(IntPtr s, string path, int magnification);

    public VtkScene(IntPtr parentWindow, int width, int height, bool offscreen = false)
    {
        _handle = gvtk_create(parentWindow, width, height, offscreen ? 1 : 0);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("VTK scene creation failed.");
    }

    public void Resize(int w, int h) => gvtk_resize(_handle, w, h);
    public void SetBackground(double r, double g, double b) => gvtk_set_background(_handle, r, g, b);
    public unsafe void SetVolume(float[] data, int nx, int ny, int nz, double sx, double sy, double sz, double ox, double oy, double oz)
    {
        fixed (float* p = data) if (gvtk_set_volume(_handle, p, nx, ny, nz, sx, sy, sz, ox, oy, oz) != 0) throw new InvalidOperationException("Invalid volume.");
    }
    public unsafe void SetTransferFunction(IReadOnlyList<(double Value, double R, double G, double B, double Opacity)> pts)
    {
        var a = pts.SelectMany(p => new[] { p.Value, p.R, p.G, p.B, p.Opacity }).ToArray();
        fixed (double* p = a) gvtk_set_transfer_function(_handle, p, pts.Count);
    }
    public void SetVolumeVisible(bool v) => gvtk_set_volume_visible(_handle, v ? 1 : 0);
    public unsafe int AddMesh(float[] xyz, int[] triangles, byte[]? rgba, double r, double g, double b, double opacity)
    {
        fixed (float* p = xyz) fixed (int* t = triangles) fixed (byte* c = rgba)
            return gvtk_add_mesh(_handle, p, xyz.Length / 3, t, triangles.Length / 3, c, r, g, b, opacity);
    }
    public unsafe int AddPolyline(float[] xyz, double r, double g, double b, double width)
    {
        fixed (float* p = xyz) return gvtk_add_polyline(_handle, p, xyz.Length / 3, r, g, b, width);
    }
    public void RemoveActor(int id) => gvtk_remove_actor(_handle, id);
    public void Clear() => gvtk_clear(_handle);
    public void SetParallelProjection(bool on) => gvtk_set_parallel_projection(_handle, on ? 1 : 0);
    public void ResetCamera() => gvtk_reset_camera(_handle);
    public void Render() => gvtk_render(_handle);
    public void Screenshot(string path, int magnification = 1) { if (gvtk_screenshot(_handle, path, magnification) != 0) throw new IOException("Screenshot failed."); }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { gvtk_destroy(_handle); _handle = IntPtr.Zero; }
    }
}
