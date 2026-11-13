using System.Buffers.Binary;

namespace GeoVisionPro.Seismic.Segy;

/// <summary>A header field definition: 1-based byte position (as in the SEG-Y standard) and size in bytes.</summary>
public sealed record SegyField(string Name, int Byte, int Size, string Description)
{
    public long Read(ReadOnlySpan<byte> header, int baseOffset, bool bigEndian)
    {
        var s = header.Slice(Byte - 1 - baseOffset, Size);
        return Size switch
        {
            2 => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
            4 => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
            8 => bigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
            1 => s[0],
            _ => 0
        };
    }
}

/// <summary>Standard SEG-Y rev 1/2 header field tables for inspection and configurable key mapping.</summary>
public static class SegyFields
{
    public static readonly IReadOnlyList<SegyField> Binary =
    [
        new("JobId", 3201, 4, "Job identification number"),
        new("LineNumber", 3205, 4, "Line number"),
        new("ReelNumber", 3209, 4, "Reel number"),
        new("TracesPerEnsemble", 3213, 2, "Data traces per ensemble"),
        new("AuxTracesPerEnsemble", 3215, 2, "Auxiliary traces per ensemble"),
        new("SampleInterval", 3217, 2, "Sample interval (µs or depth units)"),
        new("SampleIntervalOriginal", 3219, 2, "Original field sample interval"),
        new("SamplesPerTrace", 3221, 2, "Samples per data trace"),
        new("SamplesPerTraceOriginal", 3223, 2, "Original samples per trace"),
        new("SampleFormat", 3225, 2, "Data sample format code"),
        new("EnsembleFold", 3227, 2, "Ensemble fold"),
        new("TraceSorting", 3229, 2, "Trace sorting code"),
        new("VerticalSum", 3231, 2, "Vertical sum code"),
        new("SweepFrequencyStart", 3233, 2, "Sweep frequency at start"),
        new("SweepFrequencyEnd", 3235, 2, "Sweep frequency at end"),
        new("SweepLength", 3237, 2, "Sweep length (ms)"),
        new("SweepType", 3239, 2, "Sweep type code"),
        new("CorrelatedTraces", 3249, 2, "Correlated data traces"),
        new("BinaryGainRecovered", 3251, 2, "Binary gain recovered"),
        new("AmplitudeRecovery", 3253, 2, "Amplitude recovery method"),
        new("MeasurementSystem", 3255, 2, "Measurement system (1=m, 2=ft)"),
        new("ImpulsePolarity", 3257, 2, "Impulse signal polarity"),
        new("VibratoryPolarity", 3259, 2, "Vibratory polarity code"),
        new("ExtTracesPerEnsemble", 3261, 4, "Extended data traces per ensemble (rev2)"),
        new("ExtAuxTraces", 3265, 4, "Extended auxiliary traces (rev2)"),
        new("ExtSamplesPerTrace", 3269, 4, "Extended samples per trace (rev2)"),
        new("ExtSampleInterval", 3273, 8, "Extended sample interval, IEEE double (rev2)"),
        new("ByteOrderConstant", 3297, 4, "Byte order constant 16909060 (rev2)"),
        new("FormatRevision", 3501, 2, "SEG-Y format revision number"),
        new("FixedLengthTraces", 3503, 2, "Fixed length trace flag"),
        new("ExtendedTextHeaders", 3505, 2, "Number of extended textual headers"),
    ];

