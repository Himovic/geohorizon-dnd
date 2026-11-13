using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;

namespace GeoVisionPro.WellLog;

/// <summary>A computed trajectory station: MD, TVD (below KB), north/east offsets, map X/Y and TVDSS (below datum, positive down).</summary>
public sealed record TrajectoryStation(double MeasuredDepth, double Tvd, double North, double East, double X, double Y, double TvdSs, double DoglegSeverity);

/// <summary>Well trajectory computed from a deviation survey with the minimum curvature method.</summary>
public sealed class WellTrajectory
{
    public IReadOnlyList<TrajectoryStation> Stations { get; }

    private WellTrajectory(IReadOnlyList<TrajectoryStation> stations) => Stations = stations;

    /// <summary>
    /// Minimum curvature: ΔN = ΔMD/2 (sinI1 cosA1 + sinI2 cosA2) RF, ΔE = ΔMD/2 (sinI1 sinA1 + sinI2 sinA2) RF,
    /// ΔTVD = ΔMD/2 (cosI1 + cosI2) RF, with ratio factor RF = (2/β) tan(β/2) and dogleg β.
    /// A vertical well is assumed when no survey exists. Azimuth is clockwise from grid north.
    /// </summary>
    public static WellTrajectory Compute(Well well)
    {
        var survey = well.Deviation.OrderBy(s => s.MeasuredDepth).ToList();
        if (survey.Count == 0 || survey[0].MeasuredDepth > 0) survey.Insert(0, new DeviationStation(0, survey.Count > 0 ? 0 : 0, 0));
        if (survey.Count == 1) survey.Add(new DeviationStation(Math.Max(well.TotalDepth, 1), 0, 0));
        var list = new List<TrajectoryStation>();
        double n = 0, e = 0, tvd = 0;
        double kb = well.KellyBushing;
        list.Add(new TrajectoryStation(survey[0].MeasuredDepth, survey[0].MeasuredDepth, 0, 0, well.SurfaceX, well.SurfaceY, survey[0].MeasuredDepth - kb, 0));
        tvd = survey[0].MeasuredDepth;
        for (int i = 1; i < survey.Count; i++)
        {
            var s1 = survey[i - 1]; var s2 = survey[i];
            double i1 = s1.Inclination * Math.PI / 180, i2 = s2.Inclination * Math.PI / 180;
            double a1 = s1.Azimuth * Math.PI / 180, a2 = s2.Azimuth * Math.PI / 180;
            double dmd = s2.MeasuredDepth - s1.MeasuredDepth;
            double cosBeta = Math.Cos(i2 - i1) - Math.Sin(i1) * Math.Sin(i2) * (1 - Math.Cos(a2 - a1));
            double beta = Math.Acos(Math.Clamp(cosBeta, -1, 1));
            double rf = beta < 1e-9 ? 1 : 2 / beta * Math.Tan(beta / 2);
            n += dmd / 2 * (Math.Sin(i1) * Math.Cos(a1) + Math.Sin(i2) * Math.Cos(a2)) * rf;
            e += dmd / 2 * (Math.Sin(i1) * Math.Sin(a1) + Math.Sin(i2) * Math.Sin(a2)) * rf;
            tvd += dmd / 2 * (Math.Cos(i1) + Math.Cos(i2)) * rf;
            double dls = dmd > 0 ? beta * 180 / Math.PI / dmd * 30 : 0; // deg per 30 units
            list.Add(new TrajectoryStation(s2.MeasuredDepth, tvd, n, e, well.SurfaceX + e, well.SurfaceY + n, tvd - kb, dls));
        }
        return new WellTrajectory(list);
    }

    private double Interp(double md, Func<TrajectoryStation, double> sel)
    {
        var mds = Stations.Select(s => s.MeasuredDepth).ToArray();
        var v = Stations.Select(sel).ToArray();
        if (md >= mds[^1] && mds.Length >= 2)
        {
            // linear extrapolation along last segment
            double t = (md - mds[^2]) / Math.Max(1e-9, mds[^1] - mds[^2]);
            return v[^2] + t * (v[^1] - v[^2]);
        }
        return Dsp.Interp(mds, v, md, clamp: true);
    }

    public double TvdAt(double md) => Interp(md, s => s.Tvd);
    public double TvdSsAt(double md) => Interp(md, s => s.TvdSs);
    public Point2D LocationAt(double md) => new(Interp(md, s => s.X), Interp(md, s => s.Y));

    /// <summary>Measured depth at a given TVDSS (inverse lookup, assumes TVD increases with MD).</summary>
    public double MdAtTvdSs(double tvdss)
    {
        var tv = Stations.Select(s => s.TvdSs).ToArray();
        var md = Stations.Select(s => s.MeasuredDepth).ToArray();
        return Dsp.Interp(tv, md, tvdss, clamp: true);
    }

    /// <summary>Samples the trajectory every <paramref name="step"/> of MD as 3D points (X, Y, TVDSS).</summary>
    public IReadOnlyList<Point3D> Sample(double step)
    {
        var pts = new List<Point3D>();
        double md0 = Stations[0].MeasuredDepth, md1 = Stations[^1].MeasuredDepth;
        for (double md = md0; md <= md1 + 1e-9; md += step)
        {
            var p = LocationAt(md);
            pts.Add(new Point3D(p.X, p.Y, TvdSsAt(md)));
        }
        return pts;
    }
}
