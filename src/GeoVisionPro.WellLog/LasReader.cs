using System.Globalization;
using System.Text;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;

namespace GeoVisionPro.WellLog;

/// <summary>A header line of a LAS section: MNEM.UNIT VALUE : DESCRIPTION.</summary>
public sealed record LasHeaderItem(string Mnemonic, string Unit, string Value, string Description);

/// <summary>Raw parsed content of a LAS file.</summary>
public sealed class LasDocument
{
    public string Version { get; set; } = "2.0";
    public bool Wrapped { get; set; }
    public double NullValue { get; set; } = -999.25;
    public List<LasHeaderItem> WellItems { get; } = [];
    public List<LasHeaderItem> CurveItems { get; } = [];
    public List<LasHeaderItem> ParameterItems { get; } = [];
    public List<(string Name, double Depth)> Tops { get; } = [];
    public string Other { get; set; } = string.Empty;
    public double[] Depths { get; set; } = [];
    public List<float[]> CurveData { get; } = [];
    public ValidationReport Validation { get; } = new();

    public string? Well(string mnem) => WellItems.FirstOrDefault(i => i.Mnemonic.Equals(mnem, StringComparison.OrdinalIgnoreCase))?.Value;
    public string? Param(string mnem) => ParameterItems.FirstOrDefault(i => i.Mnemonic.Equals(mnem, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>Reader for CWLS LAS 2.0 (wrapped and unwrapped) and the log/tops sections of LAS 3.0.</summary>
public static class LasReader
{
    public static LasDocument Parse(string text)
    {
        var doc = new LasDocument();
        string section = "";
        string delimiter = " ";
        var dataTokens = new List<string>();
        var topsData = new List<string>();
        var other = new StringBuilder();
        int lineNo = 0;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('~'))
            {
                var name = trimmed[1..].Split([' ', '|'], 2)[0].ToUpperInvariant();
                section = name switch
                {
                    _ when name.StartsWith("V") => "V",
                    _ when name.StartsWith("W") => "W",
                    _ when name == "C" || name.StartsWith("CURVE") || name == "LOG_DEFINITION" || name == "LOG_DEF" => "C",
                    _ when name == "P" || name.StartsWith("PARAM") || name == "LOG_PARAMETER" => "P",
                    _ when name == "A" || name.StartsWith("ASCII") || name == "LOG_DATA" || (name.StartsWith("LOG") && name.EndsWith("DATA")) => "A",
                    _ when name.StartsWith("TOPS_DATA") => "TOPSDATA",
                    _ when name.StartsWith("TOPS") => "TOPSDEF",
                    _ when name.StartsWith("O") => "O",
                    _ => "SKIP:" + name
                };
                continue;
            }
            switch (section)
            {
                case "V":
                {
                    var item = ParseHeaderLine(trimmed);
                    if (item is null) break;
                    if (item.Mnemonic.Equals("VERS", StringComparison.OrdinalIgnoreCase)) doc.Version = item.Value;
                    if (item.Mnemonic.Equals("WRAP", StringComparison.OrdinalIgnoreCase)) doc.Wrapped = item.Value.StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                    if (item.Mnemonic.Equals("DLM", StringComparison.OrdinalIgnoreCase))
                        delimiter = item.Value.ToUpperInvariant() switch { "COMMA" => ",", "TAB" => "\t", _ => " " };
                    break;
                }
                case "W":
                {
                    var item = ParseHeaderLine(trimmed);
                    if (item is null) { doc.Validation.Warn($"Unparsed well header line {lineNo}: {trimmed}"); break; }
                    doc.WellItems.Add(item);
                    if (item.Mnemonic.Equals("NULL", StringComparison.OrdinalIgnoreCase) && TryParse(item.Value, out var nv)) doc.NullValue = nv;
                    break;
                }
                case "C":
                {
                    var item = ParseHeaderLine(trimmed);
                    if (item is null) doc.Validation.Warn($"Unparsed curve definition line {lineNo}: {trimmed}");
                    else doc.CurveItems.Add(item);
                    break;
                }
                case "P":
                {
                    var item = ParseHeaderLine(trimmed);
                    if (item is not null) doc.ParameterItems.Add(item);
                    break;
                }
                case "O": other.AppendLine(line); break;
                case "A":
                    foreach (var tok in Tokenize(trimmed, delimiter)) dataTokens.Add(tok);
                    break;
                case "TOPSDATA": topsData.Add(trimmed); break;
            }
        }
        doc.Other = other.ToString();
        if (doc.CurveItems.Count == 0) { doc.Validation.Error("No curve definitions (~C section) found."); return doc; }

        int nc = doc.CurveItems.Count;
        if (dataTokens.Count % nc != 0)
            doc.Validation.Warn($"Data value count ({dataTokens.Count}) is not a multiple of the curve count ({nc}); the last incomplete row was dropped.");
        int rows = dataTokens.Count / nc;
        var depths = new double[rows];
        for (int c = 0; c < nc; c++) doc.CurveData.Add(new float[rows - 0]);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < nc; c++)
            {
                var tok = dataTokens[r * nc + c];
                double v = TryParse(tok, out var d) ? d : double.NaN;
                if (Math.Abs(v - doc.NullValue) < 1e-6 * Math.Max(1, Math.Abs(doc.NullValue))) v = double.NaN;
                if (c == 0) depths[r] = v;
                else doc.CurveData[c][r] = (float)v;
            }
        }
        doc.CurveData.RemoveAt(0);
        doc.Depths = depths;
        if (depths.Any(double.IsNaN)) doc.Validation.Error("Index (depth) curve contains null values.");
        bool ascending = true, descending = true;
        for (int i = 1; i < depths.Length; i++) { if (depths[i] <= depths[i - 1]) ascending = false; if (depths[i] >= depths[i - 1]) descending = false; }
        if (!ascending && descending)
        {
            Array.Reverse(depths);
            foreach (var c in doc.CurveData) Array.Reverse(c);
            doc.Validation.Info("Depth index was decreasing; data reversed to ascending order.");
        }
        else if (!ascending && depths.Length > 1) doc.Validation.Error("Depth index is not monotonic.");

