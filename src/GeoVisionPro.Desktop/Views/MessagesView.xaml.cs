using System.Collections.Specialized;
using System.Windows.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class MessagesView : UserControl
{
    public MessagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MessagesViewModel vm)
                vm.Items.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Add && List.Items.Count > 0) List.ScrollIntoView(List.Items[^1]); };
        };
    }
}
