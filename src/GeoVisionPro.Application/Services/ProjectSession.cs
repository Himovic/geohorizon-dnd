using System.Collections.Concurrent;
using System.Text.Json;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Application.Services;

public enum ProjectChangeKind { Opened, Closed, Saved, ItemAdded, ItemRemoved, ItemChanged }

public sealed record ProjectChange(ProjectChangeKind Kind, object? Item = null);

/// <summary>
/// The open project: metadata collections (persisted through <see cref="IProjectRepository"/>), the project data folder
/// for bulk data (Zarr volumes, exports, reports) and a cache of open seismic volumes.
/// Project layout: &lt;name&gt;.gvp (SQLite metadata) and &lt;name&gt;.gvdata/ (bulk data).
/// </summary>
public sealed class ProjectSession : IDisposable
{
    public const string ProjectExtension = ".gvp";
    private readonly IProjectRepository _repo;
    private readonly IMessageLog _log;
    private readonly ConcurrentDictionary<Guid, ISeismicVolume> _volumes = new();
    private readonly ConcurrentDictionary<Guid, IGatherDataset> _gathers = new();
    private ProjectSnapshot? _snapshot;

    public event EventHandler<ProjectChange>? Changed;

    public ProjectSession(IProjectRepository repo, IMessageLog log)
    {
        _repo = repo; _log = log;
    }

    public bool IsOpen => _snapshot is not null;
    public bool IsDirty { get; private set; }
    public string? ProjectFile { get; private set; }
    public string DataDirectory => ProjectFile is null ? throw new InvalidOperationException("No project open.") : Path.ChangeExtension(ProjectFile, ".gvdata");
    public Project Project => Snap.Project;
    private ProjectSnapshot Snap => _snapshot ?? throw new InvalidOperationException("No project is open.");

    public IReadOnlyList<SeismicDataset> Seismic => Snap.Seismic;
    public IReadOnlyList<Well> Wells => Snap.Wells;
    public IReadOnlyList<Horizon> Horizons => Snap.Horizons;
    public IReadOnlyList<Fault> Faults => Snap.Faults;
    public IReadOnlyList<WaveletEntity> Wavelets => Snap.Wavelets;
    public IReadOnlyList<WellTieRecord> WellTies => Snap.WellTies;
    public IReadOnlyList<ProcessingRecord> History => Snap.History;
    public IReadOnlyList<WorkflowDefinitionEntity> Workflows => Snap.Workflows;
    public IReadOnlyList<ReportEntity> Reports => Snap.Reports;
    public IReadOnlyList<GeologicalModelEntity> Models => Snap.Models;
    public IReadOnlyList<CrossplotEntity> Crossplots => Snap.Crossplots;

