using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Engine;

/// <summary>Turns monitor state into PASS/WARN/FAIL/UNKNOWN results with metrics + evidence. Never averages statuses.</summary>
public static class Evaluator
{
    private static TestResult R(string id, string cat, string name, TestStatus s, string d, Dictionary<string, double>? m = null, IEnumerable<string>? ev = null) => TestResult.Make(id, cat, name, s, d, m, ev);

    public static async Task<List<TestResult>> EvaluateStreamAsync(StreamContext s, BenchConfig c, FFprobeRunner probe, CancellationToken ct)
    {
        var r = new List<TestResult>(); var n = s.Name; var ses = s.Session!;
        string P(string x) => $"{x} [{n}]";

        // ---------------- Process ----------------
        var expectedEnd = ses.State is SessionState.Exited;
        if (s.KilledByFaultInjection)
            r.Add(R("PROC-001", "Process", P("FFmpeg process lifetime"), TestStatus.Pass, $"terminated by fault injection after {ses.Uptime.TotalSeconds:F0}s (expected)", new() { ["uptimeSeconds"] = ses.Uptime.TotalSeconds }));
        else
            r.Add(R("PROC-001", "Process", P("FFmpeg process lifetime"), expectedEnd ? TestStatus.Pass : TestStatus.Fail,
                expectedEnd ? $"ran {ses.Uptime.TotalSeconds:F0}s, stopped gracefully (exit {ses.ExitCode})"
                : ses.GracefulStopFailed ? $"did not exit within the graceful-stop timeout after 'q' and was killed after {ses.Uptime.TotalSeconds:F0}s - outputs may be unfinalized (blocked input or muxer?)"
                : $"state {ses.State}, exit code {ses.ExitCode} after {ses.Uptime.TotalSeconds:F0}s - unexpected termination",
                new() { ["uptimeSeconds"] = ses.Uptime.TotalSeconds, ["exitCode"] = ses.ExitCode ?? -1 }, ses.RecentProblems.TakeLast(10).Select(l => l.Raw)));
        var lp = ses.LastProgress;
        r.Add(R("PROC-002", "Process", P("Forward progress watchdog"), lp == null || lp.Frame == 0 ? TestStatus.Fail : ses.StallCount > 0 ? TestStatus.Fail : TestStatus.Pass,
            lp == null ? "no progress output ever received" : $"{ses.StallCount} stall(s) (>{c.Ffmpeg.ProcessStallTimeoutSeconds}s), max progress gap {ses.MaxProgressGapSeconds:F1}s, time to first frame {ses.TimeToFirstFrameSeconds:F1}s",
            new() { ["stalls"] = ses.StallCount, ["maxProgressGapSeconds"] = ses.MaxProgressGapSeconds, ["timeToFirstFrameSeconds"] = ses.TimeToFirstFrameSeconds ?? -1 }));
        var errs = ses.ErrorCounts.Where(kv => kv.Key != FfErrorCategory.ProcessTermination).ToDictionary(kv => kv.Key.ToString(), kv => (double)kv.Value);
        var severe = ses.ErrorCounts.Keys.Any(k => k is FfErrorCategory.DiskFull or FfErrorCategory.PermissionDenied or FfErrorCategory.MuxerError or FfErrorCategory.FileAccessError or FfErrorCategory.EncoderError);
        r.Add(R("PROC-003", "Process", P("FFmpeg log errors"), severe ? TestStatus.Fail : errs.Count > 0 ? TestStatus.Warn : TestStatus.Pass,
            errs.Count == 0 ? $"no classified warnings/errors in {ses.LogLines} log lines" : string.Join(", ", errs.Select(kv => $"{kv.Key}={kv.Value}")), errs,
            ses.RecentProblems.Take(15).Select(l => $"{l.TsUtc:HH:mm:ss.fff} [{l.Category}] {CommandText.Mask(l.Raw)}")));
        if (lp != null)
        {
            var mediaVsWall = ses.Uptime.TotalSeconds > 5 && ses.TimeToFirstFrameSeconds is double ttff ? lp.OutTimeSeconds / Math.Max(1, ses.Uptime.TotalSeconds - ttff) : 1;
            var st = lp.Speed > 0 && lp.Speed < 0.8 || mediaVsWall < 0.8 ? TestStatus.Fail : lp.Speed > 0 && lp.Speed < 0.95 || mediaVsWall < 0.95 || lp.DropFrames > 0 ? TestStatus.Warn : TestStatus.Pass;
            r.Add(R("PROC-004", "Process", P("Real-time processing"), st,
                $"speed {lp.Speed:F2}x, fps {lp.Fps:F1}, frames {lp.Frame}, media/wall ratio {mediaVsWall:F2}, dropped {lp.DropFrames}, duplicated {lp.DupFrames}",
                new() { ["speed"] = lp.Speed, ["fps"] = lp.Fps, ["frames"] = lp.Frame, ["mediaWallRatio"] = mediaVsWall, ["dropFrames"] = lp.DropFrames, ["dupFrames"] = lp.DupFrames }));
        }
        List<(double T, double V)> mem; lock (s.ProcSeries) mem = s.ProcSeries.Select(x => (x.T, x.MemMb)).ToList();
        var slope = ResourceMonitor.SlopePerHour(mem); var runMin = ses.Uptime.TotalMinutes;
        var avgCpu = s.ProcSeries.Count > 0 ? s.ProcSeries.Average(x => x.Cpu) : 0;
        r.Add(R("PROC-005", "Resource", P("FFmpeg resources"), runMin >= 10 && slope > 100 ? TestStatus.Warn : TestStatus.Pass,
            $"avg CPU {avgCpu:F0}% of host, peak working set {ses.PeakWorkingSetMb:F0} MB, memory trend {slope:+0;-0} MB/h{(runMin < 10 ? " (run too short to judge leaks)" : "")}",
            new() { ["avgCpuPercent"] = avgCpu, ["peakWorkingSetMb"] = ses.PeakWorkingSetMb, ["memSlopeMbPerHour"] = slope }));

        // ---------------- HLS ----------------
        if (s.Hls is { } h)
        {
            var segs = h.Segments.Values.OrderBy(x => x.Sequence).ToList();
            r.Add(R("HLS-001", "HLS", P("Playlist validity (RFC 8216)"), h.PlaylistViolations.Count > 0 ? TestStatus.Fail : !h.PlaylistSeen ? TestStatus.Fail : h.PlaylistReadErrors > 0 ? TestStatus.Warn : TestStatus.Pass,
                !h.PlaylistSeen ? "playlist never created" : $"{h.PlaylistReads} reads, {h.PlaylistReadErrors} read errors, {h.PlaylistViolations.Count} violations; version {h.LastPlaylist?.Version}, target {h.LastPlaylist?.TargetDuration}s, independent={h.LastPlaylist?.IndependentSegments}",
                new() { ["reads"] = h.PlaylistReads, ["readErrors"] = h.PlaylistReadErrors, ["violations"] = h.PlaylistViolations.Count }, h.PlaylistViolations.Take(20)));
            r.Add(R("HLS-002", "HLS", P("Playlist advancement"), !h.PlaylistSeen || h.StallEpisodes > 0 ? TestStatus.Fail : TestStatus.Pass,
                $"{h.StallEpisodes} stall(s) > {c.Hls.MaxSegmentAgeSeconds}s, max playlist age {h.MaxPlaylistStallSeconds:F1}s, first playlist after {h.TimeToFirstPlaylistSeconds:F1}s, last sequence {h.MaxSeq}",
                new() { ["stalls"] = h.StallEpisodes, ["maxAgeSeconds"] = h.MaxPlaylistStallSeconds, ["timeToFirstPlaylistSeconds"] = h.TimeToFirstPlaylistSeconds ?? -1 }));
            var missing = segs.Where(x => x.Status == MediaStatus.Missing).ToList();
            var unverified = segs.Where(x => x.Status is MediaStatus.Unverified or MediaStatus.Pending).ToList();
            r.Add(R("HLS-003", "HLS", P("Every segment accounted for"), missing.Count > 0 ? TestStatus.Fail : unverified.Count > 0 ? TestStatus.Warn : segs.Count == 0 ? TestStatus.Fail : TestStatus.Pass,
                $"{segs.Count} sequence numbers: {missing.Count} missing, {unverified.Count} unverified", new() { ["segments"] = segs.Count, ["missing"] = missing.Count, ["unverified"] = unverified.Count },
                missing.Concat(unverified).Take(20).Select(x => $"#{x.Sequence} {x.Uri}: {x.Status} {x.Error}")));
            var invalid = segs.Where(x => x.Status == MediaStatus.Invalid).ToList();
            r.Add(R("HLS-004", "HLS", P("Segment validity (probe + full decode)"), invalid.Count > 0 ? TestStatus.Fail : segs.Count(x => x.Status == MediaStatus.Valid) == 0 ? TestStatus.Fail : TestStatus.Pass,
                $"{segs.Count(x => x.Status == MediaStatus.Valid)} valid, {invalid.Count} invalid, {h.GrowingReferences} referenced while still growing{(c.Hls.DecodeSegments ? "" : " (decode disabled: metadata only)")}",
                new() { ["valid"] = segs.Count(x => x.Status == MediaStatus.Valid), ["invalid"] = invalid.Count, ["growingReferences"] = h.GrowingReferences }, invalid.Take(20).Select(x => $"#{x.Sequence} {x.Uri}: {x.Error}")));
            // Exclude the final (possibly short) segment written at shutdown
            var lastSeq = segs.Count > 0 ? segs[^1].Sequence : -1;
            var durs = segs.Where(x => x.Status == MediaStatus.Valid && x.Sequence != lastSeq).ToList();
            var ds = DurationStats.Of(durs.Select(x => x.PlaylistDuration));
            var anomalies = durs.Where(x => x.DurationAnomaly).ToList();
            var hardLimit = c.Hls.TargetDurationSeconds + 2 * c.Hls.DurationToleranceSeconds;
            r.Add(R("HLS-005", "HLS", P("Segment duration consistency"), ds.Count == 0 ? TestStatus.Unknown : anomalies.Any(x => x.PlaylistDuration > hardLimit) ? TestStatus.Fail : anomalies.Count > 0 ? TestStatus.Warn : TestStatus.Pass,
                ds.Count == 0 ? "no validated segments" : $"target {c.Hls.TargetDurationSeconds}s: mean {ds.Mean:F3} min {ds.Min:F3} max {ds.Max:F3} sd {ds.StdDev:F3}; {anomalies.Count} outside ±{c.Hls.DurationToleranceSeconds}s",
                new() { ["targetSeconds"] = c.Hls.TargetDurationSeconds, ["mean"] = ds.Mean, ["min"] = ds.Min, ["max"] = ds.Max, ["stdDev"] = ds.StdDev, ["anomalies"] = anomalies.Count },
                anomalies.Take(20).Select(x => $"#{x.Sequence} {x.Uri}: {x.PlaylistDuration:F3}s")));
            var wall = s.ProducerSeconds - (h.TimeToFirstPlaylistSeconds ?? 0) + c.Hls.TargetDurationSeconds;
            var cov = h.Coverage(s.KilledByFaultInjection ? null : wall);
            r.Add(Coverage("HLS-006", "HLS", P("HLS verified media coverage"), cov));
            r.Add(Consumer("HLS-007", P("HLS consumption (independent local consumer)"), s.Playback, c.Hls.PlaybackTest));
            if (!string.IsNullOrWhiteSpace(c.Hls.HttpUrl))
            {
                var hc = s.Http; List<double> lat = hc == null ? new() : hc.LatenciesMs.ToList();
                var bad = hc == null || hc.PlaylistFailures + hc.SegmentFailures + hc.LengthMismatches + hc.DecodeFailures > 0 || hc.SegmentRequests == 0;
                r.Add(R("HLS-008", "HLS", P("HLS consumption over HTTP"), hc == null ? TestStatus.Unknown : bad ? TestStatus.Fail : hc.MimeWarnings > 0 ? TestStatus.Warn : TestStatus.Pass,
                    hc == null ? "HTTP consumer never started" : $"{hc.PlaylistRequests} playlist + {hc.SegmentRequests} segment requests; failures {hc.PlaylistFailures}/{hc.SegmentFailures}, length mismatches {hc.LengthMismatches}, decode failures {hc.DecodeFailures}, MIME warnings {hc.MimeWarnings}; latency p95 {Percentile.Of(lat, 95):F0} ms",
                    new() { ["p95LatencyMs"] = Percentile.Of(lat, 95), ["segmentRequests"] = hc?.SegmentRequests ?? 0 }, hc?.Evidence.Take(20)));
                r.Add(Consumer("HLS-009", P("HLS playback over HTTP"), s.HttpPlayback, true));
            }
            var gen = Health.Overall(r.Where(x => x.Id is "HLS-001" or "HLS-002" or "HLS-003" or "HLS-004" or "HLS-005" or "HLS-006"));
            var con = Health.Overall(r.Where(x => x.Id is "HLS-007" or "HLS-008" or "HLS-009"));
            r.Insert(r.FindIndex(x => x.Id == "HLS-001"), R("HLS-GEN", "HLS", P("HLS generation (summary)"), gen, "worst of HLS-001..006"));
            r.Add(R("HLS-CON", "HLS", P("HLS consumption (summary)"), con, "worst of HLS-007..009"));
        }

        // ---------------- Recording ----------------
        if (s.Rec is { } rc)
        {
            var files = rc.Files.Values.OrderBy(x => x.Index).ToList();
            var segT = c.Recording.SegmentDurationSeconds;
            var expected = Math.Max(1, (int)Math.Ceiling((s.ProducerSeconds - (rc.TimeToFirstFileSeconds ?? 0)) / segT));
            r.Add(R("REC-001", "Recording", P("Recording cadence"), files.Count == 0 ? TestStatus.Fail : rc.StallEpisodes > 0 ? TestStatus.Fail : files.Count < expected * 0.8 ? TestStatus.Warn : TestStatus.Pass,
                $"{files.Count} files for ~{expected} expected intervals of {segT}s; first file after {rc.TimeToFirstFileSeconds:F1}s, max gap between new files {rc.MaxGapBetweenFilesSeconds:F1}s, {rc.StallEpisodes} stall(s)",
                new() { ["files"] = files.Count, ["expected"] = expected, ["maxGapSeconds"] = rc.MaxGapBetweenFilesSeconds, ["stalls"] = rc.StallEpisodes }));
            var missing = files.Where(x => x.Status == MediaStatus.Missing).ToList();
            r.Add(R("REC-002", "Recording", P("Missing recording intervals"), missing.Count > 0 ? TestStatus.Fail : TestStatus.Pass,
                missing.Count == 0 ? "no index gaps" : $"{missing.Count} missing file(s)", new() { ["missing"] = missing.Count }, missing.Select(x => $"{x.FileName}: {x.Error}")));
            var invalid = files.Where(x => x.Status == MediaStatus.Invalid).ToList();
            var unverified = files.Where(x => x.Status is MediaStatus.Unverified or MediaStatus.Pending).ToList();
            r.Add(R("REC-003", "Recording", P("Completed files valid (finalized, probe, decode)"), invalid.Count > 0 ? TestStatus.Fail : unverified.Count > 0 ? TestStatus.Warn : files.Count(x => x.Status == MediaStatus.Valid) == 0 ? TestStatus.Fail : TestStatus.Pass,
                $"{files.Count(x => x.Status == MediaStatus.Valid)} valid, {invalid.Count} invalid, {unverified.Count} unverified ({(c.Recording.FullDecode ? "full" : "quick")} decode, mode {s.Command?.RecordingMode})",
                new() { ["valid"] = files.Count(x => x.Status == MediaStatus.Valid), ["invalid"] = invalid.Count, ["unverified"] = unverified.Count },
                invalid.Concat(unverified).Take(20).Select(x => $"{x.FileName} [{x.Stage}]: {x.Error}")));
            var lastIdx = files.Count > 0 ? files[^1].Index : -1;
            var ds = DurationStats.Of(files.Where(x => x.Status == MediaStatus.Valid && x.Index != lastIdx).Select(x => x.Duration));
            var anomalies = files.Where(x => x.DurationAnomaly).ToList();
            r.Add(R("REC-004", "Recording", P("Recording duration consistency"), ds.Count == 0 ? TestStatus.Unknown : anomalies.Count > 0 ? TestStatus.Warn : TestStatus.Pass,
                ds.Count == 0 ? "not enough validated files" : $"target {segT}s: mean {ds.Mean:F3} min {ds.Min:F3} max {ds.Max:F3} sd {ds.StdDev:F3}; {anomalies.Count} outside ±{c.Recording.DurationToleranceSeconds}s",
                new() { ["mean"] = ds.Mean, ["min"] = ds.Min, ["max"] = ds.Max, ["stdDev"] = ds.StdDev, ["anomalies"] = anomalies.Count }, anomalies.Take(20).Select(x => $"{x.FileName}: {x.Duration:F3}s")));
            var seekFail = files.Sum(x => x.SeekFailed); var seekOk = files.Sum(x => x.SeekOk);
            r.Add(R("REC-005", "Recording", P("Playback & seek"), c.Recording.SeekProbes <= 0 ? TestStatus.Unknown : seekFail > 0 ? TestStatus.Fail : seekOk == 0 ? TestStatus.Unknown : TestStatus.Pass,
                $"{seekOk} seek+decode probes OK, {seekFail} failed (25/50/75/95%)", new() { ["seekOk"] = seekOk, ["seekFailed"] = seekFail }));
            var wall = s.ProducerSeconds - (rc.TimeToFirstFileSeconds ?? 0);
            r.Add(Coverage("REC-006", "Recording", P("Recording verified media coverage"), rc.Coverage(s.KilledByFaultInjection ? null : wall)));
            var locked = files.Where(x => x.SharingViolations > 0).ToList();
            r.Add(R("REC-007", "Storage", P("File locking after close"), locked.Count > 0 ? TestStatus.Warn : TestStatus.Pass,
                locked.Count == 0 ? "no sharing violations on completed files" : $"{locked.Count} file(s) had sharing violations after close (AV/indexer/backup/reader?) - investigate, do not assume antivirus",
                new() { ["files"] = locked.Count }, locked.Take(20).Select(x => $"{x.FileName}: {x.SharingViolations} violations, lockers [{string.Join(", ", x.Lockers)}]")));

            // Media timestamp continuity (D3) on a sample of valid recordings
            var sample = files.Where(x => x.Status == MediaStatus.Valid).ToList();
            var picks = sample.Count <= 3 ? sample : new List<RecordingRecord> { sample[0], sample[sample.Count / 2], sample[^1] };
            if (picks.Count > 0)
            {
                var ev = new List<string>(); int bad = 0, pk = 0; double maxGap = 0;
                foreach (var f in picks)
                {
                    var t = await probe.PacketTimesAsync(f.Path, ct);
                    var rep = TimestampAnalyzer.Analyze(t, 1.0 / Math.Max(1, c.Source.ExpectedFps));
                    pk += rep.Packets; maxGap = Math.Max(maxGap, rep.MaxGapSeconds);
                    if (!rep.Clean) { bad++; ev.AddRange(rep.Evidence.Take(5).Select(e => $"{f.FileName}: {e}")); }
                }
                r.Add(R("TS-001", "Time", P("Media timestamp continuity (PTS/DTS)"), bad > 0 ? TestStatus.Warn : TestStatus.Pass,
                    $"{picks.Count} files / {pk} video packets analyzed; {bad} with anomalies; max inter-frame gap {maxGap * 1000:F0} ms. (Media-clock only; says nothing about camera wall-clock accuracy.)",
                    new() { ["packets"] = pk, ["filesWithAnomalies"] = bad, ["maxGapMs"] = maxGap * 1000 }, ev));
            }
        }

        // ---------------- Fault injection: forced termination ----------------
        if (s.KilledByFaultInjection)
        {
            var notes = new List<string>(); var lost = 0.0;
            if (s.Rec is { } rec2 && rec2.Files.Values.OrderBy(x => x.Index).LastOrDefault() is { } last)
            {
                notes.Add($"last recording {last.FileName}: {last.Status} at stage {last.Stage} {last.Error}");
                if (last.Status != MediaStatus.Valid) lost += c.Recording.SegmentDurationSeconds;
            }
            if (s.Hls != null)
            {
                var tmp = Directory.Exists(s.HlsDir) ? Directory.GetFiles(s.HlsDir, "*.tmp").Length : 0;
                notes.Add($"HLS: {tmp} leftover .tmp file(s); playlist has ENDLIST={s.Hls.LastPlaylist?.EndList}");
                var lastSeg = s.Hls.Segments.Values.OrderBy(x => x.Sequence).LastOrDefault();
                if (lastSeg != null) notes.Add($"last HLS segment #{lastSeg.Sequence}: {lastSeg.Status} {lastSeg.Error}");
            }
            var mode = s.Command?.RecordingMode;
            r.Add(R("FI-001", "FaultInjection", P("Forced termination impact"), TestStatus.Pass,
                $"forced kill detected and analysed; estimated lost recording media ≤ {lost:F0}s (mode {mode}{(mode == "fmp4" ? ": fragmented MP4 should keep completed fragments playable" : ": conventional MP4 is unplayable without its moov atom")}).",
                new() { ["lostSecondsEstimate"] = lost }, notes));
        }
        return r;
    }

