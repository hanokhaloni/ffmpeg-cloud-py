using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Lab;

public sealed record FixtureInfo(string Path, string Preset, double DurationSeconds, int Width, int Height, double Fps, string VideoCodec, bool HasAudio, string Sha256, bool Generated, double PreparationSeconds);

/// <summary>
/// Deterministic media fixture, prepared BEFORE measurement and then published with stream copy, so that
/// four publishers cost almost no CPU. Keyframe every second; H.264 + AAC 48 kHz stereo.
/// </summary>
public static class Fixture
{
    public static readonly IReadOnlyDictionary<string, (int W, int H, int Fps)> Presets = new Dictionary<string, (int, int, int)>
    {
        ["720p25-h264-aac"] = (1280, 720, 25),
        ["1080p25-h264-aac"] = (1920, 1080, 25),
        ["360p25-h264-aac"] = (640, 360, 25),
    };

    /// <summary>v2: no B-frames (like typical IP cameras; RTSP carries no DTS, so B-frames force FFmpeg to guess timestamps).</summary>
    public static string FileName(string preset, int durationSeconds) => $"{preset}-{durationSeconds}s-v2.mp4";

    public static async Task<FixtureInfo> EnsureAsync(string ffmpeg, FFprobeRunner probe, string dir, string preset, int durationSeconds, CancellationToken ct)
    {
        if (!Presets.TryGetValue(preset, out var p)) throw new ArgumentException($"unknown fixture preset '{preset}'. Known: {string.Join(", ", Presets.Keys)}");
        Directory.CreateDirectory(dir);
        var path = Path.GetFullPath(Path.Combine(dir, FileName(preset, durationSeconds)));
        var sw = Stopwatch.StartNew();
        if (File.Exists(path))
        {
            var existing = await probe.ProbeAsync(path, ct);
            if (Matches(existing, p, durationSeconds)) return Info(path, preset, existing, false, sw.Elapsed.TotalSeconds);
        }
        var tmp = path + ".partial.mp4";
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            "-f", "lavfi", "-i", $"testsrc2=size={p.W}x{p.H}:rate={p.Fps}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:beep_factor=4",
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture), "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p", "-crf", "23",
            "-bf", "0", "-g", p.Fps.ToString(), "-keyint_min", p.Fps.ToString(), "-sc_threshold", "0", "-force_key_frames", "expr:gte(t,n_forced*1)",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", tmp
        };
        var r = await ProcessUtil.RunAsync(ffmpeg, args, TimeSpan.FromMinutes(15), ct, ProcessRole.Tool, "fixture");
        if (r.ExitCode != 0) throw new InvalidOperationException("fixture generation failed: " + r.StdErr.Trim());
        File.Move(tmp, path, overwrite: true);
        var info = await probe.ProbeAsync(path, ct);
        if (!Matches(info, p, durationSeconds)) throw new InvalidOperationException($"generated fixture does not match preset {preset}: {info.Error}");
        return Info(path, preset, info, true, sw.Elapsed.TotalSeconds);
    }

    private static bool Matches(ProbeInfo i, (int W, int H, int Fps) p, int d) =>
        i.Ok && i.HasVideo && i.HasAudio && i.VideoCodec == "h264" && i.Width == p.W && i.Height == p.H && Math.Abs(i.Fps - p.Fps) < 0.01 && i.Duration >= d - 0.5;

    private static FixtureInfo Info(string path, string preset, ProbeInfo i, bool generated, double secs)
    {
        string sha;
        using (var fs = File.OpenRead(path)) sha = Convert.ToHexString(SHA256.HashData(fs));
        return new FixtureInfo(path, preset, i.Duration, i.Width, i.Height, i.Fps, i.VideoCodec ?? "", i.HasAudio, sha, generated, secs);
    }
}