    public string SubDirectory(string name)
    {
        var d = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public async Task CreateAsync(string projectFile, string name, string? description = null, CancellationToken ct = default)
    {
        Close();
        if (!projectFile.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)) projectFile += ProjectExtension;
        var snap = new ProjectSnapshot { Project = new Project { Name = name, Description = description, Author = Environment.UserName } };
        await _repo.CreateAsync(projectFile, snap, ct);
        _snapshot = snap; ProjectFile = Path.GetFullPath(projectFile); IsDirty = false;
        Directory.CreateDirectory(DataDirectory);
        _log.Success("Project", $"Created project '{name}' at {ProjectFile}");
        Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.Opened));
    }

    public async Task OpenAsync(string projectFile, CancellationToken ct = default)
    {
        Close();
        var snap = await _repo.LoadAsync(projectFile, ct);
        _snapshot = snap; ProjectFile = Path.GetFullPath(projectFile); IsDirty = false;
        Directory.CreateDirectory(DataDirectory);
        int missing = 0;
        foreach (var s in snap.Seismic)
        {
            var p = ResolveStoragePath(s);
            bool exists = s.StorageFormat == StorageFormat.ZarrBricks ? ZarrVolume.IsZarrVolume(p) : File.Exists(p);
            if (!exists) { missing++; _log.Warn("Project", $"Seismic '{s.Name}': data not found at {p}."); }
        }
        _log.Success("Project", $"Opened '{snap.Project.Name}': {snap.Seismic.Count} seismic, {snap.Wells.Count} wells, {snap.Horizons.Count} horizons, {snap.Faults.Count} faults" + (missing > 0 ? $" ({missing} missing data files)" : ""));
        Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.Opened));
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (_snapshot is null || ProjectFile is null) return;
        _snapshot.Project.Touch();
        await _repo.SaveAsync(ProjectFile, _snapshot, ct);
        IsDirty = false;
        _log.Success("Project", $"Saved {Path.GetFileName(ProjectFile)}");
        Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.Saved));
    }

    public async Task SaveAsAsync(string projectFile, CancellationToken ct = default)
    {
        if (_snapshot is null || ProjectFile is null) return;
        if (!projectFile.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase)) projectFile += ProjectExtension;
        var oldData = DataDirectory;
        var newData = Path.ChangeExtension(Path.GetFullPath(projectFile), ".gvdata");
        if (!string.Equals(oldData, newData, StringComparison.OrdinalIgnoreCase) && Directory.Exists(oldData))
            CopyDirectory(oldData, newData);
        await _repo.CreateAsync(projectFile, _snapshot, ct);
        ProjectFile = Path.GetFullPath(projectFile);
        await SaveAsync(ct);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    public void Close()
    {
        foreach (var v in _volumes.Values) v.Dispose();
        foreach (var g in _gathers.Values) g.Dispose();
        _volumes.Clear(); _gathers.Clear();
        if (_snapshot is not null)
        {
            _snapshot = null; ProjectFile = null;
            Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.Closed));
        }
    }

    public void MarkDirty(object? item = null)
    {
        IsDirty = true;
        Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.ItemChanged, item));
    }

    private void Added(object item) { IsDirty = true; Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.ItemAdded, item)); }

    public void Add(SeismicDataset d) { Snap.Seismic.Add(d); Added(d); }
    public void Add(Well w) { Snap.Wells.Add(w); Added(w); }
    public void Add(Horizon h) { Snap.Horizons.Add(h); Added(h); }
    public void Add(Fault f) { Snap.Faults.Add(f); Added(f); }
    public void Add(WaveletEntity w) { Snap.Wavelets.Add(w); Added(w); }
    public void Add(WellTieRecord r) { Snap.WellTies.RemoveAll(x => x.WellId == r.WellId && x.SeismicId == r.SeismicId); Snap.WellTies.Add(r); Added(r); }
    public void Add(ProcessingRecord r) { Snap.History.Add(r); Added(r); }
    public void Add(WorkflowDefinitionEntity w) { Snap.Workflows.RemoveAll(x => x.Id == w.Id); Snap.Workflows.Add(w); Added(w); }
    public void Add(ReportEntity r) { Snap.Reports.Add(r); Added(r); }
    public void Add(GeologicalModelEntity m) { Snap.Models.Add(m); Added(m); }
    public void Add(CrossplotEntity c) { Snap.Crossplots.Add(c); Added(c); }

    public bool Remove(object item)
    {
        bool removed = item switch
        {
            SeismicDataset d => RemoveSeismic(d),
            Well w => Snap.Wells.Remove(w),
            Horizon h => Snap.Horizons.Remove(h),
            Fault f => Snap.Faults.Remove(f),
            WaveletEntity w => Snap.Wavelets.Remove(w),
            WellTieRecord r => Snap.WellTies.Remove(r),
            WorkflowDefinitionEntity w => Snap.Workflows.Remove(w),
            ReportEntity r => Snap.Reports.Remove(r),
            GeologicalModelEntity m => Snap.Models.Remove(m),
            CrossplotEntity c => Snap.Crossplots.Remove(c),
            LogCurve c => Snap.Wells.FirstOrDefault(w => w.Id == c.WellId)?.Curves.Remove(c) ?? false,
            WellTop t => Snap.Wells.FirstOrDefault(w => w.Id == t.WellId)?.Tops.Remove(t) ?? false,
            CheckshotSurvey c => Snap.Wells.FirstOrDefault(w => w.Id == c.WellId)?.Checkshots.Remove(c) ?? false,
            _ => false
        };
        if (removed) { IsDirty = true; Changed?.Invoke(this, new ProjectChange(ProjectChangeKind.ItemRemoved, item)); }
        return removed;
    }

    private bool RemoveSeismic(SeismicDataset d)
    {
        if (_volumes.TryRemove(d.Id, out var v)) v.Dispose();
        if (_gathers.TryRemove(d.Id, out var g)) g.Dispose();
        bool r = Snap.Seismic.Remove(d);
        if (r && d.StorageFormat == StorageFormat.ZarrBricks)
        {
            var p = ResolveStoragePath(d);
            try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch (IOException e) { _log.Warn("Project", $"Could not delete {p}: {e.Message}"); }
        }
        return r;
    }

    public SeismicDataset? FindSeismic(Guid id) => Snap.Seismic.FirstOrDefault(s => s.Id == id);
    public Well? FindWell(Guid id) => Snap.Wells.FirstOrDefault(s => s.Id == id);

    /// <summary>Absolute path of a dataset's data (Zarr folder relative to the data directory, or linked SEG-Y file).</summary>
    public string ResolveStoragePath(SeismicDataset d) =>
        d.StorageFormat == StorageFormat.ZarrBricks && !Path.IsPathRooted(d.StoragePath) ? Path.Combine(DataDirectory, d.StoragePath) : d.StoragePath;

    /// <summary>Opens (cached) a post-stack volume for reading.</summary>
    public ISeismicVolume GetVolume(SeismicDataset d)
    {
        if (d.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers) throw new InvalidOperationException($"'{d.Name}' is a pre-stack dataset.");
        return _volumes.GetOrAdd(d.Id, _ =>
        {
            var path = ResolveStoragePath(d);
            return d.StorageFormat switch
            {
                StorageFormat.ZarrBricks => ZarrVolume.Open(path),
                StorageFormat.SegyLinked => OpenSegy(d, path),
                _ => throw new NotSupportedException($"Storage {d.StorageFormat} cannot be reopened.")
            };
        });
    }

    public IGatherDataset GetGathers(SeismicDataset d)
    {
        if (d.Kind is not (SeismicKind.PreStackGathers or SeismicKind.AngleGathers)) throw new InvalidOperationException($"'{d.Name}' is not a gather dataset.");
        return _gathers.GetOrAdd(d.Id, _ =>
        {
            var path = ResolveStoragePath(d);
            var settings = SegyImportSettings.FromJson(d.ImportSettingsJson);
            var file = SegyFile.Open(path, settings.Options);
            var scan = SegyScanner.Scan(file, settings.Keys);
            var offsets = d.Offsets.Length == scan.Offsets.Length ? d.Offsets : scan.Offsets;
            var gs = new SegyGatherDataset(d.Name, file, scan, d.Kind == SeismicKind.AngleGathers);
            if (!ReferenceEquals(offsets, scan.Offsets)) Array.Copy(offsets, gs.Offsets, Math.Min(offsets.Length, gs.Offsets.Length));
            return gs;
        });
    }

    private static ISeismicVolume OpenSegy(SeismicDataset d, string path)
    {
        var settings = SegyImportSettings.FromJson(d.ImportSettingsJson);
        var v = SegyVolume.Open(path, settings.Keys, settings.Options);
        return v;
    }

    /// <summary>Registers an already opened volume (e.g. a just-written output) in the cache.</summary>
    public void CacheVolume(Guid id, ISeismicVolume v) => _volumes[id] = v;

    public void Dispose() => Close();
}

