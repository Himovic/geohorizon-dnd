using GeoVisionPro.Checkshot;
using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Domain;
using GeoVisionPro.Seismic.Volumes;
using GeoVisionPro.WellLog;

namespace GeoVisionPro.WellTie;

/// <summary>A time-to-time mapping knot for stretch and squeeze: synthetic time → tied time (seconds).</summary>
public sealed record StretchKnot(double OriginalTime, double ShiftedTime);

/// <summary>Correlation statistics of a tie.</summary>
public sealed record TieQuality(double Correlation, double BestLagCorrelation, double BestLag, double NormalizedRmsError, string Rating)
{
    public static string Rate(double r) => r switch { >= 0.8 => "Excellent", >= 0.65 => "Good", >= 0.5 => "Fair", _ => "Poor" };
}

/// <summary>All parameters of a well tie; serialisable to the project database.</summary>
public sealed record WellTieParameters
{
    public double BulkShift { get; init; }          // s, added to synthetic times
    public double PhaseRotation { get; init; }      // degrees applied to the wavelet
    public bool ReversePolarity { get; init; }
    public double WindowStart { get; init; } = double.NaN;  // s, correlation window
    public double WindowEnd { get; init; } = double.NaN;
    public int TraceRadius { get; init; } = 1;      // neighbourhood of seismic traces averaged at the well
    public List<StretchKnot> Knots { get; init; } = [];
}

/// <summary>Interactive well-to-seismic tie: maintains the inputs, recomputes the synthetic and quality metrics after each adjustment.</summary>
public sealed class WellTieSession
{
    public Well Well { get; }
    public WellTrajectory Trajectory { get; }
    public LogCurve Sonic { get; }
    public LogCurve Density { get; }
    public ISeismicVolume Seismic { get; }
    public TimeDepthModel InitialTdr { get; }
    public Wavelet Wavelet { get; set; }
    public WellTieParameters Parameters { get; set; } = new();

    public SyntheticResult? Synthetic { get; private set; }
    public float[] SeismicTrace { get; private set; } = [];
    public float[] TiedSynthetic { get; private set; } = [];
    public TimeDepthModel TiedTdr { get; private set; }
    public TieQuality? Quality { get; private set; }

    public WellTieSession(Well well, LogCurve sonic, LogCurve density, ISeismicVolume seismic, TimeDepthModel tdr, Wavelet wavelet)
    {
        Well = well; Sonic = sonic; Density = density; Seismic = seismic; InitialTdr = tdr; Wavelet = wavelet;
        Trajectory = WellTrajectory.Compute(well);
        TiedTdr = tdr;
    }

    public double SampleInterval => Seismic.Geometry.SampleInterval;

