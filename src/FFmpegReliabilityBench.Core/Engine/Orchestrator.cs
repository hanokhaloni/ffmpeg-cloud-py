using System.Diagnostics;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Reporting;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Engine;

/// <summary>Owns the whole lifecycle: preflight → filesystem → network → time → producers + monitors + consumers → final validation → evaluation → reports.</summary>
public static class Orchestrator
{
    public static async Task RunAsync(BenchState st, CancellationToken ct)
    {
        var c = st.Config; var bus = st.Bus;
        var hlsOn = c.Hls.Enabled && st.Mode != RunMode.RecordingOnly;
        var recOn = c.Recording.Enabled && st.Mode != RunMode.HlsOnly;
        st.ReportDirectory = Path.GetFullPath(Path.Combine(c.ReportDirectory, st.SessionId));
        Directory.CreateDirectory(st.ReportDirectory);
        var runner = new FFmpegRunner();
        try
        {
            bus.Publish(EventLevel.Info, "Session", "*", $"session {st.SessionId} started (mode {st.Mode}, profile {c.Benchmark.Profile})");
            if (st.Mode != RunMode.FilesystemOnly)
            {
                st.Phase = "Preflight";
                st.Caps = await Capabilities.DiscoverAsync(c.Ffmpeg.Executable, c.Ffmpeg.Ffprobe, ct);
                bus.Publish(EventLevel.Info, "Preflight", "*", st.Caps.VersionLine.Length > 0 ? st.Caps.VersionLine : "ffmpeg not executable");
                Add(st, await PreflightTests.RunAsync(c, st.Caps, ct));
            }
            st.Phase = "Filesystem";
            var dirs = new List<(string, string)>();
            if (hlsOn) dirs.Add(("HLS", c.Storage.HlsDirectory));
            if (recOn && !(hlsOn && SamePath(c.Storage.HlsDirectory, c.Storage.RecordingDirectory))) dirs.Add(("Recording", c.Storage.RecordingDirectory));
            foreach (var (label, dir) in dirs) Add(st, await FilesystemTests.RunAsync(label, dir, c.Storage, ct));
            if (st.Mode != RunMode.FilesystemOnly)
            {
                st.Phase = "Network"; Add(st, await NetworkTests.RunAsync(c, ct));
                st.Phase = "Time"; Add(st, await TimeTests.RunAsync(c.Time, ct));
            }
            st.PrereqStatus = Health.Overall(st.Prerequisites);
            if (st.Mode is RunMode.PreflightOnly or RunMode.FilesystemOnly) return;

            var blocking = st.Prerequisites.Where(r => r.Status == TestStatus.Fail && (r.Id is "SYS-007" or "SYS-008" or "SYS-009" or "SYS-010" or "FS-ERR" or "FS-001" or "NET-005")).ToList();
            if (blocking.Count > 0)
            {
                st.FatalError = "cannot start benchmark, blocking prerequisite failures: " + string.Join("; ", blocking.Select(b => $"{b.Id} {b.Name}: {b.Diagnosis}"));
                bus.Publish(EventLevel.Fail, "Session", "*", st.FatalError);
                return;
            }

            await RunStreamsAsync(st, runner, hlsOn, recOn, ct);
        }
        catch (OperationCanceledException) { bus.Publish(EventLevel.Warn, "Session", "*", "cancelled"); }
        catch (Exception ex) { st.FatalError = ex.Message; bus.Publish(EventLevel.Fail, "Session", "*", "fatal: " + ex); }
        finally
        {
            foreach (var s in st.Streams) if (s.Session is { } ses && !ses.Process.HasExited) FFmpegRunner.Kill(ses);
            foreach (var s in st.Streams) foreach (var p in new[] { s.Playback, s.HttpPlayback }) if (p?.Session is { } ps && !ps.Process.HasExited) FFmpegRunner.Kill(ps);
            st.Phase = "Reporting";
            try { Reporter.WriteAll(st); } catch (Exception ex) { bus.Publish(EventLevel.Fail, "Report", "*", "report failed: " + ex.Message); }
            st.Phase = "Done"; st.Finished = true;
            bus.Publish(EventLevel.Info, "Session", "*", $"finished: prerequisites {st.PrereqStatus}, runtime {st.RuntimeStatus}, overall {st.Overall}");
        }
    }

