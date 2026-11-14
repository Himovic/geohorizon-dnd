using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class ProjectExplorerView : UserControl
{
    public ProjectExplorerView()
    {
        InitializeComponent();
        Drop += OnDrop;
    }

    public MainViewModel? Main { get; set; }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ExplorerViewModel vm) vm.SelectedNode = e.NewValue as ExplorerNode;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ExplorerViewModel vm && Tree.SelectedItem is ExplorerNode n) vm.ActivateCommand.Execute(n);
    }

    /// <summary>Drag-and-drop import: SEG-Y files open the SEG-Y browser, LAS files are imported directly.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (Main is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        foreach (var f in (string[])e.Data.GetData(DataFormats.FileDrop))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".sgy" or ".segy" or ".seg") Main.ImportSegyFile(f);
            else if (ext == ".las") { if (Main.RequireProject()) Main.ImportLasFile(f); }
            else if (ext == ".gvp") _ = Main.OpenProjectAsync(f);
            else Main.Log.Warn("Import", $"Unsupported file dropped: {Path.GetFileName(f)} (use the Data menu for ASCII imports).");
        }
    }
}
