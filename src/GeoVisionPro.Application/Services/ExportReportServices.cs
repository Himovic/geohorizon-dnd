using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Samples;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;
using GeoVisionPro.Reporting;
using GeoVisionPro.Seismic.Segy;
using GeoVisionPro.Visualization2D;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Application.Services;

public sealed class ExportService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public ExportService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    public async Task ExportSegyAsync(SeismicDataset ds, string path, SegySampleFormat format, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (ds.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers)
            await Task.Run(() => SegyWriter.WriteGathers(path, _session.GetGathers(ds), format, progress, ct), ct);
        else
            await Task.Run(() => SegyWriter.WriteVolume(path, _session.GetVolume(ds), format,
                [$"GeoVision Pro export: {ds.Name}", $"Category {ds.Category}; {ds.Description}", $"Units: {ds.Units ?? "amplitude"}", "SEG-Y REV1"], true, progress, ct), ct);
        _log.Success("Export", $"SEG-Y written: {path}");
    }

    public void ExportLas(Well well, string path, IReadOnlyList<LogCurve>? curves = null) { LasWriter.WriteFile(path, well, curves); _log.Success("Export", $"LAS written: {path}"); }

    public void ExportHorizon(Horizon h, SeismicDataset survey, string path, bool zmap)
    {
        File.WriteAllText(path, zmap ? InterpretationIO.ExportZmap(h, survey.Geometry) : InterpretationIO.ExportXyz(h, survey.Geometry));
        _log.Success("Export", $"Horizon written: {path}");
    }

    public void ExportFault(Fault f, SeismicDataset survey, string path) { File.WriteAllText(path, InterpretationIO.ExportFaultSticks(f, survey.Geometry)); _log.Success("Export", $"Fault written: {path}"); }

    public static DataTable WellCurvesTable(Well w, IReadOnlyList<LogCurve> curves)
    {
        var t = new DataTable(w.Name, ["MD", .. curves.Select(c => $"{c.Mnemonic} ({c.Unit})")]);
        var d = curves[0].Depths;
        for (int i = 0; i < d.Length; i++) t.Add([d[i], .. curves.Select(c => (object?)c.ValueAtDepth(d[i]))]);
        return t;
    }

    public static DataTable HistoryTable(IEnumerable<ProcessingRecord> history)
    {
        var t = new DataTable("Processing history", ["Started (UTC)", "Operation", "Version", "Status", "Inputs", "Outputs", "Parameters", "Messages"]);
        foreach (var r in history.OrderBy(h => h.StartedUtc))
            t.Add(r.StartedUtc.ToString("u"), r.Operation, r.AlgorithmVersion, r.Status.ToString(), r.InputIds, r.OutputIds, r.ParametersJson, r.Messages ?? "");
        return t;
    }
}

/// <summary>Generates the project technical report (PDF) and companion spreadsheet (XLSX).</summary>
public sealed class ReportService
{
    private readonly ProjectSession _session;
    private readonly IMessageLog _log;
    public ReportService(ProjectSession session, IMessageLog log) { _session = session; _log = log; }

