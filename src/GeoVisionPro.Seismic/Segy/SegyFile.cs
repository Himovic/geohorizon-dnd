using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace GeoVisionPro.Seismic.Segy;

/// <summary>Overrides applied when opening a SEG-Y file whose headers are wrong or incomplete.</summary>
public sealed record SegyOpenOptions
{
    public SegySampleFormat? FormatOverride { get; init; }
    public double? SampleIntervalOverride { get; init; }  // seconds
    public int? SampleCountOverride { get; init; }
    public bool? BigEndianOverride { get; init; }
}

/// <summary>
/// Random-access, thread-safe, memory-mapped SEG-Y reader. Only the requested traces are decoded, so
/// files much larger than physical memory can be accessed (the OS pages data on demand).
/// </summary>
public sealed unsafe class SegyFile : IDisposable
{
    public const int TextHeaderSize = 3200;
    public const int BinaryHeaderSize = 400;
    public const int TraceHeaderSize = 240;

    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte* _base;
    private bool _disposed;

    public string Path { get; }
    public long FileLength { get; }
    public bool BigEndian { get; }
    public string TextHeader { get; }
    public SegyBinaryHeader BinaryHeader { get; }
    public SegySampleFormat Format { get; }
    public int SamplesPerTrace { get; }
    /// <summary>Sample interval in seconds (or metres for depth data stored in µs-equivalent units /1e6).</summary>
    public double SampleInterval { get; }
    public double StartTime { get; }
    public long DataStart { get; }
    public int TraceSizeBytes { get; }
    public long TraceCount { get; }
    public List<string> Warnings { get; } = [];

