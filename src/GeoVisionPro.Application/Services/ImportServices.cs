using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Application.Services;

/// <summary>Header inspection of a SEG-Y file (for the SEG-Y browser) without importing it.</summary>
public sealed record SegyInspection(string Path, string Summary, string TextHeader, IReadOnlyList<(SegyField Field, long Value)> BinaryHeader,
    IReadOnlyList<IReadOnlyList<(SegyField Field, long Value)>> FirstTraceHeaders, IReadOnlyList<float[]> FirstTraces, SegyKeyLocations DetectedKeys,
    IReadOnlyList<string> Warnings, long TraceCount, int Samples, double SampleInterval, SegySampleFormat Format);

public enum SeismicImportMode { LinkMemoryMapped, CopyToChunkedStore }

public sealed class SeismicImportService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;

    public SeismicImportService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    public static SegyInspection Inspect(string path, int traces = 20, SegyOpenOptions? options = null)
    {
        using var f = SegyFile.Open(path, options);
        int n = (int)Math.Min(traces, f.TraceCount);
        var th = Enumerable.Range(0, n).Select(i => f.ReadAllTraceFields(i)).ToList();
        var tr = Enumerable.Range(0, n).Select(i => f.ReadTrace(i)).ToList();
        var keys = SegyScanner.DetectKeyLocations(f);
        return new SegyInspection(path, f.Summary(), f.TextHeader, f.ReadAllBinaryFields(), th, tr, keys, f.Warnings.ToList(), f.TraceCount, f.SamplesPerTrace, f.SampleInterval, f.Format);
    }

    /// <summary>
    /// Imports a SEG-Y file: scans geometry, validates, and either links it (memory-mapped, no copy) or copies it into a
    /// compressed chunked Zarr store inside the project (recommended for interpretation of large volumes).
    /// </summary>
    public async Task<SeismicDataset> ImportAsync(string path, string name, SegyKeyLocations? keys, SeismicImportMode mode, bool angleGathers = false,
        SegyOpenOptions? options = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var file = SegyFile.Open(path, options);
            SegyScanResult scan;
            try { scan = SegyScanner.Scan(file, keys, progress, ct); }
            catch { file.Dispose(); throw; }
            var ds = new SeismicDataset
            {
                Name = name, SourcePath = Path.GetFullPath(path), Geometry = scan.Geometry, Kind = angleGathers && scan.Kind == SeismicKind.PreStackGathers ? SeismicKind.AngleGathers : scan.Kind,
                ImportSettingsJson = SegyImportSettings.From(scan.Keys, options).ToJson(), Fold = scan.Fold,
                Description = file.Summary()
            };
            if (scan.Offsets.Length > 0) ds.Offsets = scan.Offsets;
            _log.Report($"Import {name}", scan.Validation);
            if (ds.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers)
            {
                file.Dispose();
                ds.StorageFormat = StorageFormat.SegyLinked;
                ds.StoragePath = ds.SourcePath;
            }
            else
            {
                var vol = new SegyVolume(name, file, scan);
                if (mode == SeismicImportMode.LinkMemoryMapped)
                {
                    ds.StorageFormat = StorageFormat.SegyLinked;
                    ds.StoragePath = ds.SourcePath;
                    var (mn, mx, rms) = vol.EstimateStatistics();
                    ds.AmplitudeMin = mn; ds.AmplitudeMax = mx; ds.AmplitudeRms = rms;
                    _session.CacheVolume(ds.Id, vol);
                }
                else
                {
                    var rel = Path.Combine("seismic", ds.Id.ToString("N"));
                    var store = ZarrVolume.Create(Path.Combine(_session.DataDirectory, rel), name, scan.Geometry);
                    vol.CopyTo(store, null, progress, ct);
                    vol.Dispose();
                    ds.StorageFormat = StorageFormat.ZarrBricks;
                    ds.StoragePath = rel;
                    var (mn, mx, rms) = store.EstimateStatistics();
                    ds.AmplitudeMin = mn; ds.AmplitudeMax = mx; ds.AmplitudeRms = rms;
                    _session.CacheVolume(ds.Id, store);
                }
            }
            ds.ValidationStatus = !scan.Validation.IsValid ? DataValidationStatus.Invalid : scan.Validation.HasWarnings ? DataValidationStatus.ValidWithWarnings : DataValidationStatus.Valid;
            ds.ValidationMessages = scan.Validation.ToString();
            _session.Add(ds);
            _log.Success("Import", $"Seismic '{name}' imported: {scan.Geometry.InlineCount}×{scan.Geometry.CrosslineCount} traces, {scan.Geometry.SampleCount} samples ({ds.Kind}, {ds.StorageFormat}).");
            return ds;
        }, ct);
    }

    /// <summary>Registers an in-memory or computed volume as a project dataset stored in the chunked store.</summary>
    public SeismicDataset AddVolume(ISeismicVolume source, string name, SeismicCategory category, Guid? parent, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var ds = new SeismicDataset { Name = name, Category = category, Geometry = source.Geometry, ParentDatasetId = parent };
        var rel = Path.Combine("seismic", ds.Id.ToString("N"));
        var store = ZarrVolume.Create(Path.Combine(_session.DataDirectory, rel), name, source.Geometry);
        source.CopyTo(store, null, progress, ct);
        ds.StorageFormat = StorageFormat.ZarrBricks;
        ds.StoragePath = rel;
        var (mn, mx, rms) = store.EstimateStatistics();
        ds.AmplitudeMin = mn; ds.AmplitudeMax = mx; ds.AmplitudeRms = rms;
        ds.ValidationStatus = DataValidationStatus.Valid;
        _session.CacheVolume(ds.Id, store);
        _session.Add(ds);
        return ds;
    }

    /// <summary>Writes gathers to a SEG-Y in the project folder and registers them.</summary>
    public SeismicDataset AddGathers(IGatherDataset gathers, string name, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var dir = _session.SubDirectory("seismic");
        var id = Guid.NewGuid();
        var path = Path.Combine(dir, $"{id:N}_gathers.sgy");
        SegyWriter.WriteGathers(path, gathers, SegySampleFormat.IeeeFloat32, progress, ct);
        var ds = new SeismicDataset
        {
            Id = id, Name = name, Kind = gathers.IsAngleGather ? SeismicKind.AngleGathers : SeismicKind.PreStackGathers, Geometry = gathers.Geometry,
            StorageFormat = StorageFormat.SegyLinked, StoragePath = path, SourcePath = path, Fold = gathers.Offsets.Length, Offsets = gathers.Offsets,
            ImportSettingsJson = SegyImportSettings.From(SegyKeyLocations.Rev1).ToJson(), ValidationStatus = DataValidationStatus.Valid
        };
        _session.Add(ds);
        return ds;
    }
}

