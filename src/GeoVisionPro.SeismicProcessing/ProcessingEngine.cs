using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.SeismicProcessing;

/// <summary>One configured step of a processing sequence.</summary>
public sealed record ProcessingStep(string OperationId, ParameterSet Parameters)
{
    public IProcessingOperation Operation => ProcessingCatalog.Get(OperationId);
}

/// <summary>Executes processing operations over volumes and gathers, in parallel by inline, with progress and cancellation.</summary>
public static class ProcessingEngine
{
    public static int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    public static ValidationReport Validate(IEnumerable<ProcessingStep> steps, bool preStack)
    {
        var report = new ValidationReport();
        foreach (var s in steps)
        {
            IProcessingOperation op;
            try { op = s.Operation; } catch (KeyNotFoundException e) { report.Error(e.Message); continue; }
            report.Merge(s.Parameters.Validate(op.Parameters));
            if (op.Scope == OperationScope.Gather && !preStack) report.Error($"'{op.DisplayName}' requires pre-stack gathers.");
        }
        return report;
    }

    /// <summary>Processes a single section/trace array (used for previews and before/after QC).</summary>
    public static Array2D ApplyToSection(Array2D section, IReadOnlyList<ProcessingStep> steps, ProcessingContext ctx)
    {
        var data = section.Clone();
        foreach (var s in steps) s.Operation.ProcessEnsemble(data, s.Parameters, ctx);
        return data;
    }

    /// <summary>Runs a post-stack sequence (trace and section operations) from input to output, inline by inline.</summary>
    public static void RunVolume(ISeismicVolume input, IWritableSeismicVolume output, IReadOnlyList<ProcessingStep> steps,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = input.Geometry;
        var v = Validate(steps, preStack: false);
        if (!v.IsValid) throw new InvalidOperationException("Invalid processing parameters:" + Environment.NewLine + v);
        double spacing = g.Transform.CrosslineSpacing > 0 ? g.Transform.CrosslineSpacing : 25;
        int done = 0;
        var lockObj = new object();
        Parallel.For(0, g.InlineCount, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct }, i =>
        {
            int il = g.Inlines.ValueAt(i);
            var section = new Array2D(g.CrosslineCount, g.SampleCount);
            var live = new bool[g.CrosslineCount];
            for (int j = 0; j < g.CrosslineCount; j++) live[j] = input.TryReadTrace(il, g.Crosslines.ValueAt(j), section.Row(j));
            var ctx = new ProcessingContext(g.SampleInterval, g.StartTime, spacing, null, il, ct);
            foreach (var s in steps) s.Operation.ProcessEnsemble(section, s.Parameters, ctx);
            lock (lockObj)
            {
                for (int j = 0; j < g.CrosslineCount; j++)
                    if (live[j]) output.WriteTrace(il, g.Crosslines.ValueAt(j), section.Row(j));
                done++;
                progress?.Report(ProgressInfo.Of(done, g.InlineCount, $"Processed inline {il} ({done}/{g.InlineCount})"));
            }
        });
        output.Flush();
    }

    /// <summary>Processes gathers (any scope) and streams them to a SEG-Y file.</summary>
    public static void RunGathers(IGatherDataset input, string outputSegyPath, IReadOnlyList<ProcessingStep> steps,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = input.Geometry;
        var v = Validate(steps, preStack: true);
        if (!v.IsValid) throw new InvalidOperationException("Invalid processing parameters:" + Environment.NewLine + v);
        using var writer = new SegyStreamWriter(outputSegyPath, g.SampleCount, g.SampleInterval, g.StartTime,
            ["GeoVision Pro processed gathers", .. steps.Select(s => $"{s.OperationId} {s.Parameters.ToJson()}")], 2, input.Offsets.Length);
        for (int i = 0; i < g.InlineCount; i++)
        {
            int il = g.Inlines.ValueAt(i);
            var results = new Array2D?[g.CrosslineCount];
            Parallel.For(0, g.CrosslineCount, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct }, j =>
            {
                var gather = input.ReadGather(il, g.Crosslines.ValueAt(j));
                if (gather is null) return;
                var ctx = new ProcessingContext(g.SampleInterval, g.StartTime, 25, input.Offsets, il, ct);
                foreach (var s in steps) s.Operation.ProcessEnsemble(gather, s.Parameters, ctx);
                results[j] = gather;
            });
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                var gather = results[j];
                if (gather is null) continue;
                int xl = g.Crosslines.ValueAt(j);
                var p = g.MapLocation(il, xl);
                for (int k = 0; k < gather.Rows; k++)
                    writer.AppendTrace(il, xl, p.X, p.Y, (int)Math.Round(input.Offsets[k]), k + 1, gather.Row(k));
            }
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"Processed gathers of inline {il}"));
        }
    }

    /// <summary>NMO-corrects and stacks every gather into a post-stack volume.</summary>
    public static void NmoStack(IGatherDataset input, IWritableSeismicVolume output, VelocityFunction vrms, double stretchMute,
        IReadOnlyList<ProcessingStep>? preStackSteps = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = input.Geometry;
        int done = 0;
        var lockObj = new object();
        Parallel.For(0, g.InlineCount, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct }, i =>
        {
            int il = g.Inlines.ValueAt(i);
            var stacked = new List<(int Xl, float[] Trace)>();
            for (int j = 0; j < g.CrosslineCount; j++)
            {
                int xl = g.Crosslines.ValueAt(j);
                var gather = input.ReadGather(il, xl);
                if (gather is null) continue;
                var ctx = new ProcessingContext(g.SampleInterval, g.StartTime, 25, input.Offsets, il, ct);
                if (preStackSteps is not null) foreach (var s in preStackSteps) s.Operation.ProcessEnsemble(gather, s.Parameters, ctx);
                Nmo.Correct(gather, input.Offsets, vrms, g.SampleInterval, g.StartTime, stretchMute);
                stacked.Add((xl, Nmo.Stack(gather)));
            }
            lock (lockObj)
            {
                foreach (var (xl, tr) in stacked) output.WriteTrace(il, xl, tr);
                done++;
                progress?.Report(ProgressInfo.Of(done, g.InlineCount, $"Stacked inline {il}"));
            }
        });
        output.Flush();
    }

    /// <summary>Amplitude spectrum averaged over the traces of a section (QC).</summary>
    public static (double[] Frequencies, double[] Amplitudes) AverageSpectrum(Array2D section, double dt)
    {
        int nfft = Core.Numerics.Fft.NextPowerOfTwo(Math.Max(section.Cols, 2));
        double[]? acc = null; double[]? freqs = null; int n = 0;
        for (int r = 0; r < section.Rows; r++)
        {
            var row = section.Row(r);
            if (Core.Numerics.Dsp.MaxAbs(row) == 0) continue;
            var (f, a) = Core.Numerics.Fft.AmplitudeSpectrum(row, dt, nfft);
            acc ??= new double[a.Length]; freqs = f;
            for (int k = 0; k < a.Length; k++) acc[k] += a[k];
            n++;
        }
        if (acc is null || freqs is null) return ([], []);
        for (int k = 0; k < acc.Length; k++) acc[k] /= n;
        return (freqs, acc);
    }
}
