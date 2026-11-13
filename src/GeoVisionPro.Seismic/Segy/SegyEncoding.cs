using System.Buffers.Binary;
using System.Text;

namespace GeoVisionPro.Seismic.Segy;

/// <summary>SEG-Y sample format codes (binary header bytes 3225-3226).</summary>
public enum SegySampleFormat : short
{
    IbmFloat32 = 1,
    Int32 = 2,
    Int16 = 3,
    IeeeFloat32 = 5,
    IeeeFloat64 = 6,
    Int8 = 8,
    UInt32 = 10,
    UInt16 = 11,
    UInt8 = 16,
}

public static class SegyEncoding
{
    private static readonly double[] IbmScale = BuildIbmScale();

    private static double[] BuildIbmScale()
    {
        var t = new double[128];
        for (int e = 0; e < 128; e++) t[e] = Math.Pow(16.0, e - 64) / 16777216.0;
        return t;
    }

    public static int BytesPerSample(SegySampleFormat f) => f switch
    {
        SegySampleFormat.IbmFloat32 or SegySampleFormat.Int32 or SegySampleFormat.IeeeFloat32 or SegySampleFormat.UInt32 => 4,
        SegySampleFormat.Int16 or SegySampleFormat.UInt16 => 2,
        SegySampleFormat.IeeeFloat64 => 8,
        SegySampleFormat.Int8 or SegySampleFormat.UInt8 => 1,
        _ => 0
    };

    public static bool IsSupported(int code) => code is 1 or 2 or 3 or 5 or 6 or 8 or 10 or 11 or 16;

    /// <summary>Converts an IBM System/360 single precision float (as raw 32-bit word) to IEEE single.</summary>
    public static float IbmToIeee(uint ibm)
    {
        uint mant = ibm & 0x00FFFFFF;
        if (mant == 0) return 0f;
        int exp = (int)((ibm >> 24) & 0x7F);
        double v = mant * IbmScale[exp];
        return (float)((ibm & 0x80000000) != 0 ? -v : v);
    }

    /// <summary>Converts an IEEE single to the nearest IBM System/360 single precision word.</summary>
    public static uint IeeeToIbm(float value)
    {
        if (value == 0 || float.IsNaN(value)) return 0;
        uint sign = value < 0 ? 0x80000000u : 0u;
        double a = Math.Abs((double)value);
        int exp = 64;
        while (a >= 1.0) { a /= 16.0; exp++; }
        while (a < 0.0625) { a *= 16.0; exp--; }
        uint mant = (uint)Math.Round(a * 16777216.0);
        if (mant >= 0x01000000) { mant >>= 4; exp++; }
        if (exp > 127) return sign | 0x7FFFFFFF;
        if (exp < 0) return 0;
        return sign | ((uint)exp << 24) | (mant & 0x00FFFFFF);
    }

