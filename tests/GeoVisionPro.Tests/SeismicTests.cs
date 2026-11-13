using GeoVisionPro.Application.Samples;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gvp_tests_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public static class TestData
{
    public static SyntheticSurveyGenerator SmallGenerator() => new() { Inlines = 12, Crosslines = 16, Samples = 300, NoiseLevel = 0 };

    public static InMemoryVolume Ramp(int ni = 5, int nx = 7, int ns = 50)
    {
        var g = new SurveyGeometry(new IndexRange(10, 10 + 2 * (ni - 1), 2), new IndexRange(1, nx, 1), ns, 0.004, 0.1,
            new GridTransform(10, 1, 1000, 2000, 0, 12.5, 25, 0));
        var v = new InMemoryVolume("ramp", g);
        for (int i = 0; i < ni; i++)
            for (int j = 0; j < nx; j++)
                v.WriteTrace(g.Inlines.ValueAt(i), g.Crosslines.ValueAt(j), Enumerable.Range(0, ns).Select(k => (float)(i * 1000 + j * 10 + k * 0.5 - 7.25)).ToArray());
        return v;
    }
}

public class SegyTests
{
    [Theory]
    [InlineData(0xC276A000u, -118.625f)]
    [InlineData(0x42640000u, 100f)]
    [InlineData(0x00000000u, 0f)]
    [InlineData(0x41100000u, 1f)]
    public void IbmToIeee_KnownValues(uint ibm, float expected) => Assert.Equal(expected, SegyEncoding.IbmToIeee(ibm), 5);

    [Fact]
    public void IeeeToIbm_RoundTrip()
    {
        foreach (var v in new[] { 1f, -1f, 3.14159f, 1e-5f, -123456.78f, 0.1f, 1e20f })
            Assert.Equal(v, SegyEncoding.IbmToIeee(SegyEncoding.IeeeToIbm(v)), Math.Abs(v) * 1e-6f + 1e-30f);
    }

    [Fact]
    public void Ebcdic_RoundTrip()
    {
        const string s = "C 1 CLIENT GEOVISION PRO 3D SURVEY, LINE 123-456 (TEST)";
        Assert.Equal(s, Ebcdic.Decode(Ebcdic.Encode(s)));
        Assert.True(Ebcdic.LooksLikeEbcdic(Ebcdic.Encode(new string('A', 80))));
    }

    [Theory]
    [InlineData(SegySampleFormat.IbmFloat32)]
    [InlineData(SegySampleFormat.IeeeFloat32)]
    public void WriteRead_RoundTrip_PreservesGeometryAndSamples(SegySampleFormat format)
    {
        using var tmp = new TempDir();
        using var vol = TestData.Ramp();
        var path = tmp.File("ramp.sgy");
        SegyWriter.WriteVolume(path, vol, format);
        using var segy = SegyVolume.Open(path);
        var g = segy.Geometry;
        Assert.Equal(vol.Geometry.Inlines, g.Inlines);
        Assert.Equal(vol.Geometry.Crosslines, g.Crosslines);
        Assert.Equal(50, g.SampleCount);
        Assert.Equal(0.004, g.SampleInterval, 9);
        Assert.Equal(0.1, g.StartTime, 6);
        Assert.Equal(SeismicKind.PostStack3D, segy.Scan.Kind);
        Assert.True(segy.Scan.HasCoordinates);
        var p = g.MapLocation(14, 5); var q = vol.Geometry.MapLocation(14, 5);
        Assert.Equal(q.X, p.X, 1); Assert.Equal(q.Y, p.Y, 1);
        var a = segy.ReadTrace(14, 5); var b = vol.ReadTrace(14, 5);
        for (int k = 0; k < 50; k++) Assert.Equal(b[k], a[k], 2);
        Assert.Equal(format, segy.File.Format);
    }

    [Fact]
    public void Scanner_DetectsNonStandardKeyBytes()
    {
        using var tmp = new TempDir();
        using var vol = TestData.Ramp(4, 6, 20);
        var path = tmp.File("nonstd.sgy");
        SegyWriter.WriteVolume(path, vol);
        // move inline/crossline from 189/193 to 9/21 (already written there too) and zero 189/193
        var bytes = File.ReadAllBytes(path);
        int traceSize = 240 + 20 * 4;
        for (int t = 0; t < 24; t++)
        {
            int o = 3600 + t * traceSize;
            for (int b = 188; b < 196; b++) bytes[o + b] = 0;
        }
        File.WriteAllBytes(path, bytes);
        using var f = SegyFile.Open(path);
        var keys = SegyScanner.DetectKeyLocations(f);
        Assert.Equal(9, keys.InlineByte);
        Assert.Equal(21, keys.CrosslineByte);
        var scan = SegyScanner.Scan(f, keys);
        Assert.Equal(4, scan.Geometry.InlineCount);
        Assert.Equal(6, scan.Geometry.CrosslineCount);
    }

