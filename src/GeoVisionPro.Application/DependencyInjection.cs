using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Application.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace GeoVisionPro.Application;

public static class DependencyInjection
{
    /// <summary>Registers application-layer services (requires an <see cref="IProjectRepository"/> implementation).</summary>
    public static IServiceCollection AddGeoVisionApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMessageLog, MessageLog>();
        services.AddSingleton<ProjectSession>();
        services.AddSingleton<UndoRedoManager>();
        services.AddSingleton<BackgroundTaskManager>();
        services.AddSingleton<SeismicImportService>();
        services.AddSingleton<WellImportService>();
        services.AddSingleton<InterpretationService>();
        services.AddSingleton<ProcessingService>();
        services.AddSingleton<WellService>();
        services.AddSingleton<WorkflowService>();
        services.AddSingleton<ExportService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<SampleProjectService>();
        return services;
    }
}
