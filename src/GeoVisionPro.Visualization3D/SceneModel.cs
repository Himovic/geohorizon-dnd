using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Visualization3D;

/// <summary>
/// Renderer-independent triangle mesh in world coordinates (X east, Y north, Z up; time is mapped to negative Z
/// through <see cref="SceneScale"/>). Optional per-vertex colours (BGRA) or texture coordinates with a BGRA texture.
/// </summary>
public sealed class SceneMesh
{
    public required float[] Positions { get; init; }         // xyz triples
    public required int[] Indices { get; init; }             // triangle list
    public float[]? Normals { get; set; }
    public uint[]? VertexColors { get; init; }
    public float[]? TexCoords { get; init; }                 // uv pairs
    public uint[]? Texture { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;

    public void ComputeNormals()
    {
        var n = new float[Positions.Length];
        for (int t = 0; t < Indices.Length; t += 3)
        {
            int a = Indices[t] * 3, b = Indices[t + 1] * 3, c = Indices[t + 2] * 3;
            float ux = Positions[b] - Positions[a], uy = Positions[b + 1] - Positions[a + 1], uz = Positions[b + 2] - Positions[a + 2];
            float vx = Positions[c] - Positions[a], vy = Positions[c + 1] - Positions[a + 1], vz = Positions[c + 2] - Positions[a + 2];
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            foreach (var k in new[] { a, b, c }) { n[k] += nx; n[k + 1] += ny; n[k + 2] += nz; }
        }
        for (int k = 0; k < n.Length; k += 3)
        {
            float l = MathF.Sqrt(n[k] * n[k] + n[k + 1] * n[k + 1] + n[k + 2] * n[k + 2]);
            if (l > 0) { n[k] /= l; n[k + 1] /= l; n[k + 2] /= l; }
        }
        Normals = n;
    }

    public (Point3D Min, Point3D Max) Bounds()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (int k = 0; k < Positions.Length; k += 3)
        {
            x0 = Math.Min(x0, Positions[k]); y0 = Math.Min(y0, Positions[k + 1]); z0 = Math.Min(z0, Positions[k + 2]);
            x1 = Math.Max(x1, Positions[k]); y1 = Math.Max(y1, Positions[k + 1]); z1 = Math.Max(z1, Positions[k + 2]);
        }
        return (new Point3D(x0, y0, z0), new Point3D(x1, y1, z1));
    }
}

/// <summary>Polyline (well path, fault stick, intersection) in world coordinates.</summary>
public sealed record ScenePolyline(IReadOnlyList<Point3D> Points, uint Color, float Thickness, string Name);

/// <summary>
/// Maps survey/time coordinates to the 3D world. To keep float precision, world X/Y are relative to an origin
/// (survey corner). Time (s) is converted to pseudo-depth: Z = −t × VerticalScale (metres per second, i.e. half a velocity).
/// </summary>
public sealed record SceneScale(double OriginX, double OriginY, double VerticalScale = 1500, double VerticalExaggeration = 1)
{
    public Point3D ToWorld(double x, double y, double timeOrDepth, bool isTime = true) =>
        new(x - OriginX, y - OriginY, -(isTime ? timeOrDepth * VerticalScale : timeOrDepth) * VerticalExaggeration);
}

/// <summary>Opacity/colour transfer function for volume rendering (amplitude → RGBA).</summary>
public sealed class TransferFunction
{
    public Visualization2D.ColorMap ColorMap { get; set; } = Visualization2D.ColorMaps.Seismic;
    public double Min { get; set; } = -1;
    public double Max { get; set; } = 1;
    /// <summary>Opacity control points (normalised value 0..1, opacity 0..1), piecewise linear.</summary>
    public List<(double Value, double Opacity)> Opacity { get; set; } = [(0, 1), (0.35, 0.6), (0.5, 0), (0.65, 0.6), (1, 1)];
    public double GlobalOpacity { get; set; } = 0.6;

    public uint Evaluate(double v)
    {
        if (double.IsNaN(v)) return 0;
        double t = Math.Clamp((v - Min) / Math.Max(1e-30, Max - Min), 0, 1);
        double a = Core.Numerics.Dsp.Interp(Opacity.Select(o => o.Value).ToArray(), Opacity.Select(o => o.Opacity).ToArray(), t, clamp: true) * GlobalOpacity;
        uint rgb = ColorMap.Lookup(t) & 0x00FFFFFF;
        return ((uint)Math.Clamp(a * 255, 0, 255) << 24) | rgb;
    }
}

