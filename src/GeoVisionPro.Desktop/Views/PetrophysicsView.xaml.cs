using System.Windows.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class PetrophysicsView : UserControl
{
    public PetrophysicsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not PetrophysicsViewModel vm) return;
            vm.Evaluated += (_, _) => Logs.SetWell(vm.Well);
            Logs.SetWell(vm.Well);
        };
    }
}