    [Fact]
    public void Gathers_WriteRead_RoundTrip()
    {
        using var tmp = new TempDir();
        var gen = TestData.SmallGenerator();
        using var gathers = gen.BuildGathers(6, 8, 2000, 11);
        var path = tmp.File("g.sgy");
        SegyWriter.WriteGathers(path, gathers);
        var f = SegyFile.Open(path);
        var scan = SegyScanner.Scan(f);
        Assert.Equal(SeismicKind.PreStackGathers, scan.Kind);
        Assert.Equal(11, scan.Fold);
        Assert.Equal(2000, scan.Offsets[^1], 6);
        using var gs = new SegyGatherDataset("g", f, scan, false);
        var il = gathers.Geometry.Inlines.First; var xl = gathers.Geometry.Crosslines.First;
        var a = gs.ReadGather(il, xl)!; var b = gathers.ReadGather(il, xl)!;
        for (int k = 0; k < a.Data.Length; k += 37) Assert.Equal(b.Data[k], a.Data[k], 4);
    }

    [Fact]
    public void BinaryHeaderInspection_ReportsFields()
    {
        using var tmp = new TempDir();
        using var vol = TestData.Ramp();
        var path = tmp.File("r.sgy");
        SegyWriter.WriteVolume(path, vol);
        using var f = SegyFile.Open(path);
        var fields = f.ReadAllBinaryFields();
        Assert.Equal(4000, fields.First(x => x.Field.Name == "SampleInterval").Value);
        Assert.Equal(50, fields.First(x => x.Field.Name == "SamplesPerTrace").Value);
        var th = f.ReadAllTraceFields(0);
        Assert.Equal(10, th.First(x => x.Field.Name == "Inline").Value);
        Assert.Contains("GeoVision", f.TextHeader);
    }

    [Fact]
    public void TruncatedFile_ReportsWarning()
    {
        using var tmp = new TempDir();
        using var vol = TestData.Ramp();
        var path = tmp.File("t.sgy");
        SegyWriter.WriteVolume(path, vol);
        var b = File.ReadAllBytes(path);
        File.WriteAllBytes(path, b[..^100]);
        using var f = SegyFile.Open(path);
        Assert.Contains(f.Warnings, w => w.Contains("trailing"));
        Assert.Equal(vol.Geometry.TraceCount - 1, f.TraceCount);
    }
}

public class VolumeTests
{
    [Fact]
    public void Zarr_RoundTrip_TracesAndSlices()
    {
        using var tmp = new TempDir();
        using var src = TestData.Ramp(9, 11, 70);
        using (var z = ZarrVolume.Create(tmp.File("vol.zarr"), "ramp", src.Geometry, [4, 5, 32]))
            src.CopyTo(z);
        using var r = ZarrVolume.Open(tmp.File("vol.zarr"));
        Assert.Equal(src.Geometry, r.Geometry);
        Assert.Equal(src.ReadTrace(18, 7), r.ReadTrace(18, 7));
        var a = src.ReadTimeSlice(33); var b = r.ReadTimeSlice(33);
        Assert.Equal(a.Data, b.Data);
        var c = src.ReadCrossline(4); var d = r.ReadCrossline(4);
        Assert.Equal(c.Data, d.Data);
        Assert.True(File.Exists(Path.Combine(tmp.File("vol.zarr"), ".zarray")));
    }

    [Fact]
    public void Zarr_SmallCache_StillCorrect()
    {
        using var tmp = new TempDir();
        using var src = TestData.Ramp(9, 11, 70);
        using (var z = ZarrVolume.Create(tmp.File("v"), "ramp", src.Geometry, [2, 2, 16], cacheBytes: 1))
            src.CopyTo(z);
        using var r = ZarrVolume.Open(tmp.File("v"), cacheBytes: 1);
        for (int i = 0; i < 9; i++)
            Assert.Equal(src.ReadInline(src.Geometry.Inlines.ValueAt(i)).Data, r.ReadInline(src.Geometry.Inlines.ValueAt(i)).Data);
    }

    [Fact]
    public void ArbitraryLine_InterpolatesBetweenTraces()
    {
        using var v = TestData.Ramp();
        var path = ArbitraryPath.FromVertices([(10, 1), (10, 7)]);
        var a = v.ReadArbitrary(path);
        Assert.Equal(7, a.Rows);
        Assert.Equal(v.ReadTrace(10, 4)[5], a[3, 5], 4);
        var mid = v.ReadArbitrary(new ArbitraryPath([(11, 2)]));
        Assert.Equal((v.ReadTrace(10, 2)[0] + v.ReadTrace(12, 2)[0]) / 2, mid[0, 0], 3);
    }

    [Fact]
    public void NearestTrace_FromMapCoordinates()
    {
        using var v = TestData.Ramp();
        var g = v.Geometry;
        var p = g.MapLocation(14, 3);
        Assert.Equal((14, 3), g.NearestTrace(new Point2D(p.X + 3, p.Y - 2)));
        Assert.Null(g.NearestTrace(new Point2D(0, 0)));
    }

    [Fact]
    public void DeadTraces_ReportedAsMissing()
    {
        var g = TestData.Ramp().Geometry;
        using var v = new InMemoryVolume("sparse", g);
        v.WriteTrace(10, 1, new float[g.SampleCount]);
        Assert.True(v.TryReadTrace(10, 1, new float[g.SampleCount]));
        Assert.False(v.TryReadTrace(12, 1, new float[g.SampleCount]));
    }
}
