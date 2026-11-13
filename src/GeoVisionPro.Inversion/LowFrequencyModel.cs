using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;
using GeoVisionPro.WellLog;
using GeoVisionPro.WellTie;

namespace GeoVisionPro.Inversion;

/// <summary>A well property log converted to two-way time, located in map coordinates.</summary>
public sealed record WellTimeLog(string WellName, Point2D Location, double StartTime, double SampleInterval, float[] Values)
{
    public double ValueAt(double t)
    {
        double idx = (t - StartTime) / SampleInterval;
        if (idx < 0) return Values.Length > 0 ? Values[0] : double.NaN;          // hold first value above the log
        if (idx > Values.Length - 1) return Values.Length > 0 ? Values[^1] : double.NaN;
        return Dsp.SampleLinear(Values, idx);
    }
    public double FirstTime => StartTime;
    public double LastTime => StartTime + (Values.Length - 1) * SampleInterval;

    /// <summary>Converts sonic/density logs of a well to an impedance log in time (P-impedance in (m/s)(g/cc)).</summary>
    public static WellTimeLog AcousticImpedance(Well well, TimeDepthModel tdr, double dt)
    {
        var sonic = well.FindCurve(CurveFamily.Sonic) ?? throw new InvalidOperationException($"{well.Name}: sonic log required.");
        var rhob = well.FindCurve(CurveFamily.Density) ?? throw new InvalidOperationException($"{well.Name}: density log required.");
        var traj = WellTrajectory.Compute(well);
        var logs = SyntheticSeismogram.ToTime(sonic, rhob, tdr, traj, dt);
        double tMid = logs.TimeAt(logs.Count / 2);
        var loc = traj.LocationAt(tdr.MdAtTime(tMid, traj));
        return new WellTimeLog(well.Name, loc, logs.StartTime, dt, logs.Impedance);
    }

    /// <summary>Converts an arbitrary curve (e.g. Vs-derived Is, density) to time by block averaging.</summary>
    public static WellTimeLog FromCurve(Well well, LogCurve curve, TimeDepthModel tdr, double dt)
    {
        var traj = WellTrajectory.Compute(well);
        var times = new List<double>(); var vals = new List<double>();
        for (int i = 0; i < curve.Count; i++)
        {
            if (float.IsNaN(curve.Values[i])) continue;
            times.Add(tdr.TimeAtMd(curve.Depths[i], traj)); vals.Add(curve.Values[i]);
        }
        if (times.Count < 2) throw new InvalidOperationException($"{well.Name}: curve {curve.Mnemonic} has no valid samples.");
        double t0 = Math.Ceiling(times.Min() / dt) * dt, t1 = Math.Floor(times.Max() / dt) * dt;
        int n = (int)Math.Round((t1 - t0) / dt) + 1;
        var sum = new double[n]; var cnt = new int[n];
        for (int k = 0; k < times.Count; k++)
        {
            int idx = (int)Math.Round((times[k] - t0) / dt);
            if (idx >= 0 && idx < n) { sum[idx] += vals[k]; cnt[idx]++; }
        }
        var v = new float[n];
        for (int i = 0; i < n; i++) v[i] = cnt[i] > 0 ? (float)(sum[i] / cnt[i]) : float.NaN;
        Dsp.FillGapsLinear(v);
        var loc = traj.LocationAt(curve.Depths[curve.Count / 2]);
        return new WellTimeLog(well.Name, loc, t0, dt, v);
    }
}

/// <summary>
/// Low-frequency (background) model built from well logs, interpolated laterally with inverse-distance weighting and
/// guided by horizons: within each zone between consecutive horizons, times are mapped proportionally (stratigraphic
/// conformance) before sampling the well logs. The result is low-pass filtered (Butterworth) in the log domain.
/// </summary>
public sealed class LowFrequencyModel
{
    private readonly IReadOnlyList<WellTimeLog> _wells;
    private readonly IReadOnlyList<Horizon> _horizons;
    private readonly SurveyGeometry _g;
    private readonly double _cutoffHz;
    private readonly double _power;
    private readonly bool _logDomain;

