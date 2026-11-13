namespace GeoVisionPro.Core.Primitives;

/// <summary>Progress report for long-running operations.</summary>
public readonly record struct ProgressInfo(double Fraction, string Message)
{
    public static ProgressInfo Of(long done, long total, string message) =>
        new(total <= 0 ? 0 : Math.Clamp(done / (double)total, 0, 1), message);
}

/// <summary>Severity used for validation and QC reporting.</summary>
public enum Severity { Info, Warning, Error }

public sealed record ValidationIssue(Severity Severity, string Message, string? Location = null)
{
    public override string ToString() => Location is null ? $"[{Severity}] {Message}" : $"[{Severity}] {Location}: {Message}";
}

/// <summary>Collects validation issues; IsValid when there are no errors.</summary>
public sealed class ValidationReport
{
    private readonly List<ValidationIssue> _issues = [];
    public IReadOnlyList<ValidationIssue> Issues => _issues;
    public bool IsValid => _issues.All(i => i.Severity != Severity.Error);
    public bool HasWarnings => _issues.Any(i => i.Severity == Severity.Warning);
    public void Info(string m, string? loc = null) => _issues.Add(new(Severity.Info, m, loc));
    public void Warn(string m, string? loc = null) => _issues.Add(new(Severity.Warning, m, loc));
    public void Error(string m, string? loc = null) => _issues.Add(new(Severity.Error, m, loc));
    public void Merge(ValidationReport other) => _issues.AddRange(other._issues);
    public string Status => !IsValid ? "Invalid" : HasWarnings ? "Valid (warnings)" : "Valid";
    public override string ToString() => string.Join(Environment.NewLine, _issues);
}

/// <summary>Simple discriminated result for operations that can fail with a message.</summary>
public readonly record struct Result<T>(T? Value, string? Error)
{
    public bool IsSuccess => Error is null;
    public static Result<T> Ok(T value) => new(value, null);
    public static Result<T> Fail(string error) => new(default, error);
}

/// <summary>Dense, row-major 2D float array with explicit dimensions (rows = traces, cols = samples by convention).</summary>
public sealed class Array2D
{
    public int Rows { get; }
    public int Cols { get; }
    public float[] Data { get; }

    public Array2D(int rows, int cols)
    {
        Rows = rows; Cols = cols; Data = new float[(long)rows * cols];
    }

    public Array2D(int rows, int cols, float[] data)
    {
        if (data.Length != (long)rows * cols) throw new ArgumentException("Data length mismatch.");
        Rows = rows; Cols = cols; Data = data;
    }

    public float this[int r, int c]
    {
        get => Data[r * Cols + c];
        set => Data[r * Cols + c] = value;
    }

    public Span<float> Row(int r) => Data.AsSpan(r * Cols, Cols);

    public Array2D Clone() => new(Rows, Cols, (float[])Data.Clone());

    public (float Min, float Max) Range()
    {
        float mn = float.MaxValue, mx = float.MinValue;
        foreach (var v in Data) { if (float.IsNaN(v)) continue; if (v < mn) mn = v; if (v > mx) mx = v; }
        return mn > mx ? (0, 0) : (mn, mx);
    }

    /// <summary>Robust clip value: the given percentile of absolute amplitudes.</summary>
    public float AbsPercentile(double percentile, int maxSamples = 200_000)
    {
        int n = Data.Length;
        if (n == 0) return 0;
        int step = Math.Max(1, n / maxSamples);
        var buf = new List<float>(n / step + 1);
        for (int i = 0; i < n; i += step) { var v = Data[i]; if (!float.IsNaN(v)) buf.Add(Math.Abs(v)); }
        if (buf.Count == 0) return 0;
        buf.Sort();
        int k = (int)Math.Clamp(Math.Round(percentile / 100.0 * (buf.Count - 1)), 0, buf.Count - 1);
        return buf[k];
    }
}

/// <summary>Regular 2D grid of doubles (e.g. horizon or attribute map), NaN = undefined.</summary>
public sealed class Grid2D
{
    public int NI { get; }
    public int NJ { get; }
    public double[] Values { get; }

    public Grid2D(int ni, int nj, double fill = double.NaN)
    {
        NI = ni; NJ = nj; Values = new double[ni * nj];
        Array.Fill(Values, fill);
    }

    public Grid2D(int ni, int nj, double[] values)
    {
        if (values.Length != ni * nj) throw new ArgumentException("Length mismatch.");
        NI = ni; NJ = nj; Values = values;
    }

    public double this[int i, int j]
    {
        get => Values[i * NJ + j];
        set => Values[i * NJ + j] = value;
    }

    public bool InBounds(int i, int j) => i >= 0 && j >= 0 && i < NI && j < NJ;
    public int DefinedCount => Values.Count(v => !double.IsNaN(v));
    public Grid2D Clone() => new(NI, NJ, (double[])Values.Clone());

    public (double Min, double Max) Range()
    {
        double mn = double.MaxValue, mx = double.MinValue;
        foreach (var v in Values) { if (double.IsNaN(v)) continue; mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
        return mn > mx ? (double.NaN, double.NaN) : (mn, mx);
    }
}

/// <summary>Describes an integer index range with an increment (e.g. inline numbers 100..300 step 2).</summary>
public readonly record struct IndexRange(int First, int Last, int Step)
{
    public int Count => Step == 0 ? 1 : (Last - First) / Step + 1;
    public int IndexOf(int value) => Step == 0 ? 0 : (value - First) / Step;
    public int ValueAt(int index) => First + index * Step;
    public bool Contains(int value) => Step != 0 && value >= Math.Min(First, Last) && value <= Math.Max(First, Last) && (value - First) % Step == 0;
    public override string ToString() => $"{First}-{Last} ({Step})";
}
