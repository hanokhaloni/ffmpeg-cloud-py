using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Reporting;

/// <summary>JSON (machine-readable, complete), CSV (results + timelines), HTML (human summary). Credentials are always masked.</summary>
public static class Reporter
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void WriteAll(BenchState st)
    {
        var dir = st.ReportDirectory;
        Directory.CreateDirectory(dir);
        var model = BuildModel(st);
        st.ReportJson = Path.Combine(dir, "report.json");
        File.WriteAllText(st.ReportJson, JsonSerializer.Serialize(model, Json));
        WriteCsv(st, dir);
        st.ReportHtml = Path.Combine(dir, "report.html");
        File.WriteAllText(st.ReportHtml, Html(st));
        if (st.Caps != null)
            File.WriteAllText(Path.Combine(dir, "ffmpeg-capabilities.txt"), string.Join("\n\n", st.Caps.RawOutputs.Select(kv => $"===== ffmpeg {kv.Key} =====\n{kv.Value}")));
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(dir)!, "latest.txt"), dir); } catch { }
    }

    public static object BuildModel(BenchState st)
    {
        var c = st.Config;
        return new
        {
            session = new { st.SessionId, startedAtUtc = st.StartedUtc, completedAtUtc = DateTime.UtcNow, mode = st.Mode.ToString(), phase = st.Phase, fatalError = st.FatalError },
            status = new { prerequisites = st.PrereqStatus, runtime = st.RuntimeStatus, overall = st.Overall },
            coverage = st.Streams.Select(s => new
            {
                stream = s.Name,
                hls = s.Hls?.Coverage(),
                recording = s.Rec?.Coverage()
            }),
            environment = new
            {
                machine = Environment.MachineName, os = RuntimeInformation.OSDescription, arch = RuntimeInformation.OSArchitecture.ToString(),
                dotnet = RuntimeInformation.FrameworkDescription, cpus = Environment.ProcessorCount, identity = SystemInfo.Identity(),
                hlsPathKind = SystemInfo.PathKind(c.Storage.HlsDirectory), recordingPathKind = SystemInfo.PathKind(c.Storage.RecordingDirectory)
            },
            ffmpeg = st.Caps == null ? null : new { st.Caps.VersionLine, st.Caps.Major, st.Caps.Minor, st.Caps.ExecutablePath, st.Caps.Sha256, st.Caps.BuildConfiguration, st.Caps.FfprobeVersionLine, st.Caps.StatsPeriod, st.Caps.HlsTempFile, st.Caps.HlsFmp4 },
            config = MaskConfig(st),
            prerequisites = st.Prerequisites,
            runtimeResults = st.AllRuntimeResults,
            streams = st.Streams.Select(s => new
            {
                s.Name, s.HlsDir, s.RecDir, profile = s.Command?.Profile, recordingMode = s.Command?.RecordingMode,
                command = new { human = s.HumanCommand, argumentList = s.Command?.Args.Select(CommandText.Mask), notes = s.Command?.Notes },
                process = s.Session == null ? null : new { s.Session.Pid, state = s.Session.State, s.Session.ExitCode, uptimeSeconds = s.Session.Uptime.TotalSeconds, s.Session.StallCount, s.Session.MaxProgressGapSeconds, lastProgress = s.Session.LastProgress, errorCounts = s.Session.ErrorCounts, logFile = s.Session.Job.LogFile },
                hlsSegments = s.Hls?.Segments.Values.OrderBy(x => x.Sequence),
                recordings = s.Rec?.Files.Values.OrderBy(x => x.Index),
                playback = s.Playback == null ? null : new { s.Playback.TimeToFirstFrameSeconds, s.Playback.Frames, s.Playback.MediaSeconds, s.Playback.StallEpisodes, s.Playback.MaxInterruptionSeconds, s.Playback.DecodeErrors, s.Playback.SkippedSegments, s.Playback.Evidence },
                processSeries = s.ProcSeries.Select(p => new { t = p.T, cpu = p.Cpu, memMb = p.MemMb })
            }),
            hypotheses = st.Hypotheses,
            events = st.Bus.Snapshot(),
            resources = st.Resources?.Samples
        };
    }

    private static object MaskConfig(BenchState st)
    {
        var json = JsonSerializer.Serialize(st.Config, Json);
        return JsonSerializer.Deserialize<JsonElement>(CommandText.Mask(json));
    }

    private static string Csv(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

    private static void WriteCsv(BenchState st, string dir)
    {
        var sb = new StringBuilder("phase,id,category,name,status,diagnosis,completedAtUtc\n");
        foreach (var r in st.Prerequisites) sb.Append($"prerequisite,{r.Id},{r.Category},{Csv(r.Name)},{r.Status},{Csv(r.Diagnosis)},{r.CompletedAtUtc:O}\n");
        foreach (var r in st.AllRuntimeResults) sb.Append($"runtime,{r.Id},{r.Category},{Csv(r.Name)},{r.Status},{Csv(r.Diagnosis)},{r.CompletedAtUtc:O}\n");
        File.WriteAllText(Path.Combine(dir, "results.csv"), sb.ToString());
        foreach (var s in st.Streams)
        {
            if (s.Rec != null)
            {
                var t = new StringBuilder("index,file,timelineStart,duration,sizeBytes,status,stage,error\n");
                foreach (var f in s.Rec.Files.Values.OrderBy(x => x.Index)) t.Append($"{f.Index},{f.FileName},{f.TimelineStart:F3},{f.Duration:F3},{f.Size},{f.Status},{Csv(f.Stage)},{Csv(f.Error)}\n");
                File.WriteAllText(Path.Combine(dir, $"{s.Name}.recording-timeline.csv"), t.ToString());
            }
            if (s.Hls != null)
            {
                var t = new StringBuilder("sequence,uri,playlistDuration,probedDuration,sizeBytes,status,error,firstSeenUtc\n");
                foreach (var g in s.Hls.Segments.Values.OrderBy(x => x.Sequence)) t.Append($"{g.Sequence},{g.Uri},{g.PlaylistDuration:F3},{g.ProbedDuration:F3},{g.Size},{g.Status},{Csv(g.Error)},{g.FirstSeenUtc:O}\n");
                File.WriteAllText(Path.Combine(dir, $"{s.Name}.hls-segments.csv"), t.ToString());
            }
        }
        var ev = new StringBuilder("tsUtc,t,level,category,stream,message\n");
        foreach (var e in st.Bus.Snapshot()) ev.Append($"{e.TsUtc:O},{e.T:F3},{e.Level},{e.Category},{e.Stream},{Csv(e.Message)}\n");
        File.WriteAllText(Path.Combine(dir, "events.csv"), ev.ToString());
    }

    private static string Badge(TestStatus s) => $"<span class=\"b {s.ToString().ToLowerInvariant()}\">{s.ToString().ToUpperInvariant()}</span>";
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Html(BenchState st)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>FFmpeg Reliability Benchmark ").Append(E(st.SessionId)).Append("</title><style>")
          .Append("body{font:14px/1.45 system-ui,Segoe UI,sans-serif;margin:24px;color:#1d2433;background:#fafbfc}h1{margin:0 0 4px}h2{margin-top:28px;border-bottom:1px solid #dde;padding-bottom:4px}")
          .Append("table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #e3e6ec;padding:4px 8px;text-align:left;vertical-align:top}th{background:#f1f3f7}td:first-child{white-space:nowrap}")
          .Append(".b{font-weight:700;padding:1px 8px;border-radius:10px;font-size:12px}.pass{background:#d9f2e3;color:#11643a}.warn{background:#fff1cc;color:#7a5600}.fail{background:#fbdada;color:#9b1c1c}.unknown{background:#e6e8ee;color:#444}")
          .Append(".kpi{display:inline-block;margin:8px 16px 8px 0;padding:12px 16px;background:#fff;border:1px solid #e3e6ec;border-radius:8px}.kpi b{display:block;font-size:22px}code,pre{background:#f1f3f7;padding:2px 4px;border-radius:4px;white-space:pre-wrap;word-break:break-all}small{color:#667}")
          .Append("</style></head><body>");
        sb.Append($"<h1>FFmpeg Reliability Benchmark</h1><small>{E(st.SessionId)} · {st.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC · mode {st.Mode} · profile {E(st.Config.Benchmark.Profile)} · {E(st.Caps?.VersionLine)}</small>");
        if (st.FatalError != null) sb.Append($"<p>{Badge(TestStatus.Fail)} {E(st.FatalError)}</p>");
        sb.Append($"<div><div class=kpi>Overall<b>{Badge(st.Overall)}</b></div><div class=kpi>Prerequisites<b>{Badge(st.PrereqStatus)}</b></div><div class=kpi>Runtime reliability<b>{Badge(st.RuntimeStatus)}</b></div>");
        foreach (var s in st.Streams)
        {
            if (s.Hls != null) sb.Append($"<div class=kpi>{E(s.Name)} HLS coverage<b>{s.Hls.Coverage().Percent:F2}%</b></div>");
            if (s.Rec != null) sb.Append($"<div class=kpi>{E(s.Name)} recording coverage<b>{s.Rec.Coverage().Percent:F2}%</b></div>");
        }
        sb.Append("</div><p><small>Primary metric: verified media coverage = successfully decoded and accounted-for media ÷ expected media. Statuses are never averaged; UNKNOWN is never PASS.</small></p>");

        void Table(string title, IEnumerable<TestResult> rs)
        {
            sb.Append($"<h2>{E(title)}</h2><table><tr><th>ID</th><th>Test</th><th>Status</th><th>Result</th></tr>");
            foreach (var r in rs)
            {
                sb.Append($"<tr><td>{E(r.Id)}</td><td>{E(r.Name)}</td><td>{Badge(r.Status)}</td><td>{E(r.Diagnosis)}");
                if (r.Evidence.Count > 0) sb.Append("<details><summary>evidence (").Append(r.Evidence.Count).Append(")</summary><pre>").Append(E(string.Join("\n", r.Evidence))).Append("</pre></details>");
                sb.Append("</td></tr>");
            }
            sb.Append("</table>");
        }
        Table("Prerequisites", st.Prerequisites);
        foreach (var s in st.Streams)
        {
            Table($"Runtime - {s.Name}", s.Results);
            sb.Append($"<h2>FFmpeg command - {E(s.Name)}</h2><pre>{E(s.HumanCommand)}</pre><details><summary>Exact ArgumentList</summary><pre>{E(string.Join("\n", (s.Command?.Args ?? new()).Select((a, i) => $"[{i}] {CommandText.Mask(a)}")))}</pre></details>");
            if (s.Rec != null)
            {
                sb.Append($"<h2>Recording timeline - {E(s.Name)}</h2><table><tr><th>File</th><th>Start (media)</th><th>Duration</th><th>Size</th><th>Status</th><th>Stage / error</th></tr>");
                foreach (var f in s.Rec.Files.Values.OrderBy(x => x.Index))
                {
                    var stt = f.Status switch { MediaStatus.Valid => TestStatus.Pass, MediaStatus.Unverified or MediaStatus.Pending => TestStatus.Unknown, _ => TestStatus.Fail };
                    sb.Append($"<tr><td>{E(f.FileName)}</td><td>{TimeSpan.FromSeconds(f.TimelineStart):hh\\:mm\\:ss\\.f}</td><td>{(f.Status == MediaStatus.Missing ? "missing" : f.Duration.ToString("F2") + " s")}</td><td>{f.Size / 1024.0:F0} KB</td><td>{Badge(stt)}</td><td>{E(f.Stage)} {E(f.Error)}</td></tr>");
                }
                sb.Append("</table>");
            }
        }
        if (st.GlobalResults.Count > 0) Table("Resources, network & scalability", st.GlobalResults);
        if (st.Hypotheses.Count > 0)
        {
            sb.Append("<h2>Diagnostic correlation (hypotheses)</h2><ul>");
            foreach (var h in st.Hypotheses.Take(50)) sb.Append($"<li>{h.TsUtc:HH:mm:ss} {E(h.Text)}<pre>{E(string.Join("\n", h.CorrelatedEvents))}</pre></li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>Events (warnings and failures)</h2><table><tr><th>UTC</th><th>T+</th><th>Level</th><th>Category</th><th>Stream</th><th>Message</th></tr>");
        foreach (var e in st.Bus.Snapshot().Where(e => e.Level != EventLevel.Info).Take(1000))
            sb.Append($"<tr><td>{e.TsUtc:HH:mm:ss.fff}</td><td>{e.T:F1}</td><td>{Badge(e.Level switch { EventLevel.Fail => TestStatus.Fail, EventLevel.Warn => TestStatus.Warn, _ => TestStatus.Pass })}</td><td>{E(e.Category)}</td><td>{E(e.Stream)}</td><td>{E(e.Message)}</td></tr>");
        sb.Append("</table></body></html>");
        return sb.ToString();
    }
}
