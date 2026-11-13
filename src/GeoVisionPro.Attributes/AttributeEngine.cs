using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Parameters;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;

namespace GeoVisionPro.Attributes;

public static class AttributeCatalog
{
    private static readonly Lazy<IReadOnlyList<ISeismicAttribute>> _all = new(() =>
    [
        new EnvelopeAttribute(), new WindowedAmplitudeAttribute(WindowStatistic.Rms), new WindowedAmplitudeAttribute(WindowStatistic.MeanAbsolute),
        new WindowedAmplitudeAttribute(WindowStatistic.Maximum), new WindowedAmplitudeAttribute(WindowStatistic.Minimum),
        new WindowedAmplitudeAttribute(WindowStatistic.MaximumAbsolute), new WindowedAmplitudeAttribute(WindowStatistic.Energy),
        new InstantaneousFrequencyAttribute(), new DominantFrequencyAttribute(), new SpectralDecompositionAttribute(false),
        new SpectralDecompositionAttribute(true), new AttenuationAttribute(),
        new InstantaneousPhaseAttribute(), new CosinePhaseAttribute(), new PhaseRotationAttribute(),
        new CoherenceAttribute(), new VarianceAttribute(), new DipAzimuthAttribute(), new EdgeDetectionAttribute(),
        new SweetnessAttribute(), new RelativeImpedanceAttribute(), new ChaosAttribute(),
    ]);

    public static IReadOnlyList<ISeismicAttribute> All => _all.Value;
    public static ISeismicAttribute Get(string id) => All.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown attribute '{id}'.");
}

/// <summary>Computes attribute sections and volumes (streaming inline by inline with a 3-inline neighbourhood).</summary>
public static class AttributeEngine
{
    public static Array2D ComputeInline(ISeismicVolume volume, int inline, ISeismicAttribute attr, ParameterSet p)
    {
        var g = volume.Geometry;
        int i = g.InlineIndex(inline);
        var prev = i > 0 ? volume.ReadInline(g.Inlines.ValueAt(i - 1)) : null;
        var cur = volume.ReadInline(inline);
        var next = i < g.InlineCount - 1 ? volume.ReadInline(g.Inlines.ValueAt(i + 1)) : null;
        return ComputeSlab(prev, cur, next, attr, p, g);
    }

    /// <summary>Computes the attribute for the centre inline of a 3-inline slab ([crossline, sample] arrays).</summary>
    public static Array2D ComputeSlab(Array2D? prev, Array2D cur, Array2D? next, ISeismicAttribute attr, ParameterSet p, SurveyGeometry g)
    {
        var res = new Array2D(cur.Rows, cur.Cols);
        double dxi = g.Transform.InlineSpacing > 0 ? g.Transform.InlineSpacing : 25, dxc = g.Transform.CrosslineSpacing > 0 ? g.Transform.CrosslineSpacing : 25;
        Parallel.For(0, cur.Rows, j =>
        {
            if (attr.Scope == AttributeScope.Trace)
            {
                attr.ComputeTrace(cur.Row(j), res.Row(j), p, g.SampleInterval);
                return;
            }
            var t = new float[]?[3, 3];
            for (int a = -1; a <= 1; a++)
            {
                var src = a == -1 ? prev : a == 0 ? cur : next;
                if (src is null) continue;
                for (int b = -1; b <= 1; b++)
                {
                    int jj = j + b;
                    if (jj < 0 || jj >= cur.Rows) continue;
                    var row = src.Row(jj).ToArray();
                    if (Dsp.MaxAbs(row) == 0 && !(a == 0 && b == 0)) continue; // dead trace
                    t[a + 1, b + 1] = row;
                }
            }
            t[1, 1] ??= cur.Row(j).ToArray();
            attr.ComputeNeighborhood(t, res.Row(j), p, g.SampleInterval, dxi, dxc);
        });
        return res;
    }

    public static void ComputeVolume(ISeismicVolume input, IWritableSeismicVolume output, ISeismicAttribute attr, ParameterSet p,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = input.Geometry;
        var v = p.Validate(attr.Parameters);
        if (!v.IsValid) throw new InvalidOperationException(v.ToString());
        Array2D? prev = null, cur = input.ReadInline(g.Inlines.First), next;
        for (int i = 0; i < g.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            next = i + 1 < g.InlineCount ? input.ReadInline(g.Inlines.ValueAt(i + 1)) : null;
            var res = ComputeSlab(prev, cur, next, attr, p, g);
            int il = g.Inlines.ValueAt(i);
            for (int j = 0; j < g.CrosslineCount; j++) output.WriteTrace(il, g.Crosslines.ValueAt(j), res.Row(j));
            prev = cur; cur = next!;
            progress?.Report(ProgressInfo.Of(i + 1, g.InlineCount, $"{attr.DisplayName}: inline {il}"));
        }
        output.Flush();
    }
}

public enum HorizonWindowMode { AtHorizon, WindowAroundHorizon, BetweenHorizons }

