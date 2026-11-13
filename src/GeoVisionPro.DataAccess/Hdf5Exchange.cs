using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;
using PureHDF;

namespace GeoVisionPro.DataAccess;

/// <summary>
/// HDF5 export of scientific datasets (volumes, horizons, well logs) for exchange with Python/MATLAB tools.
/// Volumes are written inline by inline into a 3D dataset [inline, crossline, sample] with geometry attributes.
/// </summary>
public static class Hdf5Exchange
{
    /// <summary>Writes a post-stack volume. Intended for volumes that fit in memory once (the dataset is materialised).</summary>
    public static void ExportVolume(ISeismicVolume v, string path, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = v.Geometry;
        long total = g.TraceCount * g.SampleCount;
        if (total > 1_000_000_000) throw new InvalidOperationException("Volume too large for HDF5 export in one pass (>1e9 samples); use SEG-Y or the Zarr store.");
        var data = new float[g.InlineCount, g.CrosslineCount, g.SampleCount];
        var t = new float[g.SampleCount];
        for (int i = 0; i < g.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                v.TryReadTrace(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j), t);
                for (int k = 0; k < g.SampleCount; k++) data[i, j, k] = t[k];
            }
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, "HDF5 export"));
        }
        var tr = g.Transform;
        var file = new H5File
        {
            ["amplitude"] = new H5Dataset(data)
            {
                Attributes = new()
                {
                    ["name"] = v.Name,
                    ["inline_first"] = g.Inlines.First, ["inline_step"] = g.Inlines.Step,
                    ["crossline_first"] = g.Crosslines.First, ["crossline_step"] = g.Crosslines.Step,
                    ["sample_interval_s"] = g.SampleInterval, ["start_time_s"] = g.StartTime,
                    ["transform"] = new[] { tr.Inline0, tr.Crossline0, tr.X0, tr.Y0, tr.A11, tr.A12, tr.A21, tr.A22 },
                    ["domain"] = g.Domain.ToString()
                }
            }
        };
        file.Write(path);
    }

    public static void ExportHorizon(Horizon h, string path)
    {
        var grid = new double[h.InlineCount, h.CrosslineCount];
        for (int i = 0; i < h.InlineCount; i++) for (int j = 0; j < h.CrosslineCount; j++) grid[i, j] = h.Values[i * h.CrosslineCount + j];
        var file = new H5File
        {
            ["horizon"] = new H5Dataset(grid)
            {
                Attributes = new() { ["name"] = h.Name, ["inline_first"] = h.InlineFirst, ["crossline_first"] = h.CrosslineFirst, ["domain"] = h.Domain.ToString() }
            }
        };
        file.Write(path);
    }

    public static void ExportWell(Well w, string path)
    {
        var group = new H5Group();
        foreach (var c in w.Curves)
            group[c.Mnemonic] = new H5Dataset(c.Values) { Attributes = new() { ["unit"] = c.Unit, ["family"] = c.Family.ToString() } };
        var file = new H5File
        {
            ["depth"] = new H5Dataset(w.Curves.FirstOrDefault()?.Depths ?? []),
            ["curves"] = group,
        };
        file.Attributes["well"] = w.Name;
        file.Attributes["x"] = w.SurfaceX;
        file.Attributes["y"] = w.SurfaceY;
        file.Attributes["kb"] = w.KellyBushing;
        file.Write(path);
    }

    /// <summary>Reads back the amplitude dataset of a volume exported by <see cref="ExportVolume"/> (for round-trip QC).</summary>
    public static float[,,] ReadVolume(string path)
    {
        using var f = H5File.OpenRead(path);
        return f.Dataset("amplitude").Read<float[,,]>();
    }
}