        foreach (var row in topsData)
        {
            var parts = Tokenize(row, delimiter).ToList();
            if (parts.Count >= 2 && TryParse(parts[1], out var td)) doc.Tops.Add((parts[0].Trim('"'), td));
        }
        doc.Validation.Info($"LAS {doc.Version}: {nc - 1} curves, {rows} samples{(doc.Wrapped ? " (wrapped)" : "")}.");
        return doc;
    }

    private static IEnumerable<string> Tokenize(string line, string delimiter)
    {
        if (delimiter == " ")
        {
            // whitespace separated, with support for quoted strings
            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
                if (i >= line.Length) yield break;
                if (line[i] == '"')
                {
                    int j = line.IndexOf('"', i + 1);
                    if (j < 0) j = line.Length;
                    yield return line[(i + 1)..j];
                    i = j + 1;
                }
                else
                {
                    int j = i;
                    while (j < line.Length && !char.IsWhiteSpace(line[j])) j++;
                    yield return line[i..j];
                    i = j;
                }
            }
        }
        else foreach (var t in line.Split(delimiter[0])) yield return t.Trim();
    }

    /// <summary>Parses "MNEM.UNIT  VALUE : DESCRIPTION". The unit ends at the first space after the first period.</summary>
    public static LasHeaderItem? ParseHeaderLine(string line)
    {
        int dot = line.IndexOf('.');
        int colon = line.LastIndexOf(':');
        if (dot < 0) return null;
        if (colon < 0 || colon < dot) colon = line.Length;
        string mnem = line[..dot].Trim();
        string rest = line[(dot + 1)..colon];
        int sp = rest.IndexOfAny([' ', '\t']);
        string unit = sp < 0 ? rest.Trim() : rest[..sp].Trim();
        string value = sp < 0 ? string.Empty : rest[sp..].Trim();
        string desc = colon < line.Length ? line[(colon + 1)..].Trim() : string.Empty;
        // LAS 3.0 curve definitions may carry format after '|' and an index {F} in the description
        int bar = desc.IndexOf('|');
        if (bar >= 0) desc = desc[..bar].Trim();
        return mnem.Length == 0 ? null : new LasHeaderItem(mnem, unit, value, desc);
    }

    private static bool TryParse(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>Converts a parsed LAS document into a domain <see cref="Well"/> with its curves and tops.</summary>
    public static Well ToWell(LasDocument doc, string sourcePath)
    {
        var well = new Well
        {
            Name = doc.Well("WELL") is { Length: > 0 } wn ? wn : Path.GetFileNameWithoutExtension(sourcePath),
            Uwi = doc.Well("UWI") ?? doc.Well("API"),
            Field = doc.Well("FLD"),
            Operator = doc.Well("COMP"),
            SourcePath = sourcePath
        };
        string depthUnit = doc.CurveItems[0].Unit;
        well.DepthUnit = depthUnit.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? DepthUnit.Feet : DepthUnit.Meters;
        double Num(params string[] mnems)
        {
            foreach (var m in mnems)
            {
                var s = doc.Well(m) ?? doc.Param(m);
                if (s is not null && TryParse(s.Split(' ')[0], out var v)) return v;
            }
            return double.NaN;
        }
        well.SurfaceX = Num("XCOORD", "X", "XWELL", "EAST", "XLOC");
        well.SurfaceY = Num("YCOORD", "Y", "YWELL", "NORTH", "YLOC");
        well.KellyBushing = Num("EKB", "KB", "EREF", "ELEV", "EDF");
        well.GroundLevel = Num("EGL", "GL");
        if (double.IsNaN(well.SurfaceX) || double.IsNaN(well.SurfaceY))
        {
            doc.Validation.Warn("Well surface coordinates not found in header (XCOORD/YCOORD); set them in the well editor.");
            well.SurfaceX = double.IsNaN(well.SurfaceX) ? 0 : well.SurfaceX;
            well.SurfaceY = double.IsNaN(well.SurfaceY) ? 0 : well.SurfaceY;
        }
        if (double.IsNaN(well.KellyBushing)) { well.KellyBushing = 0; doc.Validation.Warn("Kelly bushing elevation (EKB) not found; assumed 0."); }
        if (double.IsNaN(well.GroundLevel)) well.GroundLevel = 0;
        well.TotalDepth = doc.Depths.Length > 0 ? doc.Depths[^1] : 0;

        for (int c = 0; c < doc.CurveData.Count; c++)
        {
            var item = doc.CurveItems[c + 1];
            well.Curves.Add(new LogCurve
            {
                WellId = well.Id, Name = item.Mnemonic, Mnemonic = item.Mnemonic, Unit = item.Unit, Description = item.Description,
                Family = CurveFamilies.Detect(item.Mnemonic, item.Unit), Depths = (double[])doc.Depths.Clone(),
                Values = doc.CurveData[c], Source = Path.GetFileName(sourcePath)
            });
        }
        foreach (var (name, depth) in doc.Tops)
            well.Tops.Add(new WellTop { WellId = well.Id, Name = name, MeasuredDepth = depth, Source = "LAS" });
        well.ValidationStatus = !doc.Validation.IsValid ? DataValidationStatus.Invalid : doc.Validation.HasWarnings ? DataValidationStatus.ValidWithWarnings : DataValidationStatus.Valid;
        well.ValidationMessages = doc.Validation.ToString();
        return well;
    }

    public static Well ReadWell(string path) => ToWell(Parse(File.ReadAllText(path)), path);
}

