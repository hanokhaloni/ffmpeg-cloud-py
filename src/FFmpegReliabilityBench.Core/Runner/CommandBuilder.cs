using System.Globalization;
using FFmpegReliabilityBench.Core.Config;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed record BuiltCommand(List<string> Args, List<string> Notes, string Profile, string RecordingMode, bool HlsFmp4, string PlaylistPath);

/// <summary>Builds FFmpeg arguments from named profiles. Nothing is hardcoded in monitors; the exact argument list is shown in the TUI and report.</summary>
public static class CommandBuilder
{
    public static readonly string[] Profiles = { "baseline", "copy", "fmp4", "tee" };
    private static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public static BuiltCommand Build(BenchConfig c, Capabilities caps, string hlsDir, string recDir, bool hlsEnabled = true, bool recEnabled = true)
    {
        var notes = new List<string>();
        var profile = c.Benchmark.Profile.ToLowerInvariant();
        if (!Profiles.Contains(profile)) throw new ArgumentException($"Unknown profile '{profile}'. Known: {string.Join(", ", Profiles)}");
        var recMode = profile == "fmp4" ? "fmp4" : c.Recording.Mode.ToLowerInvariant();
        var hlsFmp4 = profile == "fmp4";
        if (hlsFmp4 && !caps.HlsFmp4) { notes.Add("HLS fMP4 not supported by this FFmpeg build; using MPEG-TS segments"); hlsFmp4 = false; }

        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "level+" + c.Ffmpeg.LogLevel, "-progress", "pipe:1" };
        if (caps.StatsPeriod) a.AddRange(new[] { "-stats_period", "1" });
        else notes.Add("-stats_period not supported by this build; progress cadence is FFmpeg's default");
        a.Add("-y");