    public static readonly IReadOnlyList<SegyField> Trace =
    [
        new("TraceSequenceLine", 1, 4, "Trace sequence number within line"),
        new("TraceSequenceFile", 5, 4, "Trace sequence number within file"),
        new("FieldRecord", 9, 4, "Original field record number"),
        new("TraceNumber", 13, 4, "Trace number within field record"),
        new("EnergySourcePoint", 17, 4, "Energy source point number"),
        new("Cdp", 21, 4, "Ensemble (CDP) number"),
        new("CdpTrace", 25, 4, "Trace number within ensemble"),
        new("TraceIdCode", 29, 2, "Trace identification code"),
        new("NSummedTraces", 31, 2, "Vertically summed traces"),
        new("NStackedTraces", 33, 2, "Horizontally stacked traces"),
        new("DataUse", 35, 2, "Data use"),
        new("Offset", 37, 4, "Source-receiver offset"),
        new("ReceiverElevation", 41, 4, "Receiver group elevation"),
        new("SourceElevation", 45, 4, "Surface elevation at source"),
        new("SourceDepth", 49, 4, "Source depth below surface"),
        new("ReceiverDatum", 53, 4, "Datum elevation at receiver"),
        new("SourceDatum", 57, 4, "Datum elevation at source"),
        new("SourceWaterDepth", 61, 4, "Water depth at source"),
        new("GroupWaterDepth", 65, 4, "Water depth at group"),
        new("ElevationScalar", 69, 2, "Scalar for elevations and depths"),
        new("CoordinateScalar", 71, 2, "Scalar for coordinates"),
        new("SourceX", 73, 4, "Source coordinate X"),
        new("SourceY", 77, 4, "Source coordinate Y"),
        new("GroupX", 81, 4, "Group coordinate X"),
        new("GroupY", 85, 4, "Group coordinate Y"),
        new("CoordinateUnits", 89, 2, "Coordinate units"),
        new("WeatheringVelocity", 91, 2, "Weathering velocity"),
        new("SubWeatheringVelocity", 93, 2, "Sub-weathering velocity"),
        new("SourceUpholeTime", 95, 2, "Uphole time at source"),
        new("GroupUpholeTime", 97, 2, "Uphole time at group"),
        new("SourceStatic", 99, 2, "Source static correction"),
        new("GroupStatic", 101, 2, "Group static correction"),
        new("TotalStatic", 103, 2, "Total static applied"),
        new("LagTimeA", 105, 2, "Lag time A"),
        new("LagTimeB", 107, 2, "Lag time B"),
        new("DelayRecordingTime", 109, 2, "Delay recording time (ms)"),
        new("MuteStart", 111, 2, "Mute time start"),
        new("MuteEnd", 113, 2, "Mute time end"),
        new("SampleCount", 115, 2, "Number of samples in this trace"),
        new("SampleInterval", 117, 2, "Sample interval for this trace (µs)"),
        new("GainType", 119, 2, "Gain type"),
        new("LowCutFrequency", 149, 2, "Low cut frequency"),
        new("HighCutFrequency", 151, 2, "High cut frequency"),
        new("Year", 157, 2, "Year data recorded"),
        new("DayOfYear", 159, 2, "Day of year"),
        new("Hour", 161, 2, "Hour"),
        new("Minute", 163, 2, "Minute"),
        new("Second", 165, 2, "Second"),
        new("CdpX", 181, 4, "CDP X coordinate"),
        new("CdpY", 185, 4, "CDP Y coordinate"),
        new("Inline", 189, 4, "3D inline number"),
        new("Crossline", 193, 4, "3D crossline number"),
        new("ShotPoint", 197, 4, "Shotpoint number"),
        new("ShotPointScalar", 201, 2, "Shotpoint scalar"),
        new("TraceValueUnit", 203, 2, "Trace value measurement unit"),
        new("TraceIdentifier", 213, 2, "Device/trace identifier"),
        new("TraceHeaderScalar", 215, 2, "Scalar for bytes 95-114"),
        new("SourceType", 217, 2, "Source type/orientation"),
        new("Unassigned1", 233, 4, "Unassigned"),
        new("Unassigned2", 237, 4, "Unassigned"),
    ];

