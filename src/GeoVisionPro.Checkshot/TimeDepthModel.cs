using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.Checkshot;

/// <summary>
/// Monotonic time-depth relationship for one well. Depth is TVDSS (positive down, below mean sea level);
/// time is two-way time (s) relative to the seismic reference datum (SRD, elevation above MSL).
/// Between knots both directions are linear; outside the knots the closest interval velocity is extrapolated.
/// </summary>
public sealed class TimeDepthModel
{
    public double[] TvdSs { get; }
    public double[] Twt { get; }
    public double SrdElevation { get; }
    public string Description { get; }

    public TimeDepthModel(double[] tvdss, double[] twt, double srdElevation, string description)
    {
        if (tvdss.Length != twt.Length || tvdss.Length < 1) throw new ArgumentException("Time-depth table must have matching, non-empty arrays.");
        var order = Enumerable.Range(0, tvdss.Length).Where(i => !double.IsNaN(tvdss[i]) && !double.IsNaN(twt[i])).OrderBy(i => tvdss[i]).ToArray();
        // enforce strict monotonicity in both depth and time
        var z = new List<double>(); var t = new List<double>();
        foreach (var i in order)
        {
            if (z.Count > 0 && (tvdss[i] <= z[^1] || twt[i] <= t[^1])) continue;
            z.Add(tvdss[i]); t.Add(twt[i]);
        }
        if (z.Count == 0) throw new ArgumentException("Time-depth table has no valid points.");
        TvdSs = z.ToArray(); Twt = t.ToArray(); SrdElevation = srdElevation; Description = description;
    }

    public int Count => TvdSs.Length;
    /// <summary>Depth below SRD (positive down).</summary>
    public double DepthBelowSrd(double tvdss) => tvdss + SrdElevation;

    public double TimeAtTvdSs(double tvdss)
    {
        if (Count == 1) return Twt[0] * DepthBelowSrd(tvdss) / Math.Max(1e-9, DepthBelowSrd(TvdSs[0]));
        if (tvdss < TvdSs[0])
        {
            // above first point: average velocity from SRD to first point
            double v = 2 * DepthBelowSrd(TvdSs[0]) / Math.Max(Twt[0], 1e-9);
            return Twt[0] - 2 * (TvdSs[0] - tvdss) / Math.Max(v, 1);
        }
        if (tvdss > TvdSs[^1])
        {
            double v = 2 * (TvdSs[^1] - TvdSs[^2]) / (Twt[^1] - Twt[^2]);
            return Twt[^1] + 2 * (tvdss - TvdSs[^1]) / v;
        }
        return Dsp.Interp(TvdSs, Twt, tvdss);
    }

    public double TvdSsAtTime(double twt)
    {
        if (Count == 1) return TvdSs[0] * twt / Math.Max(Twt[0], 1e-9);
        if (twt < Twt[0])
        {
            double v = 2 * DepthBelowSrd(TvdSs[0]) / Math.Max(Twt[0], 1e-9);
            return TvdSs[0] - (Twt[0] - twt) * v / 2;
        }
        if (twt > Twt[^1])
        {
            double v = 2 * (TvdSs[^1] - TvdSs[^2]) / (Twt[^1] - Twt[^2]);
            return TvdSs[^1] + (twt - Twt[^1]) * v / 2;
        }
        return Dsp.Interp(Twt, TvdSs, twt);
    }

    public double TimeAtMd(double md, WellTrajectory traj) => TimeAtTvdSs(traj.TvdSsAt(md));
    public double MdAtTime(double twt, WellTrajectory traj) => traj.MdAtTvdSs(TvdSsAtTime(twt));

    /// <summary>Interval velocities Vint = 2ΔZ/ΔT between consecutive knots (assigned to interval mid-depth).</summary>
    public IReadOnlyList<(double TopTvdSs, double BaseTvdSs, double Velocity)> IntervalVelocities()
    {
        var r = new List<(double, double, double)>();
        for (int i = 1; i < Count; i++) r.Add((TvdSs[i - 1], TvdSs[i], 2 * (TvdSs[i] - TvdSs[i - 1]) / (Twt[i] - Twt[i - 1])));
        return r;
    }

    /// <summary>Average velocity from SRD: Vavg = 2 Z_srd / TWT.</summary>
    public double[] AverageVelocities() => TvdSs.Select((z, i) => Twt[i] > 0 ? 2 * DepthBelowSrd(z) / Twt[i] : double.NaN).ToArray();

