using System.Buffers.Binary;
using System.Text;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Seismic.Segy;

/// <summary>Writes SEG-Y rev 1 files (big-endian) from GeoVision Pro volumes and gathers.</summary>
public static class SegyWriter
{
    public static string BuildTextHeader(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var line in lines.Take(40))
        {
            var l = $"C{i,2} {line}";
            sb.Append(l.Length > 80 ? l[..80] : l.PadRight(80));
            i++;
        }
        for (; i <= 40; i++) sb.Append($"C{i,2}".PadRight(80));
        return sb.ToString();
    }

    private static void WriteHeaders(Stream s, string textHeader, int ns, double dt, SegySampleFormat fmt, int sorting, int tracesPerEnsemble, bool ebcdic)
    {
        var text = textHeader.Length >= 3200 ? textHeader[..3200] : textHeader.PadRight(3200);
        s.Write(ebcdic ? Ebcdic.Encode(text) : Encoding.ASCII.GetBytes(text));
        var bin = new byte[400];
        // binary header uses offsets relative to byte 3201
        SegyBinaryHeader.Write(bin, (int)Math.Round(dt * 1e6), ns, fmt, sorting, 1, tracesPerEnsemble);
        s.Write(bin);
    }

    private static void FillTraceHeader(Span<byte> th, long seq, int il, int xl, double x, double y, int offset, int ns, double dt, double t0, int cdpTrace)
    {
        th.Clear();
        BinaryPrimitives.WriteInt32BigEndian(th[0..], (int)seq);
        BinaryPrimitives.WriteInt32BigEndian(th[4..], (int)seq);
        BinaryPrimitives.WriteInt32BigEndian(th[8..], il);
        BinaryPrimitives.WriteInt32BigEndian(th[12..], cdpTrace);
        BinaryPrimitives.WriteInt32BigEndian(th[20..], xl);
        BinaryPrimitives.WriteInt32BigEndian(th[24..], cdpTrace);
        BinaryPrimitives.WriteInt16BigEndian(th[28..], 1);
        BinaryPrimitives.WriteInt32BigEndian(th[36..], offset);
        BinaryPrimitives.WriteInt16BigEndian(th[70..], -100); // coordinates in centimetres precision
        BinaryPrimitives.WriteInt32BigEndian(th[72..], (int)Math.Round(x * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[76..], (int)Math.Round(y * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[80..], (int)Math.Round(x * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[84..], (int)Math.Round(y * 100));
        BinaryPrimitives.WriteInt16BigEndian(th[88..], 1);
        BinaryPrimitives.WriteInt16BigEndian(th[108..], (short)Math.Round(t0 * 1000));
        BinaryPrimitives.WriteUInt16BigEndian(th[114..], (ushort)Math.Min(ns, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(th[116..], (ushort)Math.Round(dt * 1e6));
        BinaryPrimitives.WriteInt32BigEndian(th[180..], (int)Math.Round(x * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[184..], (int)Math.Round(y * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[188..], il);
        BinaryPrimitives.WriteInt32BigEndian(th[192..], xl);
        BinaryPrimitives.WriteInt16BigEndian(th[200..], 1);
    }

    /// <summary>Writes a post-stack volume (inline-sorted).</summary>
    public static void WriteVolume(string path, ISeismicVolume volume, SegySampleFormat format = SegySampleFormat.IeeeFloat32,
        IEnumerable<string>? textLines = null, bool ebcdic = true, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = volume.Geometry;
        var lines = textLines?.ToList() ?? ["GeoVision Pro SEG-Y export", $"Inlines {g.Inlines} crosslines {g.Crosslines}",
            $"Samples {g.SampleCount} interval {g.SampleInterval * 1000:0.###} ms start {g.StartTime * 1000:0.###} ms",
            "Inline bytes 189-192, crossline bytes 193-196, CDP X/Y bytes 181-188 (scalar -100)", "SEG-Y REV1", "END EBCDIC"];
        int bps = SegyEncoding.BytesPerSample(format);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteHeaders(fs, BuildTextHeader(lines), g.SampleCount, g.SampleInterval, format, 4, 1, ebcdic);
        var th = new byte[240];
        var data = new byte[g.SampleCount * bps];
        var trace = new float[g.SampleCount];
        long seq = 1;
        for (int i = 0; i < g.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int il = g.Inlines.ValueAt(i);
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                int xl = g.Crosslines.ValueAt(j);
                if (!volume.TryReadTrace(il, xl, trace)) continue;
                var p = g.MapLocation(il, xl);
                FillTraceHeader(th, seq++, il, xl, p.X, p.Y, 0, g.SampleCount, g.SampleInterval, g.StartTime, 1);
                fs.Write(th);
                SegyEncoding.EncodeSamples(trace, data, format);
                fs.Write(data);
            }
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"Writing inline {il}"));
        }
    }

    /// <summary>Writes a pre-stack gather dataset (CDP-sorted, offset in bytes 37-40).</summary>
    public static void WriteGathers(string path, IGatherDataset gathers, SegySampleFormat format = SegySampleFormat.IeeeFloat32,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = gathers.Geometry;
        int bps = SegyEncoding.BytesPerSample(format);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        WriteHeaders(fs, BuildTextHeader(["GeoVision Pro pre-stack gather export", "Offset in bytes 37-40", "SEG-Y REV1"]),
            g.SampleCount, g.SampleInterval, format, 2, gathers.Offsets.Length, true);
        var th = new byte[240];
        var data = new byte[g.SampleCount * bps];
        long seq = 1;
        for (int i = 0; i < g.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int il = g.Inlines.ValueAt(i);
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                int xl = g.Crosslines.ValueAt(j);
                var gather = gathers.ReadGather(il, xl);
                if (gather is null) continue;
                var p = g.MapLocation(il, xl);
                for (int k = 0; k < gather.Rows; k++)
                {
                    FillTraceHeader(th, seq++, il, xl, p.X, p.Y, (int)Math.Round(gathers.Offsets[k]), g.SampleCount, g.SampleInterval, g.StartTime, k + 1);
                    fs.Write(th);
                    SegyEncoding.EncodeSamples(gather.Row(k), data, format);
                    fs.Write(data);
                }
            }
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"Writing gathers inline {il}"));
        }
    }
}

