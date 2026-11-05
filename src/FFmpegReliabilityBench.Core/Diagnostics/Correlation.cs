namespace FFmpegReliabilityBench.Core.Diagnostics;

public sealed record Hypothesis(DateTime TsUtc, string Stream, string Symptom, List<string> CorrelatedEvents, string Text);

/// <summary>Temporal correlation of failures with infrastructure events. Output is explicitly labeled as hypotheses, not root causes.</summary>
public static class Correlation
{
    private static readonly string[] Symptoms = { "HLS", "Recording", "Playback", "Process" };

    public static List<Hypothesis> Analyze(IReadOnlyList<RuntimeEvent> events, double windowSeconds = 5)
    {
        var res = new List<Hypothesis>();
        var problems = events.Where(e => e.Level != EventLevel.Info).ToList();
        foreach (var s in problems.Where(e => Symptoms.Contains(e.Category) && e.Level == EventLevel.Fail))
        {
            var near = problems.Where(o => o != s && Math.Abs(o.T - s.T) <= windowSeconds && (o.Category != s.Category || o.Stream != s.Stream)).ToList();
            if (near.Count == 0) continue;
            var causes = near.Select(n => n.Category).Distinct().ToList();
            res.Add(new Hypothesis(s.TsUtc, s.Stream, s.Message, near.Select(n => $"{n.TsUtc:HH:mm:ss.f} [{n.Category}/{n.Stream}] {n.Message}").Take(8).ToList(),
                $"HYPOTHESIS (correlation, not proof): '{s.Message}' is temporally correlated (±{windowSeconds}s) with {string.Join(", ", causes)} events."));
            if (res.Count >= 100) break;
        }
        // Cross-stream: the same symptom on several streams at once points to a shared resource (storage, network, CPU) rather than a camera.
        var multi = problems.Where(e => Symptoms.Contains(e.Category) && e.Level == EventLevel.Fail).GroupBy(e => (int)(e.T / windowSeconds)).Where(g => g.Select(e => e.Stream).Distinct().Count() > 1);
        foreach (var g in multi.Take(20))
            res.Add(new Hypothesis(g.First().TsUtc, "*", g.First().Message, g.Select(e => $"[{e.Stream}] {e.Message}").Take(8).ToList(),
                $"HYPOTHESIS: {g.Select(e => e.Stream).Distinct().Count()} streams failed within {windowSeconds}s - suggests a shared resource (storage/network/CPU) rather than an individual camera."));
        return res;
    }
}