public sealed class WellImportService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public WellImportService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    public Well ImportLas(string path)
    {
        var doc = LasReader.Parse(File.ReadAllText(path));
        var well = LasReader.ToWell(doc, path);
        _log.Report($"LAS {Path.GetFileName(path)}", doc.Validation);
        var existing = _session.Wells.FirstOrDefault(w => w.Name.Equals(well.Name, StringComparison.OrdinalIgnoreCase) || (well.Uwi is not null && well.Uwi == w.Uwi));
        if (existing is not null)
        {
            // merge curves into the existing well
            foreach (var c in well.Curves)
            {
                c.WellId = existing.Id;
                var dup = existing.FindCurve(c.Mnemonic);
                if (dup is not null) { c.Mnemonic += "_" + (existing.Curves.Count(x => x.Mnemonic.StartsWith(dup.Mnemonic)) + 1); c.Name = c.Mnemonic; }
                existing.Curves.Add(c);
            }
            foreach (var t in well.Tops) { t.WellId = existing.Id; existing.Tops.Add(t); }
            _session.MarkDirty(existing);
            _log.Info("Import", $"Merged {well.Curves.Count} curves into existing well '{existing.Name}'.");
            return existing;
        }
        _session.Add(well);
        _log.Success("Import", $"Well '{well.Name}' imported with {well.Curves.Count} curves ({string.Join(", ", well.Curves.Select(c => c.Mnemonic))}).");
        return well;
    }

    public int ImportTops(string path, Well? target = null)
    {
        int n = 0;
        foreach (var (wellName, name, md) in AsciiWellImport.ReadTops(File.ReadAllText(path)))
        {
            var w = target ?? _session.Wells.FirstOrDefault(x => x.Name.Equals(wellName, StringComparison.OrdinalIgnoreCase));
            if (w is null) { _log.Warn("Tops", $"Well '{wellName}' not found for top '{name}'."); continue; }
            w.Tops.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            w.Tops.Add(new WellTop { WellId = w.Id, Name = name, MeasuredDepth = md, Source = Path.GetFileName(path) });
            n++;
            _session.MarkDirty(w);
        }
        _log.Success("Tops", $"{n} tops imported from {Path.GetFileName(path)}.");
        return n;
    }

    public void ImportDeviation(string path, Well well)
    {
        var dev = AsciiWellImport.ReadDeviation(File.ReadAllText(path));
        if (dev.Count < 2) throw new InvalidDataException("Deviation survey needs at least two stations (MD, INC, AZI).");
        well.Deviation = dev;
        var traj = WellTrajectory.Compute(well);
        _session.MarkDirty(well);
        _log.Success("Deviation", $"{dev.Count} stations imported for {well.Name}; max dogleg {traj.Stations.Max(s => s.DoglegSeverity):0.00}°/30.");
    }

    public CheckshotSurvey ImportCheckshots(string path, Well well, bool oneWayTime = false)
    {
        var pts = AsciiWellImport.ReadTimeDepth(File.ReadAllText(path), oneWayTime);
        if (pts.Count < 2) throw new InvalidDataException("Checkshot file needs at least two time-depth pairs.");
        var cs = new CheckshotSurvey { WellId = well.Id, Name = Path.GetFileNameWithoutExtension(path), Points = pts };
        well.Checkshots.Add(cs);
        well.ActiveTimeDepthId ??= cs.Id;
        var model = Checkshot.TimeDepthModel.FromCheckshot(cs, WellTrajectory.Compute(well), _session.Project.SeismicReferenceDatum);
        foreach (var m in Checkshot.CheckshotCalibration.QualityControl(model)) _log.Warn("Checkshot", $"{well.Name}: {m}");
        _session.MarkDirty(well);
        _log.Success("Checkshot", $"{pts.Count} time-depth pairs imported for {well.Name}.");
        return cs;
    }

    public int ImportAsciiLogs(string path, Well well)
    {
        var curves = AsciiWellImport.ReadLogs(File.ReadAllText(path), well.Id, Path.GetFileName(path));
        well.Curves.AddRange(curves);
        _session.MarkDirty(well);
        return curves.Count;
    }
}

