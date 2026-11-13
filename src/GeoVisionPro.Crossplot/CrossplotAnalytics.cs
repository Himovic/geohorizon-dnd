using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Spatial;

namespace GeoVisionPro.Crossplot;

/// <summary>Point cloud for crossplotting: X, Y, optional colour (Z) and source/group labels.</summary>
public sealed class CrossplotData
{
    public double[] X { get; }
    public double[] Y { get; }
    public double[]? Z { get; }
    public string[]? Source { get; }
    public double[]? Depth { get; }
    public string XName { get; init; } = "X";
    public string YName { get; init; } = "Y";
    public string? ZName { get; init; }

    public CrossplotData(double[] x, double[] y, double[]? z = null, string[]? source = null, double[]? depth = null)
    {
        if (x.Length != y.Length) throw new ArgumentException("X and Y must have equal length.");
        X = x; Y = y; Z = z; Source = source; Depth = depth;
    }

    public int Count => X.Length;

    /// <summary>Indices of points with finite X and Y (and Z when present).</summary>
    public int[] ValidIndices() => Enumerable.Range(0, Count).Where(i => double.IsFinite(X[i]) && double.IsFinite(Y[i]) && (Z is null || double.IsFinite(Z[i]))).ToArray();

    public CrossplotData Subset(IReadOnlyList<int> idx) => new(idx.Select(i => X[i]).ToArray(), idx.Select(i => Y[i]).ToArray(),
        Z is null ? null : idx.Select(i => Z[i]).ToArray(), Source is null ? null : idx.Select(i => Source[i]).ToArray(),
        Depth is null ? null : idx.Select(i => Depth[i]).ToArray()) { XName = XName, YName = YName, ZName = ZName };
}

public sealed record CrossplotStatistics(StatisticsSummary X, StatisticsSummary Y, double Pearson, double Spearman, LinearFit Fit);

public static class CrossplotAnalytics
{
    public static CrossplotStatistics Statistics(CrossplotData d, IReadOnlyList<int>? subset = null)
    {
        var idx = (subset ?? d.ValidIndices()).Where(i => double.IsFinite(d.X[i]) && double.IsFinite(d.Y[i])).ToArray();
        var x = idx.Select(i => d.X[i]).ToArray(); var y = idx.Select(i => d.Y[i]).ToArray();
        return new CrossplotStatistics(Core.Numerics.Statistics.Summarize(x), Core.Numerics.Statistics.Summarize(y),
            Core.Numerics.Statistics.PearsonCorrelation(x, y), Core.Numerics.Statistics.SpearmanCorrelation(x, y),
            Core.Numerics.Statistics.LinearRegression(x, y));
    }

    /// <summary>Indices of points inside a polygon in data coordinates.</summary>
    public static int[] SelectInPolygon(CrossplotData d, IReadOnlyList<Point2D> polygon) =>
        d.ValidIndices().Where(i => Polygon2D.Contains(polygon, new Point2D(d.X[i], d.Y[i]))).ToArray();

    /// <summary>Indices of points inside a rectangle.</summary>
    public static int[] SelectInRectangle(CrossplotData d, double x0, double x1, double y0, double y1) =>
        d.ValidIndices().Where(i => d.X[i] >= Math.Min(x0, x1) && d.X[i] <= Math.Max(x0, x1) && d.Y[i] >= Math.Min(y0, y1) && d.Y[i] <= Math.Max(y0, y1)).ToArray();

    /// <summary>Filter by value range on the colour attribute or depth.</summary>
    public static int[] Filter(CrossplotData d, double? zMin = null, double? zMax = null, double? depthMin = null, double? depthMax = null) =>
        d.ValidIndices().Where(i =>
            (zMin is null || d.Z is null || d.Z[i] >= zMin) && (zMax is null || d.Z is null || d.Z[i] <= zMax) &&
            (depthMin is null || d.Depth is null || d.Depth[i] >= depthMin) && (depthMax is null || d.Depth is null || d.Depth[i] <= depthMax)).ToArray();

    /// <summary>Polynomial trend y = Σ c_k x^k fitted to the given points.</summary>
    public static double[] PolynomialTrend(CrossplotData d, int degree, IReadOnlyList<int>? subset = null)
    {
        var idx = subset ?? d.ValidIndices();
        return Core.Numerics.Statistics.PolynomialFit(idx.Select(i => d.X[i]).ToArray(), idx.Select(i => d.Y[i]).ToArray(), degree);
    }
}

/// <summary>k-means clustering with k-means++ initialisation on standardised features.</summary>
public static class KMeans
{
    public sealed record Result(int[] Labels, double[][] Centroids, double Inertia, int Iterations);

