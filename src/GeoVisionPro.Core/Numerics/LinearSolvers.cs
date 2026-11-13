namespace GeoVisionPro.Core.Numerics;

/// <summary>Matrix-free iterative solvers used by inversion and gridding.</summary>
public static class LinearSolvers
{
    /// <summary>
    /// Conjugate gradient for a symmetric positive (semi-)definite operator A: solves A x = b starting from x.
    /// Returns number of iterations performed.
    /// </summary>
    public static int ConjugateGradient(Action<double[], double[]> applyA, double[] b, double[] x, int maxIterations = 200,
        double tolerance = 1e-8, CancellationToken cancellationToken = default)
    {
        int n = b.Length;
        var r = new double[n];
        var p = new double[n];
        var ap = new double[n];
        applyA(x, ap);
        for (int i = 0; i < n; i++) { r[i] = b[i] - ap[i]; p[i] = r[i]; }
        double rr = Dot(r, r);
        double bnorm = Math.Sqrt(Dot(b, b));
        if (bnorm == 0) bnorm = 1;
        int it;
        for (it = 0; it < maxIterations; it++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Math.Sqrt(rr) / bnorm < tolerance) break;
            applyA(p, ap);
            double pap = Dot(p, ap);
            if (pap <= 0) break;
            double alpha = rr / pap;
            for (int i = 0; i < n; i++) { x[i] += alpha * p[i]; r[i] -= alpha * ap[i]; }
            double rrNew = Dot(r, r);
            double beta = rrNew / rr;
            for (int i = 0; i < n; i++) p[i] = r[i] + beta * p[i];
            rr = rrNew;
        }
        return it;
    }

    /// <summary>Thomas algorithm for tridiagonal systems (a: sub, b: diag, c: super).</summary>
    public static double[] SolveTridiagonal(double[] a, double[] b, double[] c, double[] d)
    {
        int n = d.Length;
        var cp = new double[n];
        var dp = new double[n];
        cp[0] = c[0] / b[0];
        dp[0] = d[0] / b[0];
        for (int i = 1; i < n; i++)
        {
            double m = b[i] - a[i] * cp[i - 1];
            cp[i] = i < n - 1 ? c[i] / m : 0;
            dp[i] = (d[i] - a[i] * dp[i - 1]) / m;
        }
        var x = new double[n];
        x[n - 1] = dp[n - 1];
        for (int i = n - 2; i >= 0; i--) x[i] = dp[i] - cp[i] * x[i + 1];
        return x;
    }

    public static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Solves a small dense system by Gaussian elimination with partial pivoting.</summary>
    public static double[] SolveDense(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int k = 0; k < n; k++)
        {
            int piv = k;
            for (int i = k + 1; i < n; i++) if (Math.Abs(m[i, k]) > Math.Abs(m[piv, k])) piv = i;
            if (Math.Abs(m[piv, k]) < 1e-300) throw new InvalidOperationException("Singular matrix.");
            if (piv != k)
            {
                for (int j = 0; j < n; j++) (m[k, j], m[piv, j]) = (m[piv, j], m[k, j]);
                (x[k], x[piv]) = (x[piv], x[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                double f = m[i, k] / m[k, k];
                if (f == 0) continue;
                for (int j = k; j < n; j++) m[i, j] -= f * m[k, j];
                x[i] -= f * x[k];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            double s = x[i];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}