public sealed class InterpretationService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public InterpretationService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    public Horizon CreateHorizon(SeismicDataset survey, string name, string color)
    {
        var h = Horizon.CreateFor(survey.Geometry, name, color);
        h.SurveyId = survey.Id;
        _session.Add(h);
        return h;
    }

    public Fault CreateFault(SeismicDataset survey, string name, string color)
    {
        var f = new Fault { Name = name, Color = color, SurveyId = survey.Id, Domain = survey.Domain };
        _session.Add(f);
        return f;
    }

    public Horizon ImportHorizon(string path, SeismicDataset survey, HorizonXyzColumns columns, bool zInMs)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var h = text.TrimStart().StartsWith('!') || text.Contains("@") && text.Contains("GRID")
            ? InterpretationIO.ImportZmap(text, survey.Geometry, name, zInMs)
            : InterpretationIO.ImportXyz(text, survey.Geometry, name, columns, zInMs);
        h.SurveyId = survey.Id; h.SourcePath = path;
        _session.Add(h);
        _log.Success("Horizon", $"'{name}': {h.Description}");
        return h;
    }

    public Fault ImportFault(string path, SeismicDataset survey, bool mapCoordinates, bool zInMs)
    {
        var f = InterpretationIO.ImportFaultSticks(File.ReadAllText(path), survey.Geometry, Path.GetFileNameWithoutExtension(path), mapCoordinates, zInMs);
        f.SurveyId = survey.Id; f.SourcePath = path;
        _session.Add(f);
        _log.Success("Fault", $"'{f.Name}': {f.Sticks.Count} sticks imported.");
        return f;
    }
}
