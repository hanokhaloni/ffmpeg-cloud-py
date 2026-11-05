using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Reporting;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace FFmpegReliabilityBench.App;

/// <summary>Read-only view over BenchState. Never runs tests or manages FFmpeg itself.</summary>
public static class Dashboard
{
    public static async Task RunAsync(BenchState st, Task run)
    {
        var screen = '1';
        await AnsiConsole.Live(new Text("starting…")).AutoClear(false).Overflow(VerticalOverflow.Ellipsis).Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                while (!run.IsCompleted)
                {
                    while (Console.KeyAvailable)
                    {
                        var k = char.ToUpperInvariant(Console.ReadKey(true).KeyChar);
                        if ("01234567".Contains(k)) screen = k;
                        else if (k == 'Q') { st.RequestStop(); st.Bus.Publish(EventLevel.Info, "Session", "*", "graceful shutdown requested from TUI"); }
                        else if (k == 'S') { try { Reporter.WriteAll(st); st.Bus.Publish(EventLevel.Info, "Report", "*", $"snapshot saved: {st.ReportHtml}"); } catch (Exception ex) { st.Bus.Publish(EventLevel.Warn, "Report", "*", "snapshot failed: " + ex.Message); } }
                    }
                    try { ctx.UpdateTarget(Render(st, screen)); } catch { /* collections changing under us; next frame */ }
                    await Task.WhenAny(run, Task.Delay(500));
                }
                try { ctx.UpdateTarget(Render(st, screen)); } catch { }
            });
    }

    public static string Color(TestStatus s) => s switch { TestStatus.Pass => "green", TestStatus.Warn => "yellow", TestStatus.Fail => "red", _ => "grey" };
    public static string Badge(TestStatus s) => $"[bold {Color(s)}]{s.ToString().ToUpperInvariant()}[/]";
    private static string E(object? o) => Markup.Escape(o?.ToString() ?? "");

    private static IRenderable Render(BenchState st, char screen)
    {
        var running = st.RunStartedAt >= 0 ? st.Bus.Now - st.RunStartedAt : 0;
        var header = new Markup(
            $"[bold]FFmpeg Reliability Benchmark[/]  [grey]{E(st.SessionId)}[/]  phase [bold]{E(st.Phase)}[/]  runtime {TimeSpan.FromSeconds(running):hh\\:mm\\:ss} / {TimeSpan.FromSeconds(st.Config.Benchmark.DurationSeconds):hh\\:mm\\:ss}  " +
            $"prereq {Badge(st.PrereqStatus)}  overall {Badge(st.Overall)}\n" +
            $"[grey]{E(st.Caps?.VersionLine)} · profile {E(st.Config.Benchmark.Profile)} · source {E(CommandText.Mask(st.Config.Source.Url))} · streams {st.Config.Benchmark.ConcurrentStreams}[/]");
        IRenderable body = screen switch
        {
            '2' => Tests(st),
            '3' => Ffmpeg(st),
            '4' => Hls(st),
            '5' => Recording(st),
            '6' => Resources(st),
            '0' => Events(st, 40),
            _ => Overview(st)
        };
        var footer = new Markup("[grey]1[/] overview  [grey]2[/] tests  [grey]3[/] ffmpeg  [grey]4[/] hls  [grey]5[/] recording  [grey]6[/] resources  [grey]0[/] events   [grey]S[/] save report  [grey]Q[/] graceful stop  [grey]Ctrl+C×2[/] abort");
        return new Rows(new Panel(header).Expand().Border(BoxBorder.Rounded), body, footer);
    }

    private static IRenderable Overview(BenchState st)
    {
        var pre = st.Prerequisites.ToList();
        var counts = string.Join("  ", Enum.GetValues<TestStatus>().Select(s => $"{Badge(s)} {pre.Count(r => r.Status == s)}"));
        var t = new Table().Border(TableBorder.Simple).Expand();
        foreach (var c in new[] { "Stream", "PID / state", "Speed / FPS", "Frames", "HLS age", "HLS ok/bad/miss", "Rec ok/bad/miss", "Playback", "CPU / RAM" }) t.AddColumn(c);
        foreach (var s in st.Streams)
        {
            var ses = s.Session; var p = ses?.LastProgress;
            var stColor = ses?.State is SessionState.Running or SessionState.Exited ? "green" : ses?.State is SessionState.Starting or SessionState.Stopping ? "yellow" : "red";
            string Seg() { if (s.Hls == null) return "-"; var v = s.Hls.Segments.Values; return $"[green]{v.Count(x => x.Status == MediaStatus.Valid)}[/]/[red]{v.Count(x => x.Status == MediaStatus.Invalid)}[/]/[red]{v.Count(x => x.Status == MediaStatus.Missing)}[/]"; }
            string Rec() { if (s.Rec == null) return "-"; var v = s.Rec.Files.Values; return $"[green]{v.Count(x => x.Status == MediaStatus.Valid)}[/]/[red]{v.Count(x => x.Status == MediaStatus.Invalid)}[/]/[red]{v.Count(x => x.Status == MediaStatus.Missing)}[/]"; }
            var pb = s.Playback == null ? "-" : $"{s.Playback.Frames} fr, {s.Playback.StallEpisodes} stalls, {s.Playback.DecodeErrors} err";
            t.AddRow(new Markup(E(s.Name)), new Markup($"{ses?.Pid} [{stColor}]{E(ses?.State)}[/]"), new Markup(p == null ? "-" : $"{p.Speed:F2}x / {p.Fps:F1}"), new Markup(p == null ? "-" : p.Frame.ToString()),
                new Markup(s.Hls == null ? "-" : $"{s.Hls.SecondsSinceAdvance:F1}s"), new Markup(Seg()), new Markup(Rec()), new Markup(E(pb)), new Markup(ses == null ? "-" : $"{ses.CpuPercent:F0}% / {ses.WorkingSetMb:F0} MB"));
        }
        var cov = new List<string>();
        foreach (var s in st.Streams)
        {
            if (s.Hls != null) cov.Add($"{E(s.Name)} HLS coverage [bold]{s.Hls.Coverage().Percent:F1}%[/]");
            if (s.Rec != null) cov.Add($"{E(s.Name)} recording coverage [bold]{s.Rec.Coverage().Percent:F1}%[/]");
        }
        var res = st.Resources;
        return new Rows(
            new Markup($"Prerequisites: {counts}   System CPU {res?.LastCpu:F0}%  RAM avail {res?.LastAvailMb:F0} MB"),
            st.Streams.Count > 0 ? t : new Markup("[grey]no FFmpeg streams (yet)[/]"),
            new Markup(cov.Count > 0 ? string.Join("   ", cov) + "  [grey](verified media / expected media)[/]" : ""),
            new Rule("[grey]recent events[/]").LeftJustified(),
            Events(st, 12));
    }

    private static IRenderable Tests(BenchState st)
    {
        var t = new Table().Border(TableBorder.Simple).Expand().AddColumn("ID").AddColumn("Test").AddColumn("Status").AddColumn("Result");
        foreach (var r in st.Prerequisites.ToList().Concat(st.AllRuntimeResults.ToList()))
            t.AddRow(new Markup(E(r.Id)), new Markup(E(r.Name)), new Markup(Badge(r.Status)), new Markup(E(r.Diagnosis)));
        return t;
    }

    private static IRenderable Ffmpeg(BenchState st)
    {
        var rows = new List<IRenderable>();
        foreach (var s in st.Streams)
        {
            var ses = s.Session; var p = ses?.LastProgress;
            rows.Add(new Panel(new Markup(
                $"PID {ses?.Pid}  state {E(ses?.State)}  uptime {ses?.Uptime:hh\\:mm\\:ss}  exit {E(ses?.ExitCode)}  stalls {ses?.StallCount}  last progress {ses?.SecondsSinceProgress:F1}s ago\n" +
                (p == null ? "" : $"frame {p.Frame}  fps {p.Fps:F1}  speed {p.Speed:F2}x  out_time {p.OutTimeSeconds:F1}s  bitrate {p.BitrateKbps:F0} kbit/s  size {p.TotalSize / 1048576.0:F1} MB  drop {p.DropFrames}  dup {p.DupFrames}\n") +
                $"CPU {ses?.CpuPercent:F0}%  RAM {ses?.WorkingSetMb:F0} MB (peak {ses?.PeakWorkingSetMb:F0})  threads {ses?.ThreadCount}  errors: {E(string.Join(", ", ses?.ErrorCounts.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>()))}\n\n" +
                $"[bold]Command[/] [grey](credentials masked)[/]\n{E(s.HumanCommand)}\n\n[bold]ArgumentList[/]\n{E(string.Join("  ", (s.Command?.Args ?? new()).Select((a, i) => $"[{i}]{CommandText.Mask(a)}")))}\n" +
                string.Join("", (s.Command?.Notes ?? new()).Select(n => $"\n[yellow]note:[/] {E(n)}")) +
                (ses == null ? "" : "\n\n[bold]Recent problems[/]\n" + E(string.Join("\n", ses.RecentProblems.TakeLast(6).Select(l => $"{l.TsUtc:HH:mm:ss} [{l.Category}] {CommandText.Mask(l.Raw)}"))))))
                .Header(E(s.Name)).Expand());
        }
        return rows.Count == 0 ? new Markup("[grey]no FFmpeg processes yet[/]") : new Rows(rows);
    }

    private static IRenderable Hls(BenchState st)
    {
        var rows = new List<IRenderable>();
        foreach (var s in st.Streams.Where(s => s.Hls != null))
        {
            var h = s.Hls!; var pl = h.LastPlaylist;
            var t = new Table().Border(TableBorder.Simple).Expand().AddColumn("#").AddColumn("Segment").AddColumn("EXTINF").AddColumn("Probed").AddColumn("Size").AddColumn("Status").AddColumn("Error");
            foreach (var g in h.Segments.Values.OrderByDescending(x => x.Sequence).Take(12))
                t.AddRow(g.Sequence.ToString(), E(g.Uri), $"{g.PlaylistDuration:F3}", $"{g.ProbedDuration:F3}", $"{g.Size / 1024.0:F0} KB", $"[{(g.Status == MediaStatus.Valid ? "green" : g.Status == MediaStatus.Pending ? "grey" : "red")}]{g.Status}[/]", E(g.Error));
            var ds = DurationStats.Of(h.Segments.Values.Where(x => x.Status == MediaStatus.Valid).Select(x => x.PlaylistDuration));
            rows.Add(new Panel(new Rows(new Markup(
                $"{E(h.PlaylistPath)}\nmedia-sequence {pl?.MediaSequence}  target {pl?.TargetDuration}s  entries {pl?.Segments.Count}  last update {h.SecondsSinceAdvance:F1}s ago  stalls {h.StallEpisodes}  violations {h.PlaylistViolations.Count}  read errors {h.PlaylistReadErrors}\n" +
                $"durations: mean {ds.Mean:F3}  min {ds.Min:F3}  max {ds.Max:F3}  sd {ds.StdDev:F3}   consumer: {(s.Playback == null ? "-" : $"TTFF {s.Playback.TimeToFirstFrameSeconds:F1}s, {s.Playback.Frames} frames, {s.Playback.StallEpisodes} stalls, max gap {s.Playback.MaxInterruptionSeconds:F1}s, {s.Playback.DecodeErrors} errors")}"), t)).Header(E(s.Name)).Expand());
        }
        return rows.Count == 0 ? new Markup("[grey]HLS not active[/]") : new Rows(rows);
    }

    private static IRenderable Recording(BenchState st)
    {
        var rows = new List<IRenderable>();
        foreach (var s in st.Streams.Where(s => s.Rec != null))
        {
            var t = new Table().Border(TableBorder.Simple).Expand().AddColumn("File").AddColumn("Size").AddColumn("Duration").AddColumn("Stage").AddColumn("Status").AddColumn("Error");
            foreach (var f in s.Rec!.Files.Values.OrderByDescending(x => x.Index).Take(14))
                t.AddRow(E(f.FileName), $"{f.Size / 1024.0:F0} KB", f.Duration > 0 ? $"{f.Duration:F2}s" : "-", E(f.Stage), $"[{(f.Status == MediaStatus.Valid ? "green" : f.Status == MediaStatus.Pending ? "grey" : "red")}]{f.Status}[/]", E(f.Error));
            var cov = s.Rec.Coverage();
            rows.Add(new Panel(new Rows(new Markup($"{E(s.RecDir)}\ncoverage {cov.Percent:F1}%  valid {cov.Valid}  invalid {cov.Invalid}  missing {cov.Missing}  pending/unverified {cov.Unverified}  stalls {s.Rec.StallEpisodes}"), t)).Header(E(s.Name)).Expand());
        }
        return rows.Count == 0 ? new Markup("[grey]recording not active[/]") : new Rows(rows);
    }

    private static IRenderable Resources(BenchState st)
    {
        var samples = st.Resources?.Samples.ToList() ?? new();
        if (samples.Count == 0) return new Markup("[grey]no samples yet[/]");
        var last = samples[^1];
        var chart = new BarChart().Width(70).Label("[grey]system CPU % (last samples)[/]");
        foreach (var s in samples.TakeLast(15)) chart.AddItem($"T+{s.T:F0}", Math.Round(s.SystemCpu ?? 0), Spectre.Console.Color.Blue);
        var net = last.Net;
        var first = samples.FirstOrDefault(x => x.Net != null)?.Net;
        return new Rows(
            new Markup($"available RAM {last.AvailableMemMb:F0} MB   benchmark working set {last.BenchWorkingSetMb:F0} MB (managed {last.BenchManagedMb:F0} MB)\n" +
                       $"free space: {E(string.Join(", ", last.FreeGb.Select(kv => $"{kv.Key} {kv.Value:F1} GB")))}\n" +
                       (net != null && first != null ? $"TCP resent {net.SegmentsResent - first.SegmentsResent}  resets {net.ResetConnections - first.ResetConnections}  NIC in-err {net.InErrors - first.InErrors}  in-discard {net.InDiscards - first.InDiscards}  rx {(net.BytesReceived - first.BytesReceived) / 1048576.0:F1} MB  tx {(net.BytesSent - first.BytesSent) / 1048576.0:F1} MB" : "network counters unavailable") +
                       "\nper FFmpeg: " + E(string.Join("  ", last.FfmpegCpu.Select(kv => $"{kv.Key} {kv.Value:F0}% {last.FfmpegMemMb.GetValueOrDefault(kv.Key):F0} MB")))),
            chart);
    }

    private static IRenderable Events(BenchState st, int n)
    {
        var lines = st.Bus.Recent(n).Select(e =>
        {
            var c = e.Level switch { EventLevel.Fail => "red", EventLevel.Warn => "yellow", _ => "grey" };
            return $"[{c}]{e.Level.ToString().ToUpperInvariant(),-4}[/] {e.TsUtc:HH:mm:ss} [grey]{E(e.Category)}/{E(e.Stream)}[/] {E(e.Message.Length > 220 ? e.Message[..220] + "…" : e.Message)}";
        });
        return new Markup(string.Join("\n", lines));
    }
}
