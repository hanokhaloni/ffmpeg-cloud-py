using System.Diagnostics;
using FFmpegReliabilityBench.App.Tui;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Lab;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

public class TuiRendererTests
{
    public static DashboardSnapshot Sample(int streams = 4)
    {
        var rows = Enumerable.Range(1, streams).Select(i => new StreamRow
        {
            Id = $"s{i:00}-dual-copy", Source = $"s{i:00}", SourceKind = "synthetic", Profile = "dual-copy", Pid = 8200 + i, Cpu = i * 0.7, Speed = 1.01,
            Hls = i == 3 ? "PASS" : "PASS", Rec = i == 3 ? "LATE" : "PASS", State = i == 3 ? "WARN" : "PASS", StateStatus = i == 3 ? TestStatus.Warn : TestStatus.Pass,
            HlsAge = 0.7, HlsSeq = 561, Backlog = i == 3 ? 4 : 0,
            Detail = new StreamDetail
            {
                InputUrl = $"rtsp://127.0.0.1:8554/s{i:00}", PublisherCommand = $"ffmpeg -re -stream_loop -1 -i fixture.mp4 -c copy -f rtsp rtsp://127.0.0.1:8554/s{i:00}",
                ConsumerCommand = "ffmpeg -rtsp_transport tcp -i rtsp://user:***@cam/s -f hls stream.m3u8 -f segment rec_%06d.mp4", ConsumerArgs = new[] { "-i", "rtsp://user:***@cam/s" },
                HlsSummary = "HLS seq 561 | age 0.7s", RecordingSummary = "Recording 223 valid / 1 writing", OutputCoverage = "Output coverage: HLS 100.0% · REC 100.0%", SourceCompleteness = "Source completeness: UNKNOWN"
            }
        }).ToList();
        return new DashboardSnapshot
        {
            Mode = "LAB", Preset = "local-4-streams", Topology = "INDEPENDENT", TopologyDetail = "4 independent sources × 1 consumer", Strategy = "parallel",
            Elapsed = TimeSpan.FromMinutes(18.7), Planned = TimeSpan.FromMinutes(10), Phase = "Running", Current = TestStatus.Warn,
            Res = new ResourceView { CpuNow = 38, CpuHistory = Enumerable.Range(0, 300).Select(i => (double)(i % 50)).ToArray(), RamUsedGb = 4.8, RamTotalGb = 16, WriteHistory = new double[] { 1, 2, 3 }, NetHistory = new double[] { 5, 9 }, StreamsRunning = streams, StreamsTotal = streams,
                Roles = new[] { new RoleTotals(ProcessRole.Relay, 1, 1, 0.3, 20, 1), new RoleTotals(ProcessRole.Publisher, 4, 4, 1.1, 96, 5), new RoleTotals(ProcessRole.Consumer, 4, 4, 8.3, 410, 20) } },
            Prereqs = new[] { new CheckLine(TestStatus.Pass, "", "FFmpeg capabilities", "4/4"), new CheckLine(TestStatus.Pass, "", "Output access", "18/18"), new CheckLine(TestStatus.Unknown, "", "Camera UTC", "pending") },
            StorageNet = new[] { "Local  write p95 4 ms   free 112 GB", "NAS    not configured", "RTSP   4 publishers / 4 consumers", "HTTP   0 errors / 1,482 requests" },
            Streams = rows,
            Processes = new[] { new ProcessRow("Relay", "mediamtx", "relay", 100, 0.3, 20, "running", TimeSpan.FromMinutes(18)), new ProcessRow("Publisher", "publisher-s01", "s01", 101, 0.2, 24, "running", TimeSpan.FromMinutes(18)) },
            Incidents = new[] { new EventLine(DateTime.UtcNow, EventLevel.Warn, "Recording", "s03-dual-copy", "Recording overdue") },
            Attribution = "unresolved",
            Checks = new[] { new CheckLine(TestStatus.Pass, "SYS-007", "FFmpeg executable", "ok"), new CheckLine(TestStatus.Fail, "LAB-003", "Source readiness [s02]", "never decodable") },
            Events = Enumerable.Range(0, 50).Select(i => new EventLine(DateTime.UtcNow, i % 7 == 0 ? EventLevel.Warn : EventLevel.Info, "Lab", "*", $"event {i}")).ToList(),
            Phases = LabRuntime.PhaseNames.Select((n, i) => new PhaseLine(n, i < 10 ? "pass" : "running", "", 1.0)).ToList(),
            Sources = Enumerable.Range(1, streams).Select(i => new SourceRow($"s{i:00}", "synthetic", "loop-file", "READY", $"rtsp://127.0.0.1:8554/s{i:00}", 900 + i, 0.2, 3.4, 18, 1, $"s{i:00}-dual-copy", "ffmpeg …")).ToList(),
            Config = new[] { new ConfigValue("lab:publishers:count", "4", "default"), new ConfigValue("lab:durationSeconds", "600", "command line") }
        };
    }

