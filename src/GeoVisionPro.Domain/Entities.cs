using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Core.Units;

namespace GeoVisionPro.Domain;

/// <summary>Base class for all persistent project objects.</summary>
public abstract class ProjectEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    public void Touch() => ModifiedUtc = DateTime.UtcNow;
    public override string ToString() => Name;
}

public sealed class Project : ProjectEntity
{
    public string CoordinateSystem { get; set; } = "Local (undefined)";
    public DepthUnit DepthUnit { get; set; } = DepthUnit.Meters;
    public double SeismicReferenceDatum { get; set; }   // SRD elevation, project depth units
    public double ReplacementVelocity { get; set; } = 1800; // m/s
    public int SchemaVersion { get; set; } = 1;
    public string? Author { get; set; }
}

public enum SeismicKind { PostStack3D, PostStack2D, PreStackGathers, AngleGathers }
public enum SeismicCategory { Original, Processed, Attribute, Inversion, Model }
public enum StorageFormat { SegyLinked, ZarrBricks, InMemory }
public enum DataValidationStatus { NotValidated, Valid, ValidWithWarnings, Invalid }

/// <summary>Regular survey geometry of a post-stack volume or a gather dataset.</summary>
public sealed record SurveyGeometry(
    IndexRange Inlines,
    IndexRange Crosslines,
    int SampleCount,
    double SampleInterval,   // seconds (time) or metres (depth)
    double StartTime,        // seconds or metres
    GridTransform Transform,
    DomainType Domain = DomainType.Time)
{
    public int InlineCount => Inlines.Count;
    public int CrosslineCount => Crosslines.Count;
    public long TraceCount => (long)InlineCount * CrosslineCount;
    public double EndTime => StartTime + (SampleCount - 1) * SampleInterval;
    public double TimeAt(double sample) => StartTime + sample * SampleInterval;
    public double SampleAt(double time) => (time - StartTime) / SampleInterval;
    public bool ContainsTrace(int inline, int crossline) => Inlines.Contains(inline) && Crosslines.Contains(crossline);
    public int InlineIndex(int inline) => Inlines.IndexOf(inline);
    public int CrosslineIndex(int crossline) => Crosslines.IndexOf(crossline);
    public Point2D MapLocation(double inline, double crossline) => Transform.ToMap(inline, crossline);

    public IReadOnlyList<Point2D> MapOutline() =>
    [
        Transform.ToMap(Inlines.First, Crosslines.First), Transform.ToMap(Inlines.First, Crosslines.Last),
        Transform.ToMap(Inlines.Last, Crosslines.Last), Transform.ToMap(Inlines.Last, Crosslines.First)
    ];
}

public sealed class SeismicDataset : ProjectEntity
{
    public SeismicKind Kind { get; set; } = SeismicKind.PostStack3D;
    public SeismicCategory Category { get; set; } = SeismicCategory.Original;
    public StorageFormat StorageFormat { get; set; } = StorageFormat.SegyLinked;
    /// <summary>Path relative to the project data folder (Zarr) or absolute path (linked SEG-Y).</summary>
    public string StoragePath { get; set; } = string.Empty;
    public string? SourcePath { get; set; }
    public int InlineFirst { get; set; }
    public int InlineLast { get; set; }
    public int InlineStep { get; set; } = 1;
    public int CrosslineFirst { get; set; }
    public int CrosslineLast { get; set; }
    public int CrosslineStep { get; set; } = 1;
    public int SampleCount { get; set; }
    public double SampleInterval { get; set; }
    public double StartTime { get; set; }
    public DomainType Domain { get; set; } = DomainType.Time;
    public double TransformIl0 { get; set; }
    public double TransformXl0 { get; set; }
    public double TransformX0 { get; set; }
    public double TransformY0 { get; set; }
    public double TransformA11 { get; set; } = 1;
    public double TransformA12 { get; set; }
    public double TransformA21 { get; set; }
    public double TransformA22 { get; set; } = 1;
    /// <summary>Gather fold for pre-stack data (number of offsets/angles per CDP).</summary>
    public int Fold { get; set; } = 1;
    /// <summary>Offset (m) or angle (deg) of each gather trace, comma separated.</summary>
    public string? OffsetsCsv { get; set; }
    public double AmplitudeMin { get; set; }
    public double AmplitudeMax { get; set; }
    public double AmplitudeRms { get; set; }
    public string? Units { get; set; }
    public DataValidationStatus ValidationStatus { get; set; }
    public string? ValidationMessages { get; set; }
    public Guid? ParentDatasetId { get; set; }
    public Guid? ProcessingRecordId { get; set; }
    /// <summary>Byte locations and format used to read the source SEG-Y (JSON).</summary>
    public string? ImportSettingsJson { get; set; }