    /// <summary>RMS velocity at each knot from interval velocities (Dix forward relation).</summary>
    public double[] RmsVelocities()
    {
        var res = new double[Count];
        double sum = 0, tPrev = 0, zPrev = -SrdElevation;
        for (int i = 0; i < Count; i++)
        {
            double dt = Twt[i] - tPrev;
            double v = dt > 0 ? 2 * (TvdSs[i] - zPrev) / dt : 0;
            sum += v * v * dt;
            res[i] = Twt[i] > 0 ? Math.Sqrt(sum / Twt[i]) : double.NaN;
            tPrev = Twt[i]; zPrev = TvdSs[i];
        }
        return res;
    }

    /// <summary>Builds a model from a checkshot survey using the well trajectory to convert MD to TVDSS.</summary>
    public static TimeDepthModel FromCheckshot(CheckshotSurvey survey, WellTrajectory traj, double srdElevation)
    {
        var z = survey.Points.Select(p => double.IsNaN(p.TvdSs) ? traj.TvdSsAt(p.MeasuredDepth) : p.TvdSs).ToArray();
        var t = survey.Points.Select(p => p.TwoWayTime).ToArray();
        return new TimeDepthModel(z, t, srdElevation, survey.Name);
    }

    /// <summary>
    /// Integrates a sonic (slowness) log in TVD: TWT(z) = t_anchor + 2∫ s(z) dz_vertical. The anchor is either a given
    /// (TVDSS, TWT) pair or, if null, the replacement velocity from SRD to the first valid log sample.
    /// Gaps in the sonic are bridged by linear interpolation of slowness.
    /// </summary>
    public static TimeDepthModel FromSonic(LogCurve sonic, WellTrajectory traj, double srdElevation, double replacementVelocity,
        (double TvdSs, double Twt)? anchor = null)
    {
        var slow = (float[])sonic.Values.Clone();
        Dsp.FillGapsLinear(slow, extend: false);
        string unit = string.IsNullOrEmpty(sonic.Unit) ? "us/ft" : sonic.Unit;
        var z = new List<double>(); var t = new List<double>();
        double time = double.NaN, zPrev = double.NaN, sPrev = double.NaN;
        for (int i = 0; i < slow.Length; i++)
        {
            if (float.IsNaN(slow[i]) || slow[i] <= 0) continue;
            double s = UnitConverter.Convert(slow[i], unit, "s/m"); // s/m
            double tvdss = traj.TvdSsAt(sonic.Depths[i]);
            if (double.IsNaN(time))
            {
                time = 2 * (tvdss + srdElevation) / replacementVelocity;
            }
            else
            {
                double dz = tvdss - zPrev;
                if (dz <= 0) continue;
                time += dz * (s + sPrev); // 2 * dz * (s+sPrev)/2 (trapezoid, two-way)
            }
            z.Add(tvdss); t.Add(time); zPrev = tvdss; sPrev = s;
        }
        if (z.Count < 2) throw new InvalidOperationException("Sonic log has insufficient valid samples for integration.");
        if (anchor is { } a)
        {
            double tAt = Dsp.Interp(z.ToArray(), t.ToArray(), a.TvdSs, clamp: true);
            double shift = a.Twt - tAt;
            for (int i = 0; i < t.Count; i++) t[i] += shift;
        }
        return new TimeDepthModel(z.ToArray(), t.ToArray(), srdElevation, $"Integrated sonic {sonic.Mnemonic}");
    }

    public CheckshotSurvey ToSurvey(Guid wellId, string name, TimeDepthSource source, WellTrajectory traj, int maxPoints = 2000)
    {
        int step = Math.Max(1, Count / maxPoints);
        var s = new CheckshotSurvey { WellId = wellId, Name = name, Source = source };
        for (int i = 0; i < Count; i += step) s.Points.Add(new TimeDepthPoint(traj.MdAtTvdSs(TvdSs[i]), Twt[i], TvdSs[i]));
        if ((Count - 1) % step != 0) s.Points.Add(new TimeDepthPoint(traj.MdAtTvdSs(TvdSs[^1]), Twt[^1], TvdSs[^1]));
        return s;
    }
}

/// <summary>Result of calibrating an integrated-sonic time-depth curve to checkshots.</summary>
public sealed record CheckshotCalibrationResult(
    TimeDepthModel Calibrated,
    IReadOnlyList<(double TvdSs, double Drift)> DriftPoints,
    double RmsResidualBefore,
    double RmsResidualAfter,
    LogCurve? CorrectedSonic);

