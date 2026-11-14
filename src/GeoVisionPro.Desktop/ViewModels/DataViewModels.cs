using System.Collections.ObjectModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Segy;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>SEG-Y browser: textual/binary/trace header inspection, key byte configuration and import.</summary>
public sealed partial class SegyBrowserViewModel : DocumentViewModel
{
    public string Path { get; }
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _textHeader = "";
    [ObservableProperty] private DataView? _binaryTable;
    [ObservableProperty] private DataView? _traceTable;
    [ObservableProperty] private int _inlineByte = 189;
    [ObservableProperty] private int _crosslineByte = 193;
    [ObservableProperty] private int _cdpXByte = 181;
    [ObservableProperty] private int _cdpYByte = 185;
    [ObservableProperty] private int _scalarByte = 71;
    [ObservableProperty] private int _offsetByte = 37;
    [ObservableProperty] private string _formatOverride = "(from header)";
    [ObservableProperty] private double _sampleIntervalOverrideMs;
    [ObservableProperty] private string _datasetName;
    [ObservableProperty] private SeismicImportMode _mode = SeismicImportMode.CopyToChunkedStore;
    [ObservableProperty] private bool _angleGathers;
    [ObservableProperty] private string _scanReport = "";
    public string[] Formats { get; } = ["(from header)", .. Enum.GetNames<SegySampleFormat>()];
    public SeismicImportMode[] Modes { get; } = Enum.GetValues<SeismicImportMode>();
    public Array2D? Preview { get; private set; }
    public event EventHandler? PreviewChanged;

    public SegyBrowserViewModel(MainViewModel main, string path) : base(main)
    {
        Path = path;
        _datasetName = System.IO.Path.GetFileNameWithoutExtension(path);
        Title = $"SEG-Y: {System.IO.Path.GetFileName(path)}";
        Inspect();
    }

    private SegyOpenOptions Options => new()
    {
        FormatOverride = FormatOverride == "(from header)" ? null : Enum.Parse<SegySampleFormat>(FormatOverride),
        SampleIntervalOverride = SampleIntervalOverrideMs > 0 ? SampleIntervalOverrideMs / 1000 : null
    };

    private SegyKeyLocations Keys => new() { InlineByte = InlineByte, CrosslineByte = CrosslineByte, CdpXByte = CdpXByte, CdpYByte = CdpYByte, CoordinateScalarByte = ScalarByte, OffsetByte = OffsetByte };