    public SurveyGeometry Geometry
    {
        get => new(new IndexRange(InlineFirst, InlineLast, InlineStep), new IndexRange(CrosslineFirst, CrosslineLast, CrosslineStep),
            SampleCount, SampleInterval, StartTime,
            new GridTransform(TransformIl0, TransformXl0, TransformX0, TransformY0, TransformA11, TransformA12, TransformA21, TransformA22), Domain);
        set
        {
            InlineFirst = value.Inlines.First; InlineLast = value.Inlines.Last; InlineStep = value.Inlines.Step;
            CrosslineFirst = value.Crosslines.First; CrosslineLast = value.Crosslines.Last; CrosslineStep = value.Crosslines.Step;
            SampleCount = value.SampleCount; SampleInterval = value.SampleInterval; StartTime = value.StartTime; Domain = value.Domain;
            var t = value.Transform;
            TransformIl0 = t.Inline0; TransformXl0 = t.Crossline0; TransformX0 = t.X0; TransformY0 = t.Y0;
            TransformA11 = t.A11; TransformA12 = t.A12; TransformA21 = t.A21; TransformA22 = t.A22;
        }
    }

    public double[] Offsets
    {
        get => string.IsNullOrWhiteSpace(OffsetsCsv) ? [] : OffsetsCsv.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        set => OffsetsCsv = string.Join(",", value.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    }
}

/// <summary>Curve family used for default display settings and automatic role detection.</summary>
public enum CurveFamily
{
    Unknown, GammaRay, Density, NeutronPorosity, Sonic, ShearSonic, PhotoElectric, Caliper, SpontaneousPotential,
    ResistivityDeep, ResistivityMedium, ResistivityShallow, ShaleVolume, EffectivePorosity, TotalPorosity, WaterSaturation,
    AcousticImpedance, ShearImpedance, VpVs, PVelocity, SVelocity, Lithology, Other
}

public sealed class Well : ProjectEntity
{
    public string? Uwi { get; set; }
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public double SurfaceX { get; set; }
    public double SurfaceY { get; set; }
    /// <summary>Kelly bushing elevation above datum (positive up), well depth units.</summary>
    public double KellyBushing { get; set; }
    public double GroundLevel { get; set; }
    public double TotalDepth { get; set; }
    public DepthUnit DepthUnit { get; set; } = DepthUnit.Meters;
    public string? SourcePath { get; set; }
    public DataValidationStatus ValidationStatus { get; set; }
    public string? ValidationMessages { get; set; }
    public List<LogCurve> Curves { get; set; } = [];
    public List<WellTop> Tops { get; set; } = [];
    public List<DeviationStation> Deviation { get; set; } = [];
    public List<CheckshotSurvey> Checkshots { get; set; } = [];
    /// <summary>Id of the checkshot/TDR survey used for time-depth conversion.</summary>
    public Guid? ActiveTimeDepthId { get; set; }

    public LogCurve? FindCurve(string mnemonic) =>
        Curves.FirstOrDefault(c => string.Equals(c.Mnemonic, mnemonic, StringComparison.OrdinalIgnoreCase));

    public LogCurve? FindCurve(CurveFamily family) => Curves.FirstOrDefault(c => c.Family == family);