    /// <summary>Decodes a block of samples to IEEE floats.</summary>
    public static void DecodeSamples(ReadOnlySpan<byte> src, Span<float> dst, SegySampleFormat format, bool bigEndian)
    {
        int n = dst.Length;
        switch (format)
        {
            case SegySampleFormat.IbmFloat32:
                for (int i = 0; i < n; i++)
                {
                    uint w = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(src.Slice(i * 4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(i * 4, 4));
                    dst[i] = IbmToIeee(w);
                }
                break;
            case SegySampleFormat.IeeeFloat32:
                for (int i = 0; i < n; i++)
                    dst[i] = bigEndian ? BinaryPrimitives.ReadSingleBigEndian(src.Slice(i * 4, 4)) : BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
                break;
            case SegySampleFormat.IeeeFloat64:
                for (int i = 0; i < n; i++)
                    dst[i] = (float)(bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(src.Slice(i * 8, 8)) : BinaryPrimitives.ReadDoubleLittleEndian(src.Slice(i * 8, 8)));
                break;
            case SegySampleFormat.Int32:
                for (int i = 0; i < n; i++)
                    dst[i] = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(src.Slice(i * 4, 4)) : BinaryPrimitives.ReadInt32LittleEndian(src.Slice(i * 4, 4));
                break;
            case SegySampleFormat.UInt32:
                for (int i = 0; i < n; i++)
                    dst[i] = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(src.Slice(i * 4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(i * 4, 4));
                break;
            case SegySampleFormat.Int16:
                for (int i = 0; i < n; i++)
                    dst[i] = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(src.Slice(i * 2, 2)) : BinaryPrimitives.ReadInt16LittleEndian(src.Slice(i * 2, 2));
                break;
            case SegySampleFormat.UInt16:
                for (int i = 0; i < n; i++)
                    dst[i] = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(src.Slice(i * 2, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
                break;
            case SegySampleFormat.Int8:
                for (int i = 0; i < n; i++) dst[i] = (sbyte)src[i];
                break;
            case SegySampleFormat.UInt8:
                for (int i = 0; i < n; i++) dst[i] = src[i];
                break;
            default:
                throw new NotSupportedException($"SEG-Y sample format {(int)format} is not supported.");
        }
    }

    /// <summary>Encodes IEEE floats into the given SEG-Y sample format (big-endian).</summary>
    public static void EncodeSamples(ReadOnlySpan<float> src, Span<byte> dst, SegySampleFormat format)
    {
        switch (format)
        {
            case SegySampleFormat.IbmFloat32:
                for (int i = 0; i < src.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(dst.Slice(i * 4, 4), IeeeToIbm(src[i]));
                break;
            case SegySampleFormat.IeeeFloat32:
                for (int i = 0; i < src.Length; i++) BinaryPrimitives.WriteSingleBigEndian(dst.Slice(i * 4, 4), float.IsNaN(src[i]) ? 0f : src[i]);
                break;
            case SegySampleFormat.Int16:
                for (int i = 0; i < src.Length; i++) BinaryPrimitives.WriteInt16BigEndian(dst.Slice(i * 2, 2), (short)Math.Clamp(Math.Round(src[i]), short.MinValue, short.MaxValue));
                break;
            case SegySampleFormat.Int32:
                for (int i = 0; i < src.Length; i++) BinaryPrimitives.WriteInt32BigEndian(dst.Slice(i * 4, 4), (int)Math.Clamp(Math.Round((double)src[i]), int.MinValue, int.MaxValue));
                break;
            default:
                throw new NotSupportedException($"Writing SEG-Y format {(int)format} is not supported.");
        }
    }
}

/// <summary>EBCDIC (code page 037) &lt;-&gt; ASCII conversion for SEG-Y textual headers, without relying on code-page providers.</summary>
public static class Ebcdic
{
    private static readonly byte[] EbcdicToAsciiTable = BuildE2A();
    private static readonly byte[] AsciiToEbcdicTable = BuildA2E();

    private static byte[] BuildE2A()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = (byte)' ';
        void Map(int e, char a) => t[e] = (byte)a;
        Map(0x40, ' '); Map(0x4B, '.'); Map(0x4C, '<'); Map(0x4D, '('); Map(0x4E, '+'); Map(0x4F, '|'); Map(0x50, '&');
        Map(0x5A, '!'); Map(0x5B, '$'); Map(0x5C, '*'); Map(0x5D, ')'); Map(0x5E, ';'); Map(0x60, '-'); Map(0x61, '/');
        Map(0x6B, ','); Map(0x6C, '%'); Map(0x6D, '_'); Map(0x6E, '>'); Map(0x6F, '?'); Map(0x7A, ':'); Map(0x7B, '#');
        Map(0x7C, '@'); Map(0x7D, '\''); Map(0x7E, '='); Map(0x7F, '"'); Map(0xBA, '['); Map(0xBB, ']'); Map(0xAD, '['); Map(0xBD, ']');
        Map(0xA1, '~'); Map(0xC0, '{'); Map(0xD0, '}'); Map(0xE0, '\\'); Map(0x5F, '^'); Map(0x79, '`');
        for (int i = 0; i < 9; i++) { Map(0x81 + i, (char)('a' + i)); Map(0xC1 + i, (char)('A' + i)); }
        for (int i = 0; i < 9; i++) { Map(0x91 + i, (char)('j' + i)); Map(0xD1 + i, (char)('J' + i)); }
        for (int i = 0; i < 8; i++) { Map(0xA2 + i, (char)('s' + i)); Map(0xE2 + i, (char)('S' + i)); }
        for (int i = 0; i < 10; i++) Map(0xF0 + i, (char)('0' + i));
        return t;
    }

    private static byte[] BuildA2E()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++) t[i] = 0x40;
        for (int e = 255; e >= 0; e--)
        {
            byte a = EbcdicToAsciiTable[e];
            if (a != (byte)' ' || e == 0x40) t[a] = (byte)e;
        }
        return t;
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = (char)EbcdicToAsciiTable[bytes[i]];
        return new string(chars);
    }

    public static byte[] Encode(string text)
    {
        var b = new byte[text.Length];
        for (int i = 0; i < text.Length; i++) b[i] = AsciiToEbcdicTable[text[i] & 0xFF];
        return b;
    }

    /// <summary>Heuristic: textual header is EBCDIC when it contains many EBCDIC letter/space codes and few printable ASCII bytes.</summary>
    public static bool LooksLikeEbcdic(ReadOnlySpan<byte> header)
    {
        int ascii = 0, ebcdic = 0;
        foreach (var b in header)
        {
            if (b >= 0x20 && b < 0x7F) ascii++;
            if (b == 0x40 || (b >= 0xC1 && b <= 0xE9) || (b >= 0xF0 && b <= 0xF9) || (b >= 0x81 && b <= 0xA9)) ebcdic++;
        }
        return ebcdic > ascii;
    }

    public static string DecodeTextHeader(ReadOnlySpan<byte> header)
    {
        string raw = LooksLikeEbcdic(header) ? Decode(header) : Encoding.ASCII.GetString(header);
        var sb = new StringBuilder();
        for (int line = 0; line * 80 < raw.Length; line++)
        {
            int len = Math.Min(80, raw.Length - line * 80);
            sb.AppendLine(raw.Substring(line * 80, len).Replace('\0', ' ').TrimEnd());
        }
        return sb.ToString();
    }
}
