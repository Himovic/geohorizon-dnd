using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Seismic.Storage;

/// <summary>
/// Chunked (bricked) seismic volume stored as a Zarr v2 array: shape [inline, crossline, sample], dtype &lt;f4,
/// optional zlib compression. Readable by any Zarr v2 implementation (e.g. zarr-python). Survey metadata is kept
/// in .zattrs and live-trace flags in a sidecar 'live.bin'. Chunks are decoded on demand through a bounded LRU cache,
/// so volumes much larger than RAM can be processed and displayed.
/// </summary>
public sealed class ZarrVolume : SeismicVolumeBase, IWritableSeismicVolume
{
    private readonly string _root;
    private readonly SurveyGeometry _geometry;
    private readonly int[] _chunks;
    private readonly bool _compress;
    private readonly bool _writable;
    private readonly byte[] _live;
    private readonly object _sync = new();
    private readonly Dictionary<(int, int, int), LinkedListNode<(int I, int J, int K, float[] Data)>> _cacheMap = [];
    private readonly LinkedList<(int I, int J, int K, float[] Data)> _lru = new();
    private readonly Dictionary<(int, int, int), float[]> _dirty = [];
    private readonly long _cacheCapacityChunks;
    private bool _liveDirty;

    public override SurveyGeometry Geometry => _geometry;
    public string RootPath => _root;
    public IReadOnlyList<int> ChunkShape => _chunks;
    public static long DefaultCacheBytes { get; set; } = 512L * 1024 * 1024;

    private ZarrVolume(string root, string name, SurveyGeometry geometry, int[] chunks, bool compress, bool writable, byte[] live, long cacheBytes)
    {
        _root = root; Name = name; _geometry = geometry; _chunks = chunks; _compress = compress; _writable = writable; _live = live;
        long chunkBytes = 4L * chunks[0] * chunks[1] * chunks[2];
        _cacheCapacityChunks = Math.Max(8, cacheBytes / chunkBytes);
    }