    public CheckshotSurvey? ActiveTimeDepth => Checkshots.FirstOrDefault(c => c.Id == ActiveTimeDepthId) ?? Checkshots.FirstOrDefault();
}

public sealed class LogCurve : ProjectEntity
{
    public Guid WellId { get; set; }
    public string Mnemonic { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public CurveFamily Family { get; set; }
    /// <summary>Measured depth of each sample (ascending).</summary>
    public double[] Depths { get; set; } = [];
    /// <summary>Curve values; NaN = missing.</summary>
    public float[] Values { get; set; } = [];
    public bool IsComputed { get; set; }
    public string? Source { get; set; }

    public int Count => Values.Length;

    public double ValueAtDepth(double md)
    {
        if (Depths.Length == 0) return double.NaN;
        int lo = 0, hi = Depths.Length - 1;
        if (md < Depths[0] || md > Depths[hi]) return double.NaN;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (Depths[mid] <= md) lo = mid; else hi = mid; }
        if (hi == lo) return Values[lo];
        double t = (md - Depths[lo]) / (Depths[hi] - Depths[lo]);
        float a = Values[lo], b = Values[hi];
        if (float.IsNaN(a)) return t > 0.5 ? b : double.NaN;
        if (float.IsNaN(b)) return t < 0.5 ? a : double.NaN;
        return a + t * (b - a);
    }

    public LogCurve CloneAs(string mnemonic, float[] values, string unit, CurveFamily family, string source) => new()
    {
        WellId = WellId, Name = mnemonic, Mnemonic = mnemonic, Unit = unit, Family = family,
        Depths = (double[])Depths.Clone(), Values = values, IsComputed = true, Source = source
    };
}

public sealed class WellTop : ProjectEntity
{
    public Guid WellId { get; set; }
    public double MeasuredDepth { get; set; }
    public string Color { get; set; } = "#FFD54F";
    public string? Source { get; set; }
}

/// <summary>Directional survey station: measured depth, inclination and azimuth in degrees.</summary>
public sealed record DeviationStation(double MeasuredDepth, double Inclination, double Azimuth);

/// <summary>A time-depth pair. Depth is measured depth; TVDSS is optional (NaN when unknown).</summary>
public sealed record TimeDepthPoint(double MeasuredDepth, double TwoWayTime, double TvdSs = double.NaN);

public enum TimeDepthSource { Checkshot, Vsp, SonicIntegrated, WellTieCalibrated, Manual }

public sealed class CheckshotSurvey : ProjectEntity
{
    public Guid WellId { get; set; }
    public TimeDepthSource Source { get; set; } = TimeDepthSource.Checkshot;
    public List<TimeDepthPoint> Points { get; set; } = [];
}

public enum PickSource : byte { None = 0, Manual = 1, AutoTracked = 2, Interpolated = 3, Imported = 4 }

/// <summary>Interpreted horizon stored as a regular grid aligned to a survey (time or depth values).</summary>
public sealed class Horizon : ProjectEntity
{
    public Guid? SurveyId { get; set; }
    public string Color { get; set; } = "#20B8A6";
    public DomainType Domain { get; set; } = DomainType.Time;
    public int InlineFirst { get; set; }
    public int InlineStep { get; set; } = 1;
    public int InlineCount { get; set; }
    public int CrosslineFirst { get; set; }
    public int CrosslineStep { get; set; } = 1;
    public int CrosslineCount { get; set; }
    /// <summary>Row-major [inline][crossline] times in seconds (or depth in m); NaN = not interpreted.</summary>
    public double[] Values { get; set; } = [];
    public byte[] Sources { get; set; } = [];
    public string? SourcePath { get; set; }

    public static Horizon CreateFor(SurveyGeometry g, string name, string color)
    {
        var h = new Horizon
        {
            Name = name, Color = color, Domain = g.Domain,
            InlineFirst = g.Inlines.First, InlineStep = g.Inlines.Step, InlineCount = g.InlineCount,
            CrosslineFirst = g.Crosslines.First, CrosslineStep = g.Crosslines.Step, CrosslineCount = g.CrosslineCount,
            Values = new double[g.InlineCount * g.CrosslineCount],
            Sources = new byte[g.InlineCount * g.CrosslineCount]
        };
        Array.Fill(h.Values, double.NaN);
        return h;
    }

