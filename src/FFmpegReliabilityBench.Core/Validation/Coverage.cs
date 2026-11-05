namespace FFmpegReliabilityBench.Core.Validation;

public enum MediaStatus { Pending, Valid, Invalid, Missing, Unverified }

/// <summary>Primary reliability metric: verified (decoded, accounted-for) media duration / expected media duration.</summary>
public sealed class CoverageReport
{
    public double ExpectedSeconds { get; init; }
    public double VerifiedSeconds { get; init; }
    public double MissingSeconds { get; init; }
    public double CorruptSeconds { get; init; }
    public double UnverifiedSeconds { get; init; }
    public int Items { get; init; }
    public int Valid { get; init; }
    public int Invalid { get; init; }
    public int Missing { get; init; }
    public int Unverified { get; init; }
    public double? WallClockSeconds { get; init; }
    public double Percent => ExpectedSeconds > 0 ? VerifiedSeconds / ExpectedSeconds * 100.0 : 0;
    /// <summary>Verified media vs. wall-clock time the producer should have been producing (catches stalls that leave no sequence gap).</summary>
    public double? WallClockPercent => WallClockSeconds is > 0 ? Math.Min(100.0, VerifiedSeconds / WallClockSeconds.Value * 100.0) : null;

    /// <param name="items">status + duration; for missing/unknown items pass the nominal (expected) duration.</param>
    public static CoverageReport Compute(IEnumerable<(MediaStatus Status, double Duration)> items, double? wallClockSeconds = null)
    {
        double exp = 0, ver = 0, mis = 0, cor = 0, unv = 0; int n = 0, v = 0, i = 0, m = 0, u = 0;
        foreach (var (s, d) in items)
        {
            n++; exp += d;
            switch (s)
            {
                case MediaStatus.Valid: v++; ver += d; break;
                case MediaStatus.Invalid: i++; cor += d; break;
                case MediaStatus.Missing: m++; mis += d; break;
                default: u++; unv += d; break; // Pending/Unverified are never counted as verified
            }
        }
        return new CoverageReport { ExpectedSeconds = exp, VerifiedSeconds = ver, MissingSeconds = mis, CorruptSeconds = cor, UnverifiedSeconds = unv, Items = n, Valid = v, Invalid = i, Missing = m, Unverified = u, WallClockSeconds = wallClockSeconds };
    }
}

public sealed class DurationStats
{
    public int Count { get; init; }
    public double Mean { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double StdDev { get; init; }
    public static DurationStats Of(IEnumerable<double> xs)
    {
        var l = xs.Where(x => x > 0 && !double.IsNaN(x)).ToList();
        if (l.Count == 0) return new DurationStats();
        var mean = l.Average();
        return new DurationStats { Count = l.Count, Mean = mean, Min = l.Min(), Max = l.Max(), StdDev = Math.Sqrt(l.Sum(x => (x - mean) * (x - mean)) / l.Count) };
    }
}

public static class Percentile
{
    public static double Of(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        var rank = p / 100.0 * (s.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return s[lo] + (s[hi] - s[lo]) * (rank - lo);
    }
}
