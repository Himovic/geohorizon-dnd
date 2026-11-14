using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace GeoVisionPro.Desktop.Views;

public partial class PropertiesView : UserControl
{
    public PropertiesView() => InitializeComponent();
}

public sealed class InverseBool : IValueConverter
{
    public static InverseBool Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : false;
}
