using GeoVisionPro.Core.Primitives;
using GeoVisionPro.Core.Spatial;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;

namespace GeoVisionPro.Seismic.Segy;

/// <summary>Result of scanning all trace headers of a SEG-Y file to reconstruct survey geometry.</summary>
public sealed class SegyScanResult
{
    public required SegyKeyLocations Keys { get; init; }
    public required SurveyGeometry Geometry { get; init; }
    public required SeismicKind Kind { get; init; }
    /// <summary>Maximum number of traces per (inline, crossline) bin (1 for post-stack).</summary>
    public required int Fold { get; init; }
    /// <summary>Offsets (header units) of gather traces, ordered; empty for post-stack.</summary>
    public required double[] Offsets { get; init; }
    /// <summary>Trace index lookup [inlineIdx * nXl * Fold + xlIdx * Fold + k] -&gt; file trace number, -1 if absent.</summary>
    public required long[] TraceIndex { get; init; }
    public required long TraceCount { get; init; }
    public required int MissingBins { get; init; }
    public required int DuplicateTraces { get; init; }
    public required bool HasCoordinates { get; init; }
    public required ValidationReport Validation { get; init; }

    public long Lookup(int inlineIndex, int crosslineIndex, int k = 0)
    {
        int nx = Geometry.CrosslineCount;
        if (inlineIndex < 0 || crosslineIndex < 0 || inlineIndex >= Geometry.InlineCount || crosslineIndex >= nx || k < 0 || k >= Fold) return -1;
        return TraceIndex[((long)inlineIndex * nx + crosslineIndex) * Fold + k];
    }
}

/// <summary>
/// Reconstructs regular 2D/3D, post-stack or pre-stack geometry from trace headers, with automatic
/// detection of inline/crossline byte locations.
/// </summary>
public static class SegyScanner
{
    public static SegyKeyLocations DetectKeyLocations(SegyFile file)
    {
        long n = file.TraceCount;
        int sample = (int)Math.Min(n, 4000);
        SegyKeyLocations best = SegyKeyLocations.Rev1;
        double bestScore = double.MinValue;
        foreach (var cand in SegyKeyLocations.Candidates)
        {
            var ils = new HashSet<int>(); var xls = new HashSet<int>(); var bins = new HashSet<long>();
            bool bad = false;
            for (int s = 0; s < sample; s++)
            {
                long t = sample == n ? s : (long)(s * (double)(n - 1) / Math.Max(1, sample - 1));
                int il, xl;
                try { il = file.ReadHeaderInt(t, cand.InlineByte, cand.InlineSize); xl = file.ReadHeaderInt(t, cand.CrosslineByte, cand.CrosslineSize); }
                catch { bad = true; break; }
                if (il < 0 || xl < 0 || il > 10_000_000 || xl > 10_000_000) { bad = true; break; }
                ils.Add(il); xls.Add(xl); bins.Add(((long)il << 32) | (uint)xl);
            }
            if (bad || bins.Count == 0) continue;
            // A good key pair has many distinct bins relative to sampled traces and both axes varying,
            // or (2D) a constant line with a varying CDP.
            double distinctRatio = bins.Count / (double)sample;
            double grid = (double)ils.Count * xls.Count;
            double fill = grid > 0 ? bins.Count / grid : 0;
            double score = distinctRatio * 2 + (ils.Count > 1 && xls.Count > 1 ? fill : 0) + (xls.Count > 1 ? 0.25 : 0);
            if (ils.Count == 1 && ils.Contains(0) && xls.Count <= 1) score -= 5;
            if (score > bestScore + 1e-9) { bestScore = score; best = cand; }
        }
        return best;
    }

