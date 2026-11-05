using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Lab;

namespace FFmpegReliabilityBench.App.Tui;

public interface IDashboardStateProvider { DashboardSnapshot GetSnapshot(); }
public interface ITuiController { Task RunAsync(CancellationToken cancellationToken); }
public interface IUiCommandDispatcher { ValueTask DispatchAsync(UiCommand command, CancellationToken cancellationToken); }

public enum UiCommandKind
{
    Tab, NextTab, Down, Up, PageDown, PageUp, Home, End, ToggleDetails, Escape,
    StartFilter, FilterChar, FilterBackspace, FilterAccept, CycleSort, ReverseSort, CycleOutputFilter, CycleSeverity, CycleGroup,
    ScrollLeft, ScrollRight, ToggleWrap, TogglePause, RequestQuit, ConfirmYes, ConfirmNo, Help, Redraw
}

public readonly record struct UiCommand(UiCommandKind Kind, int Arg = 0, char Ch = '\0');

/// <summary>Immutable state for one frame. Built by the single UI owner; workers never touch widgets.</summary>
public sealed record DashboardSnapshot
{
    public string Mode { get; init; } = "LAB";
    public string Preset { get; init; } = "";
    public string Topology { get; init; } = "";
    public string TopologyDetail { get; init; } = "";
    public string Strategy { get; init; } = "";
    public string BatchLabel { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public TimeSpan Planned { get; init; }
    public string Phase { get; init; } = "";
    public TestStatus Current { get; init; } = TestStatus.Unknown;
    public bool Finished { get; init; }
    public string? Fatal { get; init; }
    public string? ReportPath { get; init; }
    public ResourceView Res { get; init; } = new();
    public IReadOnlyList<CheckLine> Prereqs { get; init; } = Array.Empty<CheckLine>();
    public IReadOnlyList<string> StorageNet { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StreamRow> Streams { get; init; } = Array.Empty<StreamRow>();
    public IReadOnlyList<ProcessRow> Processes { get; init; } = Array.Empty<ProcessRow>();
    public IReadOnlyList<EventLine> Incidents { get; init; } = Array.Empty<EventLine>();
    public string Attribution { get; init; } = "";
    public IReadOnlyList<CheckLine> Checks { get; init; } = Array.Empty<CheckLine>();
    public IReadOnlyList<EventLine> Events { get; init; } = Array.Empty<EventLine>();
    public IReadOnlyList<PhaseLine> Phases { get; init; } = Array.Empty<PhaseLine>();
    public IReadOnlyList<SourceRow> Sources { get; init; } = Array.Empty<SourceRow>();
    public IReadOnlyList<ConfigValue> Config { get; init; } = Array.Empty<ConfigValue>();
    public IReadOnlyList<string> StorageDetail { get; init; } = Array.Empty<string>();
    public long EventCount { get; init; }
}

public sealed record ResourceView
{
    public double CpuNow { get; init; }
    public double[] CpuHistory { get; init; } = Array.Empty<double>();
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public double WriteMBps { get; init; }
    public double[] WriteHistory { get; init; } = Array.Empty<double>();
    public double NetMbps { get; init; }
    public double[] NetHistory { get; init; } = Array.Empty<double>();
    public int StreamsRunning { get; init; }
    public int StreamsTotal { get; init; }
    public int OwnedProcesses { get; init; }
    public IReadOnlyList<RoleTotals> Roles { get; init; } = Array.Empty<RoleTotals>();
    public int ValidationQueued { get; init; }
    public int ValidationRunning { get; init; }
    public int ValidationMax { get; init; }
}

public sealed record StreamRow
{
    public string Id { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public string Profile { get; init; } = "";
    public int Pid { get; init; }
    public double Cpu { get; init; }
    public double MemMb { get; init; }
    public double Fps { get; init; }
    public double Speed { get; init; }
    public string Hls { get; init; } = "-";
    public string Rec { get; init; } = "-";
    public string State { get; init; } = "";
    public TestStatus StateStatus { get; init; } = TestStatus.Unknown;
    public double HlsAge { get; init; }
    public int Backlog { get; init; }
    public long HlsSeq { get; init; } = -1;
    public int HlsValid { get; init; }
    public int HlsBad { get; init; }
    public int RecValid { get; init; }
    public int RecBad { get; init; }
    public int RecWriting { get; init; }
    public string? Attribution { get; init; }
    public StreamDetail Detail { get; init; } = new();
}

public sealed record StreamDetail
{
    public string InputUrl { get; init; } = "";
    public string PublisherCommand { get; init; } = "";
    public string ConsumerCommand { get; init; } = "";
    public IReadOnlyList<string> ConsumerArgs { get; init; } = Array.Empty<string>();
    public string HlsDir { get; init; } = "";
    public string RecDir { get; init; } = "";
    public string? HttpUrl { get; init; }
    public string HlsSummary { get; init; } = "";
    public string RecordingSummary { get; init; } = "";
    public string OutputCoverage { get; init; } = "";
    public string SourceCompleteness { get; init; } = "";
    public string PlayerSummary { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}

public sealed record ProcessRow(string Role, string Name, string Owner, int Pid, double Cpu, double MemMb, string State, TimeSpan Age);
public sealed record EventLine(DateTime Ts, EventLevel Level, string Category, string Stream, string Text);
public sealed record CheckLine(TestStatus Status, string Id, string Name, string Detail);
public sealed record PhaseLine(string Name, string Status, string Detail, double? Seconds);
public sealed record SourceRow(string Id, string Kind, string Mode, string State, string Url, int Pid, double Cpu, double? ReadyAfter, int Loops, int Readers, string Dependents, string Command);

public enum SortKey { Id, Cpu, State, Latency, Backlog }
public enum OutputFilter { All, HlsIssues, RecIssues }
public enum SeverityFilter { All, WarnAndUp, FailOnly }
public enum GroupBy { None, Source, Profile }
public enum Overlay { None, ConfirmQuit, Help }

/// <summary>Mutable view state, owned by the UI thread only.</summary>
public sealed class ViewState
{
    public int Tab = 1;
    public int Selected;
    public int ListOffset;
    public bool Details;
    public int DetailScroll;
    public string Filter = "";
    public bool FilterEditing;
    public string FilterDraft = "";
    public SortKey Sort = SortKey.Id;
    public bool SortDesc;
    public OutputFilter Output = OutputFilter.All;
    public SeverityFilter Severity = SeverityFilter.All;
    public GroupBy Group = GroupBy.None;
    public int CmdScroll;
    public bool Wrap;
    public bool Paused;
    public Overlay Overlay = Overlay.None;
    public bool QuitRequested;
}
