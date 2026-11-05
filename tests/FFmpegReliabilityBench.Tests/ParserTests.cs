using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

public class ProgressParserTests
{
    [Fact]
    public void Parses_block_and_emits_on_progress_line()
    {
        var p = new ProgressParser();
        string[] lines = { "frame=1250", "fps=25.0", "bitrate=1500.0kbits/s", "total_size=1048576", "out_time_us=50000000", "out_time=00:00:50.000000", "dup_frames=1", "drop_frames=2", "speed=1.02x" };
        foreach (var l in lines) Assert.Null(p.Feed(l));
        var s = p.Feed("progress=continue");
        Assert.NotNull(s);
        Assert.Equal(1250, s!.Frame);
        Assert.Equal(25.0, s.Fps);
        Assert.Equal(1500.0, s.BitrateKbps);
        Assert.Equal(50.0, s.OutTimeSeconds, 3);
        Assert.Equal(1.02, s.Speed, 3);
        Assert.Equal(2, s.DropFrames);
        Assert.False(s.End);
    }

    [Fact]
    public void Handles_NA_values_and_legacy_out_time_ms()
    {
        var p = new ProgressParser();
        p.Feed("frame=0"); p.Feed("bitrate=N/A"); p.Feed("speed=N/A"); p.Feed("out_time_ms=2000000");
        var s = p.Feed("progress=end")!;
        Assert.Equal(0, s.BitrateKbps);
        Assert.Equal(2.0, s.OutTimeSeconds, 3); // out_time_ms is microseconds in FFmpeg
        Assert.True(s.End);
    }
}

public class LogParserTests
{
    [Theory]
    [InlineData("[segment @ 0x1] [error] Failed to open segment 'Y:/rec/rec_000004.mp4' for writing", FfErrorCategory.FileAccessError)]
    [InlineData("[hls @ 0x1] [error] H:/hls/seg.ts: No space left on device", FfErrorCategory.DiskFull)]
    [InlineData("[mp4 @ 0x1] [warning] Non-monotonous DTS in output stream 0:0; previous: 10, current: 9", FfErrorCategory.NonMonotonicDts)]
    [InlineData("[rtsp @ 0x1] [error] method DESCRIBE failed: 401 Unauthorized", FfErrorCategory.RtspConnectionFailure)]
    [InlineData("[rtsp @ 0x1] [warning] RTP: missed 12 packets", FfErrorCategory.InputCorruption)]
    [InlineData("[out#0 @ 0x1] [error] Permission denied", FfErrorCategory.PermissionDenied)]
    [InlineData("[tcp @ 0x1] [error] Connection reset by peer", FfErrorCategory.NetworkIoError)]
    public void Classifies_problem_lines(string line, FfErrorCategory expected)
    {
        var c = FFmpegLogParser.Classify(line);
        Assert.Equal(expected, c.Category);
        Assert.True(c.IsProblem);
        Assert.Equal(line, c.Raw); // original line is always preserved
    }

    [Theory]
    [InlineData("[info]     encoder         : Lavc60.31.102 libx264")]
    [InlineData("[hls @ 0x1] [info] Opening 'seg_000001.ts' for writing")]
    public void Info_lines_are_not_problems(string line) => Assert.False(FFmpegLogParser.Classify(line).IsProblem);
}

public class HlsPlaylistTests
{
    private const string Good = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:41\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXTINF:2.000000,\nseg_000041.ts\n#EXTINF:2.000000,\nseg_000042.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:1.960000,\nseg_000043.ts\n";

    [Fact]
    public void Parses_valid_live_playlist()
    {
        var p = HlsPlaylist.Parse(Good);
        Assert.Empty(p.Errors);
        Assert.Equal(41, p.MediaSequence);
        Assert.Equal(3, p.Segments.Count);
        Assert.Equal(43, p.Segments[2].Sequence);
        Assert.True(p.Segments[2].Discontinuity);
        Assert.True(p.IndependentSegments);
        Assert.False(p.EndList);
    }

