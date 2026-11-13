using System.Text.Json;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Attributes;
using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Inversion;
using GeoVisionPro.Petrophysics;
using GeoVisionPro.Seismic.Storage;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.SeismicProcessing;
using GeoVisionPro.WellLog;
using GeoVisionPro.WellTie;

namespace GeoVisionPro.Application.Services;

/// <summary>Runs processing, attributes and inversion on project data, creating output datasets with full provenance.</summary>
public sealed class ProcessingService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public ProcessingService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    private (SeismicDataset Ds, ZarrVolume Store) CreateOutput(SurveyGeometry g, string name, SeismicCategory cat, Guid parent, string? units = null)
    {
        var ds = new SeismicDataset { Name = name, Category = cat, Geometry = g, ParentDatasetId = parent, StorageFormat = StorageFormat.ZarrBricks, Units = units };
        ds.StoragePath = Path.Combine("seismic", ds.Id.ToString("N"));
        var store = ZarrVolume.Create(Path.Combine(_session.DataDirectory, ds.StoragePath), name, g);
        return (ds, store);
    }

    private ProcessingRecord Begin(string op, string version, string parameters, params Guid[] inputs) => new()
    {
        Name = op, Operation = op, AlgorithmVersion = version, ParametersJson = parameters, InputIds = string.Join(";", inputs),
        StartedUtc = DateTime.UtcNow, Status = ExecutionStatus.Running
    };

    private void Finish(ProcessingRecord rec, SeismicDataset? output, ZarrVolume? store, Exception? error)
    {
        rec.FinishedUtc = DateTime.UtcNow;
        rec.Status = error switch { null => ExecutionStatus.Succeeded, OperationCanceledException => ExecutionStatus.Cancelled, _ => ExecutionStatus.Failed };
        rec.Messages = error?.Message;
        if (output is not null && store is not null && error is null)
        {
            var (mn, mx, rms) = store.EstimateStatistics();
            output.AmplitudeMin = mn; output.AmplitudeMax = mx; output.AmplitudeRms = rms;
            output.ProcessingRecordId = rec.Id;
            output.ValidationStatus = DataValidationStatus.Valid;
            rec.OutputIds = output.Id.ToString();
            _session.CacheVolume(output.Id, store);
            _session.Add(output);
        }
        else if (store is not null)
        {
            store.Dispose();
            try { Directory.Delete(store.RootPath, true); } catch (IOException) { }
        }
        _session.Add(rec);
    }

    public async Task<SeismicDataset> RunSequenceAsync(SeismicDataset input, IReadOnlyList<ProcessingStep> steps, string outputName,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var vin = _session.GetVolume(input);
        var json = JsonSerializer.Serialize(steps.Select(s => new { s.OperationId, Version = s.Operation.Version, Parameters = (Dictionary<string, string>)s.Parameters }));
        var rec = Begin(string.Join(" → ", steps.Select(s => s.Operation.DisplayName)), "1.0", json, input.Id);
        var (ds, store) = CreateOutput(vin.Geometry, outputName, SeismicCategory.Processed, input.Id);
        try
        {
            await Task.Run(() => ProcessingEngine.RunVolume(vin, store, steps, progress, ct), ct);
            Finish(rec, ds, store, null);
            _log.Success("Processing", $"'{outputName}' created from '{input.Name}' ({rec.Operation}).");
            return ds;
        }
        catch (Exception ex) { Finish(rec, null, store, ex); throw; }
    }

    public async Task<SeismicDataset> RunGatherSequenceAsync(SeismicDataset input, IReadOnlyList<ProcessingStep> steps, string outputName,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var gin = _session.GetGathers(input);
        var rec = Begin(string.Join(" → ", steps.Select(s => s.Operation.DisplayName)), "1.0",
            JsonSerializer.Serialize(steps.Select(s => new { s.OperationId, Parameters = (Dictionary<string, string>)s.Parameters })), input.Id);
        var id = Guid.NewGuid();
        var path = Path.Combine(_session.SubDirectory("seismic"), $"{id:N}_gathers.sgy");
        try
        {
            await Task.Run(() => ProcessingEngine.RunGathers(gin, path, steps, progress, ct), ct);
            var ds = new SeismicDataset
            {
                Id = id, Name = outputName, Kind = input.Kind, Category = SeismicCategory.Processed, Geometry = gin.Geometry, StorageFormat = StorageFormat.SegyLinked,
                StoragePath = path, SourcePath = path, Fold = gin.Offsets.Length, Offsets = gin.Offsets, ParentDatasetId = input.Id, ProcessingRecordId = rec.Id,
                ImportSettingsJson = SegyImportSettings.From(Seismic.Segy.SegyKeyLocations.Rev1).ToJson(), ValidationStatus = DataValidationStatus.Valid
            };
            rec.OutputIds = id.ToString(); rec.Status = ExecutionStatus.Succeeded; rec.FinishedUtc = DateTime.UtcNow;
            _session.Add(rec); _session.Add(ds);
            return ds;
        }
        catch (Exception ex)
        {
            rec.Status = ex is OperationCanceledException ? ExecutionStatus.Cancelled : ExecutionStatus.Failed; rec.Messages = ex.Message; rec.FinishedUtc = DateTime.UtcNow;
            _session.Add(rec);
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task<SeismicDataset> NmoStackAsync(SeismicDataset gathers, string velocityFunction, double stretchMute, IReadOnlyList<ProcessingStep>? preSteps,
        string outputName, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var gin = _session.GetGathers(gathers);
        var vf = VelocityFunction.Parse(velocityFunction);
        var rec = Begin("NMO + Stack", "1.0", JsonSerializer.Serialize(new { velocity = vf.ToString(), stretchMute }), gathers.Id);
        var (ds, store) = CreateOutput(gin.Geometry, outputName, SeismicCategory.Processed, gathers.Id);
        try
        {
            await Task.Run(() => ProcessingEngine.NmoStack(gin, store, vf, stretchMute, preSteps, progress, ct), ct);
            Finish(rec, ds, store, null);
            return ds;
        }
        catch (Exception ex) { Finish(rec, null, store, ex); throw; }
    }

    public async Task<SeismicDataset> ComputeAttributeAsync(SeismicDataset input, ISeismicAttribute attr, ParameterSet p, string outputName,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var vin = _session.GetVolume(input);
        var rec = Begin($"Attribute: {attr.DisplayName}", "1.0", p.ToJson(), input.Id);
        var (ds, store) = CreateOutput(vin.Geometry, outputName, SeismicCategory.Attribute, input.Id, attr.Unit);
        try
        {
            await Task.Run(() => AttributeEngine.ComputeVolume(vin, store, attr, p, progress, ct), ct);
            Finish(rec, ds, store, null);
            _log.Success("Attributes", $"'{outputName}' computed ({attr.DisplayName}).");
            return ds;
        }
        catch (Exception ex) { Finish(rec, null, store, ex); throw; }
    }

    public async Task<SeismicDataset> InvertAsync(SeismicDataset seismic, LowFrequencyModel model, Wavelet wavelet, InversionParameters p, string outputName,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var vin = _session.GetVolume(seismic);
        var rec = Begin($"Inversion: {p.Method}", "1.0", JsonSerializer.Serialize(p, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }), seismic.Id);
        var (ds, store) = CreateOutput(vin.Geometry, outputName, SeismicCategory.Inversion, seismic.Id, "m/s*g/cc");
        ds.Description = $"Estimated acoustic impedance ({p.Method}); low-frequency model below {model.CutoffHz} Hz from wells. Not a measurement.";
        try
        {
            await Task.Run(() => InversionEngine.RunVolume(vin, model, wavelet, p, store, progress, ct), ct);
            Finish(rec, ds, store, null);
            _log.Success("Inversion", $"'{outputName}' generated ({p.Method}).");
            return ds;
        }
        catch (Exception ex) { Finish(rec, null, store, ex); throw; }
    }

    /// <summary>Writes the low-frequency model as a volume (for QC and as an inversion input).</summary>
    public async Task<SeismicDataset> MaterializeModelAsync(SeismicDataset survey, LowFrequencyModel model, string name, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = survey.Geometry;
        var rec = Begin("Low-frequency model", "1.0", JsonSerializer.Serialize(new { model.CutoffHz }), survey.Id);
        var (ds, store) = CreateOutput(g, name, SeismicCategory.Model, survey.Id, "m/s*g/cc");
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < g.InlineCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    int il = g.Inlines.ValueAt(i);
                    for (int j = 0; j < g.CrosslineCount; j++) store.WriteTrace(il, g.Crosslines.ValueAt(j), model.Trace(il, g.Crosslines.ValueAt(j)));
                    progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"Model inline {il}"));
                }
                store.Flush();
            }, ct);
            Finish(rec, ds, store, null);
            return ds;
        }
        catch (Exception ex) { Finish(rec, null, store, ex); throw; }
    }
}