    private static TestResult Coverage(string id, string cat, string name, CoverageReport cov)
    {
        var st = cov.Items == 0 ? TestStatus.Fail : cov.Missing + cov.Invalid > 0 ? TestStatus.Fail : cov.Unverified > 0 ? TestStatus.Warn : cov.WallClockPercent is < 90 ? TestStatus.Warn : TestStatus.Pass;
        return TestResult.Make(id, cat, name, st,
            $"{cov.Percent:F2}% verified ({cov.VerifiedSeconds:F1}s of {cov.ExpectedSeconds:F1}s expected): missing {cov.MissingSeconds:F1}s, corrupt {cov.CorruptSeconds:F1}s, unverified {cov.UnverifiedSeconds:F1}s" +
            (cov.WallClockPercent is double w ? $"; vs producer wall-clock {w:F1}%" : ""),
            new() { ["coveragePercent"] = cov.Percent, ["verifiedSeconds"] = cov.VerifiedSeconds, ["expectedSeconds"] = cov.ExpectedSeconds, ["missingSeconds"] = cov.MissingSeconds, ["corruptSeconds"] = cov.CorruptSeconds, ["unverifiedSeconds"] = cov.UnverifiedSeconds, ["wallClockPercent"] = cov.WallClockPercent ?? -1 });
    }