    public static SegyScanResult Scan(SegyFile file, SegyKeyLocations? keys = null, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var report = new ValidationReport();
        foreach (var w in file.Warnings) report.Warn(w);
        keys ??= DetectKeyLocations(file);
        long n = file.TraceCount;
        if (n == 0) throw new InvalidDataException("The SEG-Y file contains no traces.");

        var il = new int[n]; var xl = new int[n]; var off = new int[n];
        int irregularNs = 0;
        var coordSamples = new List<(double Il, double Xl, double X, double Y)>();
        long coordStride = Math.Max(1, n / 2000);
        for (long t = 0; t < n; t++)
        {
            if ((t & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(ProgressInfo.Of(t, n, $"Scanning trace headers {t:N0}/{n:N0}"));
            }
            var k = file.ReadKeys(t, keys);
            il[t] = k.Inline; xl[t] = k.Crossline; off[t] = k.Offset;
            if (k.SampleCount != 0 && k.SampleCount != file.SamplesPerTrace) irregularNs++;
            if (t % coordStride == 0 && (k.X != 0 || k.Y != 0)) coordSamples.Add((k.Inline, k.Crossline, k.X, k.Y));
        }
        if (irregularNs > 0) report.Warn($"{irregularNs:N0} traces declare a sample count different from {file.SamplesPerTrace}; fixed-length traces are assumed.");

        bool is2D = il.Distinct().Take(2).Count() == 1;
        var ilRange = BuildRange(il);
        var xlRange = BuildRange(xl);
        int nIl = ilRange.Count, nXl = xlRange.Count;
        if ((long)nIl * nXl > 400_000_000L)
            throw new InvalidDataException($"Implausible geometry {nIl}×{nXl}; check inline/crossline byte locations ({keys}).");

        // fold per bin
        var foldCount = new int[nIl * nXl];
        for (long t = 0; t < n; t++) foldCount[ilRange.IndexOf(il[t]) * nXl + xlRange.IndexOf(xl[t])]++;
        int fold = foldCount.Max();
        int missing = foldCount.Count(c => c == 0);

        var offsets = Array.Empty<double>();
        SeismicKind kind = is2D ? SeismicKind.PostStack2D : SeismicKind.PostStack3D;
        int duplicates = 0;
        long[] index = new long[(long)nIl * nXl * fold];
        Array.Fill(index, -1L);
        if (fold == 1)
        {
            for (long t = 0; t < n; t++) index[ilRange.IndexOf(il[t]) * nXl + xlRange.IndexOf(xl[t])] = t;
        }
        else
        {
            var distinctOffsets = off.Distinct().OrderBy(o => o).ToArray();
            bool offsetsConsistent = distinctOffsets.Length == fold;
            if (offsetsConsistent && distinctOffsets.Length > 1)
            {
                kind = SeismicKind.PreStackGathers;
                offsets = distinctOffsets.Select(o => (double)o).ToArray();
                var offIdx = distinctOffsets.Select((o, i) => (o, i)).ToDictionary(p => p.o, p => p.i);
                for (long t = 0; t < n; t++)
                {
                    long slot = ((long)ilRange.IndexOf(il[t]) * nXl + xlRange.IndexOf(xl[t])) * fold + offIdx[off[t]];
                    if (index[slot] >= 0) duplicates++;
                    index[slot] = t;
                }
            }
            else
            {
                // Gathers whose offsets vary between bins (or duplicated post-stack traces): order by position within bin.
                kind = SeismicKind.PreStackGathers;
                var fill = new int[nIl * nXl];
                for (long t = 0; t < n; t++)
                {
                    int bin = ilRange.IndexOf(il[t]) * nXl + xlRange.IndexOf(xl[t]);
                    index[(long)bin * fold + fill[bin]++] = t;
                }
                offsets = Enumerable.Range(0, fold).Select(i => (double)i).ToArray();
                report.Warn($"Bins contain up to {fold} traces with inconsistent offsets; gather traces are ordered by file position.");
            }
        }
        if (duplicates > 0) report.Warn($"{duplicates:N0} duplicate traces (same inline, crossline and offset) - last occurrence kept.");
        if (missing > 0) report.Info($"{missing:N0} of {nIl * nXl:N0} bins have no trace (irregular survey outline); they are treated as dead traces.");

        GridTransform transform;
        bool hasCoords = coordSamples.Count >= 3 && coordSamples.Select(c => (c.Il, c.Xl)).Distinct().Count() >= 3;
        if (hasCoords && !is2D)
        {
            try { transform = GridTransform.FromControlPoints(coordSamples); }
            catch { transform = DefaultTransform(ilRange, xlRange); hasCoords = false; report.Warn("Coordinates are collinear; using a nominal 25 m bin grid."); }
        }
        else if (hasCoords && is2D)
        {
            // 2D: X/Y along CDP; build transform from first/last coordinate pair (inline axis perpendicular).
            var a = coordSamples.First(); var b = coordSamples.Last();
            double dxl = b.Xl - a.Xl;
            double ax = dxl != 0 ? (b.X - a.X) / dxl : 25, ay = dxl != 0 ? (b.Y - a.Y) / dxl : 0;
            transform = new GridTransform(a.Il, a.Xl, a.X, a.Y, -ay, ax, ax, ay);
        }
        else
        {
            transform = DefaultTransform(ilRange, xlRange);
            report.Warn("No CDP coordinates found in trace headers; using a nominal 25 m bin grid. Configure the coordinate system before integrating wells.");
        }

        var geometry = new SurveyGeometry(ilRange, xlRange, file.SamplesPerTrace, file.SampleInterval, file.StartTime, transform, DomainType.Time);
        report.Info($"Geometry: inlines {ilRange}, crosslines {xlRange}, {file.SamplesPerTrace} samples @ {file.SampleInterval * 1000:0.###} ms, fold {fold}, keys {keys}.");
        progress?.Report(new ProgressInfo(1, "Scan complete"));
        return new SegyScanResult
        {
            Keys = keys, Geometry = geometry, Kind = kind, Fold = fold, Offsets = offsets, TraceIndex = index, TraceCount = n,
            MissingBins = missing, DuplicateTraces = duplicates, HasCoordinates = hasCoords, Validation = report
        };
    }

    private static GridTransform DefaultTransform(IndexRange il, IndexRange xl) =>
        new(il.First, xl.First, 0, 0, 0, 25.0 / Math.Max(1, xl.Step), 25.0 / Math.Max(1, il.Step), 0);

    private static IndexRange BuildRange(int[] values)
    {
        var u = values.Distinct().OrderBy(v => v).ToArray();
        if (u.Length == 1) return new IndexRange(u[0], u[0], 1);
        int g = 0;
        for (int i = 1; i < u.Length; i++) g = Gcd(g, u[i] - u[i - 1]);
        if (g <= 0) g = 1;
        return new IndexRange(u[0], u[^1], g);
    }

    private static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }
}