    [Fact]
    public void Flags_extinf_exceeding_target_duration()
    {
        var p = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:5.12,\na.ts\n");
        Assert.Contains(p.Errors, e => e.Contains("exceeds TARGETDURATION"));
    }

    [Fact]
    public void Rounds_extinf_per_rfc8216()
    {
        // 2.4 rounds to 2 → allowed with target 2; 2.5 rounds to 3 → violation
        Assert.Empty(HlsPlaylist.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.4,\na.ts\n").Errors);
        Assert.NotEmpty(HlsPlaylist.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.5,\na.ts\n").Errors);
    }

    [Fact]
    public void Detects_missing_header_and_target()
    {
        var p = HlsPlaylist.Parse("#EXTINF:2,\na.ts\n");
        Assert.Contains(p.Errors, e => e.Contains("#EXTM3U"));
        Assert.Contains(p.Errors, e => e.Contains("TARGETDURATION"));
    }

    [Fact]
    public void Reads_fmp4_map()
    {
        var p = HlsPlaylist.Parse("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2,\nseg_0.m4s\n#EXT-X-ENDLIST\n");
        Assert.Equal("init.mp4", p.MapUri);
        Assert.True(p.EndList);
    }
}

public class CoverageAndHealthTests
{
    [Fact]
    public void Coverage_counts_only_valid_media_as_verified()
    {
        var c = CoverageReport.Compute(new[] { (MediaStatus.Valid, 5.0), (MediaStatus.Valid, 5.0), (MediaStatus.Missing, 5.0), (MediaStatus.Invalid, 5.0), (MediaStatus.Unverified, 5.0), (MediaStatus.Pending, 5.0) });
        Assert.Equal(30, c.ExpectedSeconds);
        Assert.Equal(10, c.VerifiedSeconds);
        Assert.Equal(5, c.MissingSeconds);
        Assert.Equal(5, c.CorruptSeconds);
        Assert.Equal(10, c.UnverifiedSeconds);
        Assert.Equal(100.0 / 3, c.Percent, 6);
    }

    [Fact]
    public void Health_is_never_averaged_and_unknown_is_never_pass()
    {
        TestResult R(TestStatus s) => TestResult.Make("X", "c", "n", s, "");
        Assert.Equal(TestStatus.Pass, Health.Overall(new[] { R(TestStatus.Pass), R(TestStatus.Pass) }));
        Assert.Equal(TestStatus.Warn, Health.Overall(new[] { R(TestStatus.Pass), R(TestStatus.Unknown) }));
        Assert.Equal(TestStatus.Fail, Health.Overall(Enumerable.Repeat(R(TestStatus.Pass), 100).Append(R(TestStatus.Fail))));
    }

    [Fact]
    public void Percentile_interpolates()
    {
        var xs = Enumerable.Range(1, 100).Select(i => (double)i).ToList();
        Assert.Equal(50.5, Percentile.Of(xs, 50), 6);
        Assert.Equal(99.01, Percentile.Of(xs, 99), 2);
    }
}

public class TimestampAnalyzerTests
{
    [Fact]
    public void Clean_cfr_stream()
    {
        var pk = Enumerable.Range(0, 250).Select(i => (i * 0.04, i * 0.04)).ToList();
        var r = TimestampAnalyzer.Analyze(pk, 0.04);
        Assert.True(r.Clean);
        Assert.Equal(0.04, r.MeanFrameInterval, 6);
    }

    [Fact]
    public void Detects_gap_duplicate_nonmonotonic_reset_and_missing()
    {
        var d = new List<double> { 0, 0.04, 0.08, 0.08, 0.06, 0.5, 0.54, double.NaN, 0.1 };
        var r = TimestampAnalyzer.Analyze(d.Select(x => (x, x)).ToList(), 0.04, resetThreshold: 0.3);
        Assert.Equal(1, r.DuplicateDts);
        Assert.Equal(1, r.NonMonotonicDts);
        Assert.Equal(1, r.Gaps);
        Assert.Equal(1, r.MissingTimestamps);
        Assert.Equal(1, r.Resets);
        Assert.False(r.Clean);
    }
}

public class CommandTests
{
    [Fact]
    public void Masks_credentials()
    {
        Assert.Equal("rtsp://admin:***@10.0.0.5:554/axis-media/media.amp", CommandText.Mask("rtsp://admin:S3cr3t!@10.0.0.5:554/axis-media/media.amp"));
        Assert.Equal("rtsp://10.0.0.5/stream", CommandText.Mask("rtsp://10.0.0.5/stream"));
    }

    [Fact]
    public void Tee_escaping_handles_windows_drive_letters()
    {
        // Two av_get_token passes: '|' split, then ':' option split → "\\:" survives as ":"
        Assert.Equal(@"H\\:/hls/seg_%06d.ts", CommandBuilder.TeeEscapeOption(@"H:\hls\seg_%06d.ts"));
        Assert.Equal("H:/hls/stream.m3u8", CommandBuilder.TeeEscapeFile(@"H:\hls\stream.m3u8"));
    }

    private static Capabilities Caps(int major) => new() { Major = major, StatsPeriod = true, HlsTempFile = true, HlsFmp4 = true, VersionLine = "ffmpeg version x" };

    [Fact]
    public void Baseline_profile_has_independent_hls_and_segment_outputs()
    {
        var cfg = new BenchConfig();
        var cmd = CommandBuilder.Build(cfg, Caps(6), "/o/hls", "/o/rec");
        var a = cmd.Args;
        Assert.Contains("hls", a); Assert.Contains("segment", a);
        Assert.Equal("2", a[a.IndexOf("-hls_time") + 1]);
        Assert.Equal("5", a[a.IndexOf("-segment_time") + 1]);
        Assert.Equal("50", a[a.IndexOf("-g") + 1]);       // 25 fps × 2 s for HLS
        Assert.Equal("125", a[a.LastIndexOf("-g") + 1]);  // 25 fps × 5 s for recording
        Assert.Contains("movflags=+faststart", a);
        Assert.Contains("-progress", a);
        Assert.DoesNotContain(a, x => x.Contains("cmd.exe"));
    }

    [Fact]
    public void Rtsp_timeout_option_depends_on_ffmpeg_version()
    {
        var cfg = new BenchConfig(); cfg.Source.Url = "rtsp://cam/stream";
        Assert.Contains("-stimeout", CommandBuilder.Build(cfg, Caps(4), "/h", "/r").Args);
        Assert.Contains("-timeout", CommandBuilder.Build(cfg, Caps(7), "/h", "/r").Args);
        Assert.DoesNotContain("-re", CommandBuilder.Build(cfg, Caps(7), "/h", "/r").Args); // no -re on live RTSP by default
    }

    [Fact]
    public void Tee_profile_uses_single_encoder_with_common_gop()
    {
        var cfg = new BenchConfig(); cfg.Benchmark.Profile = "tee";
        var a = CommandBuilder.Build(cfg, Caps(6), "/o/hls", "/o/rec").Args;
        Assert.Single(a, x => x == "libx264");
        Assert.Equal("25", a[a.IndexOf("-g") + 1]); // gcd(2,5)=1 s
        Assert.Contains(a, x => x.Contains("onfail=ignore") && x.Contains("f=hls") && x.Contains("f=segment"));
    }

    [Fact]
    public void Unsupported_temp_file_flag_is_removed_for_old_builds()
    {
        var cfg = new BenchConfig(); cfg.Hls.Flags = "delete_segments+temp_file";
        var caps = Caps(4); caps.HlsTempFile = false;
        var cmd = CommandBuilder.Build(cfg, caps, "/h", "/r");
        Assert.Equal("delete_segments", cmd.Args[cmd.Args.IndexOf("-hls_flags") + 1]);
        Assert.Contains(cmd.Notes, n => n.Contains("temp_file"));
    }

    [Fact]
    public void Copy_profile_rejects_synthetic_source()
    {
        var cfg = new BenchConfig(); cfg.Benchmark.Profile = "copy";
        Assert.Throws<ArgumentException>(() => CommandBuilder.Build(cfg, Caps(6), "/h", "/r"));
    }
}