public static class MeshFactory
{
    /// <summary>Triangulated horizon surface from its grid (two triangles per fully defined grid cell), coloured by Z with a colour map.</summary>
    public static SceneMesh HorizonSurface(Horizon h, SurveyGeometry g, SceneScale scale, Visualization2D.ColorMap cmap, int decimation = 1, Grid2D? colorBy = null)
    {
        decimation = Math.Max(1, decimation);
        int ni = (h.InlineCount + decimation - 1) / decimation, nj = (h.CrosslineCount + decimation - 1) / decimation;
        var pos = new float[ni * nj * 3];
        var col = new uint[ni * nj];
        var defined = new bool[ni * nj];
        var src = colorBy ?? h.AsGrid();
        var (cmin, cmax) = src.Range();
        for (int a = 0; a < ni; a++)
            for (int b = 0; b < nj; b++)
            {
                int i = Math.Min(a * decimation, h.InlineCount - 1), j = Math.Min(b * decimation, h.CrosslineCount - 1);
                double t = h.Values[i * h.CrosslineCount + j];
                int k = a * nj + b;
                if (double.IsNaN(t)) continue;
                var m = g.MapLocation(h.InlineAt(i), h.CrosslineAt(j));
                var w = scale.ToWorld(m.X, m.Y, t, h.Domain == Core.Units.DomainType.Time);
                pos[3 * k] = (float)w.X; pos[3 * k + 1] = (float)w.Y; pos[3 * k + 2] = (float)w.Z;
                double cv = src[i, j];
                // colour scale: shallow (small t) = high end of map for time surfaces
                col[k] = cmap.Lookup(cmax > cmin ? (colorBy is null ? 1 - (cv - cmin) / (cmax - cmin) : (cv - cmin) / (cmax - cmin)) : 0.5);
                defined[k] = true;
            }
        var idx = new List<int>();
        for (int a = 0; a + 1 < ni; a++)
            for (int b = 0; b + 1 < nj; b++)
            {
                int k00 = a * nj + b, k01 = k00 + 1, k10 = k00 + nj, k11 = k10 + 1;
                if (defined[k00] && defined[k10] && defined[k11]) idx.AddRange([k00, k10, k11]);
                if (defined[k00] && defined[k11] && defined[k01]) idx.AddRange([k00, k11, k01]);
            }
        var mesh = new SceneMesh { Positions = pos, Indices = idx.ToArray(), VertexColors = col };
        mesh.ComputeNormals();
        return mesh;
    }

    /// <summary>Fault surface mesh from a triangulated fault (survey coordinates X=il, Y=xl, Z=time).</summary>
    public static SceneMesh FaultSurface(Interpretation.SurfaceMesh fault, SurveyGeometry g, SceneScale scale, uint color, bool isTime = true)
    {
        var pos = new float[fault.Vertices.Count * 3];
        for (int i = 0; i < fault.Vertices.Count; i++)
        {
            var v = fault.Vertices[i];
            var m = g.MapLocation(v.X, v.Y);
            var w = scale.ToWorld(m.X, m.Y, v.Z, isTime);
            pos[3 * i] = (float)w.X; pos[3 * i + 1] = (float)w.Y; pos[3 * i + 2] = (float)w.Z;
        }
        var idx = fault.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var mesh = new SceneMesh { Positions = pos, Indices = idx, VertexColors = Enumerable.Repeat(color, fault.Vertices.Count).ToArray() };
        mesh.ComputeNormals();
        return mesh;
    }

    /// <summary>
    /// Textured quad for a seismic slice. Corners are given in world coordinates (top-left, top-right, bottom-right, bottom-left
    /// in texture space); the texture is the rendered section/time slice (BGRA).
    /// </summary>
    public static SceneMesh TexturedQuad(Point3D tl, Point3D tr, Point3D br, Point3D bl, uint[] texture, int texW, int texH) => new()
    {
        Positions = [(float)tl.X, (float)tl.Y, (float)tl.Z, (float)tr.X, (float)tr.Y, (float)tr.Z, (float)br.X, (float)br.Y, (float)br.Z, (float)bl.X, (float)bl.Y, (float)bl.Z],
        Indices = [0, 3, 2, 0, 2, 1],
        TexCoords = [0, 0, 1, 0, 1, 1, 0, 1],
        Texture = texture, TextureWidth = texW, TextureHeight = texH
    };

    /// <summary>World corners of an inline section quad.</summary>
    public static (Point3D TL, Point3D TR, Point3D BR, Point3D BL) InlineCorners(SurveyGeometry g, int inline, SceneScale s)
    {
        var a = g.MapLocation(inline, g.Crosslines.First); var b = g.MapLocation(inline, g.Crosslines.Last);
        bool time = g.Domain == Core.Units.DomainType.Time;
        return (s.ToWorld(a.X, a.Y, g.StartTime, time), s.ToWorld(b.X, b.Y, g.StartTime, time), s.ToWorld(b.X, b.Y, g.EndTime, time), s.ToWorld(a.X, a.Y, g.EndTime, time));
    }

