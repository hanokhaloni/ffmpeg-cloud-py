using System.Text.Json;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

/// <summary>Full orchestrator runs: synthetic source → FFmpeg → HLS + MP4 → monitors → consumers (local + HTTP) → reports.</summary>
[Collection("ffmpeg")]
public class EndToEndTests
{
    private static BenchConfig Config(string root, int seconds)
    {
        var c = new BenchConfig();
        c.Storage.HlsDirectory = Path.Combine(root, "hls");
        c.Storage.RecordingDirectory = Path.Combine(root, "rec");
        c.ReportDirectory = Path.Combine(root, "reports");
        c.Storage.SmallFileCount = 10; c.Storage.SegmentFileCount = 3; c.Storage.SegmentFileSizeMb = 1; c.Storage.MinimumFreeSpaceGb = 0.1;
        c.Benchmark.DurationSeconds = seconds;
        return c;
    }

    private static TestResult Get(BenchState st, string id) => st.AllRuntimeResults.Single(r => r.Id == id);

    [Fact]
    public async Task Healthy_pipeline_reaches_full_coverage_and_is_consumable_over_http()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var root = TestUtil.TempDir("frbe2e");
        var cfg = Config(root, 16);
        Directory.CreateDirectory(cfg.Storage.HlsDirectory);
        using var http = new StaticHttpServer(cfg.Storage.HlsDirectory);
        cfg.Hls.HttpUrl = http.BaseUrl + "{session}/{stream}/stream.m3u8";
        var st = new BenchState { Config = cfg, Mode = RunMode.Benchmark };

        await Orchestrator.RunAsync(st, CancellationToken.None);

        Assert.Null(st.FatalError);
        Assert.Equal("Done", st.Phase);
        foreach (var id in new[] { "PROC-001", "PROC-002", "HLS-001", "HLS-003", "HLS-004", "HLS-006", "HLS-007", "HLS-008", "HLS-009", "REC-002", "REC-003", "REC-005", "REC-006" })
            Assert.True(Get(st, id).Status == TestStatus.Pass, $"{id}: {Get(st, id).Status} {Get(st, id).Diagnosis}");
        Assert.Equal(100, Get(st, "HLS-006").Metrics["coveragePercent"], 3);
        Assert.Equal(100, Get(st, "REC-006").Metrics["coveragePercent"], 3);
        Assert.True(http.Requests > 5);

        // Reports
        Assert.True(File.Exists(st.ReportHtml));
        using var doc = JsonDocument.Parse(File.ReadAllText(st.ReportJson!));
        Assert.Equal(st.RuntimeStatus.ToString(), doc.RootElement.GetProperty("status").GetProperty("runtime").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("session").TryGetProperty("fatalError", out var fe) ? fe.ValueKind : JsonValueKind.Null);
        Assert.True(doc.RootElement.GetProperty("streams")[0].GetProperty("hlsSegments").GetArrayLength() >= 7);
        foreach (var f in new[] { "results.csv", "events.csv", "stream-01.ffmpeg.log", "stream-01.hls-segments.csv", "stream-01.recording-timeline.csv", "ffmpeg-capabilities.txt" })
            Assert.True(File.Exists(Path.Combine(st.ReportDirectory, f)), f);
        // Media goes to per-session subfolders, never directly into the configured directories
        Assert.True(Directory.Exists(Path.Combine(cfg.Storage.RecordingDirectory, st.SessionId, "stream-01")));
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task Forced_kill_of_conventional_mp4_is_detected_as_unfinalized_recording()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var root = TestUtil.TempDir("frbkill");
        var cfg = Config(root, 30);
        cfg.Recording.Mode = "mp4";
        cfg.Benchmark.InjectKillAtSeconds = 8; // mid-way through the second 5 s recording
        var st = new BenchState { Config = cfg, Mode = RunMode.RecordingOnly };

        await Orchestrator.RunAsync(st, CancellationToken.None);

        Assert.Null(st.FatalError);
        Assert.Equal(TestStatus.Pass, Get(st, "PROC-001").Status); // expected termination
        var rec = Get(st, "REC-003");
        Assert.Equal(TestStatus.Fail, rec.Status);
        Assert.NotEmpty(rec.Evidence);
        Assert.True(Get(st, "REC-006").Metrics["coveragePercent"] < 100);
        Assert.Contains(st.AllRuntimeResults, r => r.Id == "FI-001");
        Assert.Equal(TestStatus.Fail, st.RuntimeStatus);
        Assert.True(st.Streams[0].Session!.Uptime.TotalSeconds < 20, "run must end shortly after the producer died");
        Directory.Delete(root, true);
    }
}
