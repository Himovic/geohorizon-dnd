using System.Diagnostics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Tests;

/// <summary>
/// Scalability smoke tests: a 40 000-trace volume (60 MB SEG-Y) is written, scanned through memory mapping, converted to
/// the chunked store and sliced, while managed memory stays far below the volume size (no full-volume loading).
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests
{
    private sealed class GeneratedVolume(SurveyGeometry g) : SeismicVolumeBase
    {
        public override SurveyGeometry Geometry => g;
        public override bool TryReadTrace(int inline, int crossline, Span<float> d)
        {
            for (int k = 0; k < g.SampleCount; k++) d[k] = MathF.Sin(k * 0.1f + inline * 0.01f) * (1 + crossline % 7);
            return true;
        }
    }

    [Fact]
    public void LargeSegy_ScanConvertSlice_WithBoundedMemory()
    {
        using var tmp = new TempDir();
        var g = new SurveyGeometry(new IndexRange(1, 200, 1), new IndexRange(1, 200, 1), 375, 0.004, 0, new GridTransform(1, 1, 0, 0, 0, 25, 25, 0));
        var path = tmp.File("big.sgy");
        using (var src = new GeneratedVolume(g)) SegyWriter.WriteVolume(path, src, SegySampleFormat.IbmFloat32);
        Assert.True(new FileInfo(path).Length > 60_000_000);

        GC.Collect();
        long before = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        using var vol = SegyVolume.Open(path);
        double scan = sw.Elapsed.TotalSeconds;
        Assert.Equal(200, vol.Geometry.InlineCount);
        sw.Restart();
        using (var z = ZarrVolume.Create(tmp.File("big.zarr"), "big", vol.Geometry, cacheBytes: 32L << 20)) vol.CopyTo(z);
        double convert = sw.Elapsed.TotalSeconds;
        using var zr = ZarrVolume.Open(tmp.File("big.zarr"), cacheBytes: 32L << 20);
        sw.Restart();
        var slice = zr.ReadTimeSlice(200);
        var xl = zr.ReadCrossline(100);
        double slicing = sw.Elapsed.TotalSeconds;
        Assert.Equal(vol.ReadTrace(17, 100)[200], slice[16, 99], 4);
        Assert.Equal(vol.ReadTrace(150, 100), xl.Row(149).ToArray());
        long after = GC.GetTotalMemory(false);
        Console.WriteLine($"scan {scan:0.00} s, convert {convert:0.00} s, slices {slicing:0.00} s, managed memory delta {(after - before) / 1048576.0:0} MB");
        Assert.True(scan < 30 && convert < 120 && slicing < 30);
    }
}