/// <summary>Detection of curve families from mnemonics and units (Schlumberger/industry conventions).</summary>
public static class CurveFamilies
{
    private static readonly (string[] Mnems, CurveFamily Family)[] Rules =
    [
        (["GR", "GRC", "SGR", "CGR", "GAM", "GRD", "GR_EDTC", "HCGR", "GRGC"], CurveFamily.GammaRay),
        (["RHOB", "RHOZ", "DEN", "ZDEN", "DENS", "RHO", "RHOM", "HDEN"], CurveFamily.Density),
        (["NPHI", "TNPH", "NPOR", "CNC", "NPHS", "PHIN", "NEU", "CNCF", "HNPO"], CurveFamily.NeutronPorosity),
        (["DT", "DTC", "DTCO", "AC", "SONIC", "DT4P", "DTP", "DT24"], CurveFamily.Sonic),
        (["DTS", "DTSM", "DTSH", "DT4S", "DTSHEAR"], CurveFamily.ShearSonic),
        (["PEF", "PE", "PEFZ", "PDPE"], CurveFamily.PhotoElectric),
        (["CALI", "CAL", "HCAL", "C1", "CALS", "DCAL"], CurveFamily.Caliper),
        (["SP", "SSP"], CurveFamily.SpontaneousPotential),
        (["RT", "ILD", "LLD", "RD", "RDEP", "AT90", "RLA5", "AHT90", "HDRS", "RES_DEEP", "RESD", "M2R9", "HLLD"], CurveFamily.ResistivityDeep),
        (["ILM", "RM", "RMED", "AT30", "AHT30", "HMRS", "RLA3"], CurveFamily.ResistivityMedium),
        (["RXO", "MSFL", "LLS", "SFL", "RS", "RSHAL", "AT10", "AHT10", "SFLU", "HLLS"], CurveFamily.ResistivityShallow),
        (["VSH", "VCL", "VSHALE", "VCLAY"], CurveFamily.ShaleVolume),
        (["PHIE", "PHIEFF", "PIGE"], CurveFamily.EffectivePorosity),
        (["PHIT", "PHI", "POR", "PHITOT"], CurveFamily.TotalPorosity),
        (["SW", "SWE", "SWT", "SWA"], CurveFamily.WaterSaturation),
        (["AI", "IP", "PIMP", "ZP"], CurveFamily.AcousticImpedance),
        (["SI", "IS", "SIMP", "ZS"], CurveFamily.ShearImpedance),
        (["VPVS"], CurveFamily.VpVs),
        (["VP", "VEL", "VELP"], CurveFamily.PVelocity),
        (["VS", "VELS"], CurveFamily.SVelocity),
    ];