    public async Task<string> GenerateProjectReportAsync(string pdfPath, string conclusions, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var p = _session.Project;
            var rb = new ReportBuilder { Title = $"{p.Name} - Technical Report", Subtitle = p.Description ?? "Seismic interpretation and reservoir characterization", Author = p.Author ?? Environment.UserName };
            var info = new ReportSection { Heading = "Project information" };
            info.Paragraphs.Add($"Project file: {_session.ProjectFile}. Coordinate system: {p.CoordinateSystem}. Depth unit: {p.DepthUnit}. Seismic reference datum: {p.SeismicReferenceDatum} (replacement velocity {p.ReplacementVelocity} m/s).");
            info.Paragraphs.Add($"Content: {_session.Seismic.Count} seismic datasets, {_session.Wells.Count} wells, {_session.Horizons.Count} horizons, {_session.Faults.Count} faults, {_session.WellTies.Count} well ties, {_session.History.Count} processing records.");
            rb.Sections.Add(info);

            var seis = new ReportSection { Heading = "Seismic data" };
            var st = new DataTable("Seismic", ["Name", "Type", "Category", "Inlines", "Crosslines", "Samples", "dt (ms)", "RMS", "Status"]);
            foreach (var s in _session.Seismic)
                st.Add(s.Name, s.Kind.ToString(), s.Category.ToString(), $"{s.InlineFirst}-{s.InlineLast}", $"{s.CrosslineFirst}-{s.CrosslineLast}", s.SampleCount, s.SampleInterval * 1000, s.AmplitudeRms, s.ValidationStatus.ToString());
            seis.Tables.Add(("Table 1. Seismic datasets", st));
            var first = _session.Seismic.FirstOrDefault(s => s.Kind == SeismicKind.PostStack3D);
            if (first is not null)
            {
                try
                {
                    var v = _session.GetVolume(first);
                    var g = v.Geometry;
                    int il = g.Inlines.ValueAt(g.InlineCount / 2);
                    var sec = v.ReadInline(il);
                    var px = SectionRenderer.Render(sec, new SeismicDisplaySettings(), SectionViewport.Full(sec), 900, 450);
                    seis.Figures.Add(($"Figure 1. {first.Name}, inline {il} (variable density, P99 clip).", px, 900, 450));
                }
                catch (Exception e) { _log.Warn("Report", $"Section figure skipped: {e.Message}"); }
            }
            rb.Sections.Add(seis);
            progress?.Report(new ProgressInfo(0.3, "Wells"));

            var wells = new ReportSection { Heading = "Wells" };
            var wt = new DataTable("Wells", ["Well", "UWI", "X", "Y", "KB", "TD", "Curves", "Tops", "TDR"]);
            foreach (var w in _session.Wells)
                wt.Add(w.Name, w.Uwi ?? "", w.SurfaceX, w.SurfaceY, w.KellyBushing, w.TotalDepth, string.Join(" ", w.Curves.Select(c => c.Mnemonic)), w.Tops.Count, w.ActiveTimeDepth?.Name ?? "none");
            wells.Tables.Add(("Table 2. Wells", wt));
            if (_session.WellTies.Count > 0)
            {
                var tt = new DataTable("Ties", ["Well", "Seismic", "Correlation", "Bulk shift (ms)", "Phase (°)", "Reversed polarity"]);
                foreach (var t in _session.WellTies)
                    tt.Add(_session.FindWell(t.WellId)?.Name ?? "?", _session.FindSeismic(t.SeismicId)?.Name ?? "?", t.Correlation, t.BulkShift * 1000, t.PhaseRotation, t.ReversePolarity ? "yes" : "no");
                wells.Tables.Add(("Table 3. Well-to-seismic ties (QC)", tt));
            }
            rb.Sections.Add(wells);

            var interp = new ReportSection { Heading = "Interpretation" };
            var ht = new DataTable("Horizons", ["Horizon", "Picked nodes", "Coverage %", "Min (ms)", "Max (ms)"]);
            foreach (var h in _session.Horizons)
            {
                int defined = h.Values.Count(x => !double.IsNaN(x));
                var (mn, mx) = h.AsGrid().Range();
                ht.Add(h.Name, defined, 100.0 * defined / Math.Max(1, h.Values.Length), mn * 1000, mx * 1000);
            }
            interp.Tables.Add(("Table 4. Horizons", ht));
            var ft = new DataTable("Faults", ["Fault", "Sticks", "Points"]);
            foreach (var f in _session.Faults) ft.Add(f.Name, f.Sticks.Count, f.Sticks.Sum(s => s.Points.Count));
            interp.Tables.Add(("Table 5. Faults", ft));
            rb.Sections.Add(interp);

            var hist = new ReportSection { Heading = "Processing, attributes and inversion history" };
            hist.Paragraphs.Add("Every derived dataset is linked to the record below (inputs, parameters, algorithm version and status) so that results are reproducible.");
            var histTable = new DataTable("History", ["Started (UTC)", "Operation", "Status", "Parameters"]);
            foreach (var r in _session.History.OrderBy(h => h.StartedUtc))
                histTable.Add(r.StartedUtc.ToString("yyyy-MM-dd HH:mm"), r.Operation, r.Status.ToString(), r.ParametersJson.Length > 80 ? r.ParametersJson[..80] + "..." : r.ParametersJson);
            hist.Tables.Add(("Table 6. Provenance records", histTable));
            rb.Sections.Add(hist);

            var concl = new ReportSection { Heading = "Technical conclusions" };
            concl.Paragraphs.Add(string.IsNullOrWhiteSpace(conclusions) ? "No conclusions were entered for this report." : conclusions);
            concl.Paragraphs.Add("Note: impedance, elastic and petrophysical properties derived by inversion or modelling are estimates conditioned by the wavelet, low-frequency model and well calibration; they are not measured values.");
            rb.Sections.Add(concl);

            progress?.Report(new ProgressInfo(0.8, "Writing PDF"));
            rb.Build().Save(pdfPath);
            XlsxExport.Write(Path.ChangeExtension(pdfPath, ".xlsx"), [st, wt, ht, ft, ExportService.HistoryTable(_session.History)]);
            _session.Add(new ReportEntity { Name = Path.GetFileNameWithoutExtension(pdfPath), Path = pdfPath, Format = "PDF" });
            _log.Success("Report", $"Report written: {pdfPath} (+ .xlsx tables)");
            return pdfPath;
        }, ct);
    }
}

