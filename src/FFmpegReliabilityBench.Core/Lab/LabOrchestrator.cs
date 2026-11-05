using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Reporting;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Lab;

/// <summary>
/// Built-in multi-stream lab. StreamBench owns every process it needs (relay, publishers, benchmark consumers, players,
/// probe/decode workers, HTTP server), supervises them, validates outputs and cleans up only what it started.
/// Startup: configuration → paths/ports → capabilities → prerequisites → fixture → relay → publishers → media readiness per
/// source → HTTP → consumers (staggered) → first outputs → steady measurement → ordered shutdown → report.
/// </summary>
public static class LabOrchestrator
{
    public static async Task RunAsync(BenchState st, string command, IReadOnlyList<ConfigValue> effective, CancellationToken ct)
    {
        var c = st.Config; var lab = c.Lab; var bus = st.Bus;
        var rt = new LabRuntime { Command = command, Preset = lab.Preset, EffectiveConfig = effective, RoleBaseline = ProcessRegistry.Current.Totals() };
        st.Lab = rt;
        rt.RunDir = Path.GetFullPath(Path.Combine(lab.RunRoot, st.SessionId));
        var logs = Path.Combine(rt.RunDir, "logs");
        var consumersRoot = Path.Combine(rt.RunDir, "consumers");
        var scratch = Path.Combine(rt.RunDir, "scratch");
        st.ReportDirectory = Path.Combine(rt.RunDir, "report");
        var relay = new RelayManager(lab.Relay, bus); rt.Relay = relay;
        var consumerRunner = new FFmpegRunner { GracefulStopTimeoutSeconds = lab.Shutdown.GracefulTimeoutSeconds };
        var publisherRunner = new FFmpegRunner { GracefulStopTimeoutSeconds = 5 };
        var playerRunner = new FFmpegRunner { GracefulStopTimeoutSeconds = 3 };
        var workers = lab.Validation.MaxDecodeWorkers > 0 ? lab.Validation.MaxDecodeWorkers : Math.Max(2, Environment.ProcessorCount / 2);
        var probe = new FFprobeRunner(c.Ffmpeg.Ffprobe, c.Ffmpeg.Executable, workers); st.Probe = probe;
        var templates = new Dictionary<string, CommandTemplate>(StringComparer.OrdinalIgnoreCase);
        var capsByExe = new Dictionary<string, Capabilities>();
        var background = new List<Task>(); var firstOutputWatchers = new List<Task<bool>>();
        var shutdownDone = false; var stopping = false; var playerStarted = 0;
        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        void Begin(string name) { var p = rt.Phase(name); p.Status = "running"; p.StartedAt = bus.Now; bus.Publish(EventLevel.Info, "Lab", "*", $"▶ {name}"); }
        void End(string name, string status, string detail = "")
        {
            var p = rt.Phase(name); p.Status = status; p.EndedAt = bus.Now; p.Detail = detail;
            var lvl = status == "fail" ? EventLevel.Fail : status == "warn" ? EventLevel.Warn : EventLevel.Info;
            bus.Publish(lvl, "Lab", "*", $"{status.ToUpperInvariant()} {name}{(detail.Length > 0 ? ": " + detail : "")}");
        }
        void Fatal(string phase, string message) { if (rt.Phase(phase).Status is "pending" or "running") End(phase, "fail", message); st.FatalError = message; }
        bool StopNow() => st.StopRequested || stopCts.IsCancellationRequested || st.FatalError != null && lab.FailFast;

        try
        {
            Directory.CreateDirectory(rt.RunDir);
            // ---------------- 1. Resolve configuration
            Begin("Resolve configuration");
            BuildSources(c, rt);
            foreach (var pid in lab.Consumers.Profiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var t = CommandTemplates.Resolve(pid, lab.Templates);
                CommandTemplates.Validate(t);
                templates[pid] = t;
            }
            var k = Math.Max(1, lab.Consumers.InstancesPerSource);
            var exec = lab.Consumers.Execution.ToLowerInvariant();
            foreach (var b in LabRuntime.Schedule(rt.Sources.Select(s => s.Id).ToList(), lab.Consumers.Profiles, k, exec, lab.Consumers.Stages)) rt.Batches.Add(b);
            rt.Plan.AddRange(rt.Batches.SelectMany(b => b));
            var (topo, detail) = LabRuntime.Classify(rt.Sources.Count(s => s.IsSynthetic), rt.Sources.Count(s => !s.IsSynthetic), k, lab.Consumers.Profiles.Count);
            rt.Topology = topo; rt.TopologyDetail = detail; rt.Strategy = exec;
            var batchSeconds = exec switch
            {
                "sequential" => lab.Consumers.GroupDurationSeconds > 0 ? lab.Consumers.GroupDurationSeconds : Math.Max(30, lab.DurationSeconds / Math.Max(1, rt.Batches.Count)),
                "staged-load" => lab.Consumers.StageDurationSeconds > 0 ? lab.Consumers.StageDurationSeconds : Math.Max(20, lab.DurationSeconds / Math.Max(1, rt.Batches.Count)),
                _ => lab.DurationSeconds
            };
            rt.MeasurementPlannedSeconds = exec == "parallel" ? lab.DurationSeconds : batchSeconds * rt.Batches.Count;
            st.PlannedSeconds = rt.MeasurementPlannedSeconds;
            rt.ScheduleText = exec switch
            {
                "sequential" => $"{rt.Batches.Count} sequential group(s) × {batchSeconds}s: {string.Join(" → ", rt.Batches.Select(b => b[0].ProfileId))}",
                "staged-load" => $"staged load {string.Join(" → ", rt.Batches.Select((b, i) => rt.Batches.Take(i + 1).Sum(x => x.Count)))} active × {batchSeconds}s",
                _ => $"{rt.Plan.Count} instance(s) in parallel for {lab.DurationSeconds}s"
            };
            // Every planned instance exists up front (stable list for UI/report); isolated output directories per instance.
            foreach (var pi in rt.Plan)
            {
                var inst = Path.Combine(consumersRoot, pi.InstanceId);
                var src = rt.Source(pi.SourceId)!;
                st.Streams.Add(new StreamContext
                {
                    Index = st.Streams.Count, Name = pi.InstanceId, HlsDir = Path.Combine(inst, "hls"), RecDir = Path.Combine(inst, "rec"),
                    SourceId = pi.SourceId, SourceKind = src.Kind, ProfileId = pi.ProfileId, InstanceNo = pi.InstanceNo, Group = pi.Batch,
                    InputUrl = src.Url, SegmentListPath = Path.Combine(inst, "segments.csv"), RunState = "planned"
                });
                src.Dependents.Add(pi.InstanceId);
            }
            // Expand every command before launching anything: unknown placeholders and output collisions are rejected now.
            CommandTemplates.CheckCollisions(st.Streams.Select(s => (s.Name, (IReadOnlyList<string>)CommandTemplates.Expand(templates[s.ProfileId], Values(s, rt, "rtsp://placeholder/x")))));
            End("Resolve configuration", "pass", $"preset {lab.Preset}; {rt.Topology}: {rt.TopologyDetail}; {rt.ScheduleText}");
            bus.Publish(EventLevel.Info, "Lab", "*", $"topology {rt.Topology}: {rt.TopologyDetail}");

            // ---------------- 2. Paths, ports and dependencies (nothing has been started yet)
            Begin("Validate paths and port availability");
            var missing = new List<string>();
            foreach (var exe in templates.Values.Select(t => c.ResolveExecutable(t.ExecutableReference)).Append(c.Ffmpeg.Executable).Distinct())
                if (Capabilities.ResolvePath(exe) == null) missing.Add($"FFmpeg: {Path.GetFullPath(exe)} (also searched PATH)");
            if (Capabilities.ResolvePath(c.Ffmpeg.Ffprobe) == null) missing.Add($"FFprobe: {Path.GetFullPath(c.Ffmpeg.Ffprobe)} (also searched PATH)");
            if (rt.Sources.Any(s => s.IsSynthetic) && relay.Mode == "managed" && RelayManager.ResolveExecutable(lab.Relay.Executable) == null)
                missing.Add(Path.IsPathRooted(lab.Relay.Executable)
                    ? $"MediaMTX: {lab.Relay.Executable} (file not found; or use --relay external --relay-url rtsp://host:8554)"
                    : $"MediaMTX: {Path.GetFullPath(lab.Relay.Executable)} (also searched the application folder and PATH for 'mediamtx'; or use --relay external --relay-url rtsp://host:8554)");
            if (missing.Count > 0) { Fatal("Validate paths and port availability", "Missing dependencies (nothing was downloaded or installed):\n  " + string.Join("\n  ", missing)); return; }
            var problems = new List<string>();
            if (rt.Sources.Any(s => s.IsSynthetic))
            {
                if (relay.Mode == "none") problems.Add("synthetic sources need an RTSP relay: use --relay managed (default) or --relay external --relay-url …");
                problems.AddRange(relay.PortProblems());
            }
            if (lab.Http.Enabled && Ports.TcpInUse(lab.Http.ListenAddress, lab.Http.Port) is string hp) problems.Add(hp + " - needed for the local HTTP server (use --http-port N or --no-http)");
            try { Directory.CreateDirectory(consumersRoot); var probeFile = Path.Combine(rt.RunDir, ".write-test"); File.WriteAllText(probeFile, "x"); File.Delete(probeFile); }
            catch (Exception ex) { problems.Add($"run directory {rt.RunDir} is not writable: {ex.Message}"); }
            if (problems.Count > 0) { Fatal("Validate paths and port availability", "Cannot start (StreamBench never stops other processes):\n  " + string.Join("\n  ", problems)); return; }
            End("Validate paths and port availability", "pass", $"run directory {rt.RunDir}; ports free");

            // ---------------- 3. Capabilities
            Begin("Discover capabilities");
            foreach (var exe in templates.Values.Select(t => c.ResolveExecutable(t.ExecutableReference)).Append(c.Ffmpeg.Executable).Distinct())
                capsByExe[exe] = await Capabilities.DiscoverAsync(exe, c.Ffmpeg.Ffprobe, stopCts.Token);
            st.Caps = capsByExe[c.Ffmpeg.Executable];
            End("Discover capabilities", "pass", string.Join("; ", capsByExe.Select(kv => kv.Value.VersionLine)));
            st.Resources = new ResourceMonitor(bus, () => st.Streams.Where(s => s.Session is { } ss && !ss.Process.HasExited).Select(s => (s.Name, s.Session!.CpuPercent, s.Session.WorkingSetMb)).ToList(),
                new Dictionary<string, string> { ["Run"] = rt.RunDir }) { IntervalSeconds = 1, HistoryCapacity = Math.Max(30, c.Ui.HistorySeconds) };
            st.Resources.Start(stopCts.Token);

            // ---------------- 4. Prerequisites (deep storage probe happens here, before streaming, not during measurement)
            Begin("Run prerequisites");
            var pre = Clone(c); pre.Storage.HlsDirectory = consumersRoot; pre.Storage.RecordingDirectory = consumersRoot;
            AddPrereq(st, await PreflightTests.RunAsync(pre, st.Caps, stopCts.Token));
            AddPrereq(st, await FilesystemTests.RunAsync("Run directory", rt.RunDir, c.Storage, stopCts.Token));
            rt.StorageProbeAt = bus.Now;
            foreach (var cam in rt.Sources.Where(s => !s.IsSynthetic))
            {
                var nc = Clone(c); nc.Source.Url = cam.Url;
                AddPrereq(st, (await NetworkTests.RunAsync(nc, stopCts.Token)).Select(r => TestResult.Make(r.Id, r.Category, $"{r.Name} [{cam.Id}]", r.Status, r.Diagnosis, r.Metrics, r.Evidence)));
            }
            if (c.Time.Enabled || rt.Sources.Any(s => !s.IsSynthetic))
            {
                var tr = await TimeTests.RunAsync(c.Time, stopCts.Token);
                AddPrereq(st, tr);
                rt.CameraClock = tr.FirstOrDefault(r => r.Id == "TIME-002") is { } cam ? $"{cam.Status.ToString().ToUpperInvariant()}: {cam.Diagnosis}" : "not checked";
            }
            st.PrereqStatus = Health.Overall(st.Prerequisites);
            var blocking = st.Prerequisites.Where(r => r.Status == TestStatus.Fail && (r.Id is "SYS-007" or "SYS-008" or "SYS-009" or "SYS-010" or "FS-ERR" or "FS-001")).ToList();
            if (blocking.Count > 0) { Fatal("Run prerequisites", "blocking prerequisite failures: " + string.Join("; ", blocking.Select(b => $"{b.Id} {b.Name}: {b.Diagnosis}"))); return; }
            End("Run prerequisites", st.PrereqStatus == TestStatus.Fail ? "warn" : "pass", $"{st.Prerequisites.Count} checks: {st.Prerequisites.Count(r => r.Status == TestStatus.Pass)} pass, {st.Prerequisites.Count(r => r.Status == TestStatus.Warn)} warn, {st.Prerequisites.Count(r => r.Status == TestStatus.Fail)} fail, {st.Prerequisites.Count(r => r.Status == TestStatus.Unknown)} unknown");
            if (StopNow()) return;

            // ---------------- 5. Fixture
            Begin("Prepare fixture");
            if (rt.Sources.Any(s => s.IsSynthetic && s.Mode == "loop-file"))
            {
                rt.Fixture = await Fixture.EnsureAsync(c.Ffmpeg.Executable, probe, lab.Publishers.FixtureDirectory, lab.Publishers.FixturePreset, lab.Publishers.FixtureDurationSeconds, stopCts.Token);
                End("Prepare fixture", "pass", $"{rt.Fixture.Preset} {rt.Fixture.Width}x{rt.Fixture.Height} {rt.Fixture.Fps:0.##} fps {rt.Fixture.DurationSeconds:F0}s, {(rt.Fixture.Generated ? $"generated in {rt.Fixture.PreparationSeconds:F0}s" : "reused")} ({rt.Fixture.Path})");
            }
            else End("Prepare fixture", "skipped", "no loop-file sources");

            // ---------------- 6/7. Relay
            Begin("Start relay");
            if (!rt.Sources.Any(s => s.IsSynthetic)) { End("Start relay", "skipped", "camera sources are read directly"); End("Verify relay readiness", "skipped"); }
            else
            {
                var ok = await relay.StartAsync(logs, stopCts.Token);
                End("Start relay", ok ? "pass" : "fail", relay.Status);
                Begin("Verify relay readiness");
                if (!ok) { Fatal("Verify relay readiness", relay.Error ?? "relay not ready"); return; }
                End("Verify relay readiness", "pass", $"accepting RTSP on {relay.BaseUrl} after {relay.ReadyAfterSeconds:F1}s");
                foreach (var s in rt.Sources.Where(s => s.IsSynthetic)) s.Url = relay.PathUrl(s.Id);
            }

            // ---------------- 8. Publishers (owned by StreamBench, role Publisher)
            Begin("Start publishers");
            foreach (var s in rt.Sources.Where(s => s.IsSynthetic))
            {
                s.PublisherArgs = PublisherArgs(s, c, rt.Fixture, st.Caps);
                s.State = SourceState.Starting;
                var ses = await publisherRunner.StartAsync(new FFmpegJob { Name = "publisher-" + s.Id, Executable = c.Ffmpeg.Executable, Arguments = s.PublisherArgs, LogFile = Path.Combine(logs, $"publisher-{s.Id}.log"), StallTimeoutSeconds = 10, Role = ProcessRole.Publisher, Owner = s.Id }, stopCts.Token);
                s.Publisher = ses; s.PublisherCommand = ses.HumanCommand; s.FixtureDuration = rt.Fixture?.DurationSeconds ?? 0;
                var src = s;
                ses.StateChanged += (_, m) =>
                {
                    if ((m.StartsWith("Crashed") || m.StartsWith("Killed")) && !stopping) SourceDown(st, src, src.KilledByFault ? "killed by fault injection" : $"publisher exited ({m})");
                };
                ses.ProgressUpdated += (_, p) =>
                {
                    if (src.FixtureDuration <= 1 || src.Mode != "loop-file") return;
                    var idx = (int)Math.Floor(p.OutTimeSeconds / src.FixtureDuration);
                    if (idx > src.LoopBoundaries)
                    {
                        src.LoopBoundaries = idx;
                        var now = DateTime.UtcNow;
                        foreach (var dep in st.Streams.Where(x => x.SourceId == src.Id)) lock (dep.LoopBoundariesUtc) dep.LoopBoundariesUtc.Add(now);
                        bus.Publish(EventLevel.Info, "Fixture", src.Id, $"fixture loop boundary #{idx} at media {p.OutTimeSeconds:F1}s (expected transition; not a camera fault)");
                    }
                };
            }
            End("Start publishers", rt.Sources.Any(s => s.IsSynthetic) ? "pass" : "skipped", $"{rt.Sources.Count(s => s.IsSynthetic)} publisher(s), mode {lab.Publishers.Mode}");

            // ---------------- 9. Media readiness per source: decodable media, not just an open port
            Begin("Verify media readiness per source");
            await Task.WhenAll(rt.Sources.Select(s => VerifySourceAsync(s, lab, probe, bus, stopCts.Token)));
            var failed = rt.Sources.Where(s => s.State != SourceState.Ready).ToList();
            foreach (var f in failed) rt.StartupFailures.Add($"source {f.Id}: {f.StateReason}");
            if (failed.Count == rt.Sources.Count) { Fatal("Verify media readiness per source", "no source produced decodable media: " + string.Join("; ", failed.Select(f => $"{f.Id}: {f.StateReason}"))); return; }
            End("Verify media readiness per source", failed.Count == 0 ? "pass" : "fail",
                failed.Count == 0 ? $"{rt.Sources.Count} source(s) decodable" : $"{failed.Count} source(s) failed: {string.Join("; ", failed.Select(f => $"{f.Id}: {f.StateReason}"))} - their consumers will not be launched; other streams continue");
            if (failed.Count > 0 && lab.FailFast) { st.FatalError = "fail-fast: " + rt.StartupFailures[0]; return; }

            // ---------------- 10. HTTP
            Begin("Start HTTP service");
            if (lab.Http.Enabled)
            {
                try
                {
                    rt.Http = new LabHttpServer();
                    await rt.Http.StartAsync(rt.RunDir, lab.Http.ListenAddress, lab.Http.Port, stopCts.Token);
                    End("Start HTTP service", "pass", $"serving {rt.RunDir} at {rt.Http.BaseUrl} (loopback only)");
                }
                catch (Exception ex) { rt.Http = null; End("Start HTTP service", "fail", ex.Message + " - continuing without HTTP validation"); }
            }
            else End("Start HTTP service", "skipped", "disabled");

            // ---------------- 11/12/13. Consumers, first outputs, steady measurement
            st.Phase = "Running";
            Begin("Launch consumers");
            rt.MeasurementStartedAt = bus.Now; st.RunStartedAt = bus.Now;
            var faults = lab.Faults.Select(f => (f, done: false)).ToList();
            var measureClock = System.Diagnostics.Stopwatch.StartNew();
            for (int bi = 0; bi < rt.Batches.Count && !StopNow(); bi++)
            {
                var batch = rt.Batches[bi];
                rt.CurrentBatchLabel = exec switch
                {
                    "sequential" => $"group {bi + 1}/{rt.Batches.Count}: {batch[0].ProfileId}",
                    "staged-load" => $"stage {bi + 1}/{rt.Batches.Count}: {rt.Batches.Take(bi + 1).Sum(b => b.Count)} active",
                    _ => "all instances"
                };
                bus.Publish(EventLevel.Info, "Lab", "*", $"{rt.Strategy}: {rt.CurrentBatchLabel}");
                foreach (var pi in batch)
                {
                    if (StopNow()) break;
                    var ctx = st.Streams.First(s => s.Name == pi.InstanceId);
                    await LaunchAsync(ctx, pi);
                    if (lab.Consumers.StaggerMilliseconds > 0) await Task.Delay(lab.Consumers.StaggerMilliseconds, CancellationToken.None);
                }
                if (bi == 0)
                {
                    End("Launch consumers", rt.StartupFailures.Count > 0 ? "fail" : "pass", $"{st.Streams.Count(s => s.Session != null)} launched, {st.Streams.Count(s => s.RunState == "blocked")} blocked");
                    Begin("Validate first outputs"); Begin("Steady measurement");
                    var watchers = firstOutputWatchers.ToList();
                    _ = Task.Run(async () =>
                    {
                        var res = await Task.WhenAll(watchers);
                        End("Validate first outputs", res.All(x => x) ? "pass" : "fail", $"{res.Count(x => x)}/{res.Length} consumer(s) produced valid HLS and recording outputs");
                    });
                }
                var batchClock = System.Diagnostics.Stopwatch.StartNew(); var lastStats = 0.0;
                while (!StopNow() && batchClock.Elapsed.TotalSeconds < batchSeconds)
                {
                    await Task.Delay(250, CancellationToken.None);
                    for (int i = 0; i < faults.Count; i++)
                    {
                        if (faults[i].done || measureClock.Elapsed.TotalSeconds < faults[i].f.AtSeconds) continue;
                        faults[i] = (faults[i].f, true);
                        InjectFault(st, rt, faults[i].f);
                    }
                    if (batchClock.Elapsed.TotalSeconds - lastStats >= 2)
                    {
                        lastStats = batchClock.Elapsed.TotalSeconds;
                        if (await relay.PathStatsAsync(CancellationToken.None) is { } stats)
                            foreach (var s in rt.Sources) if (stats.FirstOrDefault(p => p.Name == s.Id) is { } ps) { s.RelayReaders = ps.Readers; s.RelayBytesIn = ps.BytesIn; }
                        if (relay.Mode == "managed" && relay.Exited && !stopping)
                            foreach (var s in rt.Sources.Where(s => s.IsSynthetic && s.State == SourceState.Ready)) SourceDown(st, s, "relay (MediaMTX) exited");
                    }
                    var active = st.Streams.Where(s => s.Group == bi || exec != "sequential").Where(s => s.Session != null).ToList();
                    if (active.Count > 0 && active.All(s => s.Session!.Process.HasExited) && (exec != "staged-load" || bi == rt.Batches.Count - 1))
                    { bus.Publish(EventLevel.Warn, "Lab", "*", "all consumers of the current batch have exited - ending this batch early"); break; }
                }
                if (exec == "sequential")
                {
                    // finish this group completely (stop + reconcile) before the next group starts
                    var group = st.Streams.Where(s => s.Group == bi && s.Session != null).ToList();
                    await Task.WhenAll(group.Select(s => StopConsumerAsync(s, consumerRunner, playerRunner)));
                    await Task.WhenAll(group.Select(ReconcileAsync));
                }
            }
            if (rt.Phase("Steady measurement").Status == "running") End("Steady measurement", "pass", $"{measureClock.Elapsed.TotalSeconds:F0}s measured ({rt.ScheduleText})");
            await ShutdownAsync();
        }
        catch (TemplateException ex) { Fatal(rt.Phases.FirstOrDefault(p => p.Status == "running")?.Name ?? "Resolve configuration", ex.Message); }
        catch (ArgumentException ex) { Fatal(rt.Phases.FirstOrDefault(p => p.Status == "running")?.Name ?? "Resolve configuration", ex.Message); }
        catch (OperationCanceledException) { bus.Publish(EventLevel.Warn, "Session", "*", "cancelled"); }
        catch (Exception ex) { st.FatalError = ex.Message; bus.Publish(EventLevel.Fail, "Session", "*", "fatal: " + ex); }
        finally
        {
            if (!shutdownDone) { try { await ShutdownAsync(); } catch (Exception ex) { bus.Publish(EventLevel.Fail, "Session", "*", "shutdown error: " + ex.Message); } }
            st.Phase = "Reporting";
            try { Reporter.WriteAll(st); } catch (Exception ex) { bus.Publish(EventLevel.Fail, "Report", "*", "report failed: " + ex.Message); }
            st.Phase = "Done"; st.Finished = true;
            bus.Publish(EventLevel.Info, "Session", "*", $"finished: prerequisites {st.PrereqStatus}, runtime {st.RuntimeStatus}, overall {st.Overall}");
        }

        // ================= local functions =================
        async Task LaunchAsync(StreamContext ctx, PlannedInstance pi)
        {
            var src = rt.Source(pi.SourceId)!;
            if (src.State != SourceState.Ready)
            {
                ctx.RunState = "blocked";
                ctx.Attribution = $"upstream: source {src.Id} not ready ({src.StateReason})";
                bus.Publish(EventLevel.Fail, "Lab", ctx.Name, $"not launched - {ctx.Attribution}");
                return;
            }
            var tmpl = templates[pi.ProfileId];
            var exe = c.ResolveExecutable(tmpl.ExecutableReference);
            var caps = capsByExe[exe];
            ctx.InputUrl = src.Url;
            Directory.CreateDirectory(ctx.HlsDir); Directory.CreateDirectory(ctx.RecDir);
            var notes = new List<string>();
            var args = CommandTemplates.Instrument(CommandTemplates.Expand(tmpl, Values(ctx, rt, src.Url)), caps, lab.Consumers.InjectRtspTimeout, notes);
            var layout = CommandTemplates.Describe(args);
            ctx.Command = new BuiltCommand(args, notes, pi.ProfileId, layout.RecordingMode, layout.HlsFmp4, layout.PlaylistPath ?? "");
            ctx.HumanCommand = CommandText.Human(exe, args);
            var ic = Clone(c);
            ic.Ffmpeg.Executable = exe;
            ic.Hls.TargetDurationSeconds = layout.HlsTime; ic.Hls.PlaylistSize = layout.HlsListSize; ic.Hls.MaxSegmentAgeSeconds = Math.Max(8, layout.HlsTime * 4);
            ic.Hls.PlaybackTest = false; ic.Hls.HttpUrl = "";
            ic.Recording.SegmentDurationSeconds = layout.SegmentTime; ic.Recording.Mode = layout.RecordingMode;
            ic.Source.ExpectedFps = (int)Math.Round(rt.Fixture?.Fps ?? c.Source.ExpectedFps);
            ctx.EffectiveConfig = ic;
            if (layout.PlaylistPath != null) ctx.Hls = new HlsMonitor(ctx.Name, layout.PlaylistPath, ic.Hls, probe, bus, layout.HlsFmp4, Path.Combine(scratch, ctx.Name));
            if (layout.RecordingDir != null)
                ctx.Rec = new RecordingMonitor(ctx.Name, layout.RecordingDir, ic.Recording, probe, bus, layout.RecordingFileRegex ?? @"^rec_(\d+)\.mp4$", layout.SegmentListPath);
            ctx.RunState = "starting";
            var ses = await consumerRunner.StartAsync(new FFmpegJob
            {
                Name = ctx.Name, Executable = exe, Arguments = args, LogFile = Path.Combine(logs, $"{ctx.Name}.ffmpeg.log"),
                StallTimeoutSeconds = c.Ffmpeg.ProcessStallTimeoutSeconds, Role = ProcessRole.Consumer, Owner = ctx.Name
            }, stopCts.Token);
            ctx.Session = ses;
            ses.Problem += (_, l) =>
            {
                var severe = l.Category is FfErrorCategory.DiskFull or FfErrorCategory.PermissionDenied or FfErrorCategory.MuxerError or FfErrorCategory.FileAccessError or FfErrorCategory.EncoderError;
                bus.PublishThrottled($"{ctx.Name}/{l.Category}", 5, severe ? EventLevel.Fail : EventLevel.Warn, "Process", ctx.Name, $"[{l.Category}] {CommandText.Mask(l.Raw)}");
            };
            ses.StateChanged += (_, m) =>
            {
                if (m.StartsWith("Running")) ctx.RunState = "running";
                if (m.StartsWith("Stalled")) bus.Publish(EventLevel.Fail, "Process", ctx.Name, m);
                if (m.StartsWith("Exited") || m.StartsWith("Crashed") || m.StartsWith("Killed"))
                {
                    ctx.Hls?.MarkProducerExited(); ctx.Rec?.MarkProducerExited();
                    if (m.StartsWith("Crashed") && !stopping)
                    {
                        ctx.RunState = "crashed";
                        if (ctx.Attribution == null && src.State == SourceState.Down) ctx.Attribution = $"upstream: publisher {src.Id} down ({src.StateReason})";
                        bus.Publish(EventLevel.Fail, "Process", ctx.Name, $"benchmark consumer exited unexpectedly ({m}); automatic restart is disabled in diagnostic mode" + (ctx.Attribution != null ? $" [{ctx.Attribution}]" : ""));
                    }
                    else ctx.RunState = ctx.RunState == "crashed" ? "crashed" : "stopped";
                }
            };
            ctx.Hls?.Start(stopCts.Token); ctx.Rec?.Start(stopCts.Token);
            bus.Publish(EventLevel.Info, "Process", ctx.Name, $"consumer started pid {ses.Pid} ({pi.ProfileId} ← {src.Id})");

            if (rt.Http?.Running == true && layout.PlaylistPath != null)
            {
                ctx.HlsHttpUrl = rt.Http.UrlFor(layout.PlaylistPath);
                ic.Hls.HttpUrl = ctx.HlsHttpUrl;
                ctx.Http = new HttpHlsConsumer(ctx.HlsHttpUrl, ctx.Name, probe, bus, Path.Combine(scratch, ctx.Name + "-http"), Path.GetDirectoryName(layout.PlaylistPath));
                ctx.Http.Start(stopCts.Token);
                if (lab.Validation.LivePlayer && Interlocked.CompareExchange(ref playerStarted, 1, 0) == 0)
                    lock (background) background.Add(Task.Run(async () =>
                    {
                        var t0 = System.Diagnostics.Stopwatch.StartNew();
                        while (!stopping && ctx.Hls!.MaxSeq < 2 && t0.Elapsed.TotalSeconds < 60) await Task.Delay(250);
                        if (stopping || ctx.Hls!.MaxSeq < 2) return;
                        ctx.HttpPlayback = new PlaybackConsumer("http-player", ctx.Name, ctx.HlsHttpUrl, ic.Hls.TargetDurationSeconds * 2 + 1, bus);
                        await ctx.HttpPlayback.StartAsync(playerRunner, exe, caps.StatsPeriod, Path.Combine(logs, $"{ctx.Name}.player.log"), stopCts.Token, ProcessRole.Player);
                        bus.Publish(EventLevel.Info, "Playback", ctx.Name, $"live HTTP player started on {ctx.HlsHttpUrl}");
                    }));
            }
            if (rt.Http?.Running == true && ctx.Rec != null && lab.Validation.RecordingHttpSampleEvery > 0)
            {
                var n = 0;
                ctx.Rec.Validated += rec =>
                {
                    if (Interlocked.Increment(ref n) % lab.Validation.RecordingHttpSampleEvery != 1 % lab.Validation.RecordingHttpSampleEvery) return;
                    lock (background) background.Add(Task.Run(() => PlayRecordingOverHttpAsync(ctx, rec, exe)));
                };
            }
            firstOutputWatchers.Add(Task.Run(async () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!stopping && sw.Elapsed.TotalSeconds < lab.Validation.FirstOutputTimeoutSeconds)
                {
                    var hlsOk = ctx.Hls == null || ctx.Hls.Segments.Values.Any(v => v.Status == MediaStatus.Valid);
                    var recOk = ctx.Rec == null || ctx.Rec.Files.Values.Any(v => v.Status == MediaStatus.Valid);
                    if (hlsOk && recOk) { bus.Publish(EventLevel.Info, "Lab", ctx.Name, $"first valid outputs after {sw.Elapsed.TotalSeconds:F1}s"); return true; }
                    if (ses.Process.HasExited) break;
                    await Task.Delay(500);
                }
                if (!stopping) bus.Publish(EventLevel.Fail, "Lab", ctx.Name, $"no valid HLS segment and recording within {lab.Validation.FirstOutputTimeoutSeconds}s of launch");
                return stopping;
            }));
        }

        async Task PlayRecordingOverHttpAsync(StreamContext ctx, RecordingRecord rec, string exe)
        {
            if (rt.Http == null) return;
            var url = rt.Http.UrlFor(rec.Path);
            var full = await ProcessUtil.RunAsync(exe, new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", url, "-map", "0:v:0", "-f", "null", "-" }, TimeSpan.FromSeconds(60), CancellationToken.None, ProcessRole.Player, ctx.Name, "http-rec-player");
            var seekAt = (rec.Duration * 0.5).ToString("0.###", CultureInfo.InvariantCulture);
            var seek = await ProcessUtil.RunAsync(exe, new[] { "-hide_banner", "-nostdin", "-v", "error", "-ss", seekAt, "-i", url, "-t", "1", "-map", "0:v:0", "-f", "null", "-" }, TimeSpan.FromSeconds(60), CancellationToken.None, ProcessRole.Player, ctx.Name, "http-rec-seek");
            static List<string> Errors(ProcResult r) => r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !FFprobeRunner.IsTimestampComplaint(l)).ToList();
            var errs = Errors(full).Concat(Errors(seek)).ToList();
            var ok = full.ExitCode == 0 && seek.ExitCode == 0 && errs.Count == 0;
            var detail = ok ? $"played over HTTP (full decode + seek {seekAt}s)" : $"HTTP playback failed: {string.Join(" | ", errs.Take(3))} (exit {full.ExitCode}/{seek.ExitCode})";
            lock (ctx.HttpRecordingSamples) ctx.HttpRecordingSamples.Add((rec.FileName, ok, detail));
            if (!ok) bus.Publish(EventLevel.Fail, "Playback", ctx.Name, $"{rec.FileName}: {detail}");
        }

        async Task ShutdownAsync()
        {
            if (shutdownDone) return;
            shutdownDone = true; stopping = true;
            if (rt.Phase("Steady measurement").Status == "running") End("Steady measurement", "pass", "stopped");
            Begin("Shutdown and reconciliation");
            st.Phase = "Stopping";
            // 1. no new work is launched (stopping flag)  2. stop consumers gracefully (players first)
            await Task.WhenAll(st.Streams.Where(s => s.Session != null).Select(s => StopConsumerAsync(s, consumerRunner, playerRunner)));
            // 3/4. drain + reconcile completed outputs; bounded validation finishes
            st.Phase = "Final validation";
            await Task.WhenAll(st.Streams.Select(ReconcileAsync));
            Task[] bg; lock (background) bg = background.ToArray();
            try { await Task.WhenAll(bg).WaitAsync(TimeSpan.FromSeconds(90)); } catch { }
            // 5. publishers  6. managed relay  7. HTTP
            await Task.WhenAll(rt.Sources.Where(s => s.Publisher != null && !s.Publisher.Process.HasExited).Select(s => publisherRunner.StopAsync(s.Publisher!, CancellationToken.None)));
            foreach (var s in rt.Sources) if (s.State is SourceState.Ready or SourceState.Starting) s.State = SourceState.Stopped;
            await relay.StopAsync();
            if (rt.Http != null) await rt.Http.StopAsync();
            if (st.Resources != null) await st.Resources.StopAsync();
            try { Directory.Delete(scratch, true); } catch { }
            // Ownership check: everything this session started must be gone; only our own PIDs are ever killed.
            await Task.Delay(300);
            var leftovers = ProcessRegistry.Current.Live.Where(p => p.StartedUtc >= st.StartedUtc).ToList();
            rt.OwnedStillRunningAfterShutdown = leftovers.Count;
            foreach (var p in leftovers) { try { p.Process?.Kill(entireProcessTree: true); } catch { } }
            // Evaluate
            st.Phase = "Evaluating";
            foreach (var s in st.Streams)
            {
                if (s.Session == null)
                {
                    s.Results = new List<TestResult> { TestResult.Make("LAB-007", "Lab", $"Consumer launched [{s.Name}]", st.StopRequested && s.RunState == "planned" ? TestStatus.Unknown : TestStatus.Fail,
                        s.RunState == "blocked" ? $"not launched - {s.Attribution}" : "not launched (run stopped before its batch)") };
                    continue;
                }
                var res = await Evaluator.EvaluateStreamAsync(s, s.EffectiveConfig ?? c, probe, CancellationToken.None);
                if (s.HttpRecordingSamples.Count > 0)
                {
                    List<(string File, bool Ok, string Detail)> sm; lock (s.HttpRecordingSamples) sm = s.HttpRecordingSamples.ToList();
                    res.Add(TestResult.Make("REC-008", "Playback", $"Recording playback over HTTP (sampled) [{s.Name}]", sm.All(x => x.Ok) ? TestStatus.Pass : TestStatus.Fail,
                        $"{sm.Count(x => x.Ok)}/{sm.Count} sampled recordings played over HTTP (full decode + 50% seek)", new() { ["samples"] = sm.Count, ["failed"] = sm.Count(x => !x.Ok) },
                        sm.Select(x => $"{x.File}: {x.Detail}")));
                }
                if (s.Attribution != null)
                    res = res.Select(r => r.Status is TestStatus.Fail or TestStatus.Warn ? TestResult.Make(r.Id, r.Category, r.Name, r.Status, $"[{s.Attribution}] {r.Diagnosis}", r.Metrics, r.Evidence) : r).ToList();
                s.Results = res;
            }
            st.GlobalResults.AddRange(LabEvaluator.Evaluate(st, rt));
            st.GlobalResults.AddRange(Evaluator.EvaluateGlobal(st));
            st.Hypotheses = Correlation.Analyze(st.Bus.Snapshot());
            st.RuntimeStatus = Health.Overall(st.AllRuntimeResults);
            End("Shutdown and reconciliation", leftovers.Count == 0 ? "pass" : "fail", leftovers.Count == 0 ? "all owned processes stopped" : $"{leftovers.Count} owned process(es) had to be killed");
        }
    }

    // ---------------- helpers ----------------

    /// <summary>Every command the lab would launch (publishers + consumers), expanded and instrumented, without starting anything.</summary>
    public static List<(string Role, string Id, string Executable, List<string> Args, List<string> Notes)> DescribeCommands(BenchConfig c, Capabilities caps, string runDir)
    {
        var rt = new LabRuntime { RunDir = runDir };
        BuildSources(c, rt);
        var res = new List<(string, string, string, List<string>, List<string>)>();
        var fx = new FixtureInfo(Path.GetFullPath(Path.Combine(c.Lab.Publishers.FixtureDirectory, Fixture.FileName(c.Lab.Publishers.FixturePreset, c.Lab.Publishers.FixtureDurationSeconds))), c.Lab.Publishers.FixturePreset, 0, 0, 0, 0, "", true, "", false, 0);
        foreach (var s in rt.Sources.Where(s => s.IsSynthetic))
        {
            s.Url = (c.Lab.Relay.Mode == "external" ? c.Lab.Relay.Url.TrimEnd('/') : $"rtsp://{c.Lab.Relay.ListenAddress}:{c.Lab.Relay.RtspPort}") + "/" + s.Id;
            res.Add(("Publisher", s.Id, c.Ffmpeg.Executable, PublisherArgs(s, c, fx, caps), new List<string>()));
        }
        var k = Math.Max(1, c.Lab.Consumers.InstancesPerSource);
        foreach (var pi in LabRuntime.Schedule(rt.Sources.Select(x => x.Id).ToList(), c.Lab.Consumers.Profiles, k, c.Lab.Consumers.Execution, c.Lab.Consumers.Stages).SelectMany(b => b))
        {
            var t = CommandTemplates.Resolve(pi.ProfileId, c.Lab.Templates);
            var src = rt.Source(pi.SourceId)!;
            var inst = Path.Combine(runDir, "consumers", pi.InstanceId);
            var ctx = new StreamContext { Name = pi.InstanceId, SourceId = src.Id, ProfileId = pi.ProfileId, HlsDir = Path.Combine(inst, "hls"), RecDir = Path.Combine(inst, "rec"), SegmentListPath = Path.Combine(inst, "segments.csv") };
            var notes = new List<string>();
            var args = CommandTemplates.Instrument(CommandTemplates.Expand(t, Values(ctx, rt, src.Url)), caps, c.Lab.Consumers.InjectRtspTimeout, notes);
            res.Add(($"Consumer (batch {pi.Batch + 1})", pi.InstanceId, c.ResolveExecutable(t.ExecutableReference), args, notes));
        }
        return res;
    }

    private static Dictionary<string, string> Values(StreamContext s, LabRuntime rt, string inputUrl) => new()
    {
        ["inputUrl"] = inputUrl, ["streamId"] = s.SourceId, ["profileId"] = s.ProfileId, ["instanceId"] = s.Name, ["runDir"] = rt.RunDir,
        ["hlsDir"] = s.HlsDir, ["recordingDir"] = s.RecDir, ["segmentListPath"] = s.SegmentListPath
    };

    private static void BuildSources(BenchConfig c, LabRuntime rt)
    {
        var lab = c.Lab;
        if (lab.Sources.Count > 0)
        {
            var i = 0;
            foreach (var sd in lab.Sources)
            {
                i++;
                var kind = sd.Kind.Equals("camera", StringComparison.OrdinalIgnoreCase) ? "camera" : "synthetic";
                var id = string.IsNullOrWhiteSpace(sd.Id) ? (kind == "camera" ? $"cam{i:00}" : FormatPath(lab.Publishers.PathTemplate, i)) : sd.Id;
                if (kind == "camera" && string.IsNullOrWhiteSpace(sd.Url)) throw new ArgumentException($"camera source '{id}' has no url");
                rt.Sources.Add(new SourceRuntime { Id = id, Kind = kind, Mode = kind == "camera" ? "camera" : (string.IsNullOrWhiteSpace(sd.Mode) ? lab.Publishers.Mode : sd.Mode), Url = kind == "camera" ? CredentialProvider.ApplyTo(sd.Url) : "" });
            }
        }
        else if (lab.Publishers.Count == 0 && c.Source.Url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            rt.Sources.Add(new SourceRuntime { Id = "cam01", Kind = "camera", Mode = "camera", Url = CredentialProvider.ApplyTo(c.Source.Url) });
        else
            for (int i = 1; i <= lab.Publishers.Count; i++)
                rt.Sources.Add(new SourceRuntime { Id = FormatPath(lab.Publishers.PathTemplate, i), Kind = "synthetic", Mode = lab.Publishers.Mode });
        if (rt.Sources.Count == 0) throw new ArgumentException("no sources: set lab.publishers.count > 0, lab.sources, or --source rtsp://…");
        foreach (var s in rt.Sources.Where(s => s.IsSynthetic && s.Mode is not ("loop-file" or "live-synthetic")))
            throw new ArgumentException($"source {s.Id}: unknown publisher mode '{s.Mode}' (loop-file | live-synthetic)");
        var dup = rt.Sources.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new ArgumentException($"duplicate source id '{dup.Key}'");
    }

    /// <summary>"s{index:00}" → s01 …</summary>
    public static string FormatPath(string template, int index) =>
        Regex.Replace(template, @"\{index(?::([0#.]+))?\}", m => m.Groups[1].Success ? index.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture) : index.ToString(CultureInfo.InvariantCulture));

    private static List<string> PublisherArgs(SourceRuntime s, BenchConfig c, FixtureInfo? fx, Capabilities caps)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "level+warning", "-progress", "pipe:1" };
        if (caps.StatsPeriod) a.AddRange(new[] { "-stats_period", "1" });
        var t = c.Lab.Publishers.Transport;
        if (s.Mode == "loop-file")
            a.AddRange(new[] { "-re", "-stream_loop", "-1", "-i", fx!.Path, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f", "rtsp", "-rtsp_transport", t, s.Url });
        else
        {
            var (w, h, fps) = Fixture.Presets.GetValueOrDefault(c.Lab.Publishers.FixturePreset, (1280, 720, 25));
            a.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"testsrc2=size={w}x{h}:rate={fps}[out0];sine=frequency=440:sample_rate=48000:beep_factor=4[out1]",
                "-map", "0:v:0", "-map", "0:a:0", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-pix_fmt", "yuv420p",
                "-g", fps.ToString(), "-keyint_min", fps.ToString(), "-sc_threshold", "0", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-f", "rtsp", "-rtsp_transport", t, s.Url });
        }
        return a;
    }

    private static async Task VerifySourceAsync(SourceRuntime s, LabCfg lab, FFprobeRunner probe, EventBus bus, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); var lastErr = "";
        s.State = SourceState.Starting;
        while (sw.Elapsed.TotalSeconds < lab.Publishers.StartupTimeoutSeconds && !ct.IsCancellationRequested)
        {
            if (s.Publisher?.Process.HasExited == true) { lastErr = $"publisher exited (code {s.Publisher.ExitCode}) - see logs/publisher-{s.Id}.log"; break; }
            var (frames, err) = await probe.DecodedFramesAsync(s.Url, 2, lab.Publishers.Transport, ct);
            if (frames > 0)
            {
                s.State = SourceState.Ready; s.ReadyAfterSeconds = sw.Elapsed.TotalSeconds; s.ReadinessFrames = frames; s.StateReason = "decodable";
                bus.Publish(EventLevel.Info, "Lab", s.Id, $"source {s.Id} decodable after {sw.Elapsed.TotalSeconds:F1}s ({frames} frames)");
                return;
            }
            lastErr = err.Length > 0 ? CommandText.Mask(err) : "no decodable frames";
            await Task.Delay(1000, ct);
        }
        s.State = SourceState.Failed; s.StateReason = lastErr.Length > 0 ? lastErr : $"not decodable within {lab.Publishers.StartupTimeoutSeconds}s";
        bus.Publish(EventLevel.Fail, "Lab", s.Id, $"source {s.Id} failed readiness: {s.StateReason}");
    }

    private static void SourceDown(BenchState st, SourceRuntime s, string reason)
    {
        if (s.State == SourceState.Down) return;
        s.State = SourceState.Down; s.StateReason = reason; s.DownSinceUtc = DateTime.UtcNow;
        var t = st.Bus.Now - (st.Lab?.MeasurementStartedAt ?? 0);
        var deps = st.Streams.Where(x => x.SourceId == s.Id && x.Session != null).ToList();
        foreach (var d in deps) d.Attribution ??= $"upstream: publisher {s.Id} {reason} at T+{t:F0}s";
        st.Bus.Publish(s.KilledByFault ? EventLevel.Warn : EventLevel.Fail, "Publisher", s.Id,
            $"source {s.Id} DOWN ({reason}); dependent consumers: {(deps.Count == 0 ? "none" : string.Join(", ", deps.Select(d => d.Name)))}; other sources unaffected");
    }

    private static void InjectFault(BenchState st, LabRuntime rt, FaultDef f)
    {
        switch (f.Type.ToLowerInvariant())
        {
            case "kill-publisher":
                if (rt.Source(f.Target) is { Publisher: { } p } src)
                {
                    src.KilledByFault = true;
                    st.Bus.Publish(EventLevel.Warn, "FaultInjection", src.Id, $"killing publisher {src.Id} (pid {p.Pid})");
                    FFmpegRunner.Kill(p);
                }
                else st.Bus.Publish(EventLevel.Warn, "FaultInjection", f.Target, $"kill-publisher: no running publisher '{f.Target}'");
                break;
            case "kill-consumer":
                if (st.Streams.FirstOrDefault(s => s.Name == f.Target) is { Session: { } cs } ctx)
                {
                    ctx.KilledByFaultInjection = true;
                    st.Bus.Publish(EventLevel.Warn, "FaultInjection", ctx.Name, $"killing consumer {ctx.Name} (pid {cs.Pid})");
                    FFmpegRunner.Kill(cs);
                }
                break;
            default:
                st.Bus.Publish(EventLevel.Warn, "FaultInjection", "*", $"unknown fault type '{f.Type}'");
                break;
        }
    }

    private static async Task StopConsumerAsync(StreamContext s, FFmpegRunner consumers, FFmpegRunner players)
    {
        if (s.HttpPlayback != null) await s.HttpPlayback.StopAsync(players);
        if (s.Playback != null) await s.Playback.StopAsync(players);
        if (s.Http != null) await s.Http.StopAsync();
        if (s.Session != null)
        {
            if (!s.Session.Process.HasExited) await consumers.StopAsync(s.Session, CancellationToken.None);
            s.ProducerSeconds = s.Session.Uptime.TotalSeconds;
        }
        s.Rec?.MarkProducerExited(); s.Hls?.MarkProducerExited();
        if (s.RunState is "running" or "starting") s.RunState = "stopped";
    }

    private static async Task ReconcileAsync(StreamContext s)
    {
        if (s.Hls != null) await s.Hls.StopAsync();
        if (s.Rec != null) await s.Rec.StopAsync();
    }

    private static void AddPrereq(BenchState st, IEnumerable<TestResult> rs)
    {
        foreach (var r in rs)
        {
            lock (st.Prerequisites) st.Prerequisites.Add(r);
            if (r.Status != TestStatus.Pass) st.Bus.Publish(r.Status == TestStatus.Fail ? EventLevel.Fail : EventLevel.Warn, r.Category, "*", $"{r.Id} {r.Name}: {r.Status} - {r.Diagnosis}");
        }
    }

    public static BenchConfig Clone(BenchConfig c)
    {
        var o = new JsonSerializerOptions { IncludeFields = false };
        var copy = JsonSerializer.Deserialize<BenchConfig>(JsonSerializer.Serialize(c, o), o)!;
        return copy;
    }
}