    public static SegyField? FindTrace(string name) => Trace.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Parsed SEG-Y binary file header (key fields).</summary>
public sealed record SegyBinaryHeader(
    int JobId,
    int LineNumber,
    int TracesPerEnsemble,
    int SampleIntervalMicro,
    int SamplesPerTrace,
    SegySampleFormat Format,
    int EnsembleFold,
    int TraceSorting,
    int MeasurementSystem,
    int FormatRevisionMajor,
    int FormatRevisionMinor,
    bool FixedLengthTraces,
    int ExtendedTextHeaders,
    double ExtendedSampleInterval)
{
    public static SegyBinaryHeader Parse(ReadOnlySpan<byte> binSpan, bool bigEndian)
    {
        byte[] bin = binSpan.ToArray();
        int I16(int b) => bigEndian ? BinaryPrimitives.ReadInt16BigEndian(bin.AsSpan(b - 3201, 2)) : BinaryPrimitives.ReadInt16LittleEndian(bin.AsSpan(b - 3201, 2));
        int U16(int b) => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bin.AsSpan(b - 3201, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan(b - 3201, 2));
        int I32(int b) => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(bin.AsSpan(b - 3201, 4)) : BinaryPrimitives.ReadInt32LittleEndian(bin.AsSpan(b - 3201, 4));
        double F64(int b) => bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(bin.AsSpan(b - 3201, 8)) : BinaryPrimitives.ReadDoubleLittleEndian(bin.AsSpan(b - 3201, 8));

        int samples = U16(3221);
        int extSamples = I32(3269);
        int revWord = U16(3501);
        int major = (revWord >> 8) & 0xFF, minor = revWord & 0xFF;
        if (major >= 2 && extSamples > 0 && samples == 0) samples = extSamples;
        double extDt = major >= 2 ? F64(3273) : 0;
        if (double.IsNaN(extDt) || extDt < 0 || extDt > 1e7) extDt = 0;
        int extText = I16(3505);
        if (extText < 0 && extText != -1) extText = 0;
        return new SegyBinaryHeader(I32(3201), I32(3205), I16(3213), U16(3217), samples, (SegySampleFormat)I16(3225),
            I16(3227), I16(3229), I16(3255), major, minor, I16(3503) == 1, extText, extDt);
    }

    public static void Write(Span<byte> bin, int sampleIntervalMicro, int samples, SegySampleFormat format, int sorting, int measurementSystem, int tracesPerEnsemble = 1)
    {
        bin.Clear();
        BinaryPrimitives.WriteInt32BigEndian(bin.Slice(3201 - 3201, 4), 1);
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3213 - 3201, 2), (short)tracesPerEnsemble);
        BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3217 - 3201, 2), (ushort)sampleIntervalMicro);
        BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3219 - 3201, 2), (ushort)sampleIntervalMicro);
        BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3221 - 3201, 2), (ushort)Math.Min(samples, 65535));
        BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3223 - 3201, 2), (ushort)Math.Min(samples, 65535));
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3225 - 3201, 2), (short)format);
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3227 - 3201, 2), (short)tracesPerEnsemble);
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3229 - 3201, 2), (short)sorting);
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3255 - 3201, 2), (short)measurementSystem);
        BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3501 - 3201, 2), 0x0100); // rev 1.0
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3503 - 3201, 2), 1);
        BinaryPrimitives.WriteInt16BigEndian(bin.Slice(3505 - 3201, 2), 0);
        if (samples > 65535)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bin.Slice(3501 - 3201, 2), 0x0200);
            BinaryPrimitives.WriteInt32BigEndian(bin.Slice(3269 - 3201, 4), samples);
        }
    }
}

/// <summary>Byte locations of the key trace header words used to build geometry. Defaults follow SEG-Y rev 1.</summary>
public sealed record SegyKeyLocations
{
    public int InlineByte { get; init; } = 189;
    public int CrosslineByte { get; init; } = 193;
    public int CdpXByte { get; init; } = 181;
    public int CdpYByte { get; init; } = 185;
    public int CoordinateScalarByte { get; init; } = 71;
    public int OffsetByte { get; init; } = 37;
    public int InlineSize { get; init; } = 4;
    public int CrosslineSize { get; init; } = 4;

    public static SegyKeyLocations Rev1 { get; } = new();

    /// <summary>Common alternative layouts tried by auto-detection.</summary>
    public static IReadOnlyList<SegyKeyLocations> Candidates { get; } =
    [
        new(),
        new() { InlineByte = 9, CrosslineByte = 21 },
        new() { InlineByte = 5, CrosslineByte = 21 },
        new() { InlineByte = 9, CrosslineByte = 13 },
        new() { InlineByte = 17, CrosslineByte = 13 },
        new() { InlineByte = 221, CrosslineByte = 21 },
        new() { InlineByte = 189, CrosslineByte = 21 },
    ];

    public override string ToString() => $"IL@{InlineByte} XL@{CrosslineByte} X@{CdpXByte} Y@{CdpYByte} Scalar@{CoordinateScalarByte} Offset@{OffsetByte}";
}

/// <summary>Key values decoded from one trace header.</summary>
public readonly record struct TraceKeys(int Inline, int Crossline, double X, double Y, int Offset, int SampleCount);