    public static (Point3D TL, Point3D TR, Point3D BR, Point3D BL) CrosslineCorners(SurveyGeometry g, int crossline, SceneScale s)
    {
        var a = g.MapLocation(g.Inlines.First, crossline); var b = g.MapLocation(g.Inlines.Last, crossline);
        bool time = g.Domain == Core.Units.DomainType.Time;
        return (s.ToWorld(a.X, a.Y, g.StartTime, time), s.ToWorld(b.X, b.Y, g.StartTime, time), s.ToWorld(b.X, b.Y, g.EndTime, time), s.ToWorld(a.X, a.Y, g.EndTime, time));
    }

    /// <summary>Time slice quad: texture rows = inlines (top = first inline), columns = crosslines.</summary>
    public static (Point3D TL, Point3D TR, Point3D BR, Point3D BL) TimeSliceCorners(SurveyGeometry g, double time, SceneScale s)
    {
        bool isTime = g.Domain == Core.Units.DomainType.Time;
        var p00 = g.MapLocation(g.Inlines.First, g.Crosslines.First); var p01 = g.MapLocation(g.Inlines.First, g.Crosslines.Last);
        var p11 = g.MapLocation(g.Inlines.Last, g.Crosslines.Last); var p10 = g.MapLocation(g.Inlines.Last, g.Crosslines.First);
        return (s.ToWorld(p00.X, p00.Y, time, isTime), s.ToWorld(p01.X, p01.Y, time, isTime), s.ToWorld(p11.X, p11.Y, time, isTime), s.ToWorld(p10.X, p10.Y, time, isTime));
    }

    /// <summary>Survey bounding box edges as polylines.</summary>
    public static IReadOnlyList<ScenePolyline> SurveyOutline(SurveyGeometry g, SceneScale s, uint color)
    {
        var o = g.MapOutline();
        bool time = g.Domain == Core.Units.DomainType.Time;
        var top = o.Select(p => s.ToWorld(p.X, p.Y, g.StartTime, time)).Append(s.ToWorld(o[0].X, o[0].Y, g.StartTime, time)).ToList();
        var bot = o.Select(p => s.ToWorld(p.X, p.Y, g.EndTime, time)).Append(s.ToWorld(o[0].X, o[0].Y, g.EndTime, time)).ToList();
        var res = new List<ScenePolyline> { new(top, color, 1, "Survey top"), new(bot, color, 1, "Survey base") };
        for (int k = 0; k < 4; k++) res.Add(new ScenePolyline([top[k], bot[k]], color, 1, "Survey edge"));
        return res;
    }

    /// <summary>
    /// Axis-aligned slice stack for 2D-texture volume rendering: n slices of constant time through a decimated volume, each
    /// rendered with the transfer function (premultiplied alpha). Returns textured quads ordered top to bottom.
    /// </summary>
    public static IReadOnlyList<SceneMesh> VolumeSliceStack(Seismic.Volumes.ISeismicVolume v, TransferFunction tf, SceneScale s, int sliceCount,
        int maxLateral = 256, CancellationToken ct = default)
    {
        var g = v.Geometry;
        var res = new List<SceneMesh>();
        int stepI = Math.Max(1, (int)Math.Ceiling(g.InlineCount / (double)maxLateral)), stepJ = Math.Max(1, (int)Math.Ceiling(g.CrosslineCount / (double)maxLateral));
        int w = (g.CrosslineCount + stepJ - 1) / stepJ, h = (g.InlineCount + stepI - 1) / stepI;
        for (int k = 0; k < sliceCount; k++)
        {
            ct.ThrowIfCancellationRequested();
            int sample = (int)Math.Round((k + 0.5) / sliceCount * (g.SampleCount - 1));
            var slice = v.ReadTimeSlice(sample);
            var tex = new uint[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex[y * w + x] = tf.Evaluate(slice[Math.Min(y * stepI, g.InlineCount - 1), Math.Min(x * stepJ, g.CrosslineCount - 1)]);
            var c = TimeSliceCorners(g, g.TimeAt(sample), s);
            res.Add(TexturedQuad(c.TL, c.TR, c.BR, c.BL, tex, w, h));
        }
        return res;
    }
}

/// <summary>Serializable camera state for saved views.</summary>
public sealed record CameraState(Point3D Position, Point3D LookDirection, Point3D UpDirection, double FieldOfView, bool Orthographic, double OrthographicWidth);
