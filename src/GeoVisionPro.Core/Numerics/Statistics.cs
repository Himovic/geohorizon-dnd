namespace GeoVisionPro.Core.Numerics;

/// <summary>Descriptive statistics summary for a data series.</summary>
public sealed record StatisticsSummary(int Count, int MissingCount, double Min, double Max, double Mean, double StdDev,
    double P10, double P50, double P90)
{
    public override string ToString() =>
        $"N={Count} (missing {MissingCount}) min={Min:G5} max={Max:G5} mean={Mean:G5} sd={StdDev:G5} P10={P10:G5} P50={P50:G5} P90={P90:G5}";
}

/// <summary>Result of an ordinary least squares fit y = Intercept + Slope * x.</summary>
public sealed record LinearFit(double Intercept, double Slope, double R, double RSquared, double StdError, int Count)
{
    public double Evaluate(double x) => Intercept + Slope * x;
}

public static class Statistics
{
    public static StatisticsSummary Summarize(IEnumerable<double> values)
    {
        var list = new List<double>();
        int missing = 0;
        foreach (var v in values)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) missing++;
            else list.Add(v);
        }
        if (list.Count == 0) return new StatisticsSummary(0, missing, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        list.Sort();
        double mean = list.Average();
        double var = list.Count > 1 ? list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1) : 0;
        return new StatisticsSummary(list.Count, missing, list[0], list[^1], mean, Math.Sqrt(var),
            PercentileSorted(list, 10), PercentileSorted(list, 50), PercentileSorted(list, 90));
    }

    public static StatisticsSummary Summarize(IEnumerable<float> values) => Summarize(values.Select(v => (double)v));

    /// <summary>Percentile with linear interpolation between order statistics (p in [0,100]).</summary>
    public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        double pos = Math.Clamp(p, 0, 100) / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var s = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        return PercentileSorted(s, p);
    }

    /// <summary>Ordinary least squares regression, skipping pairs with NaN.</summary>
    public static LinearFit LinearRegression(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        double sx = 0, sy = 0; int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(x[i]) || double.IsNaN(y[i])) continue;
            sx += x[i]; sy += y[i]; m++;
        }
        if (m < 2) return new LinearFit(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, m);
        double mx = sx / m, my = sy / m, sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            if (double.IsNaN(x[i]) || double.IsNaN(y[i])) continue;
            double dx = x[i] - mx, dy = y[i] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        double slope = sxx > 0 ? sxy / sxx : double.NaN;
        double intercept = my - slope * mx;
        double r = sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
        double sse = Math.Max(0, syy - slope * sxy);
        double se = m > 2 ? Math.Sqrt(sse / (m - 2)) : 0;
        return new LinearFit(intercept, slope, r, r * r, se, m);
    }

    /// <summary>Least squares polynomial fit; returns coefficients c0..cdeg.</summary>
    public static double[] PolynomialFit(IReadOnlyList<double> x, IReadOnlyList<double> y, int degree)
    {
        var px = new List<double>(); var py = new List<double>();
        for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            if (!double.IsNaN(x[i]) && !double.IsNaN(y[i])) { px.Add(x[i]); py.Add(y[i]); }
        if (px.Count <= degree) throw new InvalidOperationException("Not enough points for polynomial fit.");
        return MathNet.Numerics.Fit.Polynomial(px.ToArray(), py.ToArray(), degree);
    }

    public static double PearsonCorrelation(IReadOnlyList<double> x, IReadOnlyList<double> y) => LinearRegression(x, y).R;

    /// <summary>Spearman rank correlation.</summary>
    public static double SpearmanCorrelation(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var pairs = new List<(double X, double Y)>();
        for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
            if (!double.IsNaN(x[i]) && !double.IsNaN(y[i])) pairs.Add((x[i], y[i]));
        var rx = Ranks(pairs.Select(p => p.X).ToArray());
        var ry = Ranks(pairs.Select(p => p.Y).ToArray());
        return PearsonCorrelation(rx, ry);
    }

    private static double[] Ranks(double[] v)
    {
        var idx = Enumerable.Range(0, v.Length).OrderBy(i => v[i]).ToArray();
        var ranks = new double[v.Length];
        int k = 0;
        while (k < idx.Length)
        {
            int j = k;
            while (j + 1 < idx.Length && v[idx[j + 1]] == v[idx[k]]) j++;
            double r = (k + j) / 2.0 + 1;
            for (int t = k; t <= j; t++) ranks[idx[t]] = r;
            k = j + 1;
        }
        return ranks;
    }

    /// <summary>Histogram with fixed bin count over [min,max].</summary>
    public static (double[] BinCenters, int[] Counts) Histogram(IEnumerable<double> values, int bins, double min, double max)
    {
        var counts = new int[bins];
        double w = (max - min) / bins;
        foreach (var v in values)
        {
            if (double.IsNaN(v) || w <= 0) continue;
            int b = (int)((v - min) / w);
            if (b == bins && v == max) b = bins - 1;
            if (b >= 0 && b < bins) counts[b]++;
        }
        var centers = Enumerable.Range(0, bins).Select(i => min + (i + 0.5) * w).ToArray();
        return (centers, counts);
    }
}
