using GeoVisionPro.Core.Numerics;
using GeoVisionPro.Core.Units;
using GeoVisionPro.Domain;

namespace GeoVisionPro.WellLog;

/// <summary>Log editing and conditioning operations. All return new curves; inputs are never modified.</summary>
public static class LogConditioning
{
    /// <summary>Replaces values outside [min,max] by NaN.</summary>
    public static LogCurve ClipToRange(LogCurve c, double min, double max)
    {
        var v = c.Values.Select(x => x < min || x > max ? float.NaN : x).ToArray();
        return c.CloneAs(c.Mnemonic + "_CLIP", v, c.Unit, c.Family, $"Clip [{min},{max}]");
    }

    /// <summary>Despiking: values deviating from the running median by more than k·MAD are replaced by the median.</summary>
    public static LogCurve Despike(LogCurve c, int window = 11, double k = 3)
    {
        var med = Dsp.MedianFilter(c.Values, window);
        var dev = c.Values.Select((x, i) => float.IsNaN(x) ? float.NaN : Math.Abs(x - med[i])).ToArray();
        var mad = Dsp.MedianFilter(dev.Select(d => float.IsNaN(d) ? 0f : d).ToArray(), window);
        var v = new float[c.Count];
        for (int i = 0; i < v.Length; i++)
        {
            double thr = k * 1.4826 * Math.Max(mad[i], 1e-12);
            v[i] = !float.IsNaN(c.Values[i]) && Math.Abs(c.Values[i] - med[i]) > thr ? med[i] : c.Values[i];
        }
        return c.CloneAs(c.Mnemonic + "_DSP", v, c.Unit, c.Family, $"Despike w={window} k={k}");
    }

    /// <summary>Moving average (NaN-aware: gaps are not bridged).</summary>
    public static LogCurve Smooth(LogCurve c, int window)
    {
        var v = new float[c.Count];
        int h = window / 2;
        for (int i = 0; i < v.Length; i++)
        {
            if (float.IsNaN(c.Values[i])) { v[i] = float.NaN; continue; }
            double s = 0; int n = 0;
            for (int j = Math.Max(0, i - h); j <= Math.Min(v.Length - 1, i + h); j++)
                if (!float.IsNaN(c.Values[j])) { s += c.Values[j]; n++; }
            v[i] = (float)(s / n);
        }
        return c.CloneAs(c.Mnemonic + "_SM", v, c.Unit, c.Family, $"Moving average {window}");
    }

    /// <summary>Linear interpolation across gaps no longer than <paramref name="maxGapSamples"/>.</summary>
    public static LogCurve FillGaps(LogCurve c, int maxGapSamples)
    {
        var v = (float[])c.Values.Clone();
        Dsp.FillGapsLinear(v, extend: false, maxGap: maxGapSamples);
        return c.CloneAs(c.Mnemonic + "_FILL", v, c.Unit, c.Family, $"Gap fill ≤{maxGapSamples} samples");
    }

    /// <summary>Converts curve units (e.g. us/ft to us/m, g/cc to kg/m3).</summary>
    public static LogCurve ConvertUnits(LogCurve c, string toUnit)
    {
        var v = (float[])c.Values.Clone();
        UnitConverter.ConvertInPlace(v, c.Unit, toUnit);
        return c.CloneAs(c.Mnemonic, v, toUnit, c.Family, $"Unit conversion {c.Unit}→{toUnit}");
    }

    /// <summary>Applies a constant depth shift (bulk depth matching).</summary>
    public static LogCurve DepthShift(LogCurve c, double shift)
    {
        var r = c.CloneAs(c.Mnemonic + "_SHF", (float[])c.Values.Clone(), c.Unit, c.Family, $"Depth shift {shift:+0.###;-0.###}");
        r.Depths = c.Depths.Select(d => d + shift).ToArray();
        return r;
    }

