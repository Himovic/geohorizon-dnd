using System.Globalization;
using System.Text.Json;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.SeismicProcessing;

public enum ParameterKind { Number, Integer, Boolean, Text, Choice }

/// <summary>Describes one user-configurable parameter of an algorithm (used to generate UI and validate input).</summary>
public sealed record ParameterDescriptor(
    string Key,
    string Label,
    ParameterKind Kind,
    string Default,
    string Unit = "",
    double Min = double.NegativeInfinity,
    double Max = double.PositiveInfinity,
    string[]? Choices = null,
    string Description = "");

/// <summary>Parameter values keyed by descriptor key, stored as invariant-culture strings for reproducible serialization.</summary>
public sealed class ParameterSet : Dictionary<string, string>
{
    public ParameterSet() : base(StringComparer.OrdinalIgnoreCase) { }
    public ParameterSet(IDictionary<string, string> values) : base(values, StringComparer.OrdinalIgnoreCase) { }

    public static ParameterSet Defaults(IEnumerable<ParameterDescriptor> descriptors)
    {
        var p = new ParameterSet();
        foreach (var d in descriptors) p[d.Key] = d.Default;
        return p;
    }

    public double GetDouble(string key, double fallback = double.NaN) =>
        TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public int GetInt(string key, int fallback = 0) =>
        TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    public bool GetBool(string key, bool fallback = false) =>
        TryGetValue(key, out var s) && bool.TryParse(s, out var v) ? v : fallback;
    public string GetString(string key, string fallback = "") => TryGetValue(key, out var s) ? s : fallback;

    public ParameterSet Set(string key, double value) { this[key] = value.ToString("R", CultureInfo.InvariantCulture); return this; }
    public ParameterSet Set(string key, int value) { this[key] = value.ToString(CultureInfo.InvariantCulture); return this; }
    public ParameterSet Set(string key, bool value) { this[key] = value.ToString(); return this; }
    public ParameterSet Set(string key, string value) { this[key] = value; return this; }

    public string ToJson() => JsonSerializer.Serialize(new Dictionary<string, string>(this));
    public static ParameterSet FromJson(string json) => new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);

    /// <summary>Validates values against descriptors (type and range).</summary>
    public ValidationReport Validate(IEnumerable<ParameterDescriptor> descriptors)
    {
        var r = new ValidationReport();
        foreach (var d in descriptors)
        {
            if (!TryGetValue(d.Key, out var s)) { r.Error($"Missing parameter '{d.Label}'."); continue; }
            switch (d.Kind)
            {
                case ParameterKind.Number:
                case ParameterKind.Integer:
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) r.Error($"'{d.Label}' must be numeric.");
                    else if (v < d.Min || v > d.Max) r.Error($"'{d.Label}' = {v} outside [{d.Min}, {d.Max}] {d.Unit}.");
                    break;
                case ParameterKind.Boolean:
                    if (!bool.TryParse(s, out _)) r.Error($"'{d.Label}' must be true/false.");
                    break;
                case ParameterKind.Choice:
                    if (d.Choices is not null && !d.Choices.Contains(s, StringComparer.OrdinalIgnoreCase)) r.Error($"'{d.Label}' must be one of {string.Join(", ", d.Choices)}.");
                    break;
            }
        }
        return r;
    }
}

/// <summary>How an operation consumes data.</summary>
public enum OperationScope
{
    /// <summary>Each trace processed independently.</summary>
    Trace,
    /// <summary>A 2D section (one inline: [crossline, sample]) is processed at once.</summary>
    Section,
    /// <summary>A pre-stack gather [offset, sample] is processed at once.</summary>
    Gather,
}

/// <summary>Context passed to operations.</summary>
public sealed record ProcessingContext(double SampleInterval, double StartTime, double TraceSpacing = 25.0, double[]? Offsets = null,
    int Inline = 0, CancellationToken CancellationToken = default);

/// <summary>A seismic processing algorithm with a stable identifier and version for provenance.</summary>
public interface IProcessingOperation
{
    string Id { get; }
    string DisplayName { get; }
    string Category { get; }
    string Version { get; }
    string Description { get; }
    OperationScope Scope { get; }
    IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>Trace scope: processes one trace in place.</summary>
    void ProcessTrace(Span<float> trace, ParameterSet p, ProcessingContext ctx) => throw new NotSupportedException();

    /// <summary>Section/Gather scope: processes [traces, samples] in place.</summary>
    void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx) => throw new NotSupportedException();
}

/// <summary>Base class for trace-wise operations.</summary>
public abstract class TraceOperation : IProcessingOperation
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Category { get; }
    public virtual string Version => "1.0";
    public abstract string Description { get; }
    public OperationScope Scope => OperationScope.Trace;
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract void ProcessTrace(Span<float> trace, ParameterSet p, ProcessingContext ctx);
    public void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx)
    {
        for (int r = 0; r < data.Rows; r++) ProcessTrace(data.Row(r), p, ctx);
    }
}

public abstract class EnsembleOperation : IProcessingOperation
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Category { get; }
    public virtual string Version => "1.0";
    public abstract string Description { get; }
    public abstract OperationScope Scope { get; }
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract void ProcessEnsemble(Array2D data, ParameterSet p, ProcessingContext ctx);
}

/// <summary>Registry of all available processing operations.</summary>
public static class ProcessingCatalog
{
    private static readonly Lazy<IReadOnlyList<IProcessingOperation>> _all = new(() =>
    [
        new AmplitudeScaleOperation(), new DcRemovalOperation(), new DetrendOperation(), new PolarityReversalOperation(),
        new BandPassOperation(), new ButterworthOperation(), new NotchOperation(),
        new AgcOperation(), new RmsNormalizationOperation(), new TraceNormalizationOperation(), new TimeSmoothingOperation(),
        new SpikingDeconvolutionOperation(), new PredictiveDeconvolutionOperation(), new SpectralWhiteningOperation(),
        new TopMuteOperation(), new TraceEditOperation(), new FkFilterOperation(), new FxDeconvolutionOperation(),
        new LateralMixOperation(), new KirchhoffTimeMigrationOperation(), new PhaseShiftMigrationOperation(),
        new NmoCorrectionOperation(), new OffsetMuteOperation(), new ParabolicRadonOperation(),
    ]);

    public static IReadOnlyList<IProcessingOperation> All => _all.Value;
    public static IProcessingOperation Get(string id) =>
        All.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"Unknown processing operation '{id}'.");
}
