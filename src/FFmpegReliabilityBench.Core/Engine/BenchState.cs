using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Lab;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Engine;

public enum RunMode { Benchmark, PreflightOnly, FilesystemOnly, HlsOnly, RecordingOnly }

public sealed class StreamContext
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string HlsDir { get; init; } = "";
    public string RecDir { get; init; } = "";
    public BuiltCommand? Command { get; set; }
    public string HumanCommand { get; set; } = "";
    public FFmpegSession? Session { get; set; }
    public HlsMonitor? Hls { get; set; }
    public RecordingMonitor? Rec { get; set; }
    public PlaybackConsumer? Playback { get; set; }
    public PlaybackConsumer? HttpPlayback { get; set; }
    public HttpHlsConsumer? Http { get; set; }
    public List<TestResult> Results { get; set; } = new();
    public List<(double T, double Cpu, double MemMb)> ProcSeries { get; } = new();
    public bool KilledByFaultInjection { get; set; }
    public double ProducerSeconds { get; set; }

    // ---- lab orchestration (empty for the single-pipeline 'bench' mode)
    public string SourceId { get; init; } = "";
    public string SourceKind { get; init; } = "synthetic";
    public string ProfileId { get; init; } = "";
    public int InstanceNo { get; init; } = 1;
    public int Group { get; init; }
    public string InputUrl { get; set; } = "";
    public string RunState { get; set; } = "planned"; // planned | starting | running | stopped | blocked | crashed
    public string? Attribution { get; set; }
    public string? HlsHttpUrl { get; set; }
    public BenchConfig? EffectiveConfig { get; set; }
    public List<(string File, bool Ok, string Detail)> HttpRecordingSamples { get; } = new();
    public string SegmentListPath { get; set; } = "";
    /// <summary>When the source's fixture looped (expected timestamp/continuity transitions).</summary>
    public List<DateTime> LoopBoundariesUtc { get; } = new();

    /// <summary>True when an artifact first seen at <paramref name="t"/> was produced across a fixture loop boundary.</summary>
    public bool NearLoopBoundary(DateTime t, double windowSeconds)
    {
        lock (LoopBoundariesUtc) return LoopBoundariesUtc.Any(b => (t - b).TotalSeconds is var d && d >= -1 && d <= windowSeconds);
    }
}

/// <summary>Shared, read-mostly state. The TUI and reporters only read it; the orchestrator owns all lifecycles.</summary>
public sealed class BenchState
{
    public string SessionId { get; init; } = $"BENCH-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}"; // unique: concurrent instances must never share scratch/output dirs
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public EventBus Bus { get; } = new();
    public BenchConfig Config { get; init; } = new();
    public RunMode Mode { get; init; }
    public string Phase { get; set; } = "Init";
    public Capabilities? Caps { get; set; }
    public List<TestResult> Prerequisites { get; } = new();
    public List<TestResult> GlobalResults { get; } = new();
    public List<StreamContext> Streams { get; } = new();
    public ResourceMonitor? Resources { get; set; }
    public List<Hypothesis> Hypotheses { get; set; } = new();
    public TestStatus PrereqStatus { get; set; } = TestStatus.Unknown;
    public TestStatus RuntimeStatus { get; set; } = TestStatus.Unknown;
    public string ReportDirectory { get; set; } = "";
    public string? ReportJson { get; set; }
    public string? ReportHtml { get; set; }
    public string? FatalError { get; set; }
    public double RunStartedAt { get; set; } = -1;
    public bool Finished { get; set; }
    public volatile bool StopRequested;
    public void RequestStop() => StopRequested = true;
    public IEnumerable<TestResult> AllRuntimeResults => Streams.SelectMany(s => s.Results).Concat(GlobalResults);
    /// <summary>Lab orchestration state (null in the single-pipeline 'bench' mode).</summary>
    public LabRuntime? Lab { get; set; }
    public FFprobeRunner? Probe { get; set; }
    public double PlannedSeconds { get; set; }
    public TestStatus Overall => FatalError != null ? TestStatus.Fail : Health.Overall(Prerequisites.Concat(AllRuntimeResults));
}
