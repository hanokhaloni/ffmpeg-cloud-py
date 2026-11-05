using FFmpegReliabilityBench.Core.Engine;

namespace FFmpegReliabilityBench.Core.Lab;

/// <summary>Lab-level verdicts: topology, relay, per-source readiness and stability, HTTP, load by role, startup, cleanup.</summary>
public static class LabEvaluator
{
    public static List<TestResult> Evaluate(BenchState st, LabRuntime rt)
    {
        var r = new List<TestResult>();
        TestResult R(string id, string name, TestStatus s, string d, Dictionary<string, double>? m = null, IEnumerable<string>? ev = null) => TestResult.Make(id, "Lab", name, s, d, m, ev);

        r.Add(R("LAB-001", "Topology and execution strategy", TestStatus.Pass,
            $"{rt.Topology}: {rt.TopologyDetail}. Strategy {rt.Strategy}: {rt.ScheduleText}. Preset {rt.Preset}" +
            (rt.Fixture != null ? $". Fixture {rt.Fixture.Preset} ({rt.Fixture.DurationSeconds:F0}s loop, keyframe every 1 s)" : "") +
            $". Camera clock: {rt.CameraClock}. Source completeness: {rt.SourceCompleteness}.",
            new() { ["sources"] = rt.Sources.Count, ["instances"] = rt.Plan.Count }));

        var relay = rt.Relay;
        if (relay != null && rt.Sources.Any(s => s.IsSynthetic))
        {
            var died = st.Bus.Snapshot().Any(e => e.Category == "Relay" && e.Level == EventLevel.Fail);
            r.Add(R("LAB-002", "RTSP relay", relay.Ready && !died ? TestStatus.Pass : TestStatus.Fail,
                relay.Ready ? $"{relay.Mode} relay {(relay.Version != null ? "MediaMTX " + relay.Version + " " : "")}at {relay.BaseUrl}{(relay.Pid is int pid ? $" (pid {pid})" : "")}; accepted RTSP after {relay.ReadyAfterSeconds:F1}s{(died ? "; EXITED UNEXPECTEDLY during the run" : "; up until shutdown")}" : relay.Error ?? relay.Status));
        }

        foreach (var s in rt.Sources)
        {
            r.Add(R("LAB-003", $"Source media readiness [{s.Id}]", s.ReadyAfterSeconds != null ? TestStatus.Pass : TestStatus.Fail,
                s.ReadyAfterSeconds != null ? $"{s.Kind} source ({s.Mode}) produced decodable media after {s.ReadyAfterSeconds:F1}s ({s.ReadinessFrames} frames) at {Runner.CommandText.Mask(s.Url)}" : $"never decodable: {s.StateReason}",
                new() { ["readySeconds"] = s.ReadyAfterSeconds ?? -1 }));
            if (s.Publisher != null)
            {
                var p = s.Publisher;
                var crashed = s.State == SourceState.Down && !s.KilledByFault;
                r.Add(R("LAB-004", $"Publisher stability [{s.Id}]", s.KilledByFault ? TestStatus.Pass : crashed ? TestStatus.Fail : TestStatus.Pass,
                    s.KilledByFault ? $"killed by fault injection; dependents {string.Join(", ", s.Dependents)} attributed upstream"
                    : crashed ? $"publisher went down during the run: {s.StateReason}; dependents: {string.Join(", ", s.Dependents)}"
                    : $"ran {p.Uptime.TotalSeconds:F0}s until shutdown; speed {p.LastProgress?.Speed:F2}x; {s.LoopBoundaries} fixture loop boundary(ies) (expected, marked in events)",
                    new() { ["loopBoundaries"] = s.LoopBoundaries, ["uptimeSeconds"] = p.Uptime.TotalSeconds }));
            }
        }

        if (rt.Http != null)
        {
            var h = rt.Http;
            r.Add(R("LAB-005", "Local HTTP service", h.ServerErrors > 0 || h.Requests == 0 ? TestStatus.Fail : TestStatus.Pass,
                $"{h.Requests} requests, {h.BytesServed / 1048576.0:F1} MB served, {h.NotFound} not found (expired sliding-window segments are expected), {h.ServerErrors} server errors at {h.BaseUrl}",
                new() { ["requests"] = h.Requests, ["notFound"] = h.NotFound, ["serverErrors"] = h.ServerErrors }, h.RecentErrors));
        }

        var totals = rt.RoleTotalsThisRun();
        var runSeconds = Math.Max(1, st.Bus.Now - Math.Max(0, rt.MeasurementStartedAt));
        string Role(ProcessRole role) { var t = totals.First(x => x.Role == role); return $"{role} {t.Started} started, {t.CpuSeconds:F0} CPU-s"; }
        var gen = totals.First(x => x.Role == ProcessRole.Publisher).CpuSeconds; var cons = totals.First(x => x.Role == ProcessRole.Consumer).CpuSeconds;
        r.Add(R("LAB-006", "Resource accounting by role", TestStatus.Pass,
            $"generator (publishers, {(rt.Sources.Any(s => s.Mode == "live-synthetic") ? "live-synthetic encoders" : "stream-copy loop")}) {gen:F0} CPU-s vs benchmark consumers {cons:F0} CPU-s; " +
            string.Join("; ", new[] { ProcessRole.Relay, ProcessRole.Player, ProcessRole.Validator, ProcessRole.Tool }.Select(Role)) + " (short-lived workers are sampled at 1 Hz, so their CPU is a lower bound)",
            totals.ToDictionary(t => t.Role + "CpuSeconds", t => t.CpuSeconds)));

        var blocked = st.Streams.Where(s => s.RunState == "blocked").ToList();
        r.Add(R("LAB-007", "Startup", blocked.Count > 0 || rt.StartupFailures.Count > 0 ? TestStatus.Fail : TestStatus.Pass,
            blocked.Count == 0 && rt.StartupFailures.Count == 0 ? $"all {st.Streams.Count(s => s.Session != null)} planned consumer instance(s) launched after their source was verified decodable"
            : $"{rt.StartupFailures.Count} startup failure(s); {blocked.Count} consumer(s) not launched because their source failed (other streams continued)",
            new() { ["blocked"] = blocked.Count }, rt.StartupFailures.Concat(blocked.Select(b => $"{b.Name}: {b.Attribution}"))));

        var samples = st.Streams.SelectMany(s => s.HttpRecordingSamples.Select(x => (s.Name, x.File, x.Ok, x.Detail))).ToList();
        if (rt.Http != null)
            r.Add(R("LAB-008", "Completed-recording playback over HTTP (sampled)", samples.Count == 0 ? TestStatus.Unknown : samples.All(x => x.Ok) ? TestStatus.Pass : TestStatus.Fail,
                samples.Count == 0 ? "no recording was sampled (run too short?)" : $"{samples.Count(x => x.Ok)}/{samples.Count} sampled recordings played over HTTP with full decode + seek",
                new() { ["samples"] = samples.Count }, samples.Where(x => !x.Ok).Select(x => $"{x.Name}/{x.File}: {x.Detail}")));

        r.Add(R("LAB-009", "Cleanup of application-owned processes", rt.OwnedStillRunningAfterShutdown > 0 ? TestStatus.Fail : TestStatus.Pass,
            rt.OwnedStillRunningAfterShutdown > 0 ? $"{rt.OwnedStillRunningAfterShutdown} owned process(es) were still running after the ordered shutdown and were killed (own PIDs only)"
            : $"all {rt.RoleTotalsThisRun().Sum(t => t.Started)} owned processes of this run (incl. short-lived probe/decode workers) stopped in order (consumers → validation → publishers → relay → HTTP); " +
              (relay?.Mode == "external" ? "external relay left running (not owned)" : "no foreign process touched"),
            new() { ["leftover"] = Math.Max(0, rt.OwnedStillRunningAfterShutdown) }));
        return r;
    }
}
