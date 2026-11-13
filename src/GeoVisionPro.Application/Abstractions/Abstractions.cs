using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Application.Abstractions;

/// <summary>All persistent content of a project (metadata stored in the project database).</summary>
public sealed class ProjectSnapshot
{
    public required Project Project { get; init; }
    public List<SeismicDataset> Seismic { get; init; } = [];
    public List<Well> Wells { get; init; } = [];
    public List<Horizon> Horizons { get; init; } = [];
    public List<Fault> Faults { get; init; } = [];
    public List<WaveletEntity> Wavelets { get; init; } = [];
    public List<WellTieRecord> WellTies { get; init; } = [];
    public List<ProcessingRecord> History { get; init; } = [];
    public List<WorkflowDefinitionEntity> Workflows { get; init; } = [];
    public List<ReportEntity> Reports { get; init; } = [];
    public List<GeologicalModelEntity> Models { get; init; } = [];
    public List<CrossplotEntity> Crossplots { get; init; } = [];
}

/// <summary>Persistence of project metadata (implemented by GeoVisionPro.DataAccess with SQLite / EF Core).</summary>
public interface IProjectRepository
{
    Task CreateAsync(string projectFile, ProjectSnapshot snapshot, CancellationToken ct = default);
    Task<ProjectSnapshot> LoadAsync(string projectFile, CancellationToken ct = default);
    Task SaveAsync(string projectFile, ProjectSnapshot snapshot, CancellationToken ct = default);
}

public enum MessageLevel { Info, Warning, Error, Success }

public sealed record LogMessage(DateTime Time, MessageLevel Level, string Source, string Text)
{
    public override string ToString() => $"{Time:HH:mm:ss} [{Level}] {Source}: {Text}";
}

/// <summary>Application message log shown in the Messages/Console panels.</summary>
public interface IMessageLog
{
    event EventHandler<LogMessage>? MessageLogged;
    IReadOnlyList<LogMessage> Messages { get; }
    void Write(MessageLevel level, string source, string text);
    void Info(string source, string text) => Write(MessageLevel.Info, source, text);
    void Warn(string source, string text) => Write(MessageLevel.Warning, source, text);
    void Error(string source, string text) => Write(MessageLevel.Error, source, text);
    void Success(string source, string text) => Write(MessageLevel.Success, source, text);
    void Report(string source, ValidationReport report)
    {
        foreach (var i in report.Issues) Write(i.Severity switch { Severity.Error => MessageLevel.Error, Severity.Warning => MessageLevel.Warning, _ => MessageLevel.Info }, source, i.ToString());
    }
}

/// <summary>An undoable action.</summary>
public interface IUndoableCommand
{
    string Description { get; }
    void Undo();
    void Redo();
}
