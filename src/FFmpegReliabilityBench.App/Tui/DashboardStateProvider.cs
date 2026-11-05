using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Lab;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.App.Tui;

/// <summary>Reads the (concurrently updated) run state and produces an immutable snapshot. Never mutates anything.</summary>
public sealed class DashboardStateProvider : IDashboardStateProvider
{
    private readonly BenchState _st;
    private string _attribution = ""; private long _attrAt = -1;
    public DashboardStateProvider(BenchState st) { _st = st; }

    public DashboardSnapshot GetSnapshot()
    {
        var st = _st; var lab = st.Lab;
        var streams = Safe(() => st.Streams.Select(Row).ToList(), new List<StreamRow>());
        var elapsed = st.RunStartedAt >= 0 ? TimeSpan.FromSeconds((st.Finished ? LastEventT() : st.Bus.Now) - st.RunStartedAt) : TimeSpan.Zero;
        // runtime incidents (prerequisite findings have their own panel)
        var incidents = st.Bus.Recent(300).Where(e => e.Level != EventLevel.Info && e.Category is not ("System" or "Filesystem" or "Time" or "Network" or "Preflight") || e.Level == EventLevel.Fail && e.Category is "Network").Reverse()
            .Select(e => new EventLine(e.TsUtc, e.Level, e.Category, e.Stream, e.Message)).ToList();
        return new DashboardSnapshot
        {
            Mode = lab == null ? "BENCH" : lab.Command.ToUpperInvariant(),
            Preset = lab?.Preset ?? st.Config.Benchmark.Profile,
            Topology = lab?.Topology ?? "SINGLE PIPELINE",
            TopologyDetail = lab?.TopologyDetail ?? $"{st.Config.Benchmark.ConcurrentStreams} stream(s), profile {st.Config.Benchmark.Profile}",
            Strategy = lab?.Strategy ?? "parallel",
            BatchLabel = lab?.CurrentBatchLabel ?? "",
            Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed,
            Planned = TimeSpan.FromSeconds(st.PlannedSeconds > 0 ? st.PlannedSeconds : st.Config.Benchmark.DurationSeconds),
            Phase = st.Phase,
            Current = Current(st, streams),
            Finished = st.Finished,
            Fatal = st.FatalError,
            ReportPath = st.ReportHtml,
            Res = Safe(() => Resources(st, streams), new ResourceView()),
            Prereqs = Safe(() => PrereqLines(st), new List<CheckLine>()),
            StorageNet = Safe(() => StorageLines(st), new List<string>()),
            Streams = streams,
            Processes = Safe(Processes, new List<ProcessRow>()),
            Incidents = incidents,
            Attribution = Safe(() => Attribution(st, incidents.Count), ""),
            Checks = Safe(() => st.Prerequisites.ToList().Concat(st.AllRuntimeResults.ToList()).Select(r => new CheckLine(r.Status, r.Id, r.Name, r.Diagnosis)).ToList(), new List<CheckLine>()),
            Events = st.Bus.Recent(500).Select(e => new EventLine(e.TsUtc, e.Level, e.Category, e.Stream, e.Message)).ToList(),
            Phases = lab?.Phases.Select(p => new PhaseLine(p.Name, p.Status, p.Detail, p.StartedAt is double s ? ((p.EndedAt ?? st.Bus.Now) - s) : null)).ToList() ?? new List<PhaseLine>(),
            Sources = Safe(() => SourceRows(lab), new List<SourceRow>()),
            Config = lab?.EffectiveConfig ?? Array.Empty<Core.Config.ConfigValue>(),
            StorageDetail = Safe(() => StorageDetail(st), new List<string>()),
            EventCount = st.Bus.TotalCount
        };
    }

    private double LastEventT() { var r = _st.Bus.Recent(1); return r.Count > 0 ? r[0].T : _st.Bus.Now; }