    public Grid2D AsGrid() => new(InlineCount, CrosslineCount, Values);

    public bool TryIndex(int inline, int crossline, out int i, out int j)
    {
        i = InlineStep == 0 ? 0 : (inline - InlineFirst) / InlineStep;
        j = CrosslineStep == 0 ? 0 : (crossline - CrosslineFirst) / CrosslineStep;
        return i >= 0 && j >= 0 && i < InlineCount && j < CrosslineCount;
    }

    public double GetValue(int inline, int crossline) => TryIndex(inline, crossline, out var i, out var j) ? Values[i * CrosslineCount + j] : double.NaN;

    public void SetValue(int inline, int crossline, double value, PickSource source)
    {
        if (!TryIndex(inline, crossline, out var i, out var j)) return;
        Values[i * CrosslineCount + j] = value;
        Sources[i * CrosslineCount + j] = (byte)(double.IsNaN(value) ? PickSource.None : source);
    }

    public int InlineAt(int i) => InlineFirst + i * InlineStep;
    public int CrosslineAt(int j) => CrosslineFirst + j * CrosslineStep;
}

/// <summary>A fault stick: ordered points in survey coordinates (inline, crossline, time/depth).</summary>
public sealed class FaultStick
{
    public List<Point3D> Points { get; set; } = [];   // X = inline, Y = crossline, Z = time (s) or depth
}

public sealed class Fault : ProjectEntity
{
    public Guid? SurveyId { get; set; }
    public string Color { get; set; } = "#E53935";
    public DomainType Domain { get; set; } = DomainType.Time;
    public List<FaultStick> Sticks { get; set; } = [];
    public string? SourcePath { get; set; }
}

public sealed class WaveletEntity : ProjectEntity
{
    public double SampleInterval { get; set; }
    public int CenterIndex { get; set; }
    public float[] Samples { get; set; } = [];
    public string Source { get; set; } = "Analytical";
    public Core.Numerics.Wavelet ToWavelet() => new(Name, Samples, SampleInterval, CenterIndex);
    public static WaveletEntity From(Core.Numerics.Wavelet w, string source) => new()
    {
        Name = w.Name, SampleInterval = w.SampleInterval, CenterIndex = w.CenterIndex, Samples = (float[])w.Samples.Clone(), Source = source
    };
}

public sealed class WellTieRecord : ProjectEntity
{
    public Guid WellId { get; set; }
    public Guid SeismicId { get; set; }
    public Guid? WaveletId { get; set; }
    public double BulkShift { get; set; }        // seconds
    public double PhaseRotation { get; set; }    // degrees
    public bool ReversePolarity { get; set; }
    public double Correlation { get; set; }
    public string ParametersJson { get; set; } = "{}";
    /// <summary>Resulting calibrated time-depth table id (CheckshotSurvey with Source = WellTieCalibrated).</summary>
    public Guid? CalibratedTimeDepthId { get; set; }
}

public enum ExecutionStatus { Pending, Running, Succeeded, Failed, Cancelled }

/// <summary>Provenance record for any operation that produced project data.</summary>
public sealed class ProcessingRecord : ProjectEntity
{
    public string Operation { get; set; } = string.Empty;
    public string AlgorithmVersion { get; set; } = "1.0";
    public string ParametersJson { get; set; } = "{}";
    public string InputIds { get; set; } = string.Empty;
    public string OutputIds { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public ExecutionStatus Status { get; set; }
    public string? Messages { get; set; }
}

public sealed class WorkflowDefinitionEntity : ProjectEntity
{
    public string Json { get; set; } = "{}";
}

public sealed class ReportEntity : ProjectEntity
{
    public string Path { get; set; } = string.Empty;
    public string Format { get; set; } = "PDF";
}

public sealed class GeologicalModelEntity : ProjectEntity
{
    public string Json { get; set; } = "{}";
}

public sealed class CrossplotEntity : ProjectEntity
{
    public string Json { get; set; } = "{}";
}
