using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeoVisionPro.Core.Parameters;

namespace GeoVisionPro.Desktop.ViewModels;

/// <summary>Base of all documents hosted in the central docking area.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    protected DocumentViewModel(MainViewModel main) { Main = main; }
    public MainViewModel Main { get; }
    [ObservableProperty] private string _title = "Document";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    public string ContentId { get; } = Guid.NewGuid().ToString("N");
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Called when the document is removed from the workspace (unsubscribe events, release resources).</summary>
    public virtual void OnClosed() { }

    /// <summary>Called when project content changes (datasets added/removed) so pickers can refresh.</summary>
    public virtual void OnProjectChanged() { }

    /// <summary>Workspace persistence key: document kind and argument (e.g. dataset id); null = not restorable.</summary>
    public virtual (string Kind, string Argument)? WorkspaceKey => null;

    /// <summary>Runs work in the background task manager, updating IsBusy/Status.</summary>
    protected async Task RunAsync(string name, Func<IProgress<Core.Primitives.ProgressInfo>, CancellationToken, Task> work)
    {
        IsBusy = true;
        Status = name + "...";
        try
        {
            var t = Main.Tasks.Run(name, work);
            await t.Task!;
            Status = t.Status switch
            {
                Application.Services.BackgroundTaskStatus.Completed => $"{name}: done",
                Application.Services.BackgroundTaskStatus.Cancelled => $"{name}: cancelled",
                _ => $"{name}: failed - {t.Error?.Message}"
            };
        }
        finally { IsBusy = false; }
    }
}

/// <summary>Editable parameter bound to a <see cref="ParameterDescriptor"/> (generated parameter forms).</summary>
public sealed partial class ParameterItemViewModel : ObservableObject
{
    public ParameterDescriptor Descriptor { get; }
    [ObservableProperty] private string _value;
    public ParameterItemViewModel(ParameterDescriptor d, string value) { Descriptor = d; _value = value; }
    public string Label => string.IsNullOrEmpty(Descriptor.Unit) ? Descriptor.Label : $"{Descriptor.Label} ({Descriptor.Unit})";
    public string ToolTip => string.IsNullOrEmpty(Descriptor.Description) ? Descriptor.Label : Descriptor.Description;
    public bool IsChoice => Descriptor.Kind == ParameterKind.Choice;
    public bool IsBool => Descriptor.Kind == ParameterKind.Boolean;
    public bool IsText => !IsChoice && !IsBool;
    public string[] Choices => Descriptor.Choices ?? [];
    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set { Value = value.ToString(); OnPropertyChanged(); }
    }

    public static ObservableCollection<ParameterItemViewModel> From(IEnumerable<ParameterDescriptor> ds, IDictionary<string, string>? values = null) =>
        new(ds.Select(d => new ParameterItemViewModel(d, values is not null && values.TryGetValue(d.Key, out var v) ? v : d.Default)));

    public static ParameterSet ToSet(IEnumerable<ParameterItemViewModel> items)
    {
        var p = new ParameterSet();
        foreach (var i in items) p[i.Descriptor.Key] = i.Value?.Trim() ?? "";
        return p;
    }
}

/// <summary>A node in the project explorer tree.</summary>
public sealed partial class ExplorerNode : ObservableObject
{
    public ExplorerNode(string name, string glyph, object? item = null) { _name = name; Glyph = glyph; Item = item; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    public string Glyph { get; }
    public object? Item { get; }
    public string? Detail { get; init; }
    public ObservableCollection<ExplorerNode> Children { get; } = [];
    public ObservableCollection<MenuAction> Actions { get; } = [];
    public ICommand? DefaultCommand { get; init; }
}

public sealed record MenuAction(string Header, ICommand Command, object? Parameter = null);

/// <summary>One row of the property grid.</summary>
public sealed partial class PropertyRow : ObservableObject
{
    private readonly Action<string>? _setter;
    public PropertyRow(string category, string name, string value, Action<string>? setter) { Category = category; Name = name; _value = value; _setter = setter; }
    public string Category { get; }
    public string Name { get; }
    public bool IsReadOnly => _setter is null;
    [ObservableProperty] private string _value;
    partial void OnValueChanged(string value) => _setter?.Invoke(value);
}

public static class Fmt
{
    public static string N(double v, string f = "0.###") => double.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);
    public static bool TryD(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    public static double D(string s, double fallback) => TryD(s, out var v) ? v : fallback;
}