    private static async Task RunStreamsAsync(BenchState st, FFmpegRunner runner, bool hlsOn, bool recOn, CancellationToken ct)
    {
        var c = st.Config; var bus = st.Bus; var caps = st.Caps!;
        var consumerRunner = new FFmpegRunner { GracefulStopTimeoutSeconds = 3 }; // consumers have nothing to finalize
        var probe = new FFprobeRunner(c.Ffmpeg.Ffprobe, c.Ffmpeg.Executable);
        var scratch = Path.Combine(Path.GetTempPath(), "frb_" + st.SessionId);
        st.Phase = "Starting";
        for (int k = 0; k < Math.Max(1, c.Benchmark.ConcurrentStreams); k++)
        {
            var name = $"stream-{k + 1:D2}";
            // Per-session subfolders: never overwrite or delete existing production media.
            var ctx = new StreamContext { Index = k, Name = name, HlsDir = Path.GetFullPath(Path.Combine(c.Storage.HlsDirectory, st.SessionId, name)), RecDir = Path.GetFullPath(Path.Combine(c.Storage.RecordingDirectory, st.SessionId, name)) };
            if (hlsOn) Directory.CreateDirectory(ctx.HlsDir);
            if (recOn) Directory.CreateDirectory(ctx.RecDir);
            ctx.Command = CommandBuilder.Build(c, caps, ctx.HlsDir, ctx.RecDir, hlsOn, recOn);
            ctx.HumanCommand = CommandText.Human(caps.ExecutablePath, ctx.Command.Args);
            foreach (var n in ctx.Command.Notes) bus.Publish(EventLevel.Info, "Config", name, n);
            st.Streams.Add(ctx);
            if (hlsOn) ctx.Hls = new HlsMonitor(name, ctx.Command.PlaylistPath, c.Hls, probe, bus, ctx.Command.HlsFmp4, Path.Combine(scratch, name));
            if (recOn) ctx.Rec = new RecordingMonitor(name, ctx.RecDir, c.Recording, probe, bus);

            var ses = await runner.StartAsync(new FFmpegJob { Name = name, Executable = c.Ffmpeg.Executable, Arguments = ctx.Command.Args, LogFile = Path.Combine(st.ReportDirectory, $"{name}.ffmpeg.log"), StallTimeoutSeconds = c.Ffmpeg.ProcessStallTimeoutSeconds }, ct);
            ctx.Session = ses;
            ses.Problem += (_, l) =>
            {
                var severe = l.Category is FfErrorCategory.DiskFull or FfErrorCategory.PermissionDenied or FfErrorCategory.MuxerError or FfErrorCategory.FileAccessError or FfErrorCategory.EncoderError;
                bus.PublishThrottled($"{name}/{l.Category}", 5, severe ? EventLevel.Fail : EventLevel.Warn, "Process", name, $"[{l.Category}] {CommandText.Mask(l.Raw)}");
            };
            ses.StateChanged += (_, m) =>
            {
                bus.Publish(m.StartsWith("Stalled") || m.StartsWith("Crashed") ? EventLevel.Fail : EventLevel.Info, "Process", name, m);
                if (m.StartsWith("Exited") || m.StartsWith("Crashed") || m.StartsWith("Killed")) { ctx.Hls?.MarkProducerExited(); ctx.Rec?.MarkProducerExited(); }
            };
            bus.Publish(EventLevel.Info, "Process", name, $"FFmpeg started pid {ses.Pid}");
            ctx.Hls?.Start(ct);
            ctx.Rec?.Start(ct);

            if (hlsOn && c.Hls.PlaybackTest)
                _ = Task.Run(async () =>
                {
                    var t0 = Stopwatch.StartNew();
                    while (!ct.IsCancellationRequested && !st.StopRequested && (ctx.Hls!.MaxSeq < 1) && t0.Elapsed.TotalSeconds < c.Ffmpeg.StartupTimeoutSeconds + 30) await Task.Delay(250);
                    if (ctx.Hls!.MaxSeq < 1 || st.StopRequested || ses.Process.HasExited) return;
                    var stall = c.Hls.TargetDurationSeconds * 2 + 1;
                    ctx.Playback = new PlaybackConsumer("local-consumer", name, ctx.Command.PlaylistPath, stall, bus);
                    await ctx.Playback.StartAsync(consumerRunner, c.Ffmpeg.Executable, caps.StatsPeriod, Path.Combine(st.ReportDirectory, $"{name}.consumer.log"), ct);
                    bus.Publish(EventLevel.Info, "Playback", name, "independent HLS consumer started (local file path)");
                    if (!string.IsNullOrWhiteSpace(c.Hls.HttpUrl))
                    {
                        var url = c.Hls.HttpUrl.Replace("{session}", st.SessionId).Replace("{stream}", name);
                        ctx.Http = new HttpHlsConsumer(url, name, probe, bus, Path.Combine(scratch, name + "_http"));
                        ctx.Http.Start(ct);
                        ctx.HttpPlayback = new PlaybackConsumer("http-consumer", name, url, stall, bus);
                        await ctx.HttpPlayback.StartAsync(consumerRunner, c.Ffmpeg.Executable, caps.StatsPeriod, Path.Combine(st.ReportDirectory, $"{name}.http-consumer.log"), ct);
                        bus.Publish(EventLevel.Info, "Playback", name, $"HTTP consumer started: {url}");
                    }
                }, ct);
        }

        var dirs = new Dictionary<string, string>();
        if (hlsOn) dirs["HLS"] = c.Storage.HlsDirectory;
        if (recOn) dirs["Recording"] = c.Storage.RecordingDirectory;
        st.Resources = new ResourceMonitor(bus, () => st.Streams.Where(s => s.Session != null).Select(s => (s.Name, s.Session!.CpuPercent, s.Session.WorkingSetMb)).ToList(), dirs);
        st.Resources.Start(ct);

        st.Phase = "Running";
        st.RunStartedAt = bus.Now;
        var sw = Stopwatch.StartNew(); var killed = false; double lastSample = 0;
        while (!ct.IsCancellationRequested && !st.StopRequested && sw.Elapsed.TotalSeconds < c.Benchmark.DurationSeconds)
        {
            await Task.Delay(500, CancellationToken.None);
            if (c.Benchmark.InjectKillAtSeconds > 0 && !killed && sw.Elapsed.TotalSeconds >= c.Benchmark.InjectKillAtSeconds)
            {
                killed = true;
                foreach (var s in st.Streams) { s.KilledByFaultInjection = true; FFmpegRunner.Kill(s.Session!); }
                bus.Publish(EventLevel.Warn, "FaultInjection", "*", "FFmpeg forcibly terminated (no muxer finalization) - checking for incomplete media");
            }
            if (sw.Elapsed.TotalSeconds - lastSample >= 2)
            {
                lastSample = sw.Elapsed.TotalSeconds;
                foreach (var s in st.Streams) if (s.Session is { } ss && !ss.Process.HasExited) lock (s.ProcSeries) s.ProcSeries.Add((bus.Now, ss.CpuPercent, ss.WorkingSetMb));
            }
            if (st.Streams.All(s => s.Session!.Process.HasExited)) { await Task.Delay(2000); bus.Publish(killed ? EventLevel.Info : EventLevel.Fail, "Session", "*", "all FFmpeg producers have exited - ending run"); break; }
        }

        st.Phase = "Stopping";
        await Task.WhenAll(st.Streams.Select(async s => { if (s.Playback != null) await s.Playback.StopAsync(consumerRunner); if (s.HttpPlayback != null) await s.HttpPlayback.StopAsync(consumerRunner); if (s.Http != null) await s.Http.StopAsync(); }));
        await Task.WhenAll(st.Streams.Select(async s =>
        {
            if (!s.Session!.Process.HasExited) await runner.StopAsync(s.Session, CancellationToken.None);
            s.ProducerSeconds = s.Session.Uptime.TotalSeconds;
            s.Rec?.MarkProducerExited();
        }));
        bus.Publish(EventLevel.Info, "Session", "*", "producers stopped; final validation of remaining media");
        st.Phase = "Final validation";
        await Task.WhenAll(st.Streams.Select(async s => { if (s.Hls != null) await s.Hls.StopAsync(); if (s.Rec != null) await s.Rec.StopAsync(); }));
        await st.Resources.StopAsync();
        try { Directory.Delete(scratch, true); } catch { }

        st.Phase = "Evaluating";
        foreach (var s in st.Streams) s.Results = await Evaluator.EvaluateStreamAsync(s, c, probe, CancellationToken.None);
        st.GlobalResults.AddRange(Evaluator.EvaluateGlobal(st));
        st.Hypotheses = Correlation.Analyze(bus.Snapshot());
        foreach (var h in st.Hypotheses.Take(10)) bus.Publish(EventLevel.Warn, "Correlation", h.Stream, h.Text);
        st.RuntimeStatus = Health.Overall(st.AllRuntimeResults);
    }

    private static void Add(BenchState st, IEnumerable<TestResult> rs)
    {
        foreach (var r in rs)
        {
            lock (st.Prerequisites) st.Prerequisites.Add(r);
            if (r.Status != TestStatus.Pass) st.Bus.Publish(r.Status == TestStatus.Fail ? EventLevel.Fail : EventLevel.Warn, r.Category, "*", $"{r.Id} {r.Name}: {r.Status} - {r.Diagnosis}");
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
