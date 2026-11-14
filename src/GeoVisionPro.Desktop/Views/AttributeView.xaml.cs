using System.Windows.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class AttributeView : UserControl
{
    private AttributeViewModel? _vm;
    public AttributeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PreviewUpdated -= Draw;
            _vm = DataContext as AttributeViewModel;
            if (_vm is not null) { _vm.PreviewUpdated += Draw; Draw(_vm, EventArgs.Empty); }
        };
    }

    private void Draw(object? s, EventArgs e)
    {
        if (_vm?.Preview is null || _vm.Geometry is not { } g) return;
        Preview.SampleToAxis = x => g.TimeAt(x) * 1000; Preview.AxisToSample = a => g.SampleAt(a / 1000);
        Preview.SampleLabel = x => (g.TimeAt(x) * 1000).ToString("0"); Preview.SampleAxisTitle = "TWT (ms)";
        Preview.TraceLabel = i => g.Crosslines.ValueAt(i).ToString(); Preview.TraceAxisTitle = "Crossline";
        Preview.Title = $"{_vm.Attribute?.DisplayName} - inline {_vm.PreviewInline}";
        bool diverging = _vm.Attribute?.Id is "phaserot" or "cosphase" or "rai" or "dip" or "instphase";
        Preview.SetSettings(new SeismicDisplaySettings { ColorMap = diverging ? ColorMaps.Seismic : _vm.Attribute?.Id is "coherence" ? ColorMaps.Coherence : ColorMaps.Spectrum, ClipMode = diverging ? ClipMode.Percentile : ClipMode.MinMax });
        Preview.SetData(_vm.Preview, false);
    }
}
