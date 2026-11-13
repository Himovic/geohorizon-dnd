using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Domain;
using GeoVisionPro.Interpretation;

namespace GeoVisionPro.GeologicalModeling;

public enum LayeringStyle { Proportional, FollowTop, FollowBase }

/// <summary>A zone between two framework surfaces, subdivided into layers.</summary>
public sealed record Zone(string Name, int TopSurface, int BaseSurface, int Layers, LayeringStyle Style, double CellThickness = 0);

/// <summary>
/// Structural framework: an ordered set of surfaces (horizon grids on a common survey) plus faults. Surfaces are made
/// conformable (no crossing) by truncating each surface at the one above (erosional truncation convention).
/// </summary>
public sealed class StructuralFramework
{
    public SurveyGeometry Geometry { get; }
    public List<(string Name, Grid2D Surface)> Surfaces { get; } = [];
    public List<Fault> Faults { get; } = [];
    public List<string> Messages { get; } = [];

    public StructuralFramework(SurveyGeometry geometry) => Geometry = geometry;

    public void AddSurface(string name, Grid2D surface) => Surfaces.Add((name, surface));

    /// <summary>Sorts surfaces by mean value (shallow first), fills gaps by interpolation and enforces non-crossing.</summary>
    public void Build(double maxGapCells = 100)
    {
        Surfaces.Sort((a, b) => Mean(a.Surface).CompareTo(Mean(b.Surface)));
        for (int s = 0; s < Surfaces.Count; s++)
        {
            var filled = Gridding.Interpolate(Surfaces[s].Surface, GriddingMethod.Laplace, maxGapCells, 300);
            Surfaces[s] = (Surfaces[s].Name, filled);
        }
        for (int s = 1; s < Surfaces.Count; s++)
        {
            int crossings = 0;
            var above = Surfaces[s - 1].Surface; var cur = Surfaces[s].Surface;
            for (int k = 0; k < cur.Values.Length; k++)
                if (!double.IsNaN(above.Values[k]) && !double.IsNaN(cur.Values[k]) && cur.Values[k] < above.Values[k]) { cur.Values[k] = above.Values[k]; crossings++; }
            if (crossings > 0) Messages.Add($"{Surfaces[s].Name}: {crossings} nodes truncated at {Surfaces[s - 1].Name} (zero thickness).");
        }
    }

    private static double Mean(Grid2D g) => g.Values.Where(v => !double.IsNaN(v)).DefaultIfEmpty(double.MaxValue).Average();

    /// <summary>Isochore/isochron thickness between surfaces i and j (j below i).</summary>
    public Grid2D Thickness(int top, int bas) => Gridding.Difference(Surfaces[bas].Surface, Surfaces[top].Surface);
}

/// <summary>
/// Layer-cake depth conversion with average velocity from SRD derived at wells (V_avg = 2Z/t) and interpolated by IDW.
/// depth(x,y) = t(x,y) · V_avg(x,y) / 2 − SRD.
/// </summary>
public static class DepthConversion
{
    public static Grid2D HorizonToDepth(Horizon h, SurveyGeometry g, IReadOnlyList<(Point2D Location, double AverageVelocity)> wellVelocities, double srdElevation, double defaultVelocity = 2000)
    {
        var res = new Grid2D(h.InlineCount, h.CrosslineCount);
        for (int i = 0; i < h.InlineCount; i++)
            for (int j = 0; j < h.CrosslineCount; j++)
            {
                double t = h.Values[i * h.CrosslineCount + j];
                if (double.IsNaN(t)) continue;
                var p = g.MapLocation(h.InlineAt(i), h.CrosslineAt(j));
                double v = defaultVelocity;
                if (wellVelocities.Count > 0)
                {
                    double sw = 0, sv = 0;
                    foreach (var (loc, vel) in wellVelocities) { double w = 1 / Math.Pow(Math.Max(1, loc.DistanceTo(p)), 2); sw += w; sv += w * vel; }
                    v = sv / sw;
                }
                res[i, j] = t * v / 2 - srdElevation;
            }
        return res;
    }
}

/// <summary>
/// Stratigraphic (pillar) grid: columns aligned with survey bins (optionally decimated), layers between framework surfaces.
/// Cell k of a column spans Z[k]..Z[k+1]. Stores cell properties by name.
/// </summary>
public sealed class StratigraphicGrid
{
    public int NI { get; }
    public int NJ { get; }
    public int NK { get; }
    public int Decimation { get; }
    public SurveyGeometry Geometry { get; }
    /// <summary>Node Z values [i, j, k] for k = 0..NK (layer boundaries), NaN where undefined.</summary>
    public double[] NodeZ { get; }
    public int[] ZoneOfLayer { get; }
    public Dictionary<string, float[]> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    private StratigraphicGrid(SurveyGeometry g, int ni, int nj, int nk, int dec, int[] zoneOfLayer)
    {
        Geometry = g; NI = ni; NJ = nj; NK = nk; Decimation = dec; ZoneOfLayer = zoneOfLayer;
        NodeZ = new double[ni * nj * (nk + 1)];
    }

