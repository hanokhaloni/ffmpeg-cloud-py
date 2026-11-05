using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Lab;

public enum SourceState { Pending, Starting, Ready, Failed, Down, Stopped }

/// <summary>A media source: a synthetic publisher behind the relay, or an external camera.</summary>
public sealed class SourceRuntime
{
    public string Id { get; init; } = "";
    /// <summary>synthetic | camera</summary>
    public string Kind { get; init; } = "synthetic";
    /// <summary>loop-file | live-synthetic | camera</summary>
    public string Mode { get; init; } = "loop-file";
    /// <summary>URL consumers read (relay path or camera URL; may contain credentials, always masked for display).</summary>
    public string Url { get; set; } = "";
    public SourceState State { get; set; } = SourceState.Pending;
    public string StateReason { get; set; } = "";
    public FFmpegSession? Publisher { get; set; }
    public List<string> PublisherArgs { get; set; } = new();
    public string PublisherCommand { get; set; } = "";
    public double? ReadyAfterSeconds { get; set; }
    public long ReadinessFrames { get; set; }
    public int LoopBoundaries { get; set; }
    public double FixtureDuration { get; set; }
    public DateTime? DownSinceUtc { get; set; }
    public bool KilledByFault { get; set; }
    public List<string> Dependents { get; } = new();
    public int RelayReaders { get; set; } = -1;
    public long RelayBytesIn { get; set; }
    public bool IsSynthetic => Kind == "synthetic";
}

public sealed class LabPhase
{
    public string Name { get; init; } = "";
    /// <summary>pending | running | pass | warn | fail | skipped</summary>
    public string Status { get; set; } = "pending";
    public double? StartedAt { get; set; }
    public double? EndedAt { get; set; }
    public string Detail { get; set; } = "";
}

public sealed record PlannedInstance(string InstanceId, string SourceId, string ProfileId, int InstanceNo, int Batch);

/// <summary>Lab orchestration state shared (read-only) with the UI and reports.</summary>
public sealed class LabRuntime
{
    public static readonly string[] PhaseNames =
    {
        "Resolve configuration", "Validate paths and port availability", "Discover capabilities", "Run prerequisites", "Prepare fixture",
        "Start relay", "Verify relay readiness", "Start publishers", "Verify media readiness per source", "Start HTTP service",
        "Launch consumers", "Validate first outputs", "Steady measurement", "Shutdown and reconciliation"
    };