    private static T Safe<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }

    private static TestStatus Current(BenchState st, List<StreamRow> rows)
    {
        if (st.FatalError != null) return TestStatus.Fail;
        if (st.Finished) return st.Overall;
        var s = rows.Count == 0 ? TestStatus.Unknown : rows.Select(r => r.StateStatus).Aggregate(TestStatus.Pass, Worst);
        return Worst(s, st.PrereqStatus == TestStatus.Unknown ? TestStatus.Pass : st.PrereqStatus);
    }

    private static TestStatus Worst(TestStatus a, TestStatus b)
    {
        int Rank(TestStatus x) => x switch { TestStatus.Fail => 3, TestStatus.Warn => 2, TestStatus.Unknown => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private StreamRow Row(StreamContext s)
    {
        var ses = s.Session; var p = ses?.LastProgress;
        var running = ses != null && !ses.Process.HasExited;
        string hls = "-", rec = "-"; int hv = 0, hb = 0, rv = 0, rb = 0, rw = 0, backlog = 0; double age = 0; long seq = -1;
        var c = s.EffectiveConfig ?? _st.Config;
        if (s.Hls is { } h)
        {
            var vals = h.Segments.Values.ToList();
            hv = vals.Count(v => v.Status == MediaStatus.Valid); hb = vals.Count(v => v.Status is MediaStatus.Invalid or MediaStatus.Missing);
            backlog += h.PendingCount; age = h.SecondsSinceAdvance; seq = h.MaxSeq;
            hls = hb > 0 ? "FAIL" : !h.PlaylistSeen ? (running ? "WAIT" : "-") : running && age > c.Hls.TargetDurationSeconds * 3 ? "LATE" : h.PendingCount > 6 ? "BKLG" : "PASS";
        }
        if (s.Rec is { } r)
        {
            var vals = r.Files.Values.ToList();
            rv = vals.Count(v => v.Status == MediaStatus.Valid); rb = vals.Count(v => v.Status is MediaStatus.Invalid or MediaStatus.Missing); rw = vals.Count(v => v.Status == MediaStatus.Pending);
            backlog += r.PendingCount;
            rec = rb > 0 ? "FAIL" : vals.Count == 0 ? (running ? "WAIT" : "-") : running && r.SecondsSinceNewFile > c.Recording.SegmentDurationSeconds * 1.5 + 3 ? "LATE" : r.PendingCount > 3 ? "BKLG" : "PASS";
        }
        var (state, ss) = s.RunState switch
        {
            "planned" => ("PLAN", TestStatus.Unknown),
            "blocked" => ("BLOCK", TestStatus.Fail),
            "crashed" => ("FAIL", TestStatus.Fail),
            "starting" => ("START", TestStatus.Unknown),
            _ when s.Results.Count > 0 => (Health.Overall(s.Results).ToString().ToUpperInvariant(), Health.Overall(s.Results)),
            "stopped" => ("STOP", TestStatus.Unknown),
            _ => Live(hls, rec, ses)
        };
        if (s.SourceId.Length == 0 && ses != null && s.RunState == "planned") (state, ss) = Live(hls, rec, ses); // bench mode has no run states
        var hlsCov = s.Hls?.Coverage(); var recCov = s.Rec?.Coverage();
        var pub = _st.Lab?.Source(s.SourceId);
        return new StreamRow
        {
            Id = s.Name, Source = s.SourceId.Length > 0 ? s.SourceId : "synthetic", SourceKind = s.SourceKind, Profile = s.ProfileId.Length > 0 ? s.ProfileId : s.Command?.Profile ?? "",
            Pid = ses?.Pid ?? 0, Cpu = running ? ses!.CpuPercent : 0, MemMb = running ? ses!.WorkingSetMb : 0, Fps = p?.Fps ?? 0, Speed = p?.Speed ?? 0,
            Hls = hls, Rec = rec, State = state, StateStatus = ss, HlsAge = age, Backlog = backlog, HlsSeq = seq,
            HlsValid = hv, HlsBad = hb, RecValid = rv, RecBad = rb, RecWriting = rw, Attribution = s.Attribution,
            Detail = new StreamDetail
            {
                InputUrl = CommandText.Mask(s.InputUrl.Length > 0 ? s.InputUrl : _st.Config.Source.Url),
                PublisherCommand = pub?.PublisherCommand.Length > 0 ? pub.PublisherCommand : pub?.Kind == "camera" ? "(external camera - no publisher)" : "(none)",
                ConsumerCommand = s.HumanCommand,
                ConsumerArgs = (s.Command?.Args ?? new()).Select(CommandText.Mask).ToList(),
                HlsDir = s.HlsDir, RecDir = s.RecDir, HttpUrl = s.HlsHttpUrl,
                HlsSummary = s.Hls == null ? "HLS: not produced by this profile" : $"HLS seq {seq} | age {age:F1}s | {hv} valid, {hb} bad, {s.Hls.PendingCount} pending",
                RecordingSummary = s.Rec == null ? "Recording: not produced by this profile" : $"Recording {rv} valid / {rw} writing-or-pending / {rb} bad",
                OutputCoverage = $"Output coverage: HLS {(hlsCov == null ? "-" : $"{hlsCov.Percent:F1}%")} · REC {(recCov == null ? "-" : $"{recCov.Percent:F1}%")}",
                SourceCompleteness = "Source completeness: " + (_st.Lab?.SourceCompleteness.Split(' ')[0] ?? "UNKNOWN"),
                PlayerSummary = s.HttpPlayback is { } pl ? $"HTTP player: {pl.Frames} frames, {pl.StallEpisodes} stalls, {pl.DecodeErrors} errors" : s.Http is { } hc ? $"HTTP: {hc.SegmentRequests} segments fetched, {hc.HashMatches} byte-identical, {hc.HashMismatches + hc.SegmentFailures} problems" : "",
                Evidence = ses?.RecentProblems.TakeLast(8).Select(l => $"{l.TsUtc:HH:mm:ss} [{l.Category}] {CommandText.Mask(l.Raw)}").ToList() ?? new List<string>()
            }
        };
    }

    private static (string, TestStatus) Live(string hls, string rec, FFmpegSession? ses)
    {
        if (ses == null) return ("PLAN", TestStatus.Unknown);
        if (ses.State == SessionState.Stalled) return ("STALL", TestStatus.Fail);
        if (hls == "FAIL" || rec == "FAIL") return ("FAIL", TestStatus.Fail);
        if (hls is "LATE" or "BKLG" || rec is "LATE" or "BKLG") return ("WARN", TestStatus.Warn);
        if (hls == "WAIT" || rec == "WAIT") return ("START", TestStatus.Unknown);
        return ("PASS", TestStatus.Pass);
    }

    private static ResourceView Resources(BenchState st, List<StreamRow> rows)
    {
        var hist = st.Resources?.History() ?? Array.Empty<ResourceSample>();
        var last = hist.LastOrDefault();
        return new ResourceView
        {
            CpuNow = last?.SystemCpu ?? 0,
            CpuHistory = hist.Select(h => h.SystemCpu ?? 0).ToArray(),
            RamTotalGb = (last?.TotalMemMb ?? 0) / 1024.0,
            RamUsedGb = last == null ? 0 : (last.TotalMemMb - last.AvailableMemMb) / 1024.0,
            WriteMBps = last?.AppWriteMBps ?? 0, WriteHistory = hist.Select(h => h.AppWriteMBps).ToArray(),
            NetMbps = last?.NetMbps ?? 0, NetHistory = hist.Select(h => h.NetMbps).ToArray(),
            StreamsRunning = st.Streams.Count(s => s.Session is { } ss && !ss.Process.HasExited),
            StreamsTotal = st.Streams.Count,
            OwnedProcesses = ProcessRegistry.Current.LiveCount,
            Roles = ProcessRegistry.Current.Totals(),
            ValidationQueued = st.Probe?.Queued ?? 0, ValidationRunning = st.Probe?.Running ?? 0, ValidationMax = st.Probe?.MaxWorkers ?? 0
        };
    }

    private static List<CheckLine> PrereqLines(BenchState st)
    {
        var pre = st.Prerequisites.ToList(); var lab = st.Lab;
        CheckLine Group(string name, Func<TestResult, bool> f)
        {
            var g = pre.Where(f).ToList();
            return g.Count == 0 ? new CheckLine(TestStatus.Unknown, "", name, "pending") : new CheckLine(Health.Overall(g), "", name, $"{g.Count(x => x.Status == TestStatus.Pass)}/{g.Count} pass");
        }
        var l = new List<CheckLine>
        {
            Group("FFmpeg capabilities", r => r.Id is "SYS-007" or "SYS-008" or "SYS-009" or "SYS-010"),
            Group("Output access", r => r.Id.StartsWith("FS-")),
            Group("System resources", r => r.Id is "SYS-003" or "SYS-004" or "SYS-005"),
        };
        if (lab != null)
        {
            var synth = lab.Sources.Any(s => s.IsSynthetic);
            l.Add(!synth ? new CheckLine(TestStatus.Pass, "", "RTSP relay", "not used (cameras read directly)")
                : lab.Relay is { Ready: true } r ? new CheckLine(TestStatus.Pass, "", "RTSP relay ready", r.Mode + (r.Version != null ? " " + r.Version : ""))
                : lab.Relay?.Error != null ? new CheckLine(TestStatus.Fail, "", "RTSP relay ready", lab.Relay.Error) : new CheckLine(TestStatus.Unknown, "", "RTSP relay ready", "pending"));
            var ready = lab.Sources.Count(s => s.ReadyAfterSeconds != null);
            l.Add(new CheckLine(lab.Sources.Any(s => s.State == SourceState.Failed) ? TestStatus.Fail : ready == lab.Sources.Count ? TestStatus.Pass : TestStatus.Unknown, "", "Sources decodable", $"{ready}/{lab.Sources.Count}"));
            l.Add(lab.Http is { Running: true } h ? new CheckLine(TestStatus.Pass, "", "Local HTTP ready", h.BaseUrl)
                : st.Config.Lab.Http.Enabled ? new CheckLine(TestStatus.Unknown, "", "Local HTTP ready", lab.Phase("Start HTTP service").Status) : new CheckLine(TestStatus.Pass, "", "Local HTTP", "disabled"));
            l.Add(lab.CameraClock.StartsWith("not applicable") ? new CheckLine(TestStatus.Pass, "N/A", "Camera UTC", "synthetic source") : new CheckLine(pre.FirstOrDefault(x => x.Id == "TIME-002")?.Status ?? TestStatus.Unknown, "", "Camera UTC", lab.CameraClock));
        }
        else l.Add(Group("Clocks", r => r.Id.StartsWith("TIME")));
        return l;
    }

    private static List<string> StorageLines(BenchState st)
    {
        var pre = st.Prerequisites.ToList(); var lab = st.Lab;
        var p95 = pre.FirstOrDefault(r => r.Id == "FS-015")?.Metrics.GetValueOrDefault("p95Ms");
        var free = pre.FirstOrDefault(r => r.Id == "SYS-005")?.Metrics.GetValueOrDefault("freeGb");
        var dir = lab?.RunDir ?? st.Config.Storage.RecordingDirectory;
        var kind = SystemInfo.PathKind(dir);
        var l = new List<string>
        {
            $"Local  write p95 {(p95 is double a ? a.ToString("F0") + " ms" : "-")}   free {(free is double f ? f.ToString("F0") + " GB" : "-")}",
            kind == "Local" ? "NAS    not configured" : $"NAS    {kind}: {dir}",
        };
        if (lab != null)
        {
            var pubs = lab.Sources.Count(s => s.Publisher is { } p && !p.Process.HasExited);
            var readers = lab.Sources.Where(s => s.RelayReaders >= 0).Sum(s => s.RelayReaders);
            var cons = st.Streams.Count(s => s.Session is { } ss && !ss.Process.HasExited);
            l.Add($"RTSP   {pubs} publishers / {cons} consumers" + (lab.Sources.Any(s => s.RelayReaders >= 0) ? $" / {readers} relay readers" : ""));
            l.Add(lab.Http is { } h ? $"HTTP   {h.ServerErrors} errors / {h.NotFound} not found / {h.Requests:N0} requests" : "HTTP   disabled");
        }
        var probeAge = lab?.StorageProbeAt is double t ? TimeSpan.FromSeconds(st.Bus.Now - t) : (TimeSpan?)null;
        var ioWarn = st.Bus.Recent(200).Any(e => e.Category == "Storage" && e.Level != EventLevel.Info && st.Bus.Now - e.T < 60);
        l.Add($"Probe age {(probeAge is { } pa ? $"{(int)pa.TotalMinutes}m{pa.Seconds:00}s" : "-")} | runtime I/O {(ioWarn ? "DEGRADED" : "normal")}");
        l.Add($"Validation {st.Probe?.Queued ?? 0} queued / {st.Probe?.Running ?? 0} running of {st.Probe?.MaxWorkers ?? 0} workers");
        return l;
    }

    private List<ProcessRow> Processes()
    {
        var since = _st.StartedUtc;
        var live = ProcessRegistry.Current.Live.Where(p => p.StartedUtc >= since && p.Role != ProcessRole.Tool);
        var exited = ProcessRegistry.Current.RecentlyExited.Where(p => p.StartedUtc >= since && p.Role is not (ProcessRole.Validator or ProcessRole.Tool)).TakeLast(20);
        return live.Concat(exited).OrderBy(p => p.Role).ThenBy(p => p.Owner).ThenBy(p => p.Id)
            .Select(p => new ProcessRow(p.Role.ToString(), p.Name, p.Owner, p.Pid, p.CpuPercent, p.MemMb, p.Exited ? $"exited {p.ExitCode}" : "running", p.Age)).ToList();
    }

    private string Attribution(BenchState st, int incidents)
    {
        if (incidents == 0) return "none (no incidents)";
        var now = (long)st.Bus.Now;
        if (now - _attrAt >= 2 || _attrAt < 0)
        {
            _attrAt = now;
            var hyp = Correlation.Analyze(st.Bus.Recent(300));
            _attribution = hyp.Count == 0 ? "unresolved" : hyp[^1].Text;
            var upstream = st.Streams.Where(s => s.Attribution != null).Select(s => $"{s.Name}: {s.Attribution}").FirstOrDefault();
            if (upstream != null) _attribution = upstream;
        }
        return _attribution;
    }

    private static List<SourceRow> SourceRows(LabRuntime? lab)
    {
        if (lab == null) return new List<SourceRow>();
        return lab.Sources.Select(s => new SourceRow(s.Id, s.Kind, s.Mode, s.State.ToString().ToUpperInvariant() + (s.State is SourceState.Failed or SourceState.Down ? ": " + s.StateReason : ""),
            CommandText.Mask(s.Url), s.Publisher?.Pid ?? 0, s.Publisher is { } p && !p.Process.HasExited ? p.CpuPercent : 0, s.ReadyAfterSeconds, s.LoopBoundaries, s.RelayReaders,
            string.Join(",", s.Dependents), s.PublisherCommand)).ToList();
    }

    private static List<string> StorageDetail(BenchState st)
    {
        var l = st.Prerequisites.Where(r => r.Id.StartsWith("FS-") || r.Id == "SYS-005" || r.Id == "SYS-012" || r.Id == "SYS-014")
            .Select(r => $"{r.Status.ToString().ToUpperInvariant(),-7} {r.Id,-7} {r.Name}: {r.Diagnosis}").ToList();
        if (st.Lab?.Http is { } h) l.Add($"HTTP    {h.BaseUrl}  {h.Requests} requests, {h.BytesServed / 1048576.0:F1} MB served, {h.NotFound} not found, {h.ServerErrors} errors");
        if (st.Lab?.Relay is { } r) l.Add($"Relay   {r.Status}  config {r.ConfigPath}  log {r.LogPath}");
        if (st.Lab != null) l.Add($"Run dir {st.Lab.RunDir}");
        return l;
    }
}
