namespace FFmpegReliabilityBench.Core;

public enum TestStatus { Pass, Warn, Fail, Unknown }

public sealed class TestResult
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public TestStatus Status { get; init; }
    public string Diagnosis { get; init; } = "";
    public Dictionary<string, double> Metrics { get; init; } = new();
    public List<string> Evidence { get; init; } = new();
    public DateTime CompletedAtUtc { get; init; } = DateTime.UtcNow;

    public static TestResult Make(string id, string cat, string name, TestStatus s, string diag,
        Dictionary<string, double>? m = null, IEnumerable<string>? ev = null) => new()
    {
        Id = id, Category = cat, Name = name, Status = s, Diagnosis = diag,
        Metrics = m ?? new(), Evidence = ev?.ToList() ?? new()
    };
}

public static class Health
{
    /// <summary>Not averaged: any FAIL fails; WARN/UNKNOWN degrade; PASS only if all pass.</summary>
    public static TestStatus Overall(IEnumerable<TestResult> results)
    {
        var l = results.ToList();
        if (l.Any(r => r.Status == TestStatus.Fail)) return TestStatus.Fail;
        if (l.Any(r => r.Status is TestStatus.Warn or TestStatus.Unknown)) return TestStatus.Warn;
        return TestStatus.Pass;
    }
}

public sealed record LogEvent(DateTime TsUtc, string Level, string Message);
