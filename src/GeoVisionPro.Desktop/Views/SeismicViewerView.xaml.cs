using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class SeismicViewerView : UserControl
{
    private SeismicViewerViewModel? _vm;

    public SeismicViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as SeismicViewerViewModel);
        Viewer.PointerMoved += (_, e) => _vm?.OnPointerMoved(e.Trace, e.Sample, e.Amplitude);
        Viewer.PointerClicked += (_, e) => _vm?.OnPointerClicked(e.Trace, e.Sample, e.Button, e.Modifiers, e.ClickCount);
        Viewer.PolylineCompleted += (_, pts) => _vm?.OnPolylineCompleted(pts);
        Viewer.EraseRequested += (_, r) => _vm?.OnEraseRequested(r.Trace0, r.Trace1);
        PreviewKeyDown += OnKey;
    }

    private void Attach(SeismicViewerViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.SectionChanged -= OnSection; _vm.OverlaysChanged -= OnOverlays; _vm.DisplayChanged -= OnDisplay; _vm.PropertyChanged -= OnVmProperty;
        }
        _vm = vm;
        if (vm is null) return;
        vm.SectionChanged += OnSection; vm.OverlaysChanged += OnOverlays; vm.DisplayChanged += OnDisplay; vm.PropertyChanged += OnVmProperty;
        bool gather = vm.Kind == SectionKind.Gather;
        GatherXlLabel.Visibility = gather ? Visibility.Visible : Visibility.Collapsed;
        GatherXlBox.Visibility = gather ? Visibility.Visible : Visibility.Collapsed;
        Viewer.TraceLabel = vm.TraceLabel;
        Viewer.SampleLabel = vm.SampleLabel;
        Viewer.SampleToAxis = vm.SampleToAxis;
        Viewer.AxisToSample = vm.AxisToSample;
        Viewer.TraceAxisTitle = vm.TraceAxisTitle;
        Viewer.SampleAxisTitle = vm.SampleAxisTitle;
        Viewer.Tool = vm.Tool;
        Viewer.SetSettings(vm.DisplaySettings);
        if (vm.Section is not null) { OnSection(vm, true); OnOverlays(vm, EventArgs.Empty); }
    }

    private void OnVmProperty(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SeismicViewerViewModel.Tool) && _vm is not null)
        {
            Viewer.Tool = _vm.Tool;
            Viewer.Cursor = _vm.Tool == ViewerTool.Navigate ? Cursors.Hand : Cursors.Cross;
        }
    }

    private void OnSection(object? s, bool reset)
    {
        if (_vm is null) return;
        Viewer.Title = _vm.PositionLabel;
        Viewer.SetData(_vm.Section, reset);
    }

    private void OnOverlays(object? s, EventArgs e)
    {
        if (_vm is null) return;
        Viewer.Overlays.Clear(); Viewer.Overlays.AddRange(_vm.Overlays);
        Viewer.Markers.Clear(); Viewer.Markers.AddRange(_vm.Markers);
        Viewer.Crosshair = _vm.Crosshair;
        Viewer.RefreshOverlays();
    }

    private void OnDisplay(object? s, EventArgs e) { if (_vm is not null) Viewer.SetSettings(_vm.DisplaySettings); }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.PageDown) { _vm.NextCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.PageUp) { _vm.PreviousCommand.Execute(null); e.Handled = true; }
    }

    private void ResetView_Click(object sender, RoutedEventArgs e) => Viewer.ResetView();

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var p = _vm.Main.Dialogs.SaveFile("Export image", "PNG image (*.png)|*.png", _vm.Title.Replace(' ', '_') + ".png");
        if (p is not null) BitmapUtil.SavePng(Viewer, p);
    }
}
