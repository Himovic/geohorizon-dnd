using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Controls;
using AvalonDock.Layout;
using AvalonDock.Themes;
using GeoVisionPro.Desktop.Services;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, ThemeService theme)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        // tool panes get explicit data contexts (AvalonDock hosts them outside the normal visual tree when floating)
        ExplorerPane.DataContext = vm.Explorer;
        ExplorerPane.Main = vm;
        DataPane.DataContext = vm.DataManager;
        PropertiesPane.DataContext = vm.Properties;
        InterpretationPane.DataContext = vm.InterpretationControls;
        MessagesPane.DataContext = vm.Messages;
        TasksPane.DataContext = vm.TaskList;

        var docStyle = new Style(typeof(LayoutItem));
        docStyle.Setters.Add(new Setter(LayoutItem.TitleProperty, new System.Windows.Data.Binding("Model.Title")));
        docStyle.Setters.Add(new Setter(LayoutItem.CloseCommandProperty, new System.Windows.Data.Binding("Model.CloseCommand")));
        docStyle.Setters.Add(new Setter(LayoutItem.ContentIdProperty, new System.Windows.Data.Binding("Model.ContentId")));
        Dock.LayoutItemContainerStyleSelector = new DocumentStyleSelector(docStyle);
        Dock.ActiveContentChanged += (_, _) => { if (Dock.ActiveContent is DocumentViewModel d) vm.ActiveDocument = d; };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveDocument) && vm.ActiveDocument is not null && !ReferenceEquals(Dock.ActiveContent, vm.ActiveDocument))
                Dock.ActiveContent = vm.ActiveDocument;
        };
        Dock.DocumentClosed += (_, e) => { if (e.Document.Content is DocumentViewModel d) vm.CloseDocument(d); };
        theme.ThemeChanged += (_, t) => Dock.Theme = t == AppTheme.Dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
        if (theme.Current == AppTheme.Light) Dock.Theme = new Vs2013LightTheme();
        Drop += OnDrop;
        Closing += OnClosing;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".sgy" or ".segy" or ".seg") _vm.ImportSegyFile(f);
            else if (ext == ".las") { if (_vm.RequireProject()) _vm.ImportLasFile(f); }
            else if (ext == ".gvp") _ = _vm.OpenProjectAsync(f);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_vm.ConfirmDiscard()) { e.Cancel = true; return; }
        _vm.SaveWorkspace();
    }

    private void ShowPane_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string id }) return;
        var a = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(x => x.ContentId == id);
        if (a is null) return;
        if (a.IsHidden) a.Show();
        a.IsActive = true;
    }

    private sealed class DocumentStyleSelector(Style documentStyle) : StyleSelector
    {
        public override Style? SelectStyle(object item, DependencyObject container) => item is DocumentViewModel ? documentStyle : null;
    }
}
