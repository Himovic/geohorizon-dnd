using System.Text.Json;
using System.Text.Json.Serialization;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Attributes;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.SeismicProcessing;

namespace GeoVisionPro.Application.Workflows;

public enum WorkflowStepKind { Processing, Attribute }

/// <summary>A node of a workflow: a processing operation or an attribute, with parameters and an enabled flag.</summary>
public sealed class WorkflowStep
{
    public WorkflowStepKind Kind { get; set; }
    public string OperationId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
    public bool Enabled { get; set; } = true;
    public string? Comment { get; set; }

    [JsonIgnore]
    public string DisplayName => Kind == WorkflowStepKind.Processing
        ? ProcessingCatalog.All.FirstOrDefault(o => o.Id == OperationId)?.DisplayName ?? OperationId
        : AttributeCatalog.All.FirstOrDefault(o => o.Id == OperationId)?.DisplayName ?? OperationId;

    [JsonIgnore]
    public IReadOnlyList<ParameterDescriptor> Descriptors => Kind == WorkflowStepKind.Processing
        ? ProcessingCatalog.Get(OperationId).Parameters : AttributeCatalog.Get(OperationId).Parameters;
}

/// <summary>
/// A reusable, serialisable (JSON) processing workflow: consecutive post-stack processing steps form a single pass;
/// an attribute step consumes the result of the preceding steps. Gather (pre-stack) steps run on gather datasets.
/// </summary>
public sealed class WorkflowDefinition
{
    public string Name { get; set; } = "New workflow";
    public string Description { get; set; } = "";
    public int FormatVersion { get; set; } = 1;
    public List<WorkflowStep> Steps { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static WorkflowDefinition FromJson(string json) => JsonSerializer.Deserialize<WorkflowDefinition>(json, Options) ?? new WorkflowDefinition();

    public ValidationReport Validate(bool preStack)
    {
        var r = new ValidationReport();
        if (Steps.Count(s => s.Enabled) == 0) r.Error("The workflow has no enabled steps.");
        foreach (var s in Steps.Where(s => s.Enabled))
        {
            try
            {
                var ps = new ParameterSet(s.Parameters);
                r.Merge(ps.Validate(s.Descriptors));
                if (s.Kind == WorkflowStepKind.Processing && ProcessingCatalog.Get(s.OperationId).Scope == OperationScope.Gather && !preStack)
                    r.Error($"'{s.DisplayName}' requires pre-stack input.");
                if (s.Kind == WorkflowStepKind.Attribute && preStack) r.Error($"Attribute '{s.DisplayName}' requires post-stack input.");
            }
            catch (KeyNotFoundException e) { r.Error(e.Message); }
        }
        return r;
    }
}

public sealed class WorkflowService
{
    private readonly ProjectSession _session;
    private readonly ProcessingService _processing;
    private readonly IMessageLog _log;

    public WorkflowService(ProjectSession session, ProcessingService processing, IMessageLog log) { _session = session; _processing = processing; _log = log; }

    public WorkflowDefinitionEntity Save(WorkflowDefinition wf, Guid? id = null)
    {
        var e = new WorkflowDefinitionEntity { Id = id ?? Guid.NewGuid(), Name = wf.Name, Description = wf.Description, Json = wf.ToJson() };
        _session.Add(e);
        return e;
    }

    /// <summary>Executes a workflow on a dataset; returns the created datasets (intermediate attribute inputs are kept for provenance).</summary>
    public async Task<IReadOnlyList<SeismicDataset>> ExecuteAsync(WorkflowDefinition wf, SeismicDataset input, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        bool preStack = input.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers;
        var v = wf.Validate(preStack);
        if (!v.IsValid) throw new InvalidOperationException($"Workflow '{wf.Name}' is invalid:{Environment.NewLine}{v}");
        var outputs = new List<SeismicDataset>();
        var current = input;
        var pending = new List<ProcessingStep>();
        int stage = 0;
        async Task FlushProcessing()
        {
            if (pending.Count == 0) return;
            stage++;
            var name = $"{input.Name} [{wf.Name} {stage}]";
            current = preStack
                ? await _processing.RunGatherSequenceAsync(current, pending.ToList(), name, progress, ct)
                : await _processing.RunSequenceAsync(current, pending.ToList(), name, progress, ct);
            outputs.Add(current);
            pending.Clear();
        }
        foreach (var s in wf.Steps.Where(s => s.Enabled))
        {
            if (s.Kind == WorkflowStepKind.Processing) pending.Add(new ProcessingStep(s.OperationId, new ParameterSet(s.Parameters)));
            else
            {
                await FlushProcessing();
                var attr = AttributeCatalog.Get(s.OperationId);
                current = await _processing.ComputeAttributeAsync(current, attr, new ParameterSet(s.Parameters), $"{input.Name} [{attr.DisplayName}]", progress, ct);
                outputs.Add(current);
            }
        }
        await FlushProcessing();
        _log.Success("Workflow", $"'{wf.Name}' completed: {outputs.Count} dataset(s) created.");
        return outputs;
    }
}
