using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;

namespace GeoVisionPro.Seismic.Volumes;

/// <summary>Fully in-memory volume [inline][crossline][sample]; intended for small volumes, tests and QC outputs.</summary>
public sealed class InMemoryVolume : SeismicVolumeBase, IWritableSeismicVolume
{
    private readonly float[] _data;
    private readonly bool[] _live;
    private readonly SurveyGeometry _geometry;
    public override SurveyGeometry Geometry => _geometry;

    public InMemoryVolume(string name, SurveyGeometry geometry)
    {
        long n = geometry.TraceCount * geometry.SampleCount;
        if (n > int.MaxValue) throw new InvalidOperationException("Volume too large for in-memory storage; use a chunked store.");
        Name = name;
        _geometry = geometry;
        _data = new float[n];
        _live = new bool[geometry.TraceCount];
    }

    private int TraceSlot(int inline, int crossline)
    {
        if (!_geometry.ContainsTrace(inline, crossline)) return -1;
        return _geometry.InlineIndex(inline) * _geometry.CrosslineCount + _geometry.CrosslineIndex(crossline);
    }

    public override bool TryReadTrace(int inline, int crossline, Span<float> destination)
    {
        int slot = TraceSlot(inline, crossline);
        int ns = _geometry.SampleCount;
        if (slot < 0 || !_live[slot]) { destination[..ns].Clear(); return false; }
        _data.AsSpan(slot * ns, ns).CopyTo(destination);
        return true;
    }

    public void WriteTrace(int inline, int crossline, ReadOnlySpan<float> samples)
    {
        int slot = TraceSlot(inline, crossline);
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(inline), $"Trace ({inline},{crossline}) is outside the volume geometry.");
        int ns = _geometry.SampleCount;
        samples[..ns].CopyTo(_data.AsSpan(slot * ns, ns));
        _live[slot] = true;
    }

    public Span<float> TraceSpan(int inlineIndex, int crosslineIndex)
    {
        int slot = inlineIndex * _geometry.CrosslineCount + crosslineIndex;
        _live[slot] = true;
        return _data.AsSpan(slot * _geometry.SampleCount, _geometry.SampleCount);
    }

    public override Array2D ReadTimeSlice(int sampleIndex)
    {
        var g = _geometry;
        var a = new Array2D(g.InlineCount, g.CrosslineCount);
        int ns = g.SampleCount;
        sampleIndex = Math.Clamp(sampleIndex, 0, ns - 1);
        for (int s = 0; s < g.TraceCount; s++) a.Data[s] = _live[s] ? _data[(long)s * ns + sampleIndex] : 0f;
        return a;
    }

    public void Flush() { }
}

/// <summary>
/// Post-stack volume backed directly by a memory-mapped SEG-Y file and the trace index built by <see cref="SegyScanner"/>.
/// No sample data is loaded until requested.
/// </summary>
public sealed class SegyVolume : SeismicVolumeBase
{
    private readonly SegyFile _file;
    private readonly SegyScanResult _scan;
    private readonly bool _ownsFile;
    public override SurveyGeometry Geometry => _scan.Geometry;
    public SegyFile File => _file;
    public SegyScanResult Scan => _scan;

    public SegyVolume(string name, SegyFile file, SegyScanResult scan, bool ownsFile = true)
    {
        Name = name; _file = file; _scan = scan; _ownsFile = ownsFile;
    }

    public static SegyVolume Open(string path, SegyKeyLocations? keys = null, SegyOpenOptions? options = null,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var f = SegyFile.Open(path, options);
        try
        {
            var scan = SegyScanner.Scan(f, keys, progress, ct);
            return new SegyVolume(Path.GetFileNameWithoutExtension(path), f, scan);
        }
        catch { f.Dispose(); throw; }
    }

    public override bool TryReadTrace(int inline, int crossline, Span<float> destination)
    {
        var g = _scan.Geometry;
        int ns = g.SampleCount;
        if (!g.ContainsTrace(inline, crossline)) { destination[..ns].Clear(); return false; }
        long t = _scan.Lookup(g.InlineIndex(inline), g.CrosslineIndex(crossline));
        if (t < 0) { destination[..ns].Clear(); return false; }
        _file.ReadTrace(t, destination);
        return true;
    }

    public override void Dispose()
    {
        if (_ownsFile) _file.Dispose();
        base.Dispose();
    }
}

/// <summary>Pre-stack gathers read from a memory-mapped SEG-Y file.</summary>
public sealed class SegyGatherDataset : IGatherDataset
{
    private readonly SegyFile _file;
    private readonly SegyScanResult _scan;
    public string Name { get; }
    public SurveyGeometry Geometry => _scan.Geometry;
    public double[] Offsets { get; }
    public bool IsAngleGather { get; }

    public SegyGatherDataset(string name, SegyFile file, SegyScanResult scan, bool isAngle)
    {
        Name = name; _file = file; _scan = scan; Offsets = scan.Offsets; IsAngleGather = isAngle;
    }

    public Array2D? ReadGather(int inline, int crossline)
    {
        var g = Geometry;
        if (!g.ContainsTrace(inline, crossline)) return null;
        int ii = g.InlineIndex(inline), jj = g.CrosslineIndex(crossline);
        var a = new Array2D(_scan.Fold, g.SampleCount);
        bool any = false;
        for (int k = 0; k < _scan.Fold; k++)
        {
            long t = _scan.Lookup(ii, jj, k);
            if (t < 0) continue;
            _file.ReadTrace(t, a.Row(k));
            any = true;
        }
        return any ? a : null;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>In-memory gathers used for synthetic modelling, processing outputs and tests.</summary>
public sealed class InMemoryGatherDataset : IGatherDataset
{
    private readonly Dictionary<(int, int), Array2D> _gathers = [];
    public string Name { get; }
    public SurveyGeometry Geometry { get; }
    public double[] Offsets { get; }
    public bool IsAngleGather { get; }

    public InMemoryGatherDataset(string name, SurveyGeometry geometry, double[] offsets, bool isAngle)
    {
        Name = name; Geometry = geometry; Offsets = offsets; IsAngleGather = isAngle;
    }

    public void SetGather(int inline, int crossline, Array2D gather)
    {
        if (gather.Rows != Offsets.Length || gather.Cols != Geometry.SampleCount) throw new ArgumentException("Gather dimensions do not match dataset.");
        _gathers[(inline, crossline)] = gather;
    }

    public Array2D? ReadGather(int inline, int crossline) => _gathers.TryGetValue((inline, crossline), out var g) ? g.Clone() : null;
    public void Dispose() { }
}

/// <summary>Volume computed lazily trace-by-trace from another volume (used for on-the-fly previews).</summary>
public sealed class DerivedVolume : SeismicVolumeBase
{
    private readonly ISeismicVolume _source;
    private readonly Action<int, int, Span<float>> _op;
    public override SurveyGeometry Geometry => _source.Geometry;

    public DerivedVolume(string name, ISeismicVolume source, Action<int, int, Span<float>> op)
    {
        Name = name; _source = source; _op = op;
    }

    public override bool TryReadTrace(int inline, int crossline, Span<float> destination)
    {
        if (!_source.TryReadTrace(inline, crossline, destination)) return false;
        _op(inline, crossline, destination[..Geometry.SampleCount]);
        return true;
    }
}