    private static TestResult Consumer(string id, string name, PlaybackConsumer? p, bool enabled)
    {
        if (!enabled) return TestResult.Make(id, "Playback", name, TestStatus.Unknown, "playback test disabled");
        if (p?.Session == null) return TestResult.Make(id, "Playback", name, TestStatus.Fail, "consumer never started (no playable playlist)");
        var st = p.UnexpectedExit || p.DecodeErrors > 0 || p.Frames == 0 ? TestStatus.Fail : p.StallEpisodes > 0 || p.SkippedSegments > 0 || p.FailedSegmentOpens > 0 ? TestStatus.Warn : TestStatus.Pass;
        return TestResult.Make(id, "Playback", name, st,
            $"time to first frame {p.TimeToFirstFrameSeconds:F1}s, {p.Frames} frames / {p.MediaSeconds:F1}s media decoded, {p.StallEpisodes} stall(s), max interruption {p.MaxInterruptionSeconds:F1}s, decode errors {p.DecodeErrors}, skipped segments {p.SkippedSegments}, failed segment opens {p.FailedSegmentOpens}. End-to-end latency: UNKNOWN (needs an embedded reference timestamp)",
            new() { ["timeToFirstFrameSeconds"] = p.TimeToFirstFrameSeconds ?? -1, ["frames"] = p.Frames, ["stalls"] = p.StallEpisodes, ["maxInterruptionSeconds"] = p.MaxInterruptionSeconds, ["decodeErrors"] = p.DecodeErrors, ["skippedSegments"] = p.SkippedSegments },
            p.Evidence.Take(20));
    }