    private SegyFile(string path, SegyOpenOptions options)
    {
        Path = path;
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("SEG-Y file not found.", path);
        FileLength = fi.Length;
        if (FileLength < TextHeaderSize + BinaryHeaderSize + TraceHeaderSize)
            throw new InvalidDataException("File is too small to be a SEG-Y file.");

        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;

        var text = new ReadOnlySpan<byte>(_base, TextHeaderSize);
        TextHeader = Ebcdic.DecodeTextHeader(text);
        var bin = new ReadOnlySpan<byte>(_base + TextHeaderSize, BinaryHeaderSize);

        BigEndian = options.BigEndianOverride ?? DetectBigEndian(bin);
        BinaryHeader = SegyBinaryHeader.Parse(bin, BigEndian);

        var fmt = options.FormatOverride ?? BinaryHeader.Format;
        if (!SegyEncoding.IsSupported((int)fmt))
        {
            Warnings.Add($"Unsupported or missing sample format code {(int)fmt} in binary header; assuming IBM float (1).");
            fmt = SegySampleFormat.IbmFloat32;
        }
        Format = fmt;

        int extHeaders = Math.Max(0, BinaryHeader.ExtendedTextHeaders);
        if (BinaryHeader.ExtendedTextHeaders == -1)
        {
            Warnings.Add("Variable number of extended textual headers (-1) is not supported; assuming none.");
            extHeaders = 0;
        }
        DataStart = TextHeaderSize + BinaryHeaderSize + 3200L * extHeaders;

        var firstTh = new ReadOnlySpan<byte>(_base + DataStart, TraceHeaderSize);
        int thSamples = BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(firstTh.Slice(114, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(firstTh.Slice(114, 2));
        int thDt = BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(firstTh.Slice(116, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(firstTh.Slice(116, 2));
        int delay = BigEndian ? BinaryPrimitives.ReadInt16BigEndian(firstTh.Slice(108, 2)) : BinaryPrimitives.ReadInt16LittleEndian(firstTh.Slice(108, 2));

        int ns = options.SampleCountOverride ?? (BinaryHeader.SamplesPerTrace > 0 ? BinaryHeader.SamplesPerTrace : thSamples);
        if (options.SampleCountOverride is null && BinaryHeader.SamplesPerTrace > 0 && thSamples > 0 && thSamples != BinaryHeader.SamplesPerTrace)
            Warnings.Add($"Binary header samples ({BinaryHeader.SamplesPerTrace}) differ from first trace header ({thSamples}); using binary header.");
        if (ns <= 0) throw new InvalidDataException("Number of samples per trace could not be determined.");
        SamplesPerTrace = ns;

        double dt;
        if (options.SampleIntervalOverride is { } dto) dt = dto;
        else if (BinaryHeader.ExtendedSampleInterval > 0) dt = BinaryHeader.ExtendedSampleInterval * 1e-6;
        else if (BinaryHeader.SampleIntervalMicro > 0) dt = BinaryHeader.SampleIntervalMicro * 1e-6;
        else if (thDt > 0) dt = thDt * 1e-6;
        else { dt = 0.004; Warnings.Add("Sample interval missing in headers; defaulting to 4 ms."); }
        SampleInterval = dt;
        StartTime = delay * 1e-3;

        TraceSizeBytes = TraceHeaderSize + ns * SegyEncoding.BytesPerSample(Format);
        long dataBytes = FileLength - DataStart;
        TraceCount = dataBytes / TraceSizeBytes;
        if (dataBytes % TraceSizeBytes != 0)
            Warnings.Add($"File size is not a whole number of traces ({dataBytes % TraceSizeBytes} trailing bytes ignored). Variable-length traces are not supported.");
    }

    public static SegyFile Open(string path, SegyOpenOptions? options = null) => new(path, options ?? new SegyOpenOptions());

    /// <summary>
    /// Byte order detection: rev2 byte-order constant if present, otherwise the interpretation that yields a
    /// plausible sample format code and sample count.
    /// </summary>
    private static bool DetectBigEndian(ReadOnlySpan<byte> bin)
    {
        int constBe = BinaryPrimitives.ReadInt32BigEndian(bin.Slice(3297 - 3201, 4));
        if (constBe == 16909060) return true;
        int constLe = BinaryPrimitives.ReadInt32LittleEndian(bin.Slice(3297 - 3201, 4));
        if (constLe == 16909060) return false;
        int fmtBe = BinaryPrimitives.ReadInt16BigEndian(bin.Slice(3225 - 3201, 2));
        int fmtLe = BinaryPrimitives.ReadInt16LittleEndian(bin.Slice(3225 - 3201, 2));
        bool beOk = SegyEncoding.IsSupported(fmtBe);
        bool leOk = SegyEncoding.IsSupported(fmtLe);
        if (beOk && !leOk) return true;
        if (leOk && !beOk) return false;
        return true; // SEG-Y default is big-endian
    }

    private void CheckTrace(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0 || index >= TraceCount) throw new ArgumentOutOfRangeException(nameof(index), $"Trace {index} outside 0..{TraceCount - 1}.");
    }

    public ReadOnlySpan<byte> TraceHeaderBytes(long index)
    {
        CheckTrace(index);
        return new ReadOnlySpan<byte>(_base + DataStart + index * TraceSizeBytes, TraceHeaderSize);
    }

    /// <summary>Reads samples of trace <paramref name="index"/> into <paramref name="destination"/> (length &gt;= SamplesPerTrace).</summary>
    public void ReadTrace(long index, Span<float> destination)
    {
        CheckTrace(index);
        var src = new ReadOnlySpan<byte>(_base + DataStart + index * TraceSizeBytes + TraceHeaderSize, TraceSizeBytes - TraceHeaderSize);
        SegyEncoding.DecodeSamples(src, destination[..SamplesPerTrace], Format, BigEndian);
    }

    public float[] ReadTrace(long index)
    {
        var t = new float[SamplesPerTrace];
        ReadTrace(index, t);
        return t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadHeaderInt(long index, int byte1Based, int size)
    {
        var h = TraceHeaderBytes(index);
        var s = h.Slice(byte1Based - 1, size);
        return size switch
        {
            2 => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
            4 => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            _ => s[0]
        };
    }

    /// <summary>Applies the SEG-Y coordinate scalar convention (positive multiplies, negative divides, 0 = 1).</summary>
    public static double ApplyScalar(int value, int scalar) => scalar switch
    {
        0 => value,
        > 0 => value * (double)scalar,
        _ => value / (double)-scalar
    };

    public TraceKeys ReadKeys(long index, SegyKeyLocations loc)
    {
        int il = ReadHeaderInt(index, loc.InlineByte, loc.InlineSize);
        int xl = ReadHeaderInt(index, loc.CrosslineByte, loc.CrosslineSize);
        int scalar = ReadHeaderInt(index, loc.CoordinateScalarByte, 2);
        double x = ApplyScalar(ReadHeaderInt(index, loc.CdpXByte, 4), scalar);
        double y = ApplyScalar(ReadHeaderInt(index, loc.CdpYByte, 4), scalar);
        int off = ReadHeaderInt(index, loc.OffsetByte, 4);
        int ns = ReadHeaderInt(index, 115, 2) & 0xFFFF;
        return new TraceKeys(il, xl, x, y, off, ns);
    }

    /// <summary>All standard trace header fields of one trace, for inspection.</summary>
    public IReadOnlyList<(SegyField Field, long Value)> ReadAllTraceFields(long index)
    {
        var h = TraceHeaderBytes(index).ToArray();
        return SegyFields.Trace.Select(f => (f, f.Read(h, 0, BigEndian))).ToList();
    }

    public IReadOnlyList<(SegyField Field, long Value)> ReadAllBinaryFields()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bin = new ReadOnlySpan<byte>(_base + TextHeaderSize, BinaryHeaderSize);
        var list = new List<(SegyField, long)>();
        foreach (var f in SegyFields.Binary)
        {
            if (f.Size == 8)
            {
                var d = BigEndian ? BinaryPrimitives.ReadDoubleBigEndian(bin.Slice(f.Byte - 3201, 8)) : BinaryPrimitives.ReadDoubleLittleEndian(bin.Slice(f.Byte - 3201, 8));
                list.Add((f, double.IsFinite(d) ? (long)d : 0));
            }
            else list.Add((f, f.Read(bin, 3200, BigEndian)));
        }
        return list;
    }

    public string Summary() =>
        $"{System.IO.Path.GetFileName(Path)}: {TraceCount:N0} traces × {SamplesPerTrace} samples, dt = {SampleInterval * 1000:0.###} ms, " +
        $"format {(int)Format} ({Format}), {(BigEndian ? "big" : "little")}-endian, rev {BinaryHeader.FormatRevisionMajor}.{BinaryHeader.FormatRevisionMinor}, {FileLength / 1048576.0:N1} MB";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view?.SafeMemoryMappedViewHandle.ReleasePointer();
        _view?.Dispose();
        _mmf?.Dispose();
    }
}
