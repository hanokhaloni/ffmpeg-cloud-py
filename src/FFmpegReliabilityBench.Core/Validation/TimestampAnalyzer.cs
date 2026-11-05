namespace FFmpegReliabilityBench.Core.Validation;

public sealed class TimestampReport
{
    public int Packets { get; init; }
    public int MissingTimestamps { get; init; }
    public int NonMonotonicDts { get; init; }
    public int DuplicateDts { get; init; }
    public int Gaps { get; init; }
    public int Resets { get; init; }
    public double MaxGapSeconds { get; init; }
    public double MeanFrameInterval { get; init; }
    public List<string> Evidence { get; init; } = new();
    public bool Clean => MissingTimestamps == 0 && NonMonotonicDts == 0 && DuplicateDts == 0 && Gaps == 0 && Resets == 0;
}

/// <summary>Media-clock continuity (PTS/DTS). This says nothing about camera wall-clock accuracy.</summary>
public static class TimestampAnalyzer
{
    public static TimestampReport Analyze(IReadOnlyList<(double Pts, double Dts)> pk, double expectedFrameInterval, double gapFactor = 2.5, double resetThreshold = 1.0)
    {
        int missing = 0, nonmono = 0, dup = 0, gaps = 0, resets = 0; double maxGap = 0; var ev = new List<string>();
        double prev = double.NaN; var intervals = new List<double>();
        for (int i = 0; i < pk.Count; i++)
        {
            var d = double.IsNaN(pk[i].Dts) ? pk[i].Pts : pk[i].Dts;
            if (double.IsNaN(d)) { missing++; continue; }
            if (!double.IsNaN(prev))
            {
                var delta = d - prev;
                if (delta < -resetThreshold) { resets++; if (ev.Count < 20) ev.Add($"packet {i}: timestamp reset {prev:0.000}->{d:0.000}"); }
                else if (delta < 0) { nonmono++; if (ev.Count < 20) ev.Add($"packet {i}: non-monotonic DTS {prev:0.000}->{d:0.000}"); }
                else if (delta == 0) { dup++; if (ev.Count < 20) ev.Add($"packet {i}: duplicate DTS {d:0.000}"); }
                else
                {
                    intervals.Add(delta);
                    if (delta > expectedFrameInterval * gapFactor) { gaps++; if (ev.Count < 20) ev.Add($"packet {i}: gap {delta * 1000:0} ms"); }
                    maxGap = Math.Max(maxGap, delta);
                }
            }
            prev = d;
        }
        return new TimestampReport { Packets = pk.Count, MissingTimestamps = missing, NonMonotonicDts = nonmono, DuplicateDts = dup, Gaps = gaps, Resets = resets, MaxGapSeconds = maxGap, MeanFrameInterval = intervals.Count > 0 ? intervals.Average() : 0, Evidence = ev };
    }
}