        var fps = c.Source.ExpectedFps;
        var src = c.Source;
        if (src.IsSynthetic)
        {
            if (profile == "copy") throw new ArgumentException("Profile 'copy' requires an encoded source (rtsp:// URL or media file), not the synthetic generator.");
            // Synthetic, deterministic source (CFR, known GOP): isolates infrastructure/FFmpeg from camera/network problems.
            a.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"testsrc2=size=1280x720:rate={fps}[out0];sine=frequency=1000:sample_rate=48000[out1]" });
        }
        else if (src.Url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) || src.Url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            a.AddRange(new[] { "-rtsp_transport", src.Transport });
            // Socket timeout option was renamed: -stimeout (<=4.x) -> -timeout (>=5.0). Microseconds in both.
            a.AddRange(caps.Major >= 5 ? new[] { "-timeout", "5000000" } : new[] { "-stimeout", "5000000" });
            if (src.UseReadrate) { a.Add("-re"); notes.Add("-re enabled on a live RTSP input (normally unnecessary; the camera paces delivery)"); }
            a.AddRange(new[] { "-i", src.Url });
        }
        else
        {
            // File / other URL: loop it in real time to simulate a live source.
            a.AddRange(new[] { "-re", "-stream_loop", "-1", "-i", src.Url });
        }

        var hlsT = c.Hls.TargetDurationSeconds; var recT = c.Recording.SegmentDurationSeconds;
        var flags = c.Hls.Flags;
        if (flags.Contains("temp_file") && !caps.HlsTempFile) { flags = string.Join('+', flags.Split('+').Where(f => f != "temp_file")); notes.Add("hls_flags temp_file not supported by this build; removed"); }
        var playlist = Path.Combine(hlsDir, c.Hls.PlaylistName);
        var segPattern = Path.Combine(hlsDir, hlsFmp4 ? "seg_%06d.m4s" : "seg_%06d.ts");
        var recPattern = Path.Combine(recDir, "rec_%06d.mp4");
        var movflags = recMode switch
        {
            "mp4" => null,
            "faststart" => "movflags=+faststart",
            // 1 s fragments + flush after each write: a crash loses at most ~1 s instead of the whole file/GOP.
            "fmp4" => "movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=1000000:flush_packets=1",
            _ => throw new ArgumentException($"Unknown recording mode '{recMode}' (mp4|faststart|fmp4)")
        };

        List<string> Enc(int gop) => profile == "copy"
            ? new() { "-c:v", "copy", "-c:a", "aac", "-b:a", "128k" }
            : new() { "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-g", gop.ToString(), "-keyint_min", gop.ToString(), "-sc_threshold", "0", "-c:a", "aac", "-b:a", "128k" };
        var maps = new[] { "-map", "0:v:0", "-map", "0:a:0?" };
        if (profile == "copy") notes.Add("Stream copy: segment boundaries follow the camera's GOP; durations may deviate from targets");

        if (profile == "tee" && hlsEnabled && recEnabled)
        {
            // One encoder, packets fanned out to both muxers. GOP = gcd of both segment durations so both can cut on keyframes.
            var g = (hlsT % 1 == 0 && recT % 1 == 0) ? Gcd((int)hlsT, (int)recT) : hlsT;
            var gop = (int)Math.Round(fps * g);
            a.AddRange(maps); a.AddRange(Enc(gop));
            a.AddRange(new[] { "-flags", "+global_header", "-f", "tee" });
            var hlsOpts = $"f=hls:hls_time={F(hlsT)}:hls_list_size={c.Hls.PlaylistSize}:hls_flags={flags}:hls_delete_threshold={c.Hls.DeleteThreshold}:hls_segment_filename={TeeEscapeOption(segPattern)}:onfail=ignore";
            var recOpts = $"f=segment:segment_time={F(recT)}:reset_timestamps=1:segment_format=mp4" + (movflags != null ? ":segment_format_options=" + TeeEscapeOption(movflags) : "") + ":onfail=ignore";
            a.Add($"[{hlsOpts}]{TeeEscapeFile(playlist)}|[{recOpts}]{TeeEscapeFile(recPattern)}");
            notes.Add($"Tee muxer: single encode, GOP {gop} frames ({F(g)} s); onfail=ignore isolates a failing output");
        }
        else
        {
            if (profile == "tee") notes.Add("Tee profile needs both outputs; falling back to independent outputs");
            if (hlsEnabled)
            {
                a.AddRange(maps); a.AddRange(Enc((int)Math.Round(fps * hlsT)));
                a.AddRange(new[] { "-f", "hls", "-hls_time", F(hlsT), "-hls_list_size", c.Hls.PlaylistSize.ToString(), "-hls_flags", flags, "-hls_delete_threshold", c.Hls.DeleteThreshold.ToString() });
                if (hlsFmp4) a.AddRange(new[] { "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4" });
                a.AddRange(new[] { "-hls_segment_filename", segPattern, playlist });
            }
            if (recEnabled)
            {
                a.AddRange(maps); a.AddRange(Enc((int)Math.Round(fps * recT)));
                a.AddRange(new[] { "-f", "segment", "-segment_time", F(recT), "-reset_timestamps", "1", "-segment_format", "mp4" });
                if (movflags != null) a.AddRange(new[] { "-segment_format_options", movflags });
                a.Add(recPattern);
            }
            if (hlsEnabled && recEnabled && profile != "copy") notes.Add("Two independent encodes (same as production split= command): higher CPU, no transactional guarantee between outputs");
        }
        return new BuiltCommand(a, notes, profile, recMode, hlsFmp4, playlist);
    }

    private static int Gcd(int x, int y) => y == 0 ? Math.Max(1, x) : Gcd(y, x % y);

    /// <summary>
    /// Tee slave option values go through two av_get_token passes ('|' split, then ':' option split).
    /// Use forward slashes (accepted by Windows FFmpeg) and double-escape ':' (drive letters).
    /// </summary>
    public static string TeeEscapeOption(string v) => v.Replace('\\', '/').Replace("|", "\\|").Replace(":", "\\\\:");
    public static string TeeEscapeFile(string v) => v.Replace('\\', '/').Replace("|", "\\|");
}
