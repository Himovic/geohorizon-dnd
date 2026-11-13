namespace GeoVisionPro.Visualization2D;

/// <summary>A colour map defined by RGB stops at positions in [0,1]; evaluated into a 256-entry BGRA lookup table.</summary>
public sealed class ColorMap
{
    public string Name { get; }
    public IReadOnlyList<(double Position, byte R, byte G, byte B)> Stops { get; }
    public bool Diverging { get; }
    private readonly uint[] _lut = new uint[256];

    public ColorMap(string name, IReadOnlyList<(double, byte, byte, byte)> stops, bool diverging = false)
    {
        Name = name; Stops = stops.OrderBy(s => s.Item1).ToList(); Diverging = diverging;
        for (int i = 0; i < 256; i++)
        {
            var (r, g, b) = Evaluate(i / 255.0);
            _lut[i] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }
    }

    public (byte R, byte G, byte B) Evaluate(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var s = Stops;
        if (t <= s[0].Position) return (s[0].R, s[0].G, s[0].B);
        for (int i = 1; i < s.Count; i++)
        {
            if (t <= s[i].Position)
            {
                double f = (t - s[i - 1].Position) / Math.Max(1e-12, s[i].Position - s[i - 1].Position);
                return ((byte)Math.Round(s[i - 1].R + f * (s[i].R - s[i - 1].R)),
                        (byte)Math.Round(s[i - 1].G + f * (s[i].G - s[i - 1].G)),
                        (byte)Math.Round(s[i - 1].B + f * (s[i].B - s[i - 1].B)));
            }
        }
        return (s[^1].R, s[^1].G, s[^1].B);
    }

    /// <summary>Packed BGRA32 (0xAARRGGBB as little-endian uint) colour for normalised value t ∈ [0,1].</summary>
    public uint Lookup(double t)
    {
        if (double.IsNaN(t)) return 0;
        int i = (int)(Math.Clamp(t, 0, 1) * 255 + 0.5);
        return _lut[i];
    }

    public uint[] Table => _lut;

    public ColorMap Reversed() => new(Name + " (reversed)", Stops.Select(s => (1 - s.Position, s.R, s.G, s.B)).ToList(), Diverging);

    public override string ToString() => Name;
}

public static class ColorMaps
{
    public static ColorMap Seismic { get; } = new("Seismic (red-white-black)",
        [(0, 170, 0, 0), (0.25, 255, 60, 0), (0.5, 255, 255, 255), (0.75, 90, 90, 90), (1, 0, 0, 0)], true);
    public static ColorMap BlueWhiteRed { get; } = new("Blue-White-Red",
        [(0, 10, 30, 160), (0.25, 70, 140, 230), (0.5, 250, 250, 250), (0.75, 235, 90, 60), (1, 150, 10, 10)], true);
    public static ColorMap Greyscale { get; } = new("Greyscale", [(0, 0, 0, 0), (1, 255, 255, 255)], true);
    public static ColorMap Spectrum { get; } = new("Spectrum",
        [(0, 48, 18, 59), (0.13, 70, 107, 227), (0.25, 41, 187, 236), (0.38, 49, 241, 152), (0.5, 164, 252, 60),
         (0.63, 237, 208, 58), (0.75, 251, 128, 34), (0.88, 210, 49, 5), (1, 122, 4, 3)]);
    public static ColorMap Viridis { get; } = new("Viridis",
        [(0, 68, 1, 84), (0.25, 59, 82, 139), (0.5, 33, 145, 140), (0.75, 94, 201, 98), (1, 253, 231, 37)]);
    public static ColorMap Magma { get; } = new("Magma",
        [(0, 0, 0, 4), (0.25, 81, 18, 124), (0.5, 183, 55, 121), (0.75, 252, 137, 97), (1, 252, 253, 191)]);
    public static ColorMap Earth { get; } = new("Earth",
        [(0, 30, 60, 120), (0.3, 60, 140, 110), (0.55, 200, 190, 110), (0.8, 150, 90, 50), (1, 250, 250, 250)]);
    public static ColorMap Impedance { get; } = new("Impedance",
        [(0, 0, 90, 30), (0.3, 120, 200, 60), (0.55, 250, 230, 80), (0.8, 230, 110, 30), (1, 140, 20, 20)]);
    public static ColorMap Coherence { get; } = new("Coherence", [(0, 0, 0, 0), (0.6, 120, 120, 120), (1, 255, 255, 255)]);

    public static IReadOnlyList<ColorMap> All { get; } = [Seismic, BlueWhiteRed, Greyscale, Spectrum, Viridis, Magma, Earth, Impedance, Coherence];

    public static ColorMap ByName(string name) => All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? Seismic;
}
