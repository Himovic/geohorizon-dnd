using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Application.Abstractions;
using GeoVisionPro.Application.Services;
using GeoVisionPro.Application.Workflows;
using GeoVisionPro.Desktop.Services;
using GeoVisionPro.Domain;
using GeoVisionPro.Infrastructure;
using GeoVisionPro.NativeEngine;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>Shell view-model: menus/ribbon commands, project lifecycle, document management and tool panes.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public ProjectSession Session { get; }
    public IMessageLog Log { get; }
    public BackgroundTaskManager Tasks { get; }
    public UndoRedoManager Undo { get; }
    public DialogService Dialogs { get; }
    public ThemeService Theme { get; }
    public CursorBus Cursor { get; }
    public AppSettings Settings { get; }
    public SeismicImportService SeismicImport { get; }
    public WellImportService WellImport { get; }
    public InterpretationService Interpretation { get; }
    public ProcessingService Processing { get; }
    public WellService WellServices { get; }
    public WorkflowService Workflows { get; }
    public ExportService Export { get; }
    public ReportService Reports { get; }
    public SampleProjectService Samples { get; }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];
    [ObservableProperty] private DocumentViewModel? _activeDocument;
    [ObservableProperty] private object? _selectedItem;
    [ObservableProperty] private string _windowTitle = "GeoVision Pro";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _cursorText = "";
    [ObservableProperty] private string _memoryText = "";
    public string EngineStatus => NativeCompute.Status + (VtkScene.IsAvailable ? " | VTK renderer available" : " | VTK renderer not installed (Helix 3D used)");

    public ExplorerViewModel Explorer { get; }
    public PropertiesViewModel Properties { get; }
    public MessagesViewModel Messages { get; }
    public TasksViewModel TaskList { get; }
    public InterpretationControlsViewModel InterpretationControls { get; }
    public DataManagerViewModel DataManager { get; }

    private readonly DispatcherTimer _timer;

    public MainViewModel(ProjectSession session, IMessageLog log, BackgroundTaskManager tasks, UndoRedoManager undo, DialogService dialogs, ThemeService theme,
        CursorBus cursor, AppSettings settings, SeismicImportService seismicImport, WellImportService wellImport, InterpretationService interpretation,
        ProcessingService processing, WellService wellServices, WorkflowService workflows, ExportService export, ReportService reports, SampleProjectService samples)
    {
        Session = session; Log = log; Tasks = tasks; Undo = undo; Dialogs = dialogs; Theme = theme; Cursor = cursor; Settings = settings;
        SeismicImport = seismicImport; WellImport = wellImport; Interpretation = interpretation; Processing = processing; WellServices = wellServices;
        Workflows = workflows; Export = export; Reports = reports; Samples = samples;
        Dialogs.LastFolder = settings.LastImportFolder;
        Explorer = new ExplorerViewModel(this);
        Properties = new PropertiesViewModel(this);
        Messages = new MessagesViewModel(this);
        TaskList = new TasksViewModel(this);
        InterpretationControls = new InterpretationControlsViewModel(this);
        DataManager = new DataManagerViewModel(this);
        Session.Changed += (_, c) => OnUi(() => OnProjectChanged(c));
        Undo.Changed += (_, _) => OnUi(() => { UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged(); });
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => MemoryText = $"Memory {GC.GetTotalMemory(false) / 1048576.0:0} MB (working set {Environment.WorkingSet / 1048576.0:0} MB)";
        _timer.Start();
        UpdateTitle();
        Log.Info("GeoVision Pro", $"Started. {EngineStatus}");
    }

    public static void OnUi(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    partial void OnSelectedItemChanged(object? value) => Properties.Show(value);

    private void OnProjectChanged(ProjectChange c)
    {
        UpdateTitle();
        if (c.Kind is ProjectChangeKind.Opened or ProjectChangeKind.Closed or ProjectChangeKind.ItemAdded or ProjectChangeKind.ItemRemoved or ProjectChangeKind.Saved)
        {
            Explorer.Rebuild();
            DataManager.Refresh();
            InterpretationControls.Refresh();
            foreach (var d in Documents.ToList()) d.OnProjectChanged();
        }
        if (c.Kind == ProjectChangeKind.Closed) foreach (var d in Documents.ToList()) CloseDocument(d);
        SaveProjectCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTitle() =>
        WindowTitle = Session.IsOpen ? $"GeoVision Pro - {Session.Project.Name}{(Session.IsDirty ? " *" : "")}  [{Session.ProjectFile}]" : "GeoVision Pro";

    // ------------------------------------------------------------------ documents
    public T Open<T>(T doc) where T : DocumentViewModel
    {
        doc.CloseRequested += (_, _) => CloseDocument(doc);
        Documents.Add(doc);
        ActiveDocument = doc;
        return doc;
    }

    public void CloseDocument(DocumentViewModel d)
    {
        if (!Documents.Contains(d)) return;
        d.OnClosed();
        Documents.Remove(d);
    }

    public bool RequireProject()
    {
        if (Session.IsOpen) return true;
        Dialogs.Info("Create or open a project first (File > New Project or File > Open Project).");
        return false;
    }

    public SeismicDataset? SelectedSeismic(bool postStack = true) =>
        SelectedItem is SeismicDataset s && (!postStack || s.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D) ? s
        : Session.IsOpen ? Session.Seismic.FirstOrDefault(x => !postStack || x.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D) : null;

    public Well? SelectedWell() => SelectedItem as Well ?? (SelectedItem is LogCurve c ? Session.FindWell(c.WellId) : null) ?? (Session.IsOpen ? Session.Wells.FirstOrDefault() : null);

    // ------------------------------------------------------------------ project
    [RelayCommand]
    private async Task NewProject()
    {
        if (!ConfirmDiscard()) return;
        var path = Dialogs.SaveFile("New project", DialogService.ProjectFilter, "MyProject.gvp");
        if (path is null) return;
        var name = Dialogs.Prompt("Project name:", "New project", Path.GetFileNameWithoutExtension(path)) ?? Path.GetFileNameWithoutExtension(path);
        await Session.CreateAsync(path, name);
        Settings.AddRecent(Session.ProjectFile!);
        OnPropertyChanged(nameof(RecentProjects));
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (!ConfirmDiscard()) return;
        var path = Dialogs.OpenFile("Open project", DialogService.ProjectFilter);
        if (path is not null) await OpenProjectAsync(path);
    }

    public IReadOnlyList<string> RecentProjects => Settings.RecentProjects;

    [RelayCommand]
    private async Task OpenRecent(string path)
    {
        if (!ConfirmDiscard()) return;
        if (!File.Exists(path)) { Dialogs.Error($"Project not found: {path}"); Settings.RecentProjects.Remove(path); OnPropertyChanged(nameof(RecentProjects)); return; }
        await OpenProjectAsync(path);
    }

    public async Task OpenProjectAsync(string path)
    {
        try
        {
            StatusText = "Opening project...";
            await Session.OpenAsync(path);
            Settings.AddRecent(Session.ProjectFile!);
            OnPropertyChanged(nameof(RecentProjects));
            await RestoreWorkspaceAsync();
            StatusText = "Ready";
        }
        catch (Exception e) { Dialogs.Error($"Could not open project:\n{e.Message}"); Log.Error("Project", e.Message); }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveProject()
    {
        try { await Session.SaveAsync(); SaveWorkspace(); }
        catch (Exception e) { Dialogs.Error($"Save failed:\n{e.Message}\n\nThe previous version is kept as .gvp.bak."); Log.Error("Project", e.Message); }
        UpdateTitle();
    }
    private bool CanSave() => Session.IsOpen;

    [RelayCommand]
    private async Task SaveProjectAs()
    {
        if (!RequireProject()) return;
        var path = Dialogs.SaveFile("Save project as", DialogService.ProjectFilter, Session.Project.Name + ".gvp");
        if (path is null) return;
        await Session.SaveAsAsync(path);
        Settings.AddRecent(Session.ProjectFile!);
    }

    [RelayCommand]
    private void CloseProject()
    {
        if (!ConfirmDiscard()) return;
        SaveWorkspace();
        Session.Close();
        Undo.Clear();
    }

    [RelayCommand]
    private async Task CreateSampleProject()
    {
        if (!ConfirmDiscard()) return;
        var path = Dialogs.SaveFile("Create synthetic sample project", DialogService.ProjectFilter, "SyntheticAnticline.gvp");
        if (path is null) return;
        var t = Tasks.Run("Create sample project", (p, ct) => Samples.CreateAsync(path, p, ct));
        await t.Task!;
        if (t.Status == BackgroundTaskStatus.Completed)
        {
            Settings.AddRecent(Session.ProjectFile!);
            var s = Session.Seismic.FirstOrDefault(x => x.Kind == SeismicKind.PostStack3D);
            if (s is not null) Open(new SeismicViewerViewModel(this, s, SectionKind.Inline));
            Open(new MapViewModel(this));
        }
    }

    public bool ConfirmDiscard()
    {
        if (!Session.IsOpen || !Session.IsDirty) return true;
        var r = Dialogs.YesNoCancel($"Save changes to '{Session.Project.Name}'?");
        if (r is null) return false;
        if (r == true) Session.SaveAsync().GetAwaiter().GetResult();
        return true;
    }

    // ------------------------------------------------------------------ workspace persistence
    private string? WorkspaceFile => Session.IsOpen ? Path.Combine(Session.DataDirectory, "workspace.json") : null;

    [RelayCommand]
    public void SaveWorkspace()
    {
        if (WorkspaceFile is null) return;
        var docs = Documents.Select(d => d.WorkspaceKey).Where(k => k is not null).Select(k => new[] { k!.Value.Kind, k.Value.Argument }).ToList();
        File.WriteAllText(WorkspaceFile, JsonSerializer.Serialize(docs));
    }

    private Task RestoreWorkspaceAsync()
    {
        if (WorkspaceFile is null || !File.Exists(WorkspaceFile)) return Task.CompletedTask;
        try
        {
            var docs = JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(WorkspaceFile)) ?? [];
            foreach (var d in docs) RestoreDocument(d[0], d[1]);
        }
        catch (Exception e) { Log.Warn("Workspace", $"Workspace not restored: {e.Message}"); }
        return Task.CompletedTask;
    }

    private void RestoreDocument(string kind, string arg)
    {
        SeismicDataset? S() => Guid.TryParse(arg.Split('|')[0], out var id) ? Session.FindSeismic(id) : null;
        switch (kind)
        {
            case "Section" when S() is { } s && Enum.TryParse<SectionKind>(arg.Split('|')[1], out var k): Open(new SeismicViewerViewModel(this, s, k)); break;
            case "Map": Open(new MapViewModel(this)); break;
            case "Scene3D": Open(new Scene3DViewModel(this)); break;
            case "WellLogs": Open(new WellLogViewModel(this)); break;
            case "History": Open(new HistoryViewModel(this)); break;
        }
    }

    // ------------------------------------------------------------------ import / export
    [RelayCommand]
    private void ImportSegy()
    {
        if (!RequireProject()) return;
        var path = Dialogs.OpenFile("Import SEG-Y", DialogService.SegyFilter);
        if (path is null) return;
        Settings.LastImportFolder = Dialogs.LastFolder;
        Open(new SegyBrowserViewModel(this, path));
    }

    public void ImportSegyFile(string path) { if (RequireProject()) Open(new SegyBrowserViewModel(this, path)); }

    [RelayCommand]
    private void ImportLas()
    {
        if (!RequireProject()) return;
        foreach (var f in Dialogs.OpenFiles("Import LAS", DialogService.LasFilter))
            try { WellImport.ImportLas(f); } catch (Exception e) { Log.Error("LAS", $"{Path.GetFileName(f)}: {e.Message}"); }
    }

    public void ImportLasFile(string f) { try { WellImport.ImportLas(f); } catch (Exception e) { Log.Error("LAS", $"{Path.GetFileName(f)}: {e.Message}"); } }

    [RelayCommand]
    private void ImportTops()
    {
        if (!RequireProject()) return;
        var f = Dialogs.OpenFile("Import well tops (Well, Top, MD)", DialogService.TextFilter);
        if (f is not null) Safe(() => WellImport.ImportTops(f));
    }

    [RelayCommand]
    private void ImportDeviation()
    {
        if (!RequireProject() || SelectedWell() is not { } w) return;
        var f = Dialogs.OpenFile($"Import deviation survey for {w.Name} (MD, INC, AZI)", DialogService.TextFilter);
        if (f is not null) Safe(() => WellImport.ImportDeviation(f, w));
    }

    [RelayCommand]
    private void ImportCheckshots()
    {
        if (!RequireProject() || SelectedWell() is not { } w) return;
        var f = Dialogs.OpenFile($"Import checkshots for {w.Name} (MD, TWT ms)", DialogService.TextFilter);
        if (f is not null) Safe(() => WellImport.ImportCheckshots(f, w));
    }

    [RelayCommand]
    private void ImportHorizon()
    {
        if (!RequireProject() || SelectedSeismic() is not { } s) { Dialogs.Info("A post-stack survey is required to import horizons."); return; }
        foreach (var f in Dialogs.OpenFiles("Import horizons (X Y Z / IL XL Z / ZMAP+)", DialogService.TextFilter))
        {
            var cols = Dialogs.Confirm($"{Path.GetFileName(f)}: are the first two columns map X/Y coordinates?\n(No = inline/crossline numbers)", "Horizon columns")
                ? global::GeoVisionPro.Interpretation.HorizonXyzColumns.MapXYZ : global::GeoVisionPro.Interpretation.HorizonXyzColumns.InlineCrosslineZ;
            Safe(() => this.Interpretation.ImportHorizon(f, s, cols, true));
        }
    }

    [RelayCommand]
    private void ImportFault()
    {
        if (!RequireProject() || SelectedSeismic() is not { } s) return;
        var f = Dialogs.OpenFile("Import fault sticks (X Y Z STICK or IL XL Z STICK)", DialogService.TextFilter);
        if (f is not null) Safe(() => Interpretation.ImportFault(f, s, Dialogs.Confirm("Are the first two columns map X/Y coordinates?"), true));
    }

    [RelayCommand]
    private async Task ExportSelected()
    {
        if (!RequireProject()) return;
        try
        {
            switch (SelectedItem)
            {
                case SeismicDataset s:
                {
                    var p = Dialogs.SaveFile("Export SEG-Y", DialogService.SegyFilter, s.Name + ".sgy");
                    if (p is not null) await Tasks.Run($"Export {s.Name}", (pr, ct) => Export.ExportSegyAsync(s, p, Seismic.Segy.SegySampleFormat.IeeeFloat32, pr, ct)).Task!;
                    break;
                }
                case Well w:
                {
                    var p = Dialogs.SaveFile("Export LAS", DialogService.LasFilter, w.Name + ".las");
                    if (p is not null) Export.ExportLas(w, p);
                    break;
                }
                case Horizon h when (Session.FindSeismic(h.SurveyId ?? Guid.Empty) ?? SelectedSeismic()) is { } sv:
                {
                    var p = Dialogs.SaveFile("Export horizon", "XYZ (*.xyz)|*.xyz|ZMAP+ (*.zmap)|*.zmap", h.Name + ".xyz");
                    if (p is not null) Export.ExportHorizon(h, sv, p, p.EndsWith(".zmap", StringComparison.OrdinalIgnoreCase));
                    break;
                }
                case Fault f when SelectedSeismic() is { } sv:
                {
                    var p = Dialogs.SaveFile("Export fault sticks", DialogService.TextFilter, f.Name + ".txt");
                    if (p is not null) Export.ExportFault(f, sv, p);
                    break;
                }
                default: Dialogs.Info("Select a seismic dataset, well, horizon or fault in the Project Explorer to export it."); break;
            }
        }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private async Task ExportHdf5()
    {
        if (!RequireProject()) return;
        try
        {
            switch (SelectedItem)
            {
                case SeismicDataset s when s.Kind is SeismicKind.PostStack3D or SeismicKind.PostStack2D:
                    var p = Dialogs.SaveFile("Export HDF5", "HDF5 (*.h5)|*.h5", s.Name + ".h5");
                    if (p is not null) await Tasks.Run("HDF5 export", (pr, ct) => Task.Run(() => DataAccess.Hdf5Exchange.ExportVolume(Session.GetVolume(s), p, pr, ct), ct)).Task!;
                    break;
                case Well w:
                    var pw = Dialogs.SaveFile("Export HDF5", "HDF5 (*.h5)|*.h5", w.Name + ".h5");
                    if (pw is not null) DataAccess.Hdf5Exchange.ExportWell(w, pw);
                    break;
                case Horizon h:
                    var ph = Dialogs.SaveFile("Export HDF5", "HDF5 (*.h5)|*.h5", h.Name + ".h5");
                    if (ph is not null) DataAccess.Hdf5Exchange.ExportHorizon(h, ph);
                    break;
                default: Dialogs.Info("Select a post-stack dataset, well or horizon to export to HDF5."); break;
            }
        }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    public void Safe(Action a)
    {
        try { a(); }
        catch (Exception e) { Log.Error("Error", e.Message); Dialogs.Error(e.Message); }
    }

    // ------------------------------------------------------------------ views and tools
    [RelayCommand] private void OpenInline() { if (RequireProject() && SelectedSeismic() is { } s) Open(new SeismicViewerViewModel(this, s, SectionKind.Inline)); }
    [RelayCommand] private void OpenCrossline() { if (RequireProject() && SelectedSeismic() is { } s) Open(new SeismicViewerViewModel(this, s, SectionKind.Crossline)); }
    [RelayCommand] private void OpenTimeSlice() { if (RequireProject() && SelectedSeismic() is { } s) Open(new SeismicViewerViewModel(this, s, SectionKind.TimeSlice)); }
    [RelayCommand]
    private void OpenGathers()
    {
        if (!RequireProject()) return;
        var g = SelectedItem as SeismicDataset is { Kind: SeismicKind.PreStackGathers or SeismicKind.AngleGathers } sel ? sel : Session.Seismic.FirstOrDefault(x => x.Kind is SeismicKind.PreStackGathers or SeismicKind.AngleGathers);
        if (g is null) { Dialogs.Info("No pre-stack gather dataset in the project."); return; }
        Open(new SeismicViewerViewModel(this, g, SectionKind.Gather));
    }
    [RelayCommand] private void OpenMap() { if (RequireProject()) Open(new MapViewModel(this)); }
    [RelayCommand] private void Open3D() { if (RequireProject()) Open(new Scene3DViewModel(this)); }
    [RelayCommand] private void OpenVtkVolume() { if (RequireProject() && SelectedSeismic() is { } s) Open(new VtkVolumeViewModel(this, s)); }
    [RelayCommand] private void OpenProcessing() { if (RequireProject()) Open(new ProcessingViewModel(this)); }
    [RelayCommand] private void OpenWorkflow() { if (RequireProject()) Open(new WorkflowViewModel(this)); }
    [RelayCommand] private void OpenVelocityAnalysis() { if (RequireProject()) Open(new VelocityAnalysisViewModel(this)); }
    [RelayCommand] private void OpenAttributes() { if (RequireProject()) Open(new AttributeViewModel(this)); }
    [RelayCommand] private void OpenWellLogs() { if (RequireProject()) Open(new WellLogViewModel(this)); }
    [RelayCommand] private void OpenCheckshots() { if (RequireProject()) Open(new CheckshotViewModel(this)); }
    [RelayCommand] private void OpenWellTie() { if (RequireProject()) Open(new WellTieViewModel(this)); }
    [RelayCommand] private void OpenInversion() { if (RequireProject()) Open(new InversionViewModel(this)); }
    [RelayCommand] private void OpenAvo() { if (RequireProject()) Open(new AvoViewModel(this)); }
    [RelayCommand] private void OpenCrossplot() { if (RequireProject()) Open(new CrossplotViewModel(this)); }
    [RelayCommand] private void OpenRockPhysics() { if (RequireProject()) Open(new RockPhysicsViewModel(this)); }
    [RelayCommand] private void OpenPetrophysics() { if (RequireProject()) Open(new PetrophysicsViewModel(this)); }
    [RelayCommand] private void OpenGeoModel() { if (RequireProject()) Open(new GeoModelViewModel(this)); }
    [RelayCommand] private void OpenHistory() { if (RequireProject()) Open(new HistoryViewModel(this)); }
    [RelayCommand] private void OpenReport() { if (RequireProject()) Open(new ReportViewModel(this)); }
    [RelayCommand] private void OpenProjectSettings() { if (RequireProject()) SelectedItem = Session.Project; }

    [RelayCommand]
    private void NewHorizon()
    {
        if (!RequireProject() || SelectedSeismic() is not { } s) return;
        var name = Dialogs.Prompt("Horizon name:", "New horizon", $"Horizon {Session.Horizons.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        string[] colors = ["#FFB300", "#20B8A6", "#E040FB", "#4FC3F7", "#FF7043", "#AED581"];
        var h = Interpretation.CreateHorizon(s, name, colors[Session.Horizons.Count % colors.Length]);
        InterpretationControls.ActiveHorizon = h;
    }

    [RelayCommand]
    private void NewFault()
    {
        if (!RequireProject() || SelectedSeismic() is not { } s) return;
        var name = Dialogs.Prompt("Fault name:", "New fault", $"Fault {Session.Faults.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var f = Interpretation.CreateFault(s, name, "#E53935");
        InterpretationControls.ActiveFault = f;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))] private void UndoAction() { Undo.Undo(); RefreshViewers(); }
    private bool CanUndo() => Undo.CanUndo;
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() { Undo.Redo(); RefreshViewers(); }
    private bool CanRedo() => Undo.CanRedo;
    public IRelayCommand UndoCommand => UndoActionCommand;

    public void RefreshViewers()
    {
        Session.MarkDirty();
        foreach (var v in Documents.OfType<SeismicViewerViewModel>()) v.RefreshOverlays();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = Theme.Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Theme.Apply(next);
        Settings.Theme = next.ToString();
        foreach (var d in Documents) d.OnProjectChanged();
    }

    [RelayCommand]
    private void OpenLogFolder() => Process.Start(new ProcessStartInfo(Path.Combine(AppSettings.Directory, "logs")) { UseShellExecute = true });

    [RelayCommand]
    private void OpenDocumentation()
    {
        var doc = Path.Combine(AppContext.BaseDirectory, "docs", "USER_GUIDE.md");
        if (File.Exists(doc)) Process.Start(new ProcessStartInfo(doc) { UseShellExecute = true });
        else Dialogs.Info("The user guide (docs/USER_GUIDE.md) is installed with the application documentation.");
    }

    [RelayCommand]
    private void About() => Dialogs.Info(
        $"GeoVision Pro {typeof(MainViewModel).Assembly.GetName().Version}\n\nSeismic interpretation, processing, inversion and reservoir characterization.\n\n{EngineStatus}\n" +
        $"Settings: {AppSettings.FilePath}\n\nEstimated properties (inversion, AVO, rock physics, petrophysics) are model-dependent estimates, not measurements.", "About GeoVision Pro");

    [RelayCommand]
    private void Exit() => System.Windows.Application.Current.MainWindow?.Close();

    [RelayCommand]
    private void ActivateDocument(DocumentViewModel? d) { if (d is not null) ActiveDocument = d; }

    [RelayCommand]
    private void CloseAllDocuments() { foreach (var d in Documents.ToList()) CloseDocument(d); }
}
