using System.Windows.Controls;
using System.Windows.Media;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Desktop.Controls;
using GeoVisionPro.Desktop.ViewModels;

namespace GeoVisionPro.Desktop.Views;

public partial class WellTieView : UserControl
{
    private WellTieViewModel? _vm;
    private bool _ranged;

    public WellTieView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Updated -= Draw;
            _vm = DataContext as WellTieViewModel;
            if (_vm is not null) { _vm.Updated += Draw; Draw(_vm, EventArgs.Empty); }
        };
    }

    private void Draw(object? s, EventArgs e)
    {
        Tracks.Tracks.Clear(); Tracks.Markers.Clear();
        var t = _vm?.Session;
        if (t?.Synthetic is null) { Tracks.InvalidateVisual(); return; }
        var logs = t.Synthetic.Logs;
        double dt = t.SampleInterval;
        // logs and reflectivity on the tied time axis
        Tracks.Tracks.Add(new TimeSeriesTrack("Vp (m/s)", t.TiedImpedance.Length > 0 ? Resample(logs.Velocity, logs.StartTime, logs.SampleInterval, t) : logs.Velocity, t.TiedStartTime, dt, Color.FromRgb(137, 221, 255), TrackStyle.Blocky));
        Tracks.Tracks.Add(new TimeSeriesTrack("Density (g/cc)", Resample(logs.Density, logs.StartTime, logs.SampleInterval, t), t.TiedStartTime, dt, Color.FromRgb(240, 113, 120), TrackStyle.Blocky));
        Tracks.Tracks.Add(new TimeSeriesTrack("AI", t.TiedImpedance, t.TiedStartTime, dt, Color.FromRgb(247, 140, 108), TrackStyle.Blocky));
        Tracks.Tracks.Add(new TimeSeriesTrack("RC", t.TiedReflectivity, t.TiedStartTime, dt, Color.FromRgb(232, 237, 244), TrackStyle.Stems));
        Tracks.Tracks.Add(new TimeSeriesTrack("Synthetic", t.TiedSynthetic, t.TiedStartTime, dt, Color.FromRgb(61, 139, 219), TrackStyle.Wiggle, 4));
        var sei = t.SeismicTrace;
        Tracks.Tracks.Add(new TimeSeriesTrack("Seismic", sei, t.TiedStartTime, dt, Color.FromRgb(32, 184, 166), TrackStyle.Wiggle, 4));
        var resid = new float[Math.Min(sei.Length, t.TiedSynthetic.Length)];
        float sa = Dsp.Rms(t.TiedSynthetic), sb = Dsp.Rms(sei.Select(v => float.IsNaN(v) ? 0f : v).ToArray());
        for (int i = 0; i < resid.Length; i++) resid[i] = sa > 0 && sb > 0 ? t.TiedSynthetic[i] / sa - sei[i] / sb : 0;
        Tracks.Tracks.Add(new TimeSeriesTrack("Residual", resid, t.TiedStartTime, dt, Color.FromRgb(199, 146, 234), TrackStyle.Wiggle, 1));
        var traj = t.Trajectory;
        foreach (var top in t.Well.Tops)
            Tracks.Markers.Add((top.Name, t.TiedTdr.TimeAtMd(top.MeasuredDepth, traj), BitmapUtil.ParseColor(top.Color, Colors.Yellow)));
        if (_vm!.WindowStartMs is double ws && !double.IsNaN(ws) && _vm.WindowEndMs is double we && !double.IsNaN(we)) Tracks.HighlightWindow = (ws / 1000, we / 1000);
        else Tracks.HighlightWindow = null;
        if (!_ranged) { Tracks.AutoRange(); _ranged = true; }
        Tracks.InvalidateMeasure(); Tracks.InvalidateVisual();

        var w = t.Synthetic.Wavelet;
        ChartHelper.Prepare(WaveletPlot, "Wavelet", "Time (ms)", "Amplitude");
        ChartHelper.Line(WaveletPlot, Enumerable.Range(0, w.Length).Select(i => w.TimeAt(i) * 1000).ToArray(), w.Samples.Select(v => (double)v).ToArray(), "#3D8BDB", w.Name);
        ChartHelper.Finish(WaveletPlot);
        int maxLag = (int)(0.06 / dt);
        var xc = Dsp.CrossCorrelate(t.TiedSynthetic, sei.Select(v => float.IsNaN(v) ? 0f : v).ToArray(), maxLag);
        double norm = Math.Sqrt(t.TiedSynthetic.Sum(v => v * (double)v) * sei.Where(v => !float.IsNaN(v)).Sum(v => v * (double)v));
        ChartHelper.Prepare(XcorrPlot, "Cross-correlation", "Lag (ms)", "r");
        ChartHelper.Line(XcorrPlot, Enumerable.Range(-maxLag, 2 * maxLag + 1).Select(l => l * dt * 1000).ToArray(), xc.Select(v => norm > 0 ? v / norm : 0).ToArray(), "#20B8A6");
        ChartHelper.VLine(XcorrPlot, 0, "#9AA6B8");
        ChartHelper.Finish(XcorrPlot);
    }

    private static float[] Resample(float[] v, double t0, double dt, GeoVisionPro.WellTie.WellTieSession t)
    {
        var res = new float[t.TiedImpedance.Length];
        var orig = Enumerable.Range(0, v.Length).Select(i => t.MapTime(t0 + i * dt)).ToArray();
        var idx = Enumerable.Range(0, v.Length).Select(i => (double)i).ToArray();
        for (int k = 0; k < res.Length; k++) res[k] = Dsp.SampleLinear(v, Dsp.Interp(orig, idx, t.TiedStartTime + k * t.SampleInterval, clamp: true));
        return res;
    }
}