/// <summary>Well-centric services: time-depth relations, petrophysics, rock physics outputs.</summary>
public sealed class WellService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public WellService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    /// <summary>
    /// Time-depth model of a well: the active checkshot/TDR survey if present; otherwise the integrated sonic anchored with
    /// the project replacement velocity (flagged as uncalibrated).
    /// </summary>
    public TimeDepthModel GetTimeDepth(Well well)
    {
        var traj = WellTrajectory.Compute(well);
        double srd = _session.IsOpen ? _session.Project.SeismicReferenceDatum : 0;
        if (well.ActiveTimeDepth is { Points.Count: >= 2 } cs) return TimeDepthModel.FromCheckshot(cs, traj, srd);
        var sonic = well.FindCurve(CurveFamily.Sonic) ?? throw new InvalidOperationException($"{well.Name}: no checkshot/TDR and no sonic log to build a time-depth relationship.");
        _log.Warn("Time-depth", $"{well.Name}: no checkshots - using integrated sonic with replacement velocity {_session.Project.ReplacementVelocity} m/s (uncalibrated).");
        return TimeDepthModel.FromSonic(sonic, traj, srd, _session.IsOpen ? _session.Project.ReplacementVelocity : 1800);
    }

    public CheckshotCalibrationResult CalibrateSonic(Well well, CheckshotSurvey checkshots, bool storeResults)
    {
        var traj = WellTrajectory.Compute(well);
        double srd = _session.Project.SeismicReferenceDatum;
        var sonic = well.FindCurve(CurveFamily.Sonic) ?? throw new InvalidOperationException("Sonic log required.");
        var cs = TimeDepthModel.FromCheckshot(checkshots, traj, srd);
        var first = cs.TvdSs[0];
        var sm = TimeDepthModel.FromSonic(sonic, traj, srd, CheckshotCalibration.ReplacementVelocity(cs), (first, cs.Twt[0]));
        var res = CheckshotCalibration.Calibrate(sm, cs, sonic, traj);
        if (storeResults)
        {
            var tdr = res.Calibrated.ToSurvey(well.Id, "Calibrated sonic TDR", TimeDepthSource.SonicIntegrated, traj);
            well.Checkshots.Add(tdr);
            if (res.CorrectedSonic is not null) well.Curves.Add(res.CorrectedSonic);
            _session.MarkDirty(well);
        }
        _log.Info("Checkshot", $"{well.Name}: drift RMS {res.RmsResidualBefore * 1000:0.0} ms before, {res.RmsResidualAfter * 1000:0.00} ms after calibration.");
        return res;
    }

    public PetroResult Evaluate(Well well, PetroParameters p, double top, double bottom, bool storeCurves)
    {
        var r = WellEvaluation.Evaluate(well, p, top, bottom);
        if (storeCurves)
        {
            foreach (var c in new[] { r.Vsh, r.PhiT, r.PhiE, r.Sw, r.Shc, r.NetFlag }.Concat(r.Lithology is null ? [] : [r.Lithology]))
            {
                well.Curves.RemoveAll(x => x.IsComputed && x.Mnemonic == c.Mnemonic);
                c.WellId = well.Id;
                well.Curves.Add(c);
            }
            _session.MarkDirty(well);
        }
        foreach (var n in r.Notes) _log.Info("Petrophysics", $"{well.Name}: {n}");
        return r;
    }

    public void AddCurve(Well well, LogCurve curve)
    {
        curve.WellId = well.Id;
        well.Curves.RemoveAll(c => c.IsComputed && c.Mnemonic == curve.Mnemonic);
        well.Curves.Add(curve);
        _session.MarkDirty(well);
    }

    /// <summary>Saves a well tie: record, wavelet and calibrated TDR (set active).</summary>
    public WellTieRecord SaveTie(WellTieSession tie, SeismicDataset seismic, bool activateTdr)
    {
        var wav = WaveletEntity.From(tie.Wavelet, "Well tie");
        _session.Add(wav);
        var rec = tie.ToRecord(seismic.Id, wav.Id);
        var tdr = tie.TiedTdr.ToSurvey(tie.Well.Id, $"Tie TDR ({seismic.Name})", TimeDepthSource.WellTieCalibrated, tie.Trajectory);
        tie.Well.Checkshots.Add(tdr);
        rec.CalibratedTimeDepthId = tdr.Id;
        if (activateTdr) tie.Well.ActiveTimeDepthId = tdr.Id;
        _session.Add(rec);
        _session.MarkDirty(tie.Well);
        _log.Success("Well tie", $"{tie.Well.Name} tied to '{seismic.Name}': r = {rec.Correlation:0.000}, shift {rec.BulkShift * 1000:0.0} ms, phase {rec.PhaseRotation:0}°.");
        return rec;
    }
}
