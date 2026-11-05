using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace FFmpegReliabilityBench.Core.Runner;

/// <summary>Discovered at startup; never assume an option from a newer FFmpeg exists in 4.4.</summary>
public sealed class Capabilities
{
    public string VersionLine { get; set; } = "";
    public int Major { get; set; }
    public int Minor { get; set; }
    public string BuildConfiguration { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public HashSet<string> Encoders { get; set; } = new();
    public HashSet<string> Muxers { get; set; } = new();
    public bool StatsPeriod { get; set; }
    public bool HlsTempFile { get; set; }
    public bool HlsFmp4 { get; set; }
    public bool TeeOnFail { get; set; }
    public string FfprobeVersionLine { get; set; } = "";
    public Dictionary<string, string> RawOutputs { get; set; } = new();

    public static async Task<Capabilities> DiscoverAsync(string ffmpeg, string ffprobe, CancellationToken ct = default)
    {
        var c = new Capabilities();
        var t = TimeSpan.FromSeconds(20);
        var ver = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-version" }, t, ct);
        c.RawOutputs["-version"] = ver.StdOut + ver.StdErr;
        c.VersionLine = (ver.StdOut + ver.StdErr).Split('\n').FirstOrDefault()?.Trim() ?? "";
        var m = Regex.Match(c.VersionLine, @"version\s+n?(\d+)\.(\d+)");
        if (m.Success) { c.Major = int.Parse(m.Groups[1].Value); c.Minor = int.Parse(m.Groups[2].Value); }
        else if (Regex.IsMatch(c.VersionLine, @"version\s+(N-|git-|\d{4}-)")) { c.Major = 99; } // nightly/master build
        c.BuildConfiguration = Regex.Match(ver.StdOut, @"configuration:\s*(.*)").Groups[1].Value.Trim();
        c.ExecutablePath = ResolvePath(ffmpeg) ?? ffmpeg;
        try { using var fs = File.OpenRead(c.ExecutablePath); c.Sha256 = Convert.ToHexString(SHA256.HashData(fs)); } catch { c.Sha256 = "unavailable"; }

        var enc = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, t, ct);
        c.RawOutputs["-encoders"] = enc.StdOut;
        c.Encoders = ParseList(enc.StdOut);
        var mux = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-muxers" }, t, ct);
        c.RawOutputs["-muxers"] = mux.StdOut;
        c.Muxers = ParseList(mux.StdOut);
        var hls = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-h", "muxer=hls" }, t, ct);
        c.RawOutputs["-h muxer=hls"] = hls.StdOut;
        c.HlsTempFile = hls.StdOut.Contains("temp_file");
        c.HlsFmp4 = hls.StdOut.Contains("fmp4");
        foreach (var mx in new[] { "segment", "mp4", "tee" })
        {
            var h = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-h", "muxer=" + mx }, t, ct);
            c.RawOutputs["-h muxer=" + mx] = h.StdOut;
        }
        c.TeeOnFail = c.Muxers.Contains("tee") && (c.Major >= 4 || c.Major == 0);
        var full = await ProcessUtil.RunAsync(ffmpeg, new[] { "-hide_banner", "-h", "long" }, t, ct);
        c.StatsPeriod = full.StdOut.Contains("-stats_period");
        var pv = await ProcessUtil.RunAsync(ffprobe, new[] { "-hide_banner", "-version" }, t, ct);
        c.FfprobeVersionLine = (pv.StdOut + pv.StdErr).Split('\n').FirstOrDefault()?.Trim() ?? "";
        return c;
    }

    private static HashSet<string> ParseList(string s)
    {
        // Lines look like " V....D libx264  ..." or "  E mp4  MP4 (MPEG-4 Part 14)" (muxers may be "E" or " E")
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in s.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length <= 6 && !parts[0].Contains('=') && parts[0] != "--")
                foreach (var n in parts[1].Split(',')) set.Add(n);
        }
        return set;
    }

    public static string? ResolvePath(string exe)
    {
        if (File.Exists(exe)) return Path.GetFullPath(exe);
        var names = OperatingSystem.IsWindows() && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { exe + ".exe", exe } : new[] { exe };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var n in names)
            {
                try { var p = Path.Combine(dir, n); if (File.Exists(p)) return p; } catch { }
            }
        return null;
    }
}