    public static List<TestResult> EvaluateGlobal(BenchState st)
    {
        var r = new List<TestResult>();
        var samples = st.Resources?.Samples.ToList() ?? new();
        if (samples.Count > 0)
        {
            var cpus = samples.Where(x => x.SystemCpu != null).Select(x => x.SystemCpu!.Value).ToList();
            var p95 = Percentile.Of(cpus, 95);
            r.Add(TestResult.Make("RES-001", "Resource", "System CPU", p95 > 95 ? TestStatus.Fail : p95 > 80 ? TestStatus.Warn : TestStatus.Pass,
                $"avg {(cpus.Count > 0 ? cpus.Average() : 0):F0}%, p95 {p95:F0}%, max {(cpus.Count > 0 ? cpus.Max() : 0):F0}% across {st.Streams.Count} stream(s)", new() { ["p95Percent"] = p95 }));
            var minAvail = samples.Min(x => x.AvailableMemMb);
            r.Add(TestResult.Make("RES-002", "Resource", "Available memory", minAvail < 512 ? TestStatus.Fail : minAvail < 1024 ? TestStatus.Warn : TestStatus.Pass, $"minimum available {minAvail:F0} MB", new() { ["minAvailableMb"] = minAvail }));
            var self = samples.Select(x => (x.T, x.BenchManagedMb)).ToList();
            var slope = ResourceMonitor.SlopePerHour(self); var mins = samples[^1].T / 60 - samples[0].T / 60;
            r.Add(TestResult.Make("RES-003", "Resource", "Benchmark self memory", mins >= 10 && slope > 50 ? TestStatus.Warn : TestStatus.Pass,
                $"managed heap {samples[^1].BenchManagedMb:F0} MB, working set {samples[^1].BenchWorkingSetMb:F0} MB, trend {slope:+0;-0} MB/h{(mins < 10 ? " (run too short to judge growth)" : "")}", new() { ["slopeMbPerHour"] = slope }));
            var nets = samples.Where(x => x.Net != null).Select(x => x.Net!).ToList();
            if (nets.Count >= 2)
            {
                var a = nets[0]; var b = nets[^1];
                var sent = Math.Max(1, b.SegmentsSent - a.SegmentsSent); var resent = b.SegmentsResent - a.SegmentsResent;
                var pct = 100.0 * resent / sent;
                r.Add(TestResult.Make("NET-008", "Network", "TCP retransmissions (host-wide)", pct > 2 ? TestStatus.Warn : TestStatus.Pass, $"{resent} of {sent} segments resent ({pct:F2}%) during run", new() { ["retransPercent"] = pct, ["resent"] = resent }));
                var nic = b.InErrors - a.InErrors + b.InDiscards - a.InDiscards + b.OutErrors - a.OutErrors + b.OutDiscards - a.OutDiscards;
                r.Add(TestResult.Make("NET-009", "Network", "NIC errors / discards", nic > 0 ? TestStatus.Warn : TestStatus.Pass, $"{nic} errors+discards during run", new() { ["count"] = nic }));
                var resets = b.ResetConnections - a.ResetConnections;
                r.Add(TestResult.Make("NET-012", "Network", "TCP connection resets (host-wide)", TestStatus.Pass, $"{resets} resets during run (host-wide counter; informational)", new() { ["resets"] = resets }));
                var mbps = (b.BytesReceived - a.BytesReceived) * 8 / 1e6 / Math.Max(1, samples[^1].T - samples[0].T);
                r.Add(TestResult.Make("NET-011", "Network", "Incoming bandwidth (host-wide)", TestStatus.Pass, $"{mbps:F2} Mbit/s average", new() { ["mbps"] = mbps }));
            }
        }
        if (st.Streams.Count > 1)
        {
            var ok = st.Streams.Count(s => Health.Overall(s.Results) != TestStatus.Fail);
            r.Add(TestResult.Make("SCALE-001", "Scalability", "Concurrent streams", ok == st.Streams.Count ? TestStatus.Pass : TestStatus.Fail,
                $"{ok}/{st.Streams.Count} concurrent streams met all mandatory requirements", new() { ["streams"] = st.Streams.Count, ["passing"] = ok }));
        }
        return r;
    }
}
