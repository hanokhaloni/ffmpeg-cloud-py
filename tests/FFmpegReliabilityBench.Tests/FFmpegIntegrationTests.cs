using FFmpegReliabilityBench.Core.Runner;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

/// <summary>Runs real FFmpeg if available on PATH (skips silently otherwise).</summary>
[Collection("ffmpeg")]
public class FFmpegIntegrationTests
{

    [Fact]
    public async Task Runner_reports_progress_and_stops_gracefully_with_q()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var runner = new FFmpegRunner();
        var job = new FFmpegJob
        {
            Executable = "ffmpeg",
            Arguments = new[] { "-hide_banner", "-nostats", "-loglevel", "level+info", "-progress", "pipe:1", "-re", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-c:v", "libx264", "-preset", "ultrafast", "-f", "null", "-" },
            StallTimeoutSeconds = 10
        };
        var s = await runner.StartAsync(job, CancellationToken.None);
        await Task.Delay(3000);
        Assert.NotNull(s.LastProgress);
        Assert.True(s.LastProgress!.Frame > 25);
        await runner.StopAsync(s, CancellationToken.None);
        Assert.Equal(SessionState.Exited, s.State); // requested stop → graceful
        Assert.False(s.GracefulStopFailed);
    }

    [Fact]
    public async Task Unexpected_exit_of_live_producer_is_a_crash()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var s = await new FFmpegRunner().StartAsync(new FFmpegJob { Executable = "ffmpeg", Arguments = new[] { "-hide_banner", "-f", "lavfi", "-i", "testsrc2", "-t", "0.5", "-f", "null", "-" } }, CancellationToken.None);
        await s.Exited.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(SessionState.Crashed, s.State);
    }

    [Fact]
    public async Task Probe_detects_unfinalized_mp4()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var dir = Directory.CreateTempSubdirectory("frbtest").FullName;
        var good = Path.Combine(dir, "good.mp4"); var bad = Path.Combine(dir, "bad.mp4");
        await ProcessUtil.RunAsync("ffmpeg", new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", good }, TimeSpan.FromSeconds(60));
        var bytes = await File.ReadAllBytesAsync(good);
        // Simulate a killed writer: conventional MP4 without its trailing moov
        var moov = FindAtom(bytes, "moov");
        await File.WriteAllBytesAsync(bad, bytes[..(moov > 0 ? moov - 4 : bytes.Length / 2)]);
        var p = new FFprobeRunner("ffprobe", "ffmpeg");
        Assert.True((await p.ProbeAsync(good)).Ok);
        Assert.True((await p.DecodeAsync(good)).Ok);
        Assert.False((await p.ProbeAsync(bad)).Ok);
        Directory.Delete(dir, true);
    }

    private static int FindAtom(byte[] b, string name)
    {
        for (int i = 4; i < b.Length - 4; i++)
            if (b[i] == name[0] && b[i + 1] == name[1] && b[i + 2] == name[2] && b[i + 3] == name[3]) return i;
        return -1;
    }
}
