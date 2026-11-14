using System.Windows.Controls;
using GeoVisionPro.Desktop.ViewModels;
using GeoVisionPro.Visualization2D;

namespace GeoVisionPro.Desktop.Views;

public partial class SegyBrowserView : UserControl
{
    public SegyBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not SegyBrowserViewModel vm) return;
            vm.PreviewChanged += (_, _) => Show(vm);
            Show(vm);
        };
    }

    private void Show(SegyBrowserViewModel vm)
    {
        TracePreview.TraceAxisTitle = "Trace number"; TracePreview.SampleAxisTitle = "Sample";
        TracePreview.TraceLabel = i => (i + 1).ToString();
        TracePreview.SetSettings(new SeismicDisplaySettings { Mode = SeismicDisplayMode.VariableDensityWiggle });
        TracePreview.SetData(vm.Preview, true);
    }
}
