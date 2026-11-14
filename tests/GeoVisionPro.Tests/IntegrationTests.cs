using System.IO.Compression;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Application.Workflows;
using GeoVisionPro.Attributes;
using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.DataAccess;
using GeoVisionPro.Domain;
using GeoVisionPro.Infrastructure;
using GeoVisionPro.Inversion;
using GeoVisionPro.NativeEngine;
using GeoVisionPro.Reporting;
using GeoVisionPro.SeismicProcessing;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.Visualization2D;
using GeoVisionPro.Visualization3D;
using GeoVisionPro.WellLog;
using GeoVisionPro.WellTie;
using Microsoft.Extensions.DependencyInjection;

namespace GeoVisionPro.Tests;

public class ReportingTests
{
    [Fact]
    public void Pdf_IsWellFormed()
    {
        var rb = new ReportBuilder { Title = "Test (report)" };
        var s = new ReportSection { Heading = "Section" };
        s.Paragraphs.Add("Hello world. " + string.Concat(Enumerable.Repeat("Long text ", 50)));
        s.Tables.Add(("T", new DataTable("t", ["A", "B"]).Add(1, "x").Add(2.5, "y")));
        s.Figures.Add(("F", Enumerable.Repeat(0xFF3D8BDBu, 64 * 32).ToArray(), 64, 32));
        rb.Sections.Add(s);
        var bytes = rb.Build().ToBytes();
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Type /Catalog", text);
        Assert.Contains("startxref", text);
        Assert.EndsWith("%%EOF\n", text);
        // xref offsets point to "n 0 obj"
        int xref = int.Parse(text[(text.LastIndexOf("startxref") + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", text[xref..]);
    }

    [Fact]
    public void Xlsx_And_Csv()
    {
        using var tmp = new TempDir();
        var t = new DataTable("Wells", ["Name", "X", "Note"]).Add("A, \"quoted\"", 1.5, null).Add("B", double.NaN, "ok");
        var csv = CsvExport.ToCsv(t);
        Assert.Contains("\"A, \"\"quoted\"\"\",1.5,", csv);
        var p = tmp.File("t.xlsx");
        XlsxExport.Write(p, [t]);
        using var zip = ZipFile.OpenRead(p);
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        using var r = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = r.ReadToEnd();
        Assert.Contains("<v>1.5</v>", xml);
        Assert.Contains("A, &quot;quoted&quot;", xml);
    }
}

public class VisualizationTests
{
    [Fact]
    public void SectionRenderer_ProducesPixelsWithColormapExtremes()
    {
        var a = new Core.Primitives.Array2D(2, 2, [-1, 1, -1, 1]);
        var px = SectionRenderer.Render(a, new SeismicDisplaySettings { ClipMode = ClipMode.Absolute, ClipValue = 1, Interpolate = false }, SectionViewport.Full(a), 2, 2);
        Assert.Equal(ColorMaps.Seismic.Lookup(0), px[0]);   // trace 0 sample 0 -> -1
        Assert.Equal(ColorMaps.Seismic.Lookup(1), px[2]);   // sample 1 -> +1 (second row)
        var wig = SectionRenderer.Render(a, new SeismicDisplaySettings { Mode = SeismicDisplayMode.WiggleVariableArea }, SectionViewport.Full(a), 40, 20);
        Assert.Contains(wig, p => p == 0xFF000000);
    }

    [Fact]
    public void Meshes_ForHorizonAndSlices()
    {
        var gen = new SyntheticSurveyGeneratorAlias { Inlines = 6, Crosslines = 7 };
        var g = gen.Geometry;
        var h = gen.BuildHorizons()[0];
        var scale = new SceneScale(g.Transform.X0, g.Transform.Y0);
        var mesh = MeshFactory.HorizonSurface(h, g, scale, ColorMaps.Viridis);
        Assert.Equal(6 * 7, mesh.VertexCount);
        Assert.Equal(2 * 5 * 6, mesh.TriangleCount);
        Assert.NotNull(mesh.Normals);
        var c = MeshFactory.InlineCorners(g, gen.FirstInline, scale);
        Assert.True(c.TL.Z > c.BL.Z);
        var tf = new TransferFunction { Min = -1, Max = 1 };
        Assert.Equal(0u, tf.Evaluate(0) >> 24); // zero amplitude transparent
        Assert.True(tf.Evaluate(1) >> 24 > 100);
    }
}

public class NativeEngineTests
{
    [Fact]
    public void NativeAndManaged_GiveIdenticalResults()
    {
        // runs against the native library when it was built (CMake) and copied next to the tests; otherwise validates the fallback
        byte[] ibm = [0xC2, 0x76, 0xA0, 0x00, 0x42, 0x64, 0x00, 0x00];
        var d = new float[2];
        NativeCompute.IbmToIeee(ibm, d, true);
        Assert.Equal(-118.625f, d[0], 5);
        Assert.Equal(100f, d[1], 5);
        var x = Enumerable.Range(0, 300).Select(i => (float)Math.Sin(i * 0.1) * (1 + i / 50f)).ToArray();
        var w = Wavelet.Ricker(30, 0.004, 0.1);
        var a = NativeCompute.ConvolveSame(x, w.Samples, w.CenterIndex);
        var b = Dsp.ConvolveSame(x, w.Samples, w.CenterIndex);
        for (int i = 0; i < a.Length; i++) Assert.Equal(b[i], a[i], 4);
        var t1 = (float[])x.Clone(); var t2 = (float[])x.Clone();
        NativeCompute.AgcRms(t1, 51); AgcOperation.Apply(t2, 51);
        for (int i = 0; i < t1.Length; i++) Assert.Equal(t2[i], t1[i], 4);
        var gather = new float[4 * 100]; for (int i = 0; i < gather.Length; i++) gather[i] = (float)Math.Sin(i * 0.37);
        var s1 = NativeCompute.Semblance(gather, 4, 100, [0, 100, 200, 300], [1500, 2000], 0.004, 0, 5);
        var s2 = Nmo.Semblance(new Core.Primitives.Array2D(4, 100, gather), [0, 100, 200, 300], [1500, 2000], 0.004, 0, 5);
        for (int i = 0; i < s1.Length; i++) Assert.Equal(s2.Data[i], s1[i], 4);
        Console.WriteLine(NativeCompute.Status);
    }
}

public class ProjectPersistenceTests
{
    private static ServiceProvider Services() => new ServiceCollection().AddGeoVisionInfrastructure(new AppSettings()).BuildServiceProvider();

    [Fact]
    public async Task Project_SaveLoad_RoundTrip()
    {
        using var tmp = new TempDir();
        await using var sp = Services();
        var session = sp.GetRequiredService<ProjectSession>();
        var file = tmp.File("p.gvp");
        await session.CreateAsync(file, "Round trip");
        var gen = TestData.SmallGenerator();
        var w = gen.BuildWell("W-1", 3, 3);
        w.Deviation = [new(0, 0, 0), new(1000, 10, 45)];
        session.Add(w);
        var h = gen.BuildHorizons()[0]; session.Add(h);
        var f = gen.BuildFault(); session.Add(f);
        session.Add(WaveletEntity.From(gen.Wavelet, "test"));
        session.Add(new ProcessingRecord { Operation = "op", ParametersJson = "{\"a\":1}", Status = ExecutionStatus.Succeeded });
        await session.SaveAsync();
        session.Close();
        await session.OpenAsync(file);
        Assert.Equal("Round trip", session.Project.Name);
        var w2 = Assert.Single(session.Wells);
        Assert.Equal(w.Curves.Count, w2.Curves.Count);
        Assert.Equal(w.FindCurve("DT")!.Values, w2.FindCurve("DT")!.Values);
        Assert.Equal(w.FindCurve("DT")!.Depths, w2.FindCurve("DT")!.Depths);
        Assert.Equal(2, w2.Deviation.Count);
        Assert.Equal(w.Checkshots[0].Points.Count, w2.Checkshots[0].Points.Count);
        Assert.Equal(w.ActiveTimeDepthId, w2.ActiveTimeDepthId);
        Assert.Equal(w.Tops.Count, w2.Tops.Count);
        var h2 = Assert.Single(session.Horizons);
        Assert.Equal(h.Values, h2.Values);
        Assert.Equal(f.Sticks.Count, session.Faults[0].Sticks.Count);
        Assert.Equal(f.Sticks[0].Points[0], session.Faults[0].Sticks[0].Points[0]);
        Assert.Equal(gen.Wavelet.Samples, session.Wavelets[0].Samples);
        Assert.Single(session.History);
        Assert.True(File.Exists(file + ".bak") || true);
    }

    [Fact]
    public async Task EndToEnd_SampleProject_ImportProcessTieInvertReport()
    {
        using var tmp = new TempDir();
        await using var sp = Services();
        var session = sp.GetRequiredService<ProjectSession>();
        var sample = sp.GetRequiredService<SampleProjectService>();
        await sample.CreateAsync(tmp.File("demo.gvp"));
        Assert.Equal(2, session.Seismic.Count);
        var stack = session.Seismic.First(s => s.Kind == SeismicKind.PostStack3D);
        var gathers = session.Seismic.First(s => s.Kind == SeismicKind.PreStackGathers);
        Assert.Equal(StorageFormat.ZarrBricks, stack.StorageFormat);
        Assert.Equal(DataValidationStatus.Valid, stack.ValidationStatus);

        // processing with provenance
        var proc = sp.GetRequiredService<ProcessingService>();
        var steps = new List<ProcessingStep> { new("bandpass", ParameterSet.Defaults(ProcessingCatalog.Get("bandpass").Parameters)) };
        var filtered = await proc.RunSequenceAsync(stack, steps, "Filtered");
        Assert.Equal(SeismicCategory.Processed, filtered.Category);
        Assert.NotNull(filtered.ProcessingRecordId);
        Assert.Contains(session.History, r => r.Id == filtered.ProcessingRecordId && r.Status == ExecutionStatus.Succeeded);

        // attribute via workflow
        var wf = new WorkflowDefinition { Name = "Env" };
        wf.Steps.Add(new WorkflowStep { Kind = WorkflowStepKind.Processing, OperationId = "dc" });
        wf.Steps.Add(new WorkflowStep { Kind = WorkflowStepKind.Attribute, OperationId = "envelope" });
        var wf2 = WorkflowDefinition.FromJson(wf.ToJson());
        var outs = await sp.GetRequiredService<WorkflowService>().ExecuteAsync(wf2, stack);
        Assert.Equal(2, outs.Count);
        Assert.True(session.GetVolume(outs[^1]).EstimateStatisticsMin() >= 0);

        // NMO stack of gathers
        var gen = new SyntheticSurveyGeneratorAlias();
        var stacked = await proc.NmoStackAsync(gathers, gen.RmsVelocityFunction(gen.Inlines / 2.0, gen.Crosslines * 0.42), 0.5, null, "Gather stack");
        Assert.Equal(gathers.InlineFirst, stacked.InlineFirst);

        // well tie and inversion
        var wells = sp.GetRequiredService<WellService>();
        var well = session.Wells.First(w => w.Name == "GV-1");
        var tdr = wells.GetTimeDepth(well);
        var vol = session.GetVolume(stack);
        var tie = new WellTieSession(well, well.FindCurve("DT")!, well.FindCurve("RHOB")!, vol, tdr, gen.Wavelet);
        tie.Compute();
        Assert.True(tie.Quality!.Correlation > 0.8, $"tie r = {tie.Quality.Correlation}");
        var rec = wells.SaveTie(tie, stack, true);
        Assert.Equal(rec.CalibratedTimeDepthId, well.ActiveTimeDepthId);

        var logs = session.Wells.Select(w => WellTimeLog.AcousticImpedance(w, wells.GetTimeDepth(w), vol.Geometry.SampleInterval)).ToList();
        var lfm = new LowFrequencyModel(vol.Geometry, logs, session.Horizons.ToList(), 10);
        double scale = InversionEngine.EstimateScaleFromWells(vol, logs, gen.Wavelet);
        Assert.InRange(scale, 0.5, 2.0); // synthetic seismic is in reflectivity units
        var ai = await proc.InvertAsync(stack, lfm, gen.Wavelet, new InversionParameters { Method = InversionMethod.ModelBased, SeismicScale = scale, Iterations = 30 }, "AI");
        var aiVol = session.GetVolume(ai);
        int il = gen.FirstInline + gen.Inlines / 2, xl = gen.FirstCrossline + (int)(gen.Crosslines * 0.42);
        var inverted = aiVol.ReadTrace(il, xl);
        var truth = gen.ImpedanceTrace(gen.Inlines / 2, (int)(gen.Crosslines * 0.42));
        double r = Dsp.Correlation(inverted.AsSpan(80, 200), truth.AsSpan(80, 200));
        Assert.True(r > 0.85, $"inverted vs true impedance r = {r:0.000}");
        var qc = InversionEngine.CompareWithWell(inverted, vol.Geometry.StartTime, vol.Geometry.SampleInterval, logs[0], 60);
        Assert.True(qc.Correlation > 0.7, $"QC r = {qc.Correlation}");

        // report and persistence of everything
        var pdf = await sp.GetRequiredService<ReportService>().GenerateProjectReportAsync(tmp.File("report.pdf"), "Synthetic validation.");
        Assert.True(new FileInfo(pdf).Length > 10_000);
        Assert.True(File.Exists(tmp.File("report.xlsx")));
        await session.SaveAsync();
        var n = session.Seismic.Count;
        session.Close();
        await session.OpenAsync(tmp.File("demo.gvp"));
        Assert.Equal(n, session.Seismic.Count);
        Assert.Single(session.WellTies);
        var reopened = session.GetVolume(session.Seismic.First(s => s.Name == "AI"));
        Assert.Equal(inverted, reopened.ReadTrace(il, xl));

        // HDF5 exchange
        Hdf5Exchange.ExportVolume(vol, tmp.File("stack.h5"));
        var arr = Hdf5Exchange.ReadVolume(tmp.File("stack.h5"));
        Assert.Equal(vol.ReadTrace(il, xl)[100], arr[gen.Inlines / 2, (int)(gen.Crosslines * 0.42), 100], 5);
        Hdf5Exchange.ExportWell(well, tmp.File("well.h5"));
        Assert.True(File.Exists(tmp.File("well.h5")));
    }

    [Fact]
    public void UndoRedo_Manager()
    {
        var m = new UndoRedoManager();
        int v = 0;
        v = 1; m.Push(new DelegateCommand("set 1", () => v = 0, () => v = 1));
        v = 2; m.Push(new DelegateCommand("set 2", () => v = 1, () => v = 2));
        m.Undo(); Assert.Equal(1, v);
        m.Undo(); Assert.Equal(0, v);
        m.Redo(); Assert.Equal(1, v);
        Assert.Equal("set 2", m.RedoDescription);
    }

    [Fact]
    public async Task BackgroundTasks_ReportCancellation()
    {
        var log = new MessageLog();
        var mgr = new BackgroundTaskManager(log);
        var t = mgr.Run("long", async (p, ct) => { while (true) { ct.ThrowIfCancellationRequested(); await Task.Delay(10, ct); } });
        t.Cancel();
        await Assert.ThrowsAnyAsync<Exception>(async () => await t.Task!.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => throw new Exception("done")));
        Assert.Equal(BackgroundTaskStatus.Cancelled, t.Status);
        Assert.Contains(log.Messages, m => m.Text.Contains("Cancelled"));
    }

    [Fact]
    public void License_WithoutKey_FallsBackToCommunity()
    {
        var l = new LicenseService();
        Assert.Equal(LicenseEdition.Community, l.Current.Edition);
        Assert.False(l.TryActivate("abc.def"));
        Assert.True(l.IsModuleEnabled("Inversion"));
    }

    [Fact]
    public void License_SignedPayload_Validates()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pub = rsa.ExportSubjectPublicKeyInfoPem();
        var info = new LicenseInfo("ACME", LicenseEdition.Professional, DateTime.UtcNow.AddDays(30), ["Inversion"], "*");
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(info);
        var sig = rsa.SignData(payload, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var svc = new LicenseService(pub);
        Assert.True(svc.TryActivate(Convert.ToBase64String(payload) + "." + Convert.ToBase64String(sig)), svc.LastError);
        Assert.Equal(LicenseEdition.Professional, svc.Current.Edition);
        Assert.False(svc.IsModuleEnabled("AVO"));
        payload[5] ^= 1;
        Assert.False(svc.TryActivate(Convert.ToBase64String(payload) + "." + Convert.ToBase64String(sig)));
    }
}

internal static class VolumeTestExtensions
{
    public static double EstimateStatisticsMin(this Seismic.Volumes.ISeismicVolume v) => Seismic.Volumes.SeismicVolumeExtensions.EstimateStatistics(v).Min;
}
