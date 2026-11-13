namespace GeoVisionPro.Core.Units;

/// <summary>
/// Unit conversion for quantities used in geophysics and petrophysics. Units are identified by
/// case-insensitive mnemonics as they commonly appear in LAS headers.
/// </summary>
public static class UnitConverter
{
    public const double FeetToMeters = 0.3048;

    private enum Quantity { Length, Slowness, Velocity, Density, Time, Ratio, Unknown }

    private static readonly Dictionary<string, (Quantity Q, double ToSi)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // length -> m
        ["m"] = (Quantity.Length, 1), ["meter"] = (Quantity.Length, 1), ["meters"] = (Quantity.Length, 1), ["metres"] = (Quantity.Length, 1),
        ["ft"] = (Quantity.Length, FeetToMeters), ["f"] = (Quantity.Length, FeetToMeters), ["feet"] = (Quantity.Length, FeetToMeters),
        ["km"] = (Quantity.Length, 1000),
        // slowness -> s/m
        ["us/ft"] = (Quantity.Slowness, 1e-6 / FeetToMeters), ["usec/ft"] = (Quantity.Slowness, 1e-6 / FeetToMeters), ["us/f"] = (Quantity.Slowness, 1e-6 / FeetToMeters),
        ["us/m"] = (Quantity.Slowness, 1e-6), ["usec/m"] = (Quantity.Slowness, 1e-6), ["s/m"] = (Quantity.Slowness, 1),
        // velocity -> m/s
        ["m/s"] = (Quantity.Velocity, 1), ["ft/s"] = (Quantity.Velocity, FeetToMeters), ["km/s"] = (Quantity.Velocity, 1000), ["f/s"] = (Quantity.Velocity, FeetToMeters),
        // density -> kg/m3
        ["g/cc"] = (Quantity.Density, 1000), ["g/cm3"] = (Quantity.Density, 1000), ["gm/cc"] = (Quantity.Density, 1000), ["kg/m3"] = (Quantity.Density, 1),
        // time -> s
        ["s"] = (Quantity.Time, 1), ["ms"] = (Quantity.Time, 1e-3), ["us"] = (Quantity.Time, 1e-6),
        // ratio
        ["v/v"] = (Quantity.Ratio, 1), ["frac"] = (Quantity.Ratio, 1), ["dec"] = (Quantity.Ratio, 1), ["%"] = (Quantity.Ratio, 0.01), ["pu"] = (Quantity.Ratio, 0.01),
    };

    public static bool IsKnown(string unit) => Table.ContainsKey(Normalize(unit));

    public static bool AreCompatible(string from, string to) =>
        Table.TryGetValue(Normalize(from), out var a) && Table.TryGetValue(Normalize(to), out var b) && a.Q == b.Q;

    public static double Convert(double value, string from, string to)
    {
        if (string.Equals(Normalize(from), Normalize(to), StringComparison.OrdinalIgnoreCase)) return value;
        if (!Table.TryGetValue(Normalize(from), out var a)) throw new ArgumentException($"Unknown unit '{from}'.");
        if (!Table.TryGetValue(Normalize(to), out var b)) throw new ArgumentException($"Unknown unit '{to}'.");
        if (a.Q != b.Q) throw new ArgumentException($"Incompatible units '{from}' and '{to}'.");
        return value * a.ToSi / b.ToSi;
    }

    public static void ConvertInPlace(Span<float> values, string from, string to)
    {
        if (string.Equals(Normalize(from), Normalize(to), StringComparison.OrdinalIgnoreCase)) return;
        double f = Convert(1.0, from, to);
        for (int i = 0; i < values.Length; i++) if (!float.IsNaN(values[i])) values[i] = (float)(values[i] * f);
    }

    /// <summary>Slowness (us/ft or us/m) to velocity in m/s.</summary>
    public static double SlownessToVelocity(double slowness, string slownessUnit)
    {
        if (double.IsNaN(slowness) || slowness <= 0) return double.NaN;
        double sPerM = Convert(slowness, slownessUnit, "s/m");
        return 1.0 / sPerM;
    }

    public static string Normalize(string unit) => (unit ?? string.Empty).Trim().Replace("USEC", "us", StringComparison.OrdinalIgnoreCase)
        .Replace("G/C3", "g/cc", StringComparison.OrdinalIgnoreCase).Replace("K/M3", "kg/m3", StringComparison.OrdinalIgnoreCase);
}

public enum DepthUnit { Meters, Feet }
public enum TimeUnit { Milliseconds, Seconds }
public enum DomainType { Time, Depth }