    public static Result Cluster(double[][] features, int k, int maxIterations = 100, int seed = 12345)
    {
        int n = features.Length;
        if (n == 0 || k <= 0) return new Result([], [], 0, 0);
        int dim = features[0].Length;
        var (std, mean, sd) = Standardize(features);
        var rng = new Random(seed);
        var centroids = new List<double[]> { (double[])std[rng.Next(n)].Clone() };
        var d2 = new double[n];
        while (centroids.Count < Math.Min(k, n))
        {
            double sum = 0;
            for (int i = 0; i < n; i++) { d2[i] = centroids.Min(c => Dist2(std[i], c)); sum += d2[i]; }
            double r = rng.NextDouble() * sum, acc = 0;
            int pick = n - 1;
            for (int i = 0; i < n; i++) { acc += d2[i]; if (acc >= r) { pick = i; break; } }
            centroids.Add((double[])std[pick].Clone());
        }
        var labels = new int[n];
        int it;
        double inertia = 0;
        for (it = 0; it < maxIterations; it++)
        {
            bool changed = false;
            inertia = 0;
            for (int i = 0; i < n; i++)
            {
                int best = 0; double bd = double.MaxValue;
                for (int c = 0; c < centroids.Count; c++) { double dd = Dist2(std[i], centroids[c]); if (dd < bd) { bd = dd; best = c; } }
                if (labels[i] != best || it == 0) { changed |= labels[i] != best; labels[i] = best; }
                inertia += bd;
            }
            var sums = centroids.Select(_ => new double[dim]).ToArray();
            var counts = new int[centroids.Count];
            for (int i = 0; i < n; i++) { counts[labels[i]]++; for (int j = 0; j < dim; j++) sums[labels[i]][j] += std[i][j]; }
            for (int c = 0; c < centroids.Count; c++) if (counts[c] > 0) for (int j = 0; j < dim; j++) centroids[c][j] = sums[c][j] / counts[c];
            if (!changed && it > 0) break;
        }
        // de-standardise centroids
        var cent = centroids.Select(c => c.Select((v, j) => v * sd[j] + mean[j]).ToArray()).ToArray();
        return new Result(labels, cent, inertia, it + 1);
    }

    internal static (double[][] Std, double[] Mean, double[] Sd) Standardize(double[][] f)
    {
        int n = f.Length, dim = f[0].Length;
        var mean = new double[dim]; var sd = new double[dim];
        for (int j = 0; j < dim; j++)
        {
            mean[j] = f.Average(r => r[j]);
            sd[j] = Math.Sqrt(f.Sum(r => (r[j] - mean[j]) * (r[j] - mean[j])) / Math.Max(1, n - 1));
            if (sd[j] == 0) sd[j] = 1;
        }
        return (f.Select(r => r.Select((v, j) => (v - mean[j]) / sd[j]).ToArray()).ToArray(), mean, sd);
    }

    private static double Dist2(double[] a, double[] b) { double s = 0; for (int i = 0; i < a.Length; i++) s += (a[i] - b[i]) * (a[i] - b[i]); return s; }
}

/// <summary>
/// Supervised facies classification with multivariate Gaussian class-conditional densities (quadratic discriminant
/// analysis) and class priors from training frequencies. Posterior probabilities quantify classification uncertainty.
/// </summary>
public sealed class GaussianFaciesClassifier
{
    private readonly List<(string Label, double[] Mean, double[,] InvCov, double LogDet, double LogPrior)> _classes = [];
    public IReadOnlyList<string> Labels => _classes.Select(c => c.Label).ToList();

    public static GaussianFaciesClassifier Train(IReadOnlyList<double[]> features, IReadOnlyList<string> labels, double regularization = 1e-6)
    {
        var clf = new GaussianFaciesClassifier();
        int dim = features[0].Length;
        int n = features.Count;
        foreach (var grp in Enumerable.Range(0, n).GroupBy(i => labels[i]))
        {
            var rows = grp.Select(i => features[i]).ToList();
            if (rows.Count < dim + 1) throw new InvalidOperationException($"Facies '{grp.Key}' needs at least {dim + 1} training samples.");
            var mean = new double[dim];
            foreach (var r in rows) for (int j = 0; j < dim; j++) mean[j] += r[j] / rows.Count;
            var cov = new double[dim, dim];
            foreach (var r in rows) for (int a = 0; a < dim; a++) for (int b = 0; b < dim; b++) cov[a, b] += (r[a] - mean[a]) * (r[b] - mean[b]) / (rows.Count - 1);
            double tr = 0; for (int a = 0; a < dim; a++) tr += cov[a, a];
            for (int a = 0; a < dim; a++) cov[a, a] += regularization * Math.Max(tr / dim, 1e-12);
            var m = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.DenseOfArray(cov);
            clf._classes.Add((grp.Key, mean, m.Inverse().ToArray(), Math.Log(m.Determinant()), Math.Log(rows.Count / (double)n)));
        }
        return clf;
    }

    /// <summary>Returns the most probable facies and the posterior probability of each class.</summary>
    public (string Label, double[] Posterior) Classify(double[] x)
    {
        var logp = new double[_classes.Count];
        for (int c = 0; c < _classes.Count; c++)
        {
            var (_, mean, inv, logDet, prior) = _classes[c];
            int dim = mean.Length;
            double q = 0;
            for (int a = 0; a < dim; a++) for (int b = 0; b < dim; b++) q += (x[a] - mean[a]) * inv[a, b] * (x[b] - mean[b]);
            logp[c] = prior - 0.5 * logDet - 0.5 * q;
        }
        double mx = logp.Max();
        var post = logp.Select(v => Math.Exp(v - mx)).ToArray();
        double s = post.Sum();
        for (int c = 0; c < post.Length; c++) post[c] /= s;
        int best = Array.IndexOf(post, post.Max());
        return (_classes[best].Label, post);
    }
}