    /// <summary>
    /// Two-point (percentile) normalization: maps the P_low/P_high values of the curve in [top,base] to reference
    /// values (typically from a key well): y = refLow + (x - pLow)(refHigh - refLow)/(pHigh - pLow).
    /// </summary>
    public static LogCurve Normalize(LogCurve c, double refLow, double refHigh, double pctLow = 5, double pctHigh = 95, double top = double.NegativeInfinity, double bottom = double.PositiveInfinity)
    {
        var sel = c.Values.Where((v, i) => !float.IsNaN(v) && c.Depths[i] >= top && c.Depths[i] <= bottom).Select(v => (double)v).ToList();
        if (sel.Count < 10) throw new InvalidOperationException("Not enough samples in the normalization interval.");
        double pl = Statistics.Percentile(sel, pctLow), ph = Statistics.Percentile(sel, pctHigh);
        if (Math.Abs(ph - pl) < 1e-12) throw new InvalidOperationException("Curve has no dynamic range in the normalization interval.");
        var v = c.Values.Select(x => float.IsNaN(x) ? float.NaN : (float)(refLow + (x - pl) * (refHigh - refLow) / (ph - pl))).ToArray();
        return c.CloneAs(c.Mnemonic + "_NRM", v, c.Unit, c.Family, $"Normalized P{pctLow}/P{pctHigh} {pl:G4}/{ph:G4} → {refLow}/{refHigh}");
    }

    /// <summary>Resamples a curve onto a regular depth grid with linear interpolation.</summary>
    public static LogCurve Resample(LogCurve c, double start, double stop, double step)
    {
        int n = (int)Math.Floor((stop - start) / step) + 1;
        var d = new double[n]; var v = new float[n];
        for (int i = 0; i < n; i++) { d[i] = start + i * step; v[i] = (float)c.ValueAtDepth(d[i]); }
        var r = c.CloneAs(c.Mnemonic, v, c.Unit, c.Family, $"Resampled {step}");
        r.Depths = d;
        return r;
    }

    /// <summary>
    /// Depth matching of a curve to a reference by maximizing the cross-correlation over shifts within ±maxShift.
    /// Returns the best shift (depth units) and the correlation coefficient.
    /// </summary>
    public static (double Shift, double Correlation) FindDepthShift(LogCurve reference, LogCurve curve, double maxShift, double step)
    {
        double start = Math.Max(reference.Depths[0], curve.Depths[0]) + maxShift;
        double stop = Math.Min(reference.Depths[^1], curve.Depths[^1]) - maxShift;
        if (stop <= start) return (0, double.NaN);
        int n = (int)((stop - start) / step) + 1;
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = (float)reference.ValueAtDepth(start + i * step);
        double best = double.NegativeInfinity, bestShift = 0;
        for (double s = -maxShift; s <= maxShift + 1e-9; s += step)
        {
            var b = new float[n];
            for (int i = 0; i < n; i++) b[i] = (float)curve.ValueAtDepth(start + i * step - s);
            double r = Dsp.Correlation(a, b);
            if (r > best) { best = r; bestShift = s; }
        }
        return (bestShift, best);
    }

    /// <summary>Statistics of a curve within an optional depth interval.</summary>
    public static StatisticsSummary Summarize(LogCurve c, double top = double.NegativeInfinity, double bottom = double.PositiveInfinity) =>
        Statistics.Summarize(c.Values.Where((v, i) => c.Depths[i] >= top && c.Depths[i] <= bottom).Select(v => (double)v));

    /// <summary>Acoustic impedance from sonic (slowness) and density: AI = ρ·V with V = 1/DT. Output in (m/s)·(g/cc).</summary>
    public static LogCurve AcousticImpedance(LogCurve sonic, LogCurve density)
    {
        var v = new float[sonic.Count];
        for (int i = 0; i < v.Length; i++)
        {
            double vel = UnitConverter.SlownessToVelocity(sonic.Values[i], string.IsNullOrEmpty(sonic.Unit) ? "us/ft" : sonic.Unit);
            double rho = density.ValueAtDepth(sonic.Depths[i]);
            if (density.Unit.Equals("kg/m3", StringComparison.OrdinalIgnoreCase)) rho /= 1000;
            v[i] = (float)(vel * rho);
        }
        return sonic.CloneAs("AI", v, "m/s*g/cc", CurveFamily.AcousticImpedance, "AI = RHOB × 1/DT");
    }

    /// <summary>Velocity (m/s) from a slowness log.</summary>
    public static LogCurve VelocityFromSlowness(LogCurve sonic, string mnemonic = "VP")
    {
        var v = sonic.Values.Select(s => (float)UnitConverter.SlownessToVelocity(s, string.IsNullOrEmpty(sonic.Unit) ? "us/ft" : sonic.Unit)).ToArray();
        return sonic.CloneAs(mnemonic, v, "m/s", sonic.Family == CurveFamily.ShearSonic ? CurveFamily.SVelocity : CurveFamily.PVelocity, "V = 1/DT");
    }
}
