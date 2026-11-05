using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FFmpegReliabilityBench.App;
using FFmpegReliabilityBench.App.Tui;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Lab;
using Xunit;
using Xunit.Abstractions;

namespace FFmpegReliabilityBench.Tests;

/// <summary>
/// Acceptance tests for the multi-stream lab (definition of done items 1-15). They start real MediaMTX + FFmpeg processes
/// on free ports and pass trivially when ffmpeg/ffprobe/mediamtx are not available.
/// </summary>
[Collection("ffmpeg")]
public class LabAcceptanceTests
{
    private readonly ITestOutputHelper _out;
    public LabAcceptanceTests(ITestOutputHelper o) { _out = o; }

    private static bool Ready => TestUtil.HaveFfmpeg && RelayManager.ResolveExecutable("tools/mediamtx/mediamtx") != null;
    private static readonly string Fixtures = Path.Combine(Path.GetTempPath(), "streambench-test-fixtures");

    private static int EvenUdpPair()
    {
        var rnd = new Random();
        for (int i = 0; i < 200; i++)
        {
            var p = rnd.Next(10000, 30000) * 2;
            if (Ports.UdpInUse("127.0.0.1", p) == null && Ports.UdpInUse("127.0.0.1", p + 1) == null) return p;
        }
        throw new InvalidOperationException("no free UDP pair");
    }

    private static Dictionary<string, string?> Base(string root)
    {
        var rtp = EvenUdpPair();
        return new()
        {
            ["lab:runRoot"] = root, ["lab:publishers:fixtureDirectory"] = Fixtures, ["lab:publishers:fixtureDurationSeconds"] = "30",
            ["lab:relay:rtspPort"] = TestUtil.FreePort().ToString(), ["lab:relay:rtpPort"] = rtp.ToString(), ["lab:relay:rtcpPort"] = (rtp + 1).ToString(),
            ["lab:relay:apiPort"] = TestUtil.FreePort().ToString(), ["lab:http:port"] = TestUtil.FreePort().ToString(),
            ["storage:smallFileCount"] = "10", ["storage:segmentFileCount"] = "3", ["storage:segmentFileSizeMb"] = "1", ["storage:minimumFreeSpaceGb"] = "0.1",
            ["lab:consumers:staggerMilliseconds"] = "200", ["lab:validation:maxDecodeWorkers"] = "3",
        };
    }

    private static (BenchConfig Cfg, IReadOnlyList<ConfigValue> Values) Config(Dictionary<string, string?> cli, Dictionary<string, List<string>>? lists = null)
    {
        var v = Base(TestUtil.TempDir("frblab"));
        foreach (var kv in cli) v[kv.Key] = kv.Value;
        var r = ConfigLoader.Load(Array.Empty<string>(), v, lists);
        return (r.Config, r.Values);
    }

    private static TestResult Res(BenchState st, string id, string? name = null) => st.AllRuntimeResults.First(r => r.Id == id && (name == null || r.Name.Contains(name)));

    private void Dump(BenchState st)
    {
        _out.WriteLine($"fatal={st.FatalError} overall={st.Overall}");
        foreach (var r in st.AllRuntimeResults.Where(r => r.Status != TestStatus.Pass)) _out.WriteLine($"{r.Id} {r.Status} {r.Name}: {r.Diagnosis}");
    }