    [Fact]
    public void Full_layout_at_140x42_has_every_panel()
    {
        var c = Renderer.Render(Sample(), new ViewState(), 139, 42, Glyphs.Unicode, 140);
        var text = c.ToString();
        foreach (var s in new[] { "StreamBench", "LAB: local-4-streams", "CURRENT: WARN", "PREREQUISITES", "STORAGE / NETWORK", "STREAM / COMMAND TABLE", "SELECTED: s01-dual-copy", "INCIDENTS", "COMMAND [redacted", "1 Overview", "6 Lab", "Recording overdue", "Publishers(gen)" })
            Assert.Contains(s, text);
        Assert.StartsWith("└", c.Row(41));                        // bottom border intact
        Assert.Contains("> s01-dual-copy", text);                  // selection marker
        Assert.Equal(Sty.Sel, c.StyleAt(Array.FindIndex(c.Lines().ToArray(), l => l.Contains("> s01-dual-copy")) is var y && y >= 0 ? c.Row(y).IndexOf('>') : 0, y));
        Assert.Contains("rtsp://user:***@cam", text);               // redacted command
    }

    [Fact]
    public void Compact_layout_at_100x30_replaces_lower_panels_with_selected_detail()
    {
        var text = Renderer.Render(Sample(), new ViewState(), 99, 30, Glyphs.Unicode, 100).ToString();
        Assert.Contains("STREAMS", text); Assert.Contains("SELECTED: s01-dual-copy", text);
        Assert.DoesNotContain("STORAGE / NETWORK", text);
        Assert.Equal("compact", Renderer.LayoutFor(100, 30));
    }

    [Fact]
    public void Minimal_view_below_compact_size()
    {
        var c = Renderer.Render(Sample(), new ViewState(), 59, 20, Glyphs.Unicode, 60);
        Assert.Equal("minimal", Renderer.LayoutFor(60, 20));
        Assert.StartsWith("StreamBench LAB", c.Row(0));
        Assert.Contains("s03-dual-copy HLS PASS REC LATE WARN", c.ToString());
    }

    [Fact]
    public void Ascii_fallback_uses_only_ascii()
    {
        var c = Renderer.Render(Sample(), new ViewState(), 139, 42, Glyphs.AsciiSet, 140);
        Assert.All(c.Lines(), l => Assert.True(l.All(ch => ch < 128), l));
        Assert.Equal('+', c.CharAt(0, 0));
    }

    [Fact]
    public void No_color_presenter_emits_no_color_codes()
    {
        var sw = new StringWriter();
        var p = new TerminalPresenter(sw, color: false);
        p.Start(); p.Present(Renderer.Render(Sample(), new ViewState(), 139, 42, Glyphs.Unicode, 140)); p.Stop();
        var o = sw.ToString();
        Assert.DoesNotMatch(@"\x1b\[[0-9;]*3[0-7]m", o); Assert.DoesNotMatch(@"\x1b\[[0-9;]*9[0-7]m", o);
        Assert.Contains("\x1b[?1049h", o); Assert.Contains("\x1b[?1049l", o); // alternate screen entered and restored
    }

    [Fact]
    public void Every_tab_overlay_and_size_renders_without_throwing()
    {
        var snap = Sample(40);
        foreach (var (w, h) in new[] { (200, 60), (140, 42), (139, 41), (100, 30), (99, 29), (80, 24), (40, 12), (20, 5) })
            for (int tab = 1; tab <= 6; tab++)
                foreach (var ov in Enum.GetValues<Overlay>())
                    foreach (var details in new[] { false, true })
                    {
                        var c = Renderer.Render(snap, new ViewState { Tab = tab, Overlay = ov, Details = details, Selected = 39, ListOffset = 1000, CmdScroll = 5000, Wrap = details }, w, h, Glyphs.Unicode, w);
                        Assert.Equal(h, c.H);
                    }
    }

    [Fact]
    public void Filtering_sorting_and_grouping()
    {
        var s = Sample(6);
        Assert.Single(Renderer.VisibleRows(s, new ViewState { Filter = "s02" }));
        Assert.Equal("s06-dual-copy", Renderer.VisibleRows(s, new ViewState { Sort = SortKey.Cpu })[0].Id);       // highest CPU first
        Assert.Equal("s03-dual-copy", Renderer.VisibleRows(s, new ViewState { Sort = SortKey.State })[0].Id);     // worst state first
        Assert.Equal("s03-dual-copy", Renderer.VisibleRows(s, new ViewState { Output = OutputFilter.RecIssues }).Single().Id);
        Assert.Contains("Profile: dual-copy", Renderer.Render(s, new ViewState { Group = GroupBy.Profile }, 139, 42, Glyphs.Unicode, 140).ToString());
    }
}

