using System.Globalization;
using System.Text.Json;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed record ProbeInfo(bool Ok, string? Format, double Duration, bool HasVideo, bool HasAudio,
    string? VideoCodec, int Width, int Height, double Fps, string Error);

public sealed record DecodeResult(bool Ok, string Error, double ElapsedSeconds);

public sealed class FFprobeRunner
{
    private readonly string _ffprobe, _ffmpeg;
    public FFprobeRunner(string ffprobe, string ffmpeg) { _ffprobe = ffprobe; _ffmpeg = ffmpeg; }

    public async Task<ProbeInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessUtil.RunAsync(_ffprobe, new[] { "-v", "error", "-show_format", "-show_streams", "-of", "json", file }, TimeSpan.FromSeconds(30), ct);
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
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-xerror" };
        if (seek is > 0) { args.Add("-ss"); args.Add(seek.Value.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.Add("-i"); args.Add(file);
        if (duration is > 0) { args.Add("-t"); args.Add(duration.Value.ToString("0.###", CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-f", "null", "-" });
        var r = await ProcessUtil.RunAsync(_ffmpeg, args, TimeSpan.FromSeconds(timeoutSeconds), ct);
        var err = r.StdErr.Trim();
        var ok = r.ExitCode == 0 && err.Length == 0;
        return new DecodeResult(ok, ok ? "" : FirstLine(err, r.TimedOut ? "decode timeout" : $"exit {r.ExitCode}"), r.Elapsed.TotalSeconds);
    }

    /// <summary>Video packet DTS/PTS times, for timestamp continuity analysis.</summary>
    public async Task<List<(double Pts, double Dts)>> PacketTimesAsync(string file, CancellationToken ct = default)
    {
        var r = await ProcessUtil.RunAsync(_ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time", "-of", "csv=p=0", file }, TimeSpan.FromSeconds(60), ct);
        var list = new List<(double, double)>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            list.Add((Num(parts[0], double.NaN), Num(parts[1], double.NaN)));
        }
        return list;
    }

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