/// <summary>Extracts attribute maps along horizons.</summary>
public static class HorizonAttributeExtraction
{
    /// <summary>
    /// Extracts a map: amplitude interpolated at the horizon time (sinc), or a window statistic over
    /// [t_h − above, t_h + below] or between two horizons. Returns a grid aligned with the horizon.
    /// </summary>
    public static Grid2D Extract(ISeismicVolume volume, Horizon horizon, HorizonWindowMode mode, WindowStatistic stat,
        double above = 0.02, double below = 0.02, Horizon? baseHorizon = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var g = volume.Geometry;
        var grid = new Grid2D(horizon.InlineCount, horizon.CrosslineCount);
        for (int i = 0; i < horizon.InlineCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int il = horizon.InlineAt(i);
            if (!g.Inlines.Contains(il)) continue;
            var section = volume.ReadInline(il);
            for (int j = 0; j < horizon.CrosslineCount; j++)
            {
                double t = horizon.Values[i * horizon.CrosslineCount + j];
                if (double.IsNaN(t)) continue;
                int xl = horizon.CrosslineAt(j);
                if (!g.Crosslines.Contains(xl)) continue;
                var tr = section.Row(g.CrosslineIndex(xl));
                switch (mode)
                {
                    case HorizonWindowMode.AtHorizon:
                        grid[i, j] = Dsp.SampleSinc(tr, g.SampleAt(t));
                        break;
                    default:
                    {
                        double t0 = t - above, t1 = t + below;
                        if (mode == HorizonWindowMode.BetweenHorizons)
                        {
                            double tb = baseHorizon?.GetValue(il, xl) ?? double.NaN;
                            if (double.IsNaN(tb)) continue;
                            (t0, t1) = (Math.Min(t, tb), Math.Max(t, tb));
                        }
                        int a = Math.Max(0, (int)Math.Ceiling(g.SampleAt(t0))), b = Math.Min(tr.Length - 1, (int)Math.Floor(g.SampleAt(t1)));
                        if (b < a) continue;
                        grid[i, j] = WindowedAmplitudeAttribute.Statistic(tr.Slice(a, b - a + 1), stat);
                        break;
                    }
                }
            }
            progress?.Report(ProgressInfo.Of(i + 1, horizon.InlineCount, $"Extracting along {horizon.Name}"));
        }
        return grid;
    }
}

public enum CurvatureType { MostPositive, MostNegative, Mean, Gaussian, Maximum, Minimum, DipAngle, DipAzimuth }

/// <summary>
/// Horizon (surface) curvature attributes from a local quadratic fit z = ax² + by² + cxy + dx + ey + f on a 3×3
/// neighbourhood (Roberts, 2001). Time horizons are converted to depth with z = t·v/2 (constant velocity).
/// x runs along crosslines (columns), y along inlines (rows). Curvatures in 1/m, dip in degrees.
/// </summary>
public static class HorizonCurvature
{
    public static Grid2D Compute(Horizon h, double binSpacingInline, double binSpacingCrossline, CurvatureType type, double velocity = 2500)
    {
        int ni = h.InlineCount, nj = h.CrosslineCount;
        var res = new Grid2D(ni, nj);
        double scale = h.Domain == Core.Units.DomainType.Time ? velocity / 2 : 1;
        double dx = Math.Sqrt(binSpacingInline * binSpacingCrossline);
        double Z(int i, int j) => h.Values[i * nj + j] * scale;
        for (int i = 1; i < ni - 1; i++)
            for (int j = 1; j < nj - 1; j++)
            {
                double z1 = Z(i - 1, j - 1), z2 = Z(i - 1, j), z3 = Z(i - 1, j + 1), z4 = Z(i, j - 1), z5 = Z(i, j), z6 = Z(i, j + 1), z7 = Z(i + 1, j - 1), z8 = Z(i + 1, j), z9 = Z(i + 1, j + 1);
                if (double.IsNaN(z1 + z2 + z3 + z4 + z5 + z6 + z7 + z8 + z9)) continue;
                double a = (z1 + z3 + z4 + z6 + z7 + z9) / (12 * dx * dx) - (z2 + z5 + z8) / (6 * dx * dx);
                double b = (z1 + z2 + z3 + z7 + z8 + z9) / (12 * dx * dx) - (z4 + z5 + z6) / (6 * dx * dx);
                double c = (z3 + z7 - z1 - z9) / (4 * dx * dx);
                double d = (z3 + z6 + z9 - z1 - z4 - z7) / (6 * dx);
                double e = (z1 + z2 + z3 - z7 - z8 - z9) / (6 * dx);
                double km = (a * (1 + e * e) + b * (1 + d * d) - c * d * e) / Math.Pow(1 + d * d + e * e, 1.5);
                double kg = (4 * a * b - c * c) / Math.Pow(1 + d * d + e * e, 2);
                double root = Math.Sqrt(Math.Max(0, km * km - kg));
                double rootPN = Math.Sqrt((a - b) * (a - b) + c * c);
                res[i, j] = type switch
                {
                    CurvatureType.MostPositive => (a + b) + rootPN,
                    CurvatureType.MostNegative => (a + b) - rootPN,
                    CurvatureType.Mean => km,
                    CurvatureType.Gaussian => kg,
                    CurvatureType.Maximum => km + root,
                    CurvatureType.Minimum => km - root,
                    CurvatureType.DipAngle => Math.Atan(Math.Sqrt(d * d + e * e)) * 180 / Math.PI,
                    _ => (Math.Atan2(-e, -d) * 180 / Math.PI + 360) % 360
                };
            }
        return res;
    }
}