/// <summary>Creates a complete demonstration project from the synthetic generator (all data computed from a known model).</summary>
public sealed class SampleProjectService
{
    private readonly ProjectSession _session;
    private readonly SeismicImportService _seismic;
    private readonly IMessageLog _log;

    public SampleProjectService(ProjectSession session, SeismicImportService seismic, IMessageLog log) { _session = session; _seismic = seismic; _log = log; }

    public async Task CreateAsync(string projectFile, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        await _session.CreateAsync(projectFile, "Synthetic Anticline", "Synthetic faulted anticline with a gas sand (ground-truth model for validation)", ct);
        var gen = new SyntheticSurveyGenerator();
        var folder = _session.SubDirectory("sample_files");
        progress?.Report(new ProgressInfo(0.05, "Generating sample files"));
        await Task.Run(() => gen.WriteSampleFiles(folder), ct);
        progress?.Report(new ProgressInfo(0.4, "Importing seismic"));
        var stack = await _seismic.ImportAsync(Path.Combine(folder, "synthetic_stack.sgy"), "Synthetic stack", null, SeismicImportMode.CopyToChunkedStore, false, null, null, ct);
        await _seismic.ImportAsync(Path.Combine(folder, "synthetic_gathers.sgy"), "Synthetic CMP gathers", null, SeismicImportMode.LinkMemoryMapped, false, null, null, ct);
        progress?.Report(new ProgressInfo(0.8, "Adding wells and interpretation"));
        foreach (var w in new[] { gen.BuildWell("GV-1", gen.Inlines / 2, (int)(gen.Crosslines * 0.42)), gen.BuildWell("GV-2", gen.Inlines / 4, gen.Crosslines / 6), gen.BuildWell("GV-3", 3 * gen.Inlines / 4, (int)(gen.Crosslines * 0.85)) })
            _session.Add(w);
        foreach (var h in gen.BuildHorizons().Take(3)) { h.SurveyId = stack.Id; _session.Add(h); }
        var f = gen.BuildFault(); f.SurveyId = stack.Id; _session.Add(f);
        _session.Add(WaveletEntity.From(gen.Wavelet, "Synthetic (true) wavelet"));
        await _session.SaveAsync(ct);
        _log.Success("Sample", "Sample project created. All data derive from a known model and can be used to validate workflows.");
    }
}
