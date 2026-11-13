using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;

namespace GeoVisionPro.SeismicProcessing;

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