    public double Z(int i, int j, int k) => NodeZ[(i * NJ + j) * (NK + 1) + k];
    public int CellIndex(int i, int j, int k) => (i * NJ + j) * NK + k;
    public int CellCount => NI * NJ * NK;
    public (int Inline, int Crossline) ColumnLocation(int i, int j) =>
        (Geometry.Inlines.ValueAt(Math.Min(i * Decimation, Geometry.InlineCount - 1)), Geometry.Crosslines.ValueAt(Math.Min(j * Decimation, Geometry.CrosslineCount - 1)));

    public static StratigraphicGrid Build(StructuralFramework fw, IReadOnlyList<Zone> zones, int decimation = 1)
    {
        var g = fw.Geometry;
        int ni = (g.InlineCount + decimation - 1) / decimation, nj = (g.CrosslineCount + decimation - 1) / decimation;
        int nk = zones.Sum(z => z.Layers);
        var zoneOf = zones.SelectMany((z, zi) => Enumerable.Repeat(zi, z.Layers)).ToArray();
        var grid = new StratigraphicGrid(g, ni, nj, nk, decimation, zoneOf);
        for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
            {
                int si = Math.Min(i * decimation, g.InlineCount - 1), sj = Math.Min(j * decimation, g.CrosslineCount - 1);
                int k0 = 0;
                int baseIdx = (i * nj + j) * (nk + 1);
                foreach (var z in zones)
                {
                    double top = fw.Surfaces[z.TopSurface].Surface[si, sj], bas = fw.Surfaces[z.BaseSurface].Surface[si, sj];
                    for (int l = 0; l <= z.Layers; l++)
                    {
                        double zz;
                        if (double.IsNaN(top) || double.IsNaN(bas)) zz = double.NaN;
                        else switch (z.Style)
                        {
                            case LayeringStyle.FollowTop when z.CellThickness > 0: zz = Math.Min(bas, top + l * z.CellThickness); break;
                            case LayeringStyle.FollowBase when z.CellThickness > 0: zz = Math.Max(top, bas - (z.Layers - l) * z.CellThickness); break;
                            default: zz = top + (bas - top) * l / z.Layers; break;
                        }
                        if (l < z.Layers || ReferenceEquals(z, zones[^1])) grid.NodeZ[baseIdx + k0 + l] = zz;
                        else grid.NodeZ[baseIdx + k0 + l] = zz; // shared boundary written by next zone too
                    }
                    k0 += z.Layers;
                }
            }
        return grid;
    }

    /// <summary>Finds the layer containing z in column (i, j), or −1.</summary>
    public int LayerAt(int i, int j, double z)
    {
        for (int k = 0; k < NK; k++)
        {
            double a = Z(i, j, k), b = Z(i, j, k + 1);
            if (!double.IsNaN(a) && !double.IsNaN(b) && z >= a && z <= b && b > a) return k;
        }
        return -1;
    }

    /// <summary>Total bulk volume (thickness × bin area) of cells with a given zone, in (Z units)·m².</summary>
    public double ZoneBulkVolume(int zone, double binArea)
    {
        double v = 0;
        for (int i = 0; i < NI; i++) for (int j = 0; j < NJ; j++) for (int k = 0; k < NK; k++)
            {
                if (ZoneOfLayer[k] != zone) continue;
                double a = Z(i, j, k), b = Z(i, j, k + 1);
                if (!double.IsNaN(a) && !double.IsNaN(b)) v += (b - a) * binArea * Decimation * Decimation;
            }
        return v;
    }
}

public enum VariogramModel { Spherical, Exponential, Gaussian }

/// <summary>Isotropic variogram γ(h) with nugget, sill and range.</summary>
public sealed record Variogram(VariogramModel Model, double Nugget, double Sill, double Range)
{
    public double Gamma(double h)
    {
        if (h <= 0) return 0;
        double c = Sill - Nugget;
        return Nugget + Model switch
        {
            VariogramModel.Spherical => h >= Range ? c : c * (1.5 * h / Range - 0.5 * Math.Pow(h / Range, 3)),
            VariogramModel.Exponential => c * (1 - Math.Exp(-3 * h / Range)),
            _ => c * (1 - Math.Exp(-3 * h * h / (Range * Range)))
        };
    }
    public double Covariance(double h) => Sill - Gamma(h);
}

public enum PropertyMethod { InverseDistance, OrdinaryKriging }

/// <summary>Upscaling of well logs into grid cells and layer-by-layer property distribution.</summary>
public static class PropertyModeling
{
    /// <summary>A conditioning datum: cell (i,j,k), map location and value.</summary>
    public sealed record CellDatum(int I, int J, int K, Point2D Location, double Value);