/// <summary>Serializable SEG-Y read settings stored with linked datasets.</summary>
public sealed record SegyImportSettings
{
    public int InlineByte { get; init; } = 189;
    public int CrosslineByte { get; init; } = 193;
    public int CdpXByte { get; init; } = 181;
    public int CdpYByte { get; init; } = 185;
    public int ScalarByte { get; init; } = 71;
    public int OffsetByte { get; init; } = 37;
    public short? FormatOverride { get; init; }
    public double? SampleIntervalOverride { get; init; }

    public SegyKeyLocations Keys => new() { InlineByte = InlineByte, CrosslineByte = CrosslineByte, CdpXByte = CdpXByte, CdpYByte = CdpYByte, CoordinateScalarByte = ScalarByte, OffsetByte = OffsetByte };
    public SegyOpenOptions Options => new() { FormatOverride = FormatOverride is { } f ? (SegySampleFormat)f : null, SampleIntervalOverride = SampleIntervalOverride };

    public static SegyImportSettings From(SegyKeyLocations k, SegyOpenOptions? o = null) => new()
    {
        InlineByte = k.InlineByte, CrosslineByte = k.CrosslineByte, CdpXByte = k.CdpXByte, CdpYByte = k.CdpYByte, ScalarByte = k.CoordinateScalarByte, OffsetByte = k.OffsetByte,
        FormatOverride = o?.FormatOverride is { } f ? (short)f : null, SampleIntervalOverride = o?.SampleIntervalOverride
    };

    public string ToJson() => JsonSerializer.Serialize(this);
    public static SegyImportSettings FromJson(string? json) => string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<SegyImportSettings>(json) ?? new();
}