    /// <summary>Maps an original synthetic time to the tied time (bulk shift + piecewise-linear stretch/squeeze).</summary>
    public double MapTime(double t)
    {
        double s = t + Parameters.BulkShift;
        var k = Parameters.Knots.OrderBy(x => x.OriginalTime).ToList();
        if (k.Count == 0) return s;
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var kn in k) { xs.Add(kn.OriginalTime + Parameters.BulkShift); ys.Add(kn.ShiftedTime); }
        if (xs.Count == 1) return s + (ys[0] - xs[0]);
        if (s <= xs[0]) return s + (ys[0] - xs[0]);
        if (s >= xs[^1]) return s + (ys[^1] - xs[^1]);
        return Dsp.Interp(xs.ToArray(), ys.ToArray(), s);
    }

    public void Compute()
    {
        double dt = SampleInterval;
        var logs = SyntheticSeismogram.ToTime(Sonic, Density, InitialTdr, Trajectory, dt);
        var w = Parameters.PhaseRotation != 0 ? Wavelet.RotatePhase(Parameters.PhaseRotation) : Wavelet;
        Synthetic = SyntheticSeismogram.Generate(logs, w, Parameters.ReversePolarity);

        // tied TDR: apply time mapping to the original relation
        var z = InitialTdr.TvdSs;
        var t = InitialTdr.Twt.Select(MapTime).ToArray();
        TiedTdr = new TimeDepthModel(z, t, InitialTdr.SrdElevation, "Well tie calibrated");

        // tied synthetic resampled on the seismic time grid
        int n = logs.Count;
        double tStart = MapTime(logs.StartTime), tEnd = MapTime(logs.TimeAt(n - 1));
        double g0 = Seismic.Geometry.StartTime;
        int k0 = (int)Math.Ceiling((tStart - g0) / dt), k1 = (int)Math.Floor((tEnd - g0) / dt);
        int m = Math.Max(0, k1 - k0 + 1);
        TiedStartTime = g0 + k0 * dt;
        var origTimes = Enumerable.Range(0, n).Select(i => MapTime(logs.TimeAt(i))).ToArray();
        TiedSynthetic = new float[m];
        TiedImpedance = new float[m];
        TiedReflectivity = new float[m];
        var sampleIdx = Enumerable.Range(0, n).Select(x => (double)x).ToArray();
        for (int i = 0; i < m; i++)
        {
            double tt = TiedStartTime + i * dt;
            double idx = Dsp.Interp(origTimes, sampleIdx, tt, clamp: true);
            TiedSynthetic[i] = Dsp.SampleSinc(Synthetic.Synthetic, idx);
            TiedImpedance[i] = Dsp.SampleLinear(logs.Impedance, idx);
            TiedReflectivity[i] = Dsp.SampleLinear(Synthetic.Reflectivity, idx);
        }
        SeismicTrace = SyntheticSeismogram.ExtractTraceAlongWell(Seismic, Trajectory, TiedTdr, TiedStartTime, dt, m, Parameters.TraceRadius);
        Quality = Evaluate();
    }

    public double TiedStartTime { get; private set; }
    public float[] TiedImpedance { get; private set; } = [];
    public float[] TiedReflectivity { get; private set; } = [];

    private (int, int) WindowIndices()
    {
        int n = TiedSynthetic.Length;
        double dt = SampleInterval;
        int a = double.IsNaN(Parameters.WindowStart) ? 0 : Math.Clamp((int)Math.Round((Parameters.WindowStart - TiedStartTime) / dt), 0, n - 1);
        int b = double.IsNaN(Parameters.WindowEnd) ? n - 1 : Math.Clamp((int)Math.Round((Parameters.WindowEnd - TiedStartTime) / dt), a, n - 1);
        return (a, b);
    }

    private TieQuality Evaluate()
    {
        var (a, b) = WindowIndices();
        if (b - a < 4) return new TieQuality(double.NaN, double.NaN, 0, double.NaN, "Insufficient overlap");
        var syn = TiedSynthetic.AsSpan(a, b - a + 1);
        var sei = SeismicTrace.AsSpan(a, b - a + 1);
        double r = Dsp.Correlation(syn, sei);
        int maxLag = (int)Math.Round(0.05 / SampleInterval);
        var xc = Dsp.CrossCorrelate(syn, sei, maxLag);
        double ss = 0, ee = 0;
        foreach (var v in syn) ss += v * (double)v;
        foreach (var v in sei) if (!float.IsNaN(v)) ee += v * (double)v;
        int best = 0; double bestV = double.MinValue;
        for (int i = 0; i < xc.Length; i++) if (xc[i] > bestV) { bestV = xc[i]; best = i; }
        double bestCorr = ss > 0 && ee > 0 ? bestV / Math.Sqrt(ss * ee) : double.NaN;
        // amplitude-normalised RMS error
        double sa = Math.Sqrt(ss / syn.Length), sb = Math.Sqrt(ee / sei.Length), err = 0; int cnt = 0;
        for (int i = 0; i < syn.Length; i++)
        {
            if (float.IsNaN(sei[i]) || sa == 0 || sb == 0) continue;
            err += Math.Pow(syn[i] / sa - sei[i] / sb, 2); cnt++;
        }
        return new TieQuality(r, bestCorr, (best - maxLag) * SampleInterval, cnt > 0 ? Math.Sqrt(err / cnt) : double.NaN, TieQuality.Rate(r));
    }

    /// <summary>Finds the bulk shift (within ±maxShift) maximising correlation and applies it.</summary>
    public double OptimizeBulkShift(double maxShift = 0.1)
    {
        Parameters = Parameters with { Knots = [] };
        double best = Parameters.BulkShift, bestR = double.MinValue;
        double start = Parameters.BulkShift;
        for (double s = start - maxShift; s <= start + maxShift + 1e-9; s += SampleInterval)
        {
            Parameters = Parameters with { BulkShift = s };
            Compute();
            if (Quality!.Correlation > bestR) { bestR = Quality.Correlation; best = s; }
        }
        Parameters = Parameters with { BulkShift = best };
        Compute();
        return best;
    }

    /// <summary>Finds the constant wavelet phase rotation maximising correlation (5° search then 1° refinement).</summary>
    public double OptimizePhase()
    {
        double best = 0, bestR = double.MinValue;
        void Try(double ph)
        {
            Parameters = Parameters with { PhaseRotation = ph };
            Compute();
            if (Quality!.Correlation > bestR) { bestR = Quality.Correlation; best = ph; }
        }
        for (int ph = -180; ph < 180; ph += 5) Try(ph);
        double coarse = best;
        for (double ph = coarse - 4; ph <= coarse + 4; ph += 1) Try(ph);
        Parameters = Parameters with { PhaseRotation = best };
        Compute();
        return best;
    }

    /// <summary>
    /// Automatic stretch and squeeze: local cross-correlation in sliding windows determines local time shifts at
    /// regularly spaced knots; shifts are limited to ±maxShift and constrained so the mapping stays monotonic
    /// with bounded strain (|Δshift/Δt| ≤ maxStrain).
    /// </summary>
    public void AutoStretchSqueeze(double knotSpacing = 0.1, double window = 0.15, double maxShift = 0.012, double maxStrain = 0.1)
    {
        Parameters = Parameters with { Knots = [] };
        Compute();
        double dt = SampleInterval;
        int n = TiedSynthetic.Length;
        int hw = (int)Math.Round(window / 2 / dt), maxLag = Math.Max(1, (int)Math.Round(maxShift / dt));
        var knots = new List<StretchKnot>();
        double lastShift = 0, lastT = double.NaN;
        for (double tc = TiedStartTime + window / 2; tc <= TiedStartTime + (n - 1) * dt - window / 2; tc += knotSpacing)
        {
            int c = (int)Math.Round((tc - TiedStartTime) / dt);
            int a = Math.Max(0, c - hw), b = Math.Min(n - 1, c + hw);
            var syn = TiedSynthetic.AsSpan(a, b - a + 1);
            var sei = SeismicTrace.AsSpan(a, b - a + 1).ToArray();
            for (int i = 0; i < sei.Length; i++) if (float.IsNaN(sei[i])) sei[i] = 0;
            var xc = Dsp.CrossCorrelate(syn, sei, maxLag);
            int best = Array.IndexOf(xc, xc.Max());
            double shift = (best - maxLag) * dt;
            if (!double.IsNaN(lastT))
            {
                double lim = maxStrain * (tc - lastT);
                shift = Math.Clamp(shift, lastShift - lim, lastShift + lim);
            }
            // tied time tc corresponds to original synthetic time (before mapping) = tc - BulkShift
            knots.Add(new StretchKnot(tc - Parameters.BulkShift, tc + shift));
            lastShift = shift; lastT = tc;
        }
        Parameters = Parameters with { Knots = knots };
        Compute();
    }

    public WellTieRecord ToRecord(Guid seismicId, Guid? waveletId) => new()
    {
        Name = $"Tie {Well.Name}", WellId = Well.Id, SeismicId = seismicId, WaveletId = waveletId,
        BulkShift = Parameters.BulkShift, PhaseRotation = Parameters.PhaseRotation, ReversePolarity = Parameters.ReversePolarity,
        Correlation = Quality?.Correlation ?? double.NaN,
        ParametersJson = System.Text.Json.JsonSerializer.Serialize(Parameters, JsonOptions)
    };

    public static WellTieParameters ParametersFromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<WellTieParameters>(json, JsonOptions) ?? new WellTieParameters();

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
}