    public LowFrequencyModel(SurveyGeometry geometry, IReadOnlyList<WellTimeLog> wells, IReadOnlyList<Horizon>? horizons = null,
        double cutoffHz = 10, double idwPower = 2, bool logDomain = true)
    {
        if (wells.Count == 0) throw new ArgumentException("At least one well log is required for a low-frequency model.");
        _g = geometry; _wells = wells; _horizons = (horizons ?? []).OrderBy(h => h.Values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average()).ToList();
        _cutoffHz = cutoffHz; _power = idwPower; _logDomain = logDomain;
    }

    public double CutoffHz => _cutoffHz;

    /// <summary>Horizon times at a location (sorted, NaN where undefined; falls back to well locations when undefined).</summary>
    private double[] HorizonTimes(int il, int xl) => _horizons.Select(h => h.GetValue(il, xl)).ToArray();

    private double[] HorizonTimesAtWell(WellTimeLog w)
    {
        var loc = (_g.Transform.ToGrid(w.Location.X, w.Location.Y));
        int il = _g.Inlines.First + (int)Math.Round((loc.Inline - _g.Inlines.First) / _g.Inlines.Step) * _g.Inlines.Step;
        int xl = _g.Crosslines.First + (int)Math.Round((loc.Crossline - _g.Crosslines.First) / _g.Crosslines.Step) * _g.Crosslines.Step;
        il = Math.Clamp(il, Math.Min(_g.Inlines.First, _g.Inlines.Last), Math.Max(_g.Inlines.First, _g.Inlines.Last));
        xl = Math.Clamp(xl, Math.Min(_g.Crosslines.First, _g.Crosslines.Last), Math.Max(_g.Crosslines.First, _g.Crosslines.Last));
        return HorizonTimes(il, xl);
    }

    /// <summary>Maps a time at a trace to the equivalent time at a well through proportional zone mapping.</summary>
    private static double MapTime(double t, double[] hTrace, double[] hWell)
    {
        var pairs = new List<(double A, double B)>();
        for (int k = 0; k < hTrace.Length; k++) if (!double.IsNaN(hTrace[k]) && !double.IsNaN(hWell[k])) pairs.Add((hTrace[k], hWell[k]));
        if (pairs.Count == 0) return t;
        if (t <= pairs[0].A) return pairs[0].B + (t - pairs[0].A);
        for (int k = 1; k < pairs.Count; k++)
        {
            if (t <= pairs[k].A)
            {
                double frac = (t - pairs[k - 1].A) / Math.Max(1e-9, pairs[k].A - pairs[k - 1].A);
                return pairs[k - 1].B + frac * (pairs[k].B - pairs[k - 1].B);
            }
        }
        return pairs[^1].B + (t - pairs[^1].A);
    }

    /// <summary>Model trace (impedance units) at (il, xl) sampled on the survey time axis.</summary>
    public float[] Trace(int il, int xl)
    {
        var g = _g;
        var loc = g.MapLocation(il, xl);
        var w = _wells.Select(wl => 1 / Math.Pow(Math.Max(wl.Location.DistanceTo(loc), 1.0), _power)).ToArray();
        double ws = w.Sum();
        var hTrace = HorizonTimes(il, xl);
        var hWells = _wells.Select(HorizonTimesAtWell).ToArray();
        var res = new float[g.SampleCount];
        for (int k = 0; k < g.SampleCount; k++)
        {
            double t = g.TimeAt(k), s = 0;
            for (int i = 0; i < _wells.Count; i++)
            {
                double tw = _horizons.Count > 0 ? MapTime(t, hTrace, hWells[i]) : t;
                double v = _wells[i].ValueAt(tw);
                s += w[i] / ws * (_logDomain ? Math.Log(Math.Max(v, 1e-6)) : v);
            }
            res[k] = (float)s;
        }
        if (_cutoffHz > 0) FrequencyFilters.Butterworth(res, g.SampleInterval, 0, _cutoffHz, 4);
        if (_logDomain) for (int k = 0; k < res.Length; k++) res[k] = (float)Math.Exp(res[k]);
        return res;
    }
}