/// <summary>Sequential SEG-Y writer used to stream processing outputs (e.g. processed gathers) to disk.</summary>
public sealed class SegyStreamWriter : IDisposable
{
    private readonly FileStream _fs;
    private readonly SegySampleFormat _format;
    private readonly int _ns;
    private readonly double _dt, _t0;
    private readonly byte[] _th = new byte[240];
    private readonly byte[] _data;
    private long _seq = 1;
    public long TracesWritten => _seq - 1;
    public string Path { get; }

    public SegyStreamWriter(string path, int sampleCount, double sampleInterval, double startTime, IEnumerable<string> textLines,
        int sorting = 2, int tracesPerEnsemble = 1, SegySampleFormat format = SegySampleFormat.IeeeFloat32)
    {
        Path = path; _format = format; _ns = sampleCount; _dt = sampleInterval; _t0 = startTime;
        _data = new byte[sampleCount * SegyEncoding.BytesPerSample(format)];
        _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var text = SegyWriter.BuildTextHeader(textLines);
        _fs.Write(Ebcdic.Encode(text));
        var bin = new byte[400];
        SegyBinaryHeader.Write(bin, (int)Math.Round(sampleInterval * 1e6), sampleCount, format, sorting, 1, tracesPerEnsemble);
        _fs.Write(bin);
    }

    public void AppendTrace(int inline, int crossline, double x, double y, int offset, int traceInEnsemble, ReadOnlySpan<float> samples)
    {
        var th = _th.AsSpan();
        th.Clear();
        BinaryPrimitives.WriteInt32BigEndian(th[0..], (int)_seq);
        BinaryPrimitives.WriteInt32BigEndian(th[4..], (int)_seq);
        BinaryPrimitives.WriteInt32BigEndian(th[8..], inline);
        BinaryPrimitives.WriteInt32BigEndian(th[12..], traceInEnsemble);
        BinaryPrimitives.WriteInt32BigEndian(th[20..], crossline);
        BinaryPrimitives.WriteInt32BigEndian(th[24..], traceInEnsemble);
        BinaryPrimitives.WriteInt16BigEndian(th[28..], 1);
        BinaryPrimitives.WriteInt32BigEndian(th[36..], offset);
        BinaryPrimitives.WriteInt16BigEndian(th[70..], -100);
        BinaryPrimitives.WriteInt32BigEndian(th[180..], (int)Math.Round(x * 100));
        BinaryPrimitives.WriteInt32BigEndian(th[184..], (int)Math.Round(y * 100));
        BinaryPrimitives.WriteInt16BigEndian(th[108..], (short)Math.Round(_t0 * 1000));
        BinaryPrimitives.WriteUInt16BigEndian(th[114..], (ushort)Math.Min(_ns, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(th[116..], (ushort)Math.Round(_dt * 1e6));
        BinaryPrimitives.WriteInt32BigEndian(th[188..], inline);
        BinaryPrimitives.WriteInt32BigEndian(th[192..], crossline);
        _fs.Write(_th);
        SegyEncoding.EncodeSamples(samples[.._ns], _data, _format);
        _fs.Write(_data);
        _seq++;
    }

    public void Dispose() => _fs.Dispose();
}
