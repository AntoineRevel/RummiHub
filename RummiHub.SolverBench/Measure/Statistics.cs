namespace RummiHub.SolverBench.Measure;

/// <summary>Small statistical helpers — median and percentile over a sample.</summary>
public static class Statistics
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>Nearest-rank percentile (p in [0,100]). p95 of a small sample is the near-max.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];

        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public static double Mean(IReadOnlyList<double> values) =>
        values.Count == 0 ? 0 : values.Average();
}