    [Fact] // 1, 2, 3, 4, 15
    public async Task Default_lab_runs_four_independent_publishers_and_consumers_with_http_and_recording_playback()
    {
        if (!Ready) return;
        var (c, v) = Config(new() { ["lab:durationSeconds"] = "25" });
        Assert.Equal((4, 1, "dual-copy", "parallel"), (c.Lab.Publishers.Count, c.Lab.Consumers.InstancesPerSource, c.Lab.Consumers.Profiles.Single(), c.Lab.Consumers.Execution));
        var st = new BenchState { Config = c };
        var run = LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None);
        DashboardSnapshot? mid = null;
        var provider = new DashboardStateProvider(st);
        for (int i = 0; i < 160 && mid == null && !run.IsCompleted; i++)
        {
            await Task.Delay(250);
            var s = provider.GetSnapshot();
            if (s.Processes.Count(p => p.Role == "Consumer" && p.State == "running") == 4 && s.Processes.Any(p => p.Role == "Player" && p.State == "running")) mid = s;
        }
        await run; Dump(st);
        Assert.Null(st.FatalError);
        Assert.NotNull(mid);
        foreach (var role in new[] { "Relay", "Publisher", "Consumer", "Player" }) Assert.Contains(mid!.Processes, p => p.Role == role);  // 2
        Assert.Equal(4, mid!.Processes.Count(p => p.Role == "Publisher" && p.State == "running"));                                       // 1
        var d = mid.Streams.First().Detail;                                                                                               // 3
        Assert.Contains("-stream_loop", d.PublisherCommand); Assert.Contains("-f rtsp", d.PublisherCommand);
        Assert.Contains("-f hls", d.ConsumerCommand); Assert.Contains("-f segment", d.ConsumerCommand); Assert.Contains(d.ConsumerArgs, a => a.StartsWith("rtsp://127.0.0.1:"));
        Assert.Equal("INDEPENDENT", st.Lab!.Topology);
        Assert.Equal(4, st.Streams.Select(s => s.HlsDir).Distinct().Count()); Assert.Equal(4, st.Streams.Select(s => s.RecDir).Distinct().Count()); // 4
        Assert.All(st.Streams, s => Assert.NotEmpty(Directory.GetFiles(s.RecDir, "rec_*.mp4")));
        foreach (var s in st.Streams)
        {
            Assert.Equal(TestStatus.Pass, Res(st, "HLS-006", s.Name).Status);
            Assert.Equal(TestStatus.Pass, Res(st, "REC-006", s.Name).Status);
            Assert.Equal(TestStatus.Pass, Res(st, "HLS-008", s.Name).Status);                                                              // 15: HTTP delivery
        }
        Assert.Equal(TestStatus.Pass, Res(st, "LAB-008").Status);                                                                         // 15: recording playback over HTTP
        Assert.Equal(TestStatus.Pass, Res(st, "HLS-009").Status);                                                                         // live HTTP player
        foreach (var id in new[] { "LAB-002", "LAB-005", "LAB-007", "LAB-009" }) Assert.Equal(TestStatus.Pass, Res(st, id).Status);
        Assert.Equal(4, st.AllRuntimeResults.Count(r => r.Id == "LAB-003" && r.Status == TestStatus.Pass));
        Assert.True(File.Exists(st.ReportJson) && File.Exists(st.ReportHtml));
    }

    [Fact] // 5
    public async Task Publisher_failure_affects_only_its_dependent_consumers()
    {
        if (!Ready) return;
        var (c, v) = Config(new() { ["lab:durationSeconds"] = "28", ["lab:publishers:count"] = "2" });
        c.Lab.Faults.Add(new FaultDef { Type = "kill-publisher", Target = "s02", AtSeconds = 12 });
        var st = new BenchState { Config = c };
        await LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None); Dump(st);
        var s1 = st.Streams.Single(s => s.SourceId == "s01"); var s2 = st.Streams.Single(s => s.SourceId == "s02");
        Assert.Null(s1.Attribution);
        Assert.StartsWith("upstream: publisher s02", s2.Attribution);
        Assert.All(new[] { "PROC-001", "HLS-006", "REC-006" }, id => Assert.Equal(TestStatus.Pass, Res(st, id, s1.Name).Status));
        Assert.Contains(s2.Results, r => r.Status == TestStatus.Fail && r.Diagnosis.StartsWith("[upstream: publisher s02"));
        Assert.Equal(TestStatus.Pass, Res(st, "LAB-004", "[s02]").Status); // fault-injected, attributed upstream
        Assert.Equal(TestStatus.Pass, Res(st, "LAB-009").Status);
    }

    [Fact] // 10
    public async Task Busy_ports_produce_a_clear_error_and_their_owners_are_left_alone()
    {
        if (!Ready) return;
        var owner = new TcpListener(IPAddress.Loopback, 0); owner.Start();
        var port = ((IPEndPoint)owner.LocalEndpoint).Port;
        try
        {
            var (c, v) = Config(new() { ["lab:relay:rtspPort"] = port.ToString(), ["lab:durationSeconds"] = "10" });
            var startedBefore = ProcessRegistry.Current.Totals().Sum(t => t.Started);
            var st = new BenchState { Config = c };
            await LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None);
            Assert.NotNull(st.FatalError);
            Assert.Contains($"127.0.0.1:{port} is already in use", st.FatalError);
            Assert.Contains("never stops other processes", st.FatalError);
            Assert.Equal(startedBefore, ProcessRegistry.Current.Totals().Sum(t => t.Started)); // nothing was launched
            using var cl = new TcpClient(); cl.Connect(IPAddress.Loopback, port); Assert.True(cl.Connected); // owner still serving
        }
        finally { owner.Stop(); }
    }

    [Fact] // 13, 14
    public async Task Fan_out_with_sequential_profiles_is_labelled_and_groups_do_not_overlap()
    {
        if (!Ready) return;
        var (c, v) = Config(new() { ["lab:publishers:count"] = "1", ["lab:consumers:instancesPerSource"] = "2", ["lab:consumers:execution"] = "sequential", ["lab:consumers:groupDurationSeconds"] = "12" },
            new() { ["lab:consumers:profiles"] = new() { "hls-copy", "rec-copy" } });
        var st = new BenchState { Config = c };
        await LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None); Dump(st);
        Assert.Equal("FAN-OUT", st.Lab!.Topology);
        Assert.Contains("not 2 independent cameras", st.Lab.TopologyDetail);
        Assert.Equal("sequential", st.Lab.Strategy);
        Assert.Contains("sequential", Res(st, "LAB-001").Diagnosis);
        var g0 = st.Streams.Where(s => s.Group == 0).ToList(); var g1 = st.Streams.Where(s => s.Group == 1).ToList();
        Assert.Equal(2, g0.Count); Assert.Equal(2, g1.Count);
        Assert.All(g0, s => Assert.Equal("hls-copy", s.ProfileId)); Assert.All(g1, s => Assert.Equal("rec-copy", s.ProfileId));
        Assert.True(g0.Max(s => s.Session!.Managed!.ExitedUtc!.Value) <= g1.Min(s => s.Session!.Managed!.StartedUtc), "groups overlapped");
        Assert.All(g0, s => Assert.Contains(s.Results, r => r.Id == "HLS-006" && r.Status == TestStatus.Pass));
        Assert.All(g1, s => Assert.Contains(s.Results, r => r.Id == "REC-006" && r.Status == TestStatus.Pass));
    }

    [Fact] // 11
    public async Task Generator_load_is_reported_separately_from_consumer_load()
    {
        if (!Ready) return;
        var (c, v) = Config(new() { ["lab:publishers:count"] = "1", ["lab:publishers:mode"] = "live-synthetic", ["lab:durationSeconds"] = "15" });
        var st = new BenchState { Config = c };
        await LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None); Dump(st);
        var lab6 = Res(st, "LAB-006");
        Assert.Contains("live-synthetic encoders", lab6.Diagnosis);
        Assert.True(lab6.Metrics["PublisherCpuSeconds"] > lab6.Metrics["ConsumerCpuSeconds"], lab6.Diagnosis); // live H.264 encode vs stream copy
        Assert.True(lab6.Metrics["PublisherCpuSeconds"] > 0);
    }

    [Fact] // 6, 7, 8 (+ confirmation overlay drives the graceful stop)
    public async Task Dashboard_pause_resize_and_log_flood_do_not_disturb_collection()
    {
        if (!Ready) return;
        var (c, v) = Config(new() { ["lab:publishers:count"] = "1", ["lab:durationSeconds"] = "90", ["lab:validation:maxDecodeWorkers"] = "1" });
        var st = new BenchState { Config = c };
        var run = LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None);
        var size = (W: 140, H: 42);
        var tui = new TuiController(new DashboardStateProvider(st), st, c.Ui, Glyphs.Unicode, new TerminalPresenter(TextWriter.Null, true)) { SizeProvider = () => size, ReadConsoleKeys = false, ExitDelayAfterFinishSeconds = 0.5 };
        var ui = tui.RunAsync(CancellationToken.None);
        for (int i = 0; i < 240 && !(st.Streams[0].Rec?.Files.Values.Any(f => f.Status == Core.Validation.MediaStatus.Valid) ?? false); i++) await Task.Delay(250);
        await tui.DispatchAsync(new UiCommand(UiCommandKind.TogglePause), default);
        await Task.Delay(300);
        var frozen = tui.Snapshot; var frames = tui.Frames;
        var hlsBefore = st.Streams[0].Hls!.Segments.Count; var recBefore = st.Streams[0].Rec!.Files.Count;
        var flood = Task.Run(() => { for (int i = 0; i < 100_000; i++) st.Bus.Publish(EventLevel.Warn, "Process", "s01-dual-copy", $"log flood line {i}"); });
        size = (100, 30); await Task.Delay(1200); size = (30, 10); await Task.Delay(1200); size = (140, 42); await Task.Delay(9000);
        await flood;
        Assert.Same(frozen, tui.Snapshot);                                                    // 8: display frozen …
        Assert.True(st.Streams[0].Hls!.Segments.Count > hlsBefore, "HLS monitoring paused"); // … collection, recording and validation continue
        Assert.True(st.Streams[0].Rec!.Files.Count > recBefore, "recording monitoring paused");
        Assert.True(tui.Frames > frames);                                                     // 6/7: UI kept rendering through resize + flood
        Assert.Null(tui.LastRenderError);
        await tui.DispatchAsync(new UiCommand(UiCommandKind.TogglePause), default);
        await Task.Delay(500);
        Assert.NotSame(frozen, tui.Snapshot);
        await tui.DispatchAsync(new UiCommand(UiCommandKind.RequestQuit), default);
        await tui.DispatchAsync(new UiCommand(UiCommandKind.ConfirmYes), default);
        await run; await ui.WaitAsync(TimeSpan.FromSeconds(30));
        Dump(st);
        Assert.True(st.StopRequested);
        Assert.Equal(TestStatus.Pass, Res(st, "LAB-009").Status);
        Assert.Equal(TestStatus.Pass, Res(st, "REC-006").Status);
    }

    [Fact] // 12
    public async Task Headless_and_dashboard_runs_produce_the_same_evidence()
    {
        if (!Ready) return;
        async Task<BenchState> Once(bool tui)
        {
            var (c, v) = Config(new() { ["lab:publishers:count"] = "1", ["lab:durationSeconds"] = "14" });
            var st = new BenchState { Config = c };
            var run = LabOrchestrator.RunAsync(st, "lab", v, CancellationToken.None);
            if (tui) await new TuiController(new DashboardStateProvider(st), st, c.Ui, Glyphs.AsciiSet, new TerminalPresenter(TextWriter.Null, false)) { SizeProvider = () => (140, 42), ReadConsoleKeys = false, ExitDelayAfterFinishSeconds = 0.2 }.RunAsync(CancellationToken.None);
            else await Headless.RunAsync(st, run, new StringWriter(), 2);
            await run;
            return st;
        }
        var a = await Once(false); var b = await Once(true);
        Dump(a); Dump(b);
        string Key(TestResult r) => r.Id + "|" + r.Name;
        Assert.Equal(a.AllRuntimeResults.Select(Key).OrderBy(x => x), b.AllRuntimeResults.Select(Key).OrderBy(x => x));
        foreach (var id in new[] { "HLS-006", "REC-006", "HLS-008", "LAB-009" }) Assert.Equal(Res(a, id).Status, Res(b, id).Status);
        Assert.True(File.Exists(a.ReportJson) && File.Exists(b.ReportJson));
    }

    [Fact] // 9 (real Ctrl+C / SIGINT to the CLI process)
    public async Task Ctrl_c_stops_only_application_owned_processes()
    {
        if (!Ready || OperatingSystem.IsWindows()) return;
        var root = TestUtil.TempDir("frbsigint");
        var b = Base(root);
        var cfgFile = Path.Combine(root, "ports.json");
        int I(string k) => int.Parse(b[k]!);
        File.WriteAllText(cfgFile, $$"""
            { "storage": { "smallFileCount": 10, "segmentFileCount": 3, "segmentFileSizeMb": 1, "minimumFreeSpaceGb": 0.1 },
              "lab": { "runRoot": "{{root.Replace("\\", "\\\\")}}", "publishers": { "count": 1, "fixtureDirectory": "{{Fixtures.Replace("\\", "\\\\")}}", "fixtureDurationSeconds": 30 },
                       "relay": { "rtspPort": {{I("lab:relay:rtspPort")}}, "rtpPort": {{I("lab:relay:rtpPort")}}, "rtcpPort": {{I("lab:relay:rtcpPort")}}, "apiPort": {{I("lab:relay:apiPort")}} },
                       "http": { "port": {{I("lab:http:port")}} } } }
            """);
        // A foreign FFmpeg that StreamBench did not start: it must survive.
        using var foreign = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -re -f lavfi -i anullsrc -f null -") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true })!;
        var dll = Path.Combine(AppContext.BaseDirectory, "StreamBench.dll");
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { dll, "lab", "--no-tui", "--duration", "120", "--config", cfgFile }) psi.ArgumentList.Add(a);
        using var app = Process.Start(psi)!;
        var output = new System.Text.StringBuilder(); var firstOutputs = new TaskCompletionSource();
        app.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (output) output.AppendLine(e.Data); if (e.Data.Contains("first valid outputs")) firstOutputs.TrySetResult(); };
        app.BeginOutputReadLine(); app.BeginErrorReadLine();
        await firstOutputs.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Process.Start("kill", $"-INT {app.Id}")!.WaitForExit();
        await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
        var text = output.ToString(); _out.WriteLine(text.Length > 4000 ? text[^4000..] : text);
        Assert.Contains("Ctrl+C: graceful stop requested", text);
        Assert.Contains("PASS Shutdown and reconciliation", text);
        Assert.InRange(app.ExitCode, 0, 2);
        Assert.False(foreign.HasExited, "a process StreamBench did not start was stopped");
        foreign.Kill();
    }
}