    public static CurveFamily Detect(string mnemonic, string unit)
    {
        var m = mnemonic.Trim().ToUpperInvariant();
        foreach (var (mnems, family) in Rules) if (mnems.Contains(m)) return family;
        foreach (var (mnems, family) in Rules) if (mnems.Any(x => x.Length >= 3 && m.StartsWith(x))) return family;
        var u = unit.ToUpperInvariant();
        if (u is "GAPI" or "API") return CurveFamily.GammaRay;
        if (u is "G/CC" or "G/C3" or "G/CM3") return CurveFamily.Density;
        if (u is "US/FT" or "US/F" or "US/M") return CurveFamily.Sonic;
        if (u is "OHMM" or "OHM.M") return CurveFamily.ResistivityDeep;
        return CurveFamily.Unknown;
    }

    public static string DefaultUnit(CurveFamily f) => f switch
    {
        CurveFamily.GammaRay => "gAPI",
        CurveFamily.Density => "g/cc",
        CurveFamily.NeutronPorosity or CurveFamily.EffectivePorosity or CurveFamily.TotalPorosity or CurveFamily.ShaleVolume or CurveFamily.WaterSaturation => "v/v",
        CurveFamily.Sonic or CurveFamily.ShearSonic => "us/ft",
        CurveFamily.ResistivityDeep or CurveFamily.ResistivityMedium or CurveFamily.ResistivityShallow => "ohmm",
        CurveFamily.AcousticImpedance or CurveFamily.ShearImpedance => "m/s*g/cc",
        CurveFamily.PVelocity or CurveFamily.SVelocity => "m/s",
        _ => ""
    };

    public static bool IsLogarithmic(CurveFamily f) => f is CurveFamily.ResistivityDeep or CurveFamily.ResistivityMedium or CurveFamily.ResistivityShallow;
}
