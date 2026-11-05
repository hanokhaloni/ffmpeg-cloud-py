using System.Globalization;
using System.Text.Json;
using FFmpegReliabilityBench.Core.Lab;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed record ProbeInfo(bool Ok, string? Format, double Duration, bool HasVideo, bool HasAudio,
    string? VideoCodec, int Width, int Height, double Fps, string Error);

/// <param name="TimestampWarnings">Timestamp complaints (non-monotonic DTS etc.) seen while decoding; reported, not treated as corrupt media.</param>
public sealed record DecodeResult(bool Ok, string Error, double ElapsedSeconds, int TimestampWarnings = 0);

public sealed class FFprobeRunner
{
    private readonly string _ffprobe, _ffmpeg;
    private readonly SemaphoreSlim? _pool;
    private int _queued, _running;
    /// <summary>Validation requests waiting for a worker slot (the validation backlog).</summary>
    public int Queued => Volatile.Read(ref _queued);
    public int Running => Volatile.Read(ref _running);
    public int MaxWorkers { get; }
    public long Completed;

    /// <param name="maxWorkers">Bound on concurrent probe/decode processes shared by all monitors (0 = unbounded).</param>
    public FFprobeRunner(string ffprobe, string ffmpeg, int maxWorkers = 0)
    {
        _ffprobe = ffprobe; _ffmpeg = ffmpeg; MaxWorkers = maxWorkers;
        if (maxWorkers > 0) _pool = new SemaphoreSlim(maxWorkers);
    }

    private async Task<ProcResult> Run(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _queued);
        if (_pool != null) await _pool.WaitAsync(ct);
        Interlocked.Decrement(ref _queued); Interlocked.Increment(ref _running);
        try { return await ProcessUtil.RunAsync(exe, args, timeout, ct, ProcessRole.Validator, "", Path.GetFileNameWithoutExtension(exe) == "ffprobe" ? "probe" : "decode"); }
        finally { Interlocked.Decrement(ref _running); Interlocked.Increment(ref Completed); _pool?.Release(); }
    }

    /// <summary>Readiness: can this URL be opened AND decoded? Returns decoded video frames (0 = not ready). Stderr noise is not failure.</summary>
    public async Task<(long Frames, string Error)> DecodedFramesAsync(string url, double seconds, string? rtspTransport, CancellationToken ct = default)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-progress", "pipe:1" };
        if (rtspTransport != null && url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) { args.Add("-rtsp_transport"); args.Add(rtspTransport); }
        args.AddRange(new[] { "-i", url, "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture), "-map", "0:v:0", "-f", "null", "-" });
        var r = await ProcessUtil.RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(seconds + 20), ct, ProcessRole.Validator, "", "readiness-probe");
        long frames = 0;
        foreach (var l in r.StdOut.Split('\n'))
            if (l.StartsWith("frame=") && long.TryParse(l[6..].Trim(), out var f)) frames = Math.Max(frames, f);
        return (r.ExitCode == 0 ? frames : 0, r.ExitCode == 0 ? "" : FirstLine(r.StdErr, r.TimedOut ? "timeout" : $"exit {r.ExitCode}"));
    }

    public async Task<ProbeInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var r = await Run(_ffprobe, new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", file }, TimeSpan.FromSeconds(30), ct);
        if (r.ExitCode != 0) return new ProbeInfo(false, null, 0, false, false, null, 0, 0, 0, FirstLine(r.StdErr, r.TimedOut ? "ffprobe timeout" : $"ffprobe exit {r.ExitCode}"));
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var root = doc.RootElement;
            string? fmt = null; double dur = 0;
            if (root.TryGetProperty("format", out var f))
            {
                fmt = Str(f, "format_name");
                dur = Num(Str(f, "duration"));
            }
            bool v = false, a = false; string? vc = null; int w = 0, h = 0; double fps = 0;
            if (root.TryGetProperty("streams", out var ss))
                foreach (var st in ss.EnumerateArray())
                {
                    var type = Str(st, "codec_type");
                    if (type == "video" && !v)
                    {
                        v = true; vc = Str(st, "codec_name");
                        w = st.TryGetProperty("width", out var we) ? we.GetInt32() : 0;
                        h = st.TryGetProperty("height", out var he) ? he.GetInt32() : 0;
                        fps = Rational(Str(st, "avg_frame_rate")) is var fr && fr > 0 ? fr : Rational(Str(st, "r_frame_rate"));
                        if (dur <= 0) dur = Num(Str(st, "duration"));
                    }
                    else if (type == "audio") a = true;
                }
            return new ProbeInfo(true, fmt, dur, v, a, vc, w, h, fps, r.StdErr.Trim());
        }
        catch (Exception ex) { return new ProbeInfo(false, null, 0, false, false, null, 0, 0, 0, "ffprobe json: " + ex.Message); }
    }

    /// <summary>Real decode (not just metadata): ffmpeg -v error -xerror [-ss] -i f [-t] -f null -. Any error output is a failure.</summary>
    public async Task<DecodeResult> DecodeAsync(string file, double? seek = null, double? duration = null, CancellationToken ct = default, double timeoutSeconds = 120)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        if (seek is > 0) { args.Add("-ss"); args.Add(seek.Value.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.Add("-i"); args.Add(file);
        if (duration is > 0) { args.Add("-t"); args.Add(duration.Value.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-f", "null", "-" });
        var r = await Run(_ffmpeg, args, TimeSpan.FromSeconds(timeoutSeconds), ct);
        var lines = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ts = lines.Count(IsTimestampComplaint);
        var errors = lines.Where(l => !IsTimestampComplaint(l)).ToList();
        var ok = r.ExitCode == 0 && errors.Count == 0;
        return new DecodeResult(ok, ok ? "" : errors.FirstOrDefault() ?? (r.TimedOut ? "decode timeout" : $"exit {r.ExitCode}"), r.Elapsed.TotalSeconds, ts);
    }

    /// <summary>Video packet DTS/PTS times, for timestamp continuity analysis.</summary>
    public async Task<List<(double Pts, double Dts)>> PacketTimesAsync(string file, CancellationToken ct = default)
    {
        var r = await Run(_ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time", "-of", "csv=p=0", file }, TimeSpan.FromSeconds(60), ct);
        var list = new List<(double, double)>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            list.Add((Num(parts[0], double.NaN), Num(parts[1], double.NaN)));
        }
        return list;
    }

    /// <summary>Muxer-side timestamp complaints from the null output are timestamp anomalies, not undecodable media.</summary>
    public static bool IsTimestampComplaint(string line) =>
        line.Contains("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase) || line.Contains("Non-monotonic DTS", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Non-monotonous DTS", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string s, string fallback)
    {
        var l = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(l) ? fallback : l;
    }
    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : null;
    private static double Num(string? s, double def = 0) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : def;
    private static double Rational(string? s)
    {
        if (s == null) return 0;
        var p = s.Split('/');
        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d != 0) return n / d;
        return Num(s);
    }
}