public static class CheckshotCalibration
{
    /// <summary>
    /// Drift-curve calibration: drift(z) = t_checkshot(z) − t_sonic(z) at each checkshot depth, interpolated linearly
    /// between checkshots (held constant outside). The calibrated relation is t_sonic(z) + drift(z), which honours
    /// the checkshots exactly while keeping the sonic's high-resolution shape. A drift-corrected sonic is also produced.
    /// </summary>
    public static CheckshotCalibrationResult Calibrate(TimeDepthModel sonicModel, TimeDepthModel checkshots, LogCurve? sonic = null, WellTrajectory? traj = null)
    {
        var drift = new List<(double, double)>();
        foreach (var (z, t) in checkshots.TvdSs.Zip(checkshots.Twt))
        {
            if (z < sonicModel.TvdSs[0] - 1e-6 || z > sonicModel.TvdSs[^1] + 1e-6) continue;
            drift.Add((z, t - sonicModel.TimeAtTvdSs(z)));
        }
        if (drift.Count == 0)
        {
            // no overlap: bulk shift using the closest checkshot
            var (z0, t0) = checkshots.TvdSs.Zip(checkshots.Twt).MinBy(p => Math.Abs(p.First - sonicModel.TvdSs[0]));
            drift.Add((z0, t0 - sonicModel.TimeAtTvdSs(z0)));
        }
        var dz = drift.Select(d => d.Item1).ToArray();
        var dd = drift.Select(d => d.Item2).ToArray();
        double DriftAt(double z) => Dsp.Interp(dz, dd, z, clamp: true);
        var newT = sonicModel.TvdSs.Select((z, i) => sonicModel.Twt[i] + DriftAt(z)).ToArray();
        var calibrated = new TimeDepthModel(sonicModel.TvdSs, newT, sonicModel.SrdElevation, sonicModel.Description + " (checkshot calibrated)");
        double before = Math.Sqrt(drift.Average(d => d.Item2 * d.Item2));
        double after = Math.Sqrt(checkshots.TvdSs.Zip(checkshots.Twt).Where(p => p.First >= calibrated.TvdSs[0] && p.First <= calibrated.TvdSs[^1])
            .Select(p => Math.Pow(p.Second - calibrated.TimeAtTvdSs(p.First), 2)).DefaultIfEmpty(0).Average());

        LogCurve? corrected = null;
        if (sonic is not null && traj is not null)
        {
            string unit = string.IsNullOrEmpty(sonic.Unit) ? "us/ft" : sonic.Unit;
            var vals = new float[sonic.Count];
            for (int i = 0; i < vals.Length; i++)
            {
                if (float.IsNaN(sonic.Values[i])) { vals[i] = float.NaN; continue; }
                double z = traj.TvdSsAt(sonic.Depths[i]);
                // derivative of drift w.r.t. depth gives slowness correction: ds = d(drift)/dz / 2
                double h = 1.0;
                double dDdz = (DriftAt(z + h) - DriftAt(z - h)) / (2 * h);
                double ds = UnitConverter.Convert(dDdz / 2, "s/m", unit);
                vals[i] = (float)(sonic.Values[i] + ds);
            }
            corrected = sonic.CloneAs(sonic.Mnemonic + "_CAL", vals, sonic.Unit, sonic.Family, "Drift-corrected sonic (checkshot calibrated)");
        }
        return new CheckshotCalibrationResult(calibrated, drift, before, after, corrected);
    }

    /// <summary>Replacement velocity implied by the shallowest checkshot: V = 2·Z_srd / TWT.</summary>
    public static double ReplacementVelocity(TimeDepthModel checkshots) =>
        2 * checkshots.DepthBelowSrd(checkshots.TvdSs[0]) / checkshots.Twt[0];

    /// <summary>QC: checkshot points that imply non-physical interval velocities (outside [vmin, vmax]).</summary>
    public static IReadOnlyList<string> QualityControl(TimeDepthModel checkshots, double vmin = 1300, double vmax = 7000)
    {
        var msgs = new List<string>();
        foreach (var (top, bas, v) in checkshots.IntervalVelocities())
            if (v < vmin || v > vmax) msgs.Add($"Interval {top:0.#}-{bas:0.#}: interval velocity {v:0} m/s outside [{vmin}, {vmax}].");
        return msgs;
    }
}