    [RelayCommand]
    private void Inspect()
    {
        try
        {
            var ins = SeismicImportService.Inspect(Path, 50, Options);
            Summary = ins.Summary + (ins.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, ins.Warnings.Select(w => "⚠ " + w)) : "");
            TextHeader = ins.TextHeader;
            var bt = new DataTable();
            bt.Columns.Add("Byte", typeof(int)); bt.Columns.Add("Field"); bt.Columns.Add("Value", typeof(long)); bt.Columns.Add("Description");
            foreach (var (f, v) in ins.BinaryHeader) bt.Rows.Add(f.Byte, f.Name, v, f.Description);
            BinaryTable = bt.DefaultView;
            var tt = new DataTable();
            tt.Columns.Add("Trace", typeof(int));
            foreach (var f in SegyFields.Trace) tt.Columns.Add($"{f.Name} [{f.Byte}]", typeof(long));
            for (int i = 0; i < ins.FirstTraceHeaders.Count; i++)
            {
                var row = tt.NewRow(); row[0] = i + 1;
                for (int k = 0; k < ins.FirstTraceHeaders[i].Count; k++) row[k + 1] = ins.FirstTraceHeaders[i][k].Value;
                tt.Rows.Add(row);
            }
            TraceTable = tt.DefaultView;
            var d = ins.DetectedKeys;
            InlineByte = d.InlineByte; CrosslineByte = d.CrosslineByte;
            var a = new Array2D(ins.FirstTraces.Count, ins.Samples);
            for (int i = 0; i < ins.FirstTraces.Count; i++) ins.FirstTraces[i].AsSpan(0, ins.Samples).CopyTo(a.Row(i));
            Preview = a;
            PreviewChanged?.Invoke(this, EventArgs.Empty);
            ScanReport = $"Auto-detected keys: {d}. Use 'Scan geometry' to validate before importing.";
        }
        catch (Exception e) { Summary = "Error: " + e.Message; }
    }

    [RelayCommand]
    private async Task Scan()
    {
        try
        {
            SegyScanResult? r = null;
            var keys = Keys; var opt = Options; var path = Path;
            await RunAsync("Scan SEG-Y geometry", (p, ct) => Task.Run(() => { using var f = SegyFile.Open(path, opt); r = SegyScanner.Scan(f, keys, p, ct); }, ct));
            if (r is null) return;
            var g = r.Geometry;
            ScanReport = $"{r.Kind}: inlines {g.Inlines}, crosslines {g.Crosslines}, {g.SampleCount} samples @ {g.SampleInterval * 1000:0.###} ms, fold {r.Fold}, " +
                         $"{r.MissingBins:N0} empty bins, {r.DuplicateTraces:N0} duplicates. Bin size {g.Transform.InlineSpacing:0.##} × {g.Transform.CrosslineSpacing:0.##} m." +
                         Environment.NewLine + r.Validation;
        }
        catch (Exception e) { ScanReport = "Scan failed: " + e.Message; }
    }

    [RelayCommand]
    private async Task Import()
    {
        if (!Main.RequireProject()) return;
        SeismicDataset? ds = null;
        var keys = Keys; var opt = Options; var name = DatasetName; var mode = Mode; var angle = AngleGathers; var path = Path;
        await RunAsync($"Import {name}", async (p, ct) => ds = await Main.SeismicImport.ImportAsync(path, name, keys, mode, angle, opt, p, ct));
        if (ds is null) return;
        Main.Open(new SeismicViewerViewModel(Main, ds, ds.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers ? SectionKind.Gather : SectionKind.Inline));
    }
}

/// <summary>Processing history (provenance) browser.</summary>
public sealed partial class HistoryViewModel : DocumentViewModel
{
    public ObservableCollection<ProcessingRecord> Records { get; } = [];
    [ObservableProperty] private ProcessingRecord? _selected;
    public HistoryViewModel(MainViewModel main) : base(main) { Title = "Processing History"; OnProjectChanged(); }
    public override (string Kind, string Argument)? WorkspaceKey => ("History", "");
    public override void OnProjectChanged()
    {
        Records.Clear();
        if (!Main.Session.IsOpen) return;
        foreach (var r in Main.Session.History.OrderByDescending(r => r.StartedUtc)) Records.Add(r);
    }

    [RelayCommand]
    private void ExportXlsx()
    {
        var p = Main.Dialogs.SaveFile("Export history", "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", "history.xlsx");
        if (p is null) return;
        var t = ExportService.HistoryTable(Records);
        if (p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) Reporting.CsvExport.Write(p, t);
        else Reporting.XlsxExport.Write(p, [t]);
    }
}

/// <summary>Technical report generation (PDF + XLSX tables).</summary>
public sealed partial class ReportViewModel : DocumentViewModel
{
    [ObservableProperty] private string _conclusions = "";
    [ObservableProperty] private string _output = "";
    public ReportViewModel(MainViewModel main) : base(main)
    {
        Title = "Report";
        Output = System.IO.Path.Combine(main.Session.SubDirectory("reports"), $"{main.Session.Project.Name}_report_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
    }

    [RelayCommand]
    private void Browse()
    {
        var p = Main.Dialogs.SaveFile("Report file", "PDF (*.pdf)|*.pdf", System.IO.Path.GetFileName(Output));
        if (p is not null) Output = p;
    }

    [RelayCommand]
    private async Task Generate()
    {
        string? path = null;
        var o = Output; var c = Conclusions;
        await RunAsync("Generate report", async (p, ct) => path = await Main.Reports.GenerateProjectReportAsync(o, c, p, ct));
        if (path is not null && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