    public static ZarrVolume Create(string root, string name, SurveyGeometry geometry, int[]? chunks = null, bool compress = true, long? cacheBytes = null)
    {
        chunks ??= [Math.Min(32, geometry.InlineCount), Math.Min(32, geometry.CrosslineCount), Math.Min(128, geometry.SampleCount)];
        Directory.CreateDirectory(root);
        foreach (var f in Directory.EnumerateFiles(root)) File.Delete(f);
        var zarray = new JsonObject
        {
            ["zarr_format"] = 2,
            ["shape"] = new JsonArray(geometry.InlineCount, geometry.CrosslineCount, geometry.SampleCount),
            ["chunks"] = new JsonArray(chunks[0], chunks[1], chunks[2]),
            ["dtype"] = "<f4",
            ["compressor"] = compress ? new JsonObject { ["id"] = "zlib", ["level"] = 1 } : null,
            ["fill_value"] = 0.0,
            ["order"] = "C",
            ["filters"] = null,
            ["dimension_separator"] = "."
        };
        File.WriteAllText(Path.Combine(root, ".zarray"), zarray.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(root, ".zattrs"), GeometryToJson(name, geometry).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var live = new byte[geometry.TraceCount];
        File.WriteAllBytes(Path.Combine(root, "live.bin"), live);
        return new ZarrVolume(root, name, geometry, chunks, compress, true, live, cacheBytes ?? DefaultCacheBytes);
    }

    public static ZarrVolume Open(string root, bool writable = false, long? cacheBytes = null)
    {
        var zarray = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".zarray")))!.AsObject();
        if ((int)zarray["zarr_format"]! != 2) throw new InvalidDataException("Only Zarr format 2 is supported.");
        if ((string)zarray["dtype"]! != "<f4") throw new InvalidDataException("Only little-endian float32 Zarr arrays are supported.");
        if ((string?)zarray["order"] != "C") throw new InvalidDataException("Only C-order Zarr arrays are supported.");
        var chunks = zarray["chunks"]!.AsArray().Select(n => (int)n!).ToArray();
        var compressor = zarray["compressor"];
        bool compress = compressor is not null && compressor.GetValueKind() != JsonValueKind.Null;
        if (compress && (string?)compressor!["id"] != "zlib") throw new InvalidDataException("Only zlib or uncompressed Zarr chunks are supported.");
        var attrs = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".zattrs")))!.AsObject();
        var (name, geometry) = GeometryFromJson(attrs);
        var livePath = Path.Combine(root, "live.bin");
        var live = File.Exists(livePath) ? File.ReadAllBytes(livePath) : Enumerable.Repeat((byte)1, (int)geometry.TraceCount).ToArray();
        return new ZarrVolume(root, name, geometry, chunks, compress, writable, live, cacheBytes ?? DefaultCacheBytes);
    }

    public static bool IsZarrVolume(string root) => File.Exists(Path.Combine(root, ".zarray")) && File.Exists(Path.Combine(root, ".zattrs"));

    private static JsonObject GeometryToJson(string name, SurveyGeometry g) => new()
    {
        ["geovision"] = new JsonObject
        {
            ["name"] = name,
            ["inline_first"] = g.Inlines.First, ["inline_last"] = g.Inlines.Last, ["inline_step"] = g.Inlines.Step,
            ["crossline_first"] = g.Crosslines.First, ["crossline_last"] = g.Crosslines.Last, ["crossline_step"] = g.Crosslines.Step,
            ["sample_count"] = g.SampleCount, ["sample_interval"] = g.SampleInterval, ["start_time"] = g.StartTime,
            ["domain"] = g.Domain.ToString(),
            ["transform"] = new JsonArray(g.Transform.Inline0, g.Transform.Crossline0, g.Transform.X0, g.Transform.Y0,
                g.Transform.A11, g.Transform.A12, g.Transform.A21, g.Transform.A22)
        }
    };

    private static (string, SurveyGeometry) GeometryFromJson(JsonObject attrs)
    {
        var o = attrs["geovision"]!.AsObject();
        var t = o["transform"]!.AsArray().Select(n => (double)n!).ToArray();
        var g = new SurveyGeometry(
            new IndexRange((int)o["inline_first"]!, (int)o["inline_last"]!, (int)o["inline_step"]!),
            new IndexRange((int)o["crossline_first"]!, (int)o["crossline_last"]!, (int)o["crossline_step"]!),
            (int)o["sample_count"]!, (double)o["sample_interval"]!, (double)o["start_time"]!,
            new GridTransform(t[0], t[1], t[2], t[3], t[4], t[5], t[6], t[7]),
            Enum.Parse<DomainType>((string)o["domain"]!));
        return ((string)o["name"]!, g);
    }

    private string ChunkPath(int ci, int cj, int ck) => Path.Combine(_root, $"{ci}.{cj}.{ck}");
    private int ChunkLength => _chunks[0] * _chunks[1] * _chunks[2];

    private float[]? LoadChunkFromDisk(int ci, int cj, int ck)
    {
        var p = ChunkPath(ci, cj, ck);
        if (!File.Exists(p)) return null;
        var data = new float[ChunkLength];
        var bytes = MemoryMarshal.AsBytes(data.AsSpan());
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        if (_compress)
        {
            using var z = new ZLibStream(fs, CompressionMode.Decompress);
            z.ReadExactly(bytes);
        }
        else fs.ReadExactly(bytes);
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Big-endian hosts are not supported.");
        return data;
    }

    private void WriteChunkToDisk(int ci, int cj, int ck, float[] data)
    {
        var tmp = ChunkPath(ci, cj, ck) + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            if (_compress)
            {
                using var z = new ZLibStream(fs, CompressionLevel.Fastest);
                z.Write(bytes);
            }
            else fs.Write(bytes);
        }
        File.Move(tmp, ChunkPath(ci, cj, ck), true);
    }

    /// <summary>Returns chunk data (shared, do not modify) or null if the chunk was never written.</summary>
    private float[]? GetChunk(int ci, int cj, int ck)
    {
        var key = (ci, cj, ck);
        lock (_sync)
        {
            if (_dirty.TryGetValue(key, out var d)) return d;
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                return node.Value.Data;
            }
        }
        var data = LoadChunkFromDisk(ci, cj, ck);
        if (data is null) return null;
        lock (_sync)
        {
            if (!_cacheMap.ContainsKey(key))
            {
                var node = _lru.AddFirst((ci, cj, ck, data));
                _cacheMap[key] = node;
                while (_lru.Count > _cacheCapacityChunks)
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    _cacheMap.Remove((last.Value.I, last.Value.J, last.Value.K));
                }
            }
        }
        return data;
    }

    public override bool TryReadTrace(int inline, int crossline, Span<float> destination)
    {
        var g = _geometry;
        int ns = g.SampleCount;
        if (!g.ContainsTrace(inline, crossline)) { destination[..ns].Clear(); return false; }
        int i = g.InlineIndex(inline), j = g.CrosslineIndex(crossline);
        if (_live[(long)i * g.CrosslineCount + j] == 0) { destination[..ns].Clear(); return false; }
        int ci = i / _chunks[0], cj = j / _chunks[1];
        int li = i % _chunks[0], lj = j % _chunks[1];
        for (int ck = 0; ck * _chunks[2] < ns; ck++)
        {
            int k0 = ck * _chunks[2];
            int len = Math.Min(_chunks[2], ns - k0);
            var chunk = GetChunk(ci, cj, ck);
            if (chunk is null) { destination.Slice(k0, len).Clear(); continue; }
            chunk.AsSpan((li * _chunks[1] + lj) * _chunks[2], len).CopyTo(destination.Slice(k0, len));
        }
        return true;
    }

    public override Array2D ReadTimeSlice(int sampleIndex)
    {
        var g = _geometry;
        sampleIndex = Math.Clamp(sampleIndex, 0, g.SampleCount - 1);
        var a = new Array2D(g.InlineCount, g.CrosslineCount);
        int ck = sampleIndex / _chunks[2], lk = sampleIndex % _chunks[2];
        for (int ci = 0; ci * _chunks[0] < g.InlineCount; ci++)
            for (int cj = 0; cj * _chunks[1] < g.CrosslineCount; cj++)
            {
                var chunk = GetChunk(ci, cj, ck);
                if (chunk is null) continue;
                for (int li = 0; li < _chunks[0]; li++)
                {
                    int i = ci * _chunks[0] + li;
                    if (i >= g.InlineCount) break;
                    for (int lj = 0; lj < _chunks[1]; lj++)
                    {
                        int j = cj * _chunks[1] + lj;
                        if (j >= g.CrosslineCount) break;
                        a[i, j] = chunk[(li * _chunks[1] + lj) * _chunks[2] + lk];
                    }
                }
            }
        return a;
    }

    public void WriteTrace(int inline, int crossline, ReadOnlySpan<float> samples)
    {
        if (!_writable) throw new InvalidOperationException("Volume opened read-only.");
        var g = _geometry;
        if (!g.ContainsTrace(inline, crossline)) throw new ArgumentOutOfRangeException(nameof(inline));
        int i = g.InlineIndex(inline), j = g.CrosslineIndex(crossline);
        int ci = i / _chunks[0], cj = j / _chunks[1], li = i % _chunks[0], lj = j % _chunks[1];
        int ns = g.SampleCount;
        lock (_sync)
        {
            for (int ck = 0; ck * _chunks[2] < ns; ck++)
            {
                var key = (ci, cj, ck);
                if (!_dirty.TryGetValue(key, out var chunk))
                {
                    chunk = LoadChunkFromDisk(ci, cj, ck) ?? new float[ChunkLength];
                    if (_cacheMap.Remove(key, out var node)) _lru.Remove(node);
                    _dirty[key] = chunk;
                }
                int k0 = ck * _chunks[2];
                int len = Math.Min(_chunks[2], ns - k0);
                samples.Slice(k0, len).CopyTo(chunk.AsSpan((li * _chunks[1] + lj) * _chunks[2], len));
            }
            _live[(long)i * g.CrosslineCount + j] = 1;
            _liveDirty = true;
            // Bound memory: when a full slab of chunk rows is buffered, flush chunks not belonging to the current chunk row.
            if (_dirty.Count > 4 * _cacheCapacityChunks) FlushInternal(exceptChunkRow: ci);
        }
    }

    private void FlushInternal(int? exceptChunkRow)
    {
        foreach (var key in _dirty.Keys.ToList())
        {
            if (exceptChunkRow is { } row && key.Item1 == row) continue;
            WriteChunkToDisk(key.Item1, key.Item2, key.Item3, _dirty[key]);
            _dirty.Remove(key);
        }
        if (_liveDirty)
        {
            File.WriteAllBytes(Path.Combine(_root, "live.bin"), _live);
            _liveDirty = false;
        }
    }

    public void Flush()
    {
        lock (_sync) FlushInternal(null);
    }

    /// <summary>Total on-disk size of the chunk store.</summary>
    public long StorageBytes => Directory.EnumerateFiles(_root).Sum(f => new FileInfo(f).Length);

    public override void Dispose()
    {
        if (_writable) Flush();
        base.Dispose();
    }
}
