using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

/// <summary>Monitors against real media files with injected defects (missing, corrupt, unfinalized, gaps).</summary>
[Collection("ffmpeg")]
public class MonitorTests
{
    private static FFprobeRunner Probe => new("ffprobe", "ffmpeg");

    [Fact]
    public async Task Hls_monitor_classifies_valid_corrupt_and_missing_segments()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var dir = TestUtil.TempDir();
        await TestUtil.Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-g", "50",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "0", "-hls_segment_filename", Path.Combine(dir, "seg_%03d.ts"), Path.Combine(dir, "gen.m3u8"));
        await File.WriteAllBytesAsync(Path.Combine(dir, "corrupt.ts"), Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7919 % 251)).ToArray());
        TestUtil.WriteAtomic(Path.Combine(dir, "stream.m3u8"),
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n" +
            "#EXTINF:2.0,\nseg_000.ts\n#EXTINF:2.0,\nseg_001.ts\n#EXTINF:2.0,\ncorrupt.ts\n#EXTINF:2.0,\nmissing.ts\n#EXT-X-ENDLIST\n");

        var bus = new EventBus();
        var mon = new HlsMonitor("t", Path.Combine(dir, "stream.m3u8"), new HlsCfg(), Probe, bus, fmp4: false, Path.Combine(dir, "scratch"));
        mon.Start(CancellationToken.None);
        await Task.Delay(3000);
        await mon.StopAsync();

        Assert.Equal(MediaStatus.Valid, mon.Segments[0].Status);
        Assert.Equal(MediaStatus.Valid, mon.Segments[1].Status);
        Assert.Equal(MediaStatus.Invalid, mon.Segments[2].Status);
        Assert.Equal(MediaStatus.Missing, mon.Segments[3].Status);
        var cov = mon.Coverage();
        Assert.Equal(8, cov.ExpectedSeconds, 3);
        Assert.Equal(4, cov.VerifiedSeconds, 3);
        Assert.Equal(50, cov.Percent, 3);
        Assert.Contains(bus.Snapshot(), e => e.Level == EventLevel.Fail && e.Message.Contains("missing.ts"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Hls_monitor_detects_skipped_sequence_numbers_and_rfc_violations()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var dir = TestUtil.TempDir();
        await TestUtil.Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast",
            "-f", "hls", "-hls_time", "2", "-hls_list_size", "0", "-hls_segment_filename", Path.Combine(dir, "seg_%03d.ts"), Path.Combine(dir, "gen.m3u8"));
        var pl = Path.Combine(dir, "stream.m3u8");
        TestUtil.WriteAtomic(pl, "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\nseg_000.ts\n");
        var mon = new HlsMonitor("t", pl, new HlsCfg(), Probe, new EventBus(), false, Path.Combine(dir, "scratch"));
        mon.Start(CancellationToken.None);
        await Task.Delay(1500);
        // The window jumps from #0 to #5: #1..#4 were never published. #5 also violates TARGETDURATION.
        TestUtil.WriteAtomic(pl, "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:5\n#EXTINF:5.2,\nseg_000.ts\n");
        await Task.Delay(1500);
        await mon.StopAsync();

        for (var s = 1; s <= 4; s++) Assert.Equal(MediaStatus.Missing, mon.Segments[s].Status);
        Assert.Contains(mon.PlaylistViolations, v => v.Contains("exceeds TARGETDURATION"));
        Assert.Equal(5, mon.MaxSeq);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Recording_monitor_detects_gap_and_unfinalized_mp4()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var dir = TestUtil.TempDir();
        await TestUtil.Ffmpeg("-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25", "-t", "6", "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-sc_threshold", "0",
            "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_format", "mp4", Path.Combine(dir, "rec_%06d.mp4"));
        File.Copy(Path.Combine(dir, "rec_000000.mp4"), Path.Combine(dir, "rec_000003.mp4"));
        File.Delete(Path.Combine(dir, "rec_000001.mp4"));                       // gap
        var bad = Path.Combine(dir, "rec_000002.mp4");                           // writer "killed": moov (at the end) never written
        var bytes = await File.ReadAllBytesAsync(bad);
        await File.WriteAllBytesAsync(bad, bytes[..(bytes.Length / 2)]);

        var cfg = new RecordingCfg { SegmentDurationSeconds = 2, StableSeconds = 0.5, SeekProbes = 2 };
        var mon = new RecordingMonitor("t", dir, cfg, Probe, new EventBus());
        mon.Start(CancellationToken.None);
        await Task.Delay(2500);
        await mon.StopAsync();

        Assert.Equal(MediaStatus.Valid, mon.Files[0].Status);
        Assert.Equal("PLAYBACK VERIFIED", mon.Files[0].Stage);
        Assert.Equal(2, mon.Files[0].SeekOk);
        Assert.Equal(MediaStatus.Missing, mon.Files[1].Status);
        Assert.Equal(MediaStatus.Invalid, mon.Files[2].Status);
        Assert.Contains("moov", mon.Files[2].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MediaStatus.Valid, mon.Files[3].Status);
        var cov = mon.Coverage();
        Assert.Equal(1, cov.Missing); Assert.Equal(1, cov.Invalid); Assert.Equal(2, cov.Valid);
        Assert.InRange(cov.Percent, 45, 55);
        Directory.Delete(dir, true);
    }
}