    /// <summary>
    /// Upscales a property log sampled along a well path (map location and Z per sample) into the grid by arithmetic averaging
    /// of all samples falling in each cell.
    /// </summary>
    public static List<CellDatum> Upscale(StratigraphicGrid grid, IEnumerable<(Point2D Location, double Z, double Value)> samples)
    {
        var acc = new Dictionary<(int, int, int), (double Sum, int N, Point2D Loc)>();
        var g = grid.Geometry;
        foreach (var (loc, z, v) in samples)
        {
            if (double.IsNaN(v)) continue;
            var (fi, fx) = g.Transform.ToGrid(loc.X, loc.Y);
            int i = (int)Math.Round((fi - g.Inlines.First) / g.Inlines.Step / grid.Decimation);
            int j = (int)Math.Round((fx - g.Crosslines.First) / g.Crosslines.Step / grid.Decimation);
            if (i < 0 || j < 0 || i >= grid.NI || j >= grid.NJ) continue;
            int k = grid.LayerAt(i, j, z);
            if (k < 0) continue;
            var key = (i, j, k);
            acc[key] = acc.TryGetValue(key, out var a) ? (a.Sum + v, a.N + 1, a.Loc) : (v, 1, loc);
        }
        return acc.Select(kv => new CellDatum(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Loc, kv.Value.Sum / kv.Value.N)).ToList();
    }

    /// <summary>
    /// Distributes a property layer by layer from upscaled cells using IDW or ordinary kriging (all data of the layer;
    /// falls back to zone data when a layer has none). Returns the cell property array.
    /// </summary>
    public static float[] Distribute(StratigraphicGrid grid, IReadOnlyList<CellDatum> data, PropertyMethod method, Variogram? variogram = null,
        double idwPower = 2, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var res = new float[grid.CellCount];
        Array.Fill(res, float.NaN);
        var g = grid.Geometry;
        for (int k = 0; k < grid.NK; k++)
        {
            ct.ThrowIfCancellationRequested();
            var layerData = data.Where(d => d.K == k).ToList();
            if (layerData.Count == 0) layerData = data.Where(d => grid.ZoneOfLayer[d.K] == grid.ZoneOfLayer[k]).ToList();
            if (layerData.Count == 0) continue;
            double[,]? invK = null;
            if (method == PropertyMethod.OrdinaryKriging && variogram is not null && layerData.Count > 1)
            {
                int n = layerData.Count;
                var m = new double[n + 1, n + 1];
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++) m[a, b] = variogram.Gamma(layerData[a].Location.DistanceTo(layerData[b].Location));
                    m[a, n] = 1; m[n, a] = 1;
                }
                invK = MathNet.Numerics.LinearAlgebra.Matrix<double>.Build.DenseOfArray(m).PseudoInverse().ToArray();
            }
            Parallel.For(0, grid.NI, i =>
            {
                var rhs = new double[layerData.Count + 1];
                for (int j = 0; j < grid.NJ; j++)
                {
                    var (il, xl) = grid.ColumnLocation(i, j);
                    var p = g.MapLocation(il, xl);
                    double v;
                    if (invK is not null)
                    {
                        int n = layerData.Count;
                        for (int a = 0; a < n; a++) rhs[a] = variogram!.Gamma(layerData[a].Location.DistanceTo(p));
                        rhs[n] = 1;
                        v = 0;
                        for (int a = 0; a < n; a++)
                        {
                            double w = 0;
                            for (int b = 0; b <= n; b++) w += invK[a, b] * rhs[b];
                            v += w * layerData[a].Value;
                        }
                    }
                    else
                    {
                        double sw = 0, sv = 0;
                        foreach (var d in layerData) { double w = 1 / Math.Pow(Math.Max(1, d.Location.DistanceTo(p)), idwPower); sw += w; sv += w * d.Value; }
                        v = sv / sw;
                    }
                    res[grid.CellIndex(i, j, k)] = (float)v;
                }
            });
            progress?.Report(ProgressInfo.Of(k + 1, grid.NK, $"Layer {k + 1}/{grid.NK}"));
        }
        return res;
    }

    /// <summary>Experimental isotropic semivariogram of map data (for variogram fitting QC).</summary>
    public static List<(double Lag, double Gamma, int Pairs)> ExperimentalVariogram(IReadOnlyList<(Point2D Location, double Value)> data, double lag, int nLags)
    {
        var sums = new double[nLags]; var counts = new int[nLags];
        for (int a = 0; a < data.Count; a++)
            for (int b = a + 1; b < data.Count; b++)
            {
                double h = data[a].Location.DistanceTo(data[b].Location);
                int bin = (int)(h / lag);
                if (bin >= nLags) continue;
                sums[bin] += 0.5 * Math.Pow(data[a].Value - data[b].Value, 2); counts[bin]++;
            }
        return Enumerable.Range(0, nLags).Where(i => counts[i] > 0).Select(i => ((i + 0.5) * lag, sums[i] / counts[i], counts[i])).ToList();
    }
}
