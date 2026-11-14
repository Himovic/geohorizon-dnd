using System.Windows;
using System.Windows.Controls;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class VtkVolumeView : UserControl
{
    private VtkHost? _host;

    public VtkVolumeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is VtkVolumeViewModel { IsAvailable: true } && _host is null)
            {
                _host = new VtkHost();
                _host.SceneReady += (_, _) => Load_Click(this, new RoutedEventArgs());
                Host.Children.Add(_host);
            }
        };
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VtkVolumeViewModel vm || _host?.Scene is not { } scene) return;
        vm.Status = "Preparing volume...";
        try
        {
            var d = await Task.Run(() => vm.Prepare(CancellationToken.None));
            float a = vm.Dataset.AmplitudeRms > 0 ? (float)(vm.Dataset.AmplitudeRms * 3) : 1f;
            scene.SetVolume(d.Data, d.Nx, d.Ny, d.Nz, d.Sx, d.Sy, d.Sz, 0, 0, 0);
            double o = vm.Opacity;
            scene.SetTransferFunction([(-a, 0.7, 0.05, 0.05, o), (-a * 0.3, 1.0, 0.4, 0.2, o * 0.4), (0, 1, 1, 1, 0), (a * 0.3, 0.4, 0.4, 0.4, o * 0.4), (a, 0.05, 0.05, 0.05, o)]);
            scene.SetParallelProjection(vm.ParallelProjection);
            scene.ResetCamera();
            scene.Render();
            vm.Status = $"Volume {d.Nx}×{d.Ny}×{d.Nz} on GPU";
        }
        catch (Exception ex) { vm.Status = ex.Message; }
    }

    private void Shot_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not VtkVolumeViewModel vm || _host?.Scene is not { } scene) return;
        var p = vm.Main.Dialogs.SaveFile("Screenshot", "PNG image (*.png)|*.png", "volume.png");
        if (p is not null) scene.Screenshot(p, 2);
    }
}
