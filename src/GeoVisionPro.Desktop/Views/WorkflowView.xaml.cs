using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class WorkflowView : UserControl
{
    public WorkflowView()
    {
        InitializeComponent();
        var template = new DataTemplate();
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new CatalogNameConverter() });
        template.VisualTree = tb;
        CatalogBox.ItemTemplate = template;
    }

    private sealed class CatalogNameConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value switch
        {
            SeismicProcessing.IProcessingOperation p => $"[{p.Category}] {p.DisplayName}",
            Attributes.ISeismicAttribute a => $"[Attribute] {a.DisplayName}",
            _ => value?.ToString() ?? ""
        };
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