public class TuiControllerTests
{
    private sealed class FakeProvider : IDashboardStateProvider
    {
        public int Calls; public DashboardSnapshot Snap = TuiRendererTests.Sample();
        public DashboardSnapshot GetSnapshot() { Interlocked.Increment(ref Calls); return Snap with { Elapsed = TimeSpan.FromSeconds(Calls) }; }
    }

    private static (TuiController C, FakeProvider P, BenchState St) Make()
    {
        var st = new BenchState(); var p = new FakeProvider();
        var c = new TuiController(p, st, new UiCfg { RefreshHz = 20 }, Glyphs.Unicode, new TerminalPresenter(TextWriter.Null, true)) { SizeProvider = () => (140, 42), ReadConsoleKeys = false };
        return (c, p, st);
    }

    [Fact]
    public void Quit_needs_confirmation_and_requests_a_graceful_stop()
    {
        var (c, _, st) = Make();
        c.Apply(new UiCommand(UiCommandKind.RequestQuit));
        Assert.Equal(Overlay.ConfirmQuit, c.View.Overlay); Assert.False(st.StopRequested);
        c.Apply(new UiCommand(UiCommandKind.ConfirmNo));
        Assert.Equal(Overlay.None, c.View.Overlay); Assert.False(st.StopRequested);
        c.Apply(new UiCommand(UiCommandKind.RequestQuit)); c.Apply(new UiCommand(UiCommandKind.ConfirmYes));
        Assert.True(st.StopRequested);
    }

    [Fact]
    public void Keys_map_to_commands_including_filter_mode_and_overlays()
    {
        var v = new ViewState();
        Assert.Equal(UiCommandKind.Down, KeyMap.Map(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false), v)!.Value.Kind);
        Assert.Equal(UiCommandKind.Tab, KeyMap.Map(new ConsoleKeyInfo('6', ConsoleKey.D6, false, false, false), v)!.Value.Kind);
        Assert.Equal(UiCommandKind.TogglePause, KeyMap.Map(new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false), v)!.Value.Kind);
        v.FilterEditing = true;
        Assert.Equal(UiCommandKind.FilterChar, KeyMap.Map(new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false), v)!.Value.Kind); // typing, not quitting
        v.FilterEditing = false; v.Overlay = Overlay.ConfirmQuit;
        Assert.Equal(UiCommandKind.ConfirmYes, KeyMap.Map(new ConsoleKeyInfo('y', ConsoleKey.Y, false, false, false), v)!.Value.Kind);
        Assert.Null(KeyMap.Map(new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false), v));
    }

    [Fact]
    public async Task Pause_freezes_the_display_while_collection_continues_and_resize_is_handled()
    {
        var (c, p, st) = Make();
        var size = (W: 140, H: 42);
        c = new TuiController(p, st, new UiCfg { RefreshHz = 20 }, Glyphs.Unicode, new TerminalPresenter(TextWriter.Null, true)) { SizeProvider = () => size, ReadConsoleKeys = false };
        using var cts = new CancellationTokenSource();
        var run = c.RunAsync(cts.Token);
        await Task.Delay(400);
        await c.DispatchAsync(new UiCommand(UiCommandKind.TogglePause), default);
        await Task.Delay(100);
        var frozenCalls = p.Calls; var frozen = c.Snapshot;
        await Task.Delay(500);
        Assert.Equal(frozenCalls, p.Calls);                 // no new snapshots while paused…
        Assert.Same(frozen, c.Snapshot);
        var framesWhilePaused = c.Frames;
        size = (100, 30); await Task.Delay(200); size = (30, 10); await Task.Delay(200); size = (140, 42); await Task.Delay(200);
        Assert.True(c.Frames > framesWhilePaused);          // …but rendering (resize, input) keeps working
        await c.DispatchAsync(new UiCommand(UiCommandKind.TogglePause), default);
        await Task.Delay(300);
        Assert.True(p.Calls > frozenCalls);
        Assert.Null(c.LastRenderError);
        cts.Cancel(); await run;
    }

    [Fact]
    public void Snapshots_stay_fast_under_log_floods()
    {
        var st = new BenchState();
        for (int i = 0; i < 250_000; i++) st.Bus.Publish(i % 3 == 0 ? EventLevel.Warn : EventLevel.Info, "Process", "s01", $"flood line {i}");
        var provider = new DashboardStateProvider(st);
        provider.GetSnapshot();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++) Renderer.Render(provider.GetSnapshot(), new ViewState(), 139, 42, Glyphs.Unicode, 140);
        Assert.True(sw.ElapsedMilliseconds / 10 < 150, $"snapshot+render took {sw.ElapsedMilliseconds / 10} ms per frame under a 250k-event flood");
    }
}