    public string Command { get; init; } = "lab";
    public string Preset { get; init; } = "";
    public string Topology { get; set; } = "";
    public string TopologyDetail { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string ScheduleText { get; set; } = "";
    public string CurrentBatchLabel { get; set; } = "";
    public string RunDir { get; set; } = "";
    public RelayManager? Relay { get; set; }
    public LabHttpServer? Http { get; set; }
    public FixtureInfo? Fixture { get; set; }
    public List<SourceRuntime> Sources { get; } = new();
    public List<PlannedInstance> Plan { get; } = new();
    public List<List<PlannedInstance>> Batches { get; } = new();
    public List<LabPhase> Phases { get; } = PhaseNames.Select(n => new LabPhase { Name = n }).ToList();
    public IReadOnlyList<ConfigValue> EffectiveConfig { get; set; } = Array.Empty<ConfigValue>();
    public double? StorageProbeAt { get; set; }
    public double MeasurementStartedAt { get; set; } = -1;
    public double MeasurementPlannedSeconds { get; set; }
    public List<string> StartupFailures { get; } = new();
    public List<string> Notes { get; } = new();
    public int OwnedStillRunningAfterShutdown { get; set; } = -1;
    public string SourceCompleteness { get; set; } = "UNKNOWN (ground-truth verification not enabled; output coverage is not source completeness)";
    public string CameraClock { get; set; } = "not applicable (synthetic sources)";

    /// <summary>Registry totals when this run started: per-role accounting reports only this run's processes.</summary>
    public IReadOnlyList<RoleTotals> RoleBaseline { get; set; } = Array.Empty<RoleTotals>();
    public IReadOnlyList<RoleTotals> RoleTotalsThisRun()
    {
        var now = ProcessRegistry.Current.Totals();
        return now.Select(t => RoleBaseline.FirstOrDefault(b => b.Role == t.Role) is { } b
            ? t with { Started = t.Started - b.Started, CpuSeconds = Math.Max(0, t.CpuSeconds - b.CpuSeconds) } : t).ToList();
    }

    public LabPhase Phase(string name) => Phases.First(p => p.Name == name);
    public SourceRuntime? Source(string id) => Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>Topology label. Fan-out is never presented as independent cameras.</summary>
    public static (string Label, string Detail) Classify(int synthetic, int cameras, int consumersPerSource, int profiles)
    {
        var sources = synthetic + cameras; var k = consumersPerSource;
        var p = profiles > 1 ? $" · {profiles} command profiles" : "";
        if (sources == 0) return ("EMPTY", "no sources configured");
        if (cameras > 0 && synthetic == 0) return ("CAMERAS", $"{cameras} external camera source(s) × {k} consumer(s){p}");
        if (cameras > 0) return ("MIXED-SOURCES", $"{synthetic} synthetic + {cameras} camera source(s) × {k} consumer(s){p}");
        if (sources == 1 && k > 1) return ("FAN-OUT", $"1 source × {k} consumers: {k} readers of ONE source, not {k} independent cameras{p}");
        if (k > 1) return ("MIXED", $"{sources} independent sources × {k} consumers each{p}");
        return ("INDEPENDENT", $"{sources} independent source(s) × 1 consumer{p}");
    }

    /// <summary>
    /// Builds launch batches. parallel: one batch. sequential: one batch per profile (groups replace each other).
    /// staged-load: cumulative batches reaching the configured active counts.
    /// </summary>
    public static List<List<PlannedInstance>> Schedule(IReadOnlyList<string> sourceIds, IReadOnlyList<string> profiles, int k, string execution, IReadOnlyList<int> stages)
    {
        string Id(string src, string prof, int n) => k > 1 ? $"{src}-{prof}-{n}" : $"{src}-{prof}";
        switch (execution.ToLowerInvariant())
        {
            case "parallel":
                return new() { (from s in sourceIds from p in profiles from n in Enumerable.Range(1, k) select new PlannedInstance(Id(s, p, n), s, p, n, 0)).ToList() };
            case "sequential":
                return profiles.Select((p, gi) => (from s in sourceIds from n in Enumerable.Range(1, k) select new PlannedInstance(Id(s, p, n), s, p, n, gi)).ToList()).ToList();
            case "staged-load":
                {
                    // round-robin across sources so each stage spreads load over sources
                    var all = (from n in Enumerable.Range(1, k) from p in profiles from s in sourceIds select (s, p, n)).ToList();
                    var counts = stages.Count > 0 ? stages.Where(c => c > 0).Select(c => Math.Min(c, all.Count)).Distinct().OrderBy(c => c).ToList() : Doubling(all.Count);
                    if (counts.Count == 0 || counts[^1] < all.Count) counts.Add(all.Count);
                    var batches = new List<List<PlannedInstance>>(); var prev = 0;
                    for (int i = 0; i < counts.Count; i++)
                    {
                        batches.Add(all.Skip(prev).Take(counts[i] - prev).Select(x => new PlannedInstance(Id(x.s, x.p, x.n), x.s, x.p, x.n, i)).ToList());
                        prev = counts[i];
                    }
                    return batches;
                }
            default:
                throw new ArgumentException($"unknown execution strategy '{execution}' (parallel | sequential | staged-load)");
        }
    }

    private static List<int> Doubling(int n) { var l = new List<int>(); for (var c = 1; c < n; c *= 2) l.Add(c); l.Add(n); return l; }
}
