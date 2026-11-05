using System.Text.RegularExpressions;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Lab;

public sealed class TemplateException : Exception { public TemplateException(string m) : base(m) { } }

/// <summary>What the monitors need to know about an expanded command's outputs.</summary>
public sealed record OutputLayout(string? PlaylistPath, double HlsTime, int HlsListSize, bool HlsFmp4,
    string? RecordingDir, string? RecordingFileRegex, double SegmentTime, string RecordingMode, string? SegmentListPath);

public static class CommandTemplates
{
    public static readonly string[] Placeholders = { "inputUrl", "streamId", "profileId", "instanceId", "runDir", "hlsDir", "recordingDir", "segmentListPath" };
    private static readonly Regex PlaceholderRx = new(@"\{([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    { ["controlled-copy"] = "dual-copy", ["controlled-transcode"] = "dual-transcode", ["original-command"] = "original" };

    private static readonly string[] Hls2 = { "-f", "hls", "-hls_time", "2", "-hls_list_size", "6", "-hls_flags", "delete_segments+independent_segments+temp_file", "-hls_delete_threshold", "6",
        "-hls_segment_filename", "{hlsDir}/seg_%06d.ts", "{hlsDir}/stream.m3u8" };
    private static readonly string[] Rec5 = { "-f", "segment", "-segment_time", "5", "-reset_timestamps", "1", "-segment_format", "mp4", "-segment_format_options", "movflags=+faststart",
        "-segment_list", "{segmentListPath}", "-segment_list_type", "csv", "{recordingDir}/rec_%06d.mp4" };
    private static readonly string[] In = { "-rtsp_transport", "tcp", "-i", "{inputUrl}" };
    private static readonly string[] Maps = { "-map", "0:v:0", "-map", "0:a:0?" };

    public static readonly IReadOnlyList<CommandTemplate> BuiltIn = new List<CommandTemplate>
    {
        new() { Id = "dual-copy", Description = "Stream copy → HLS (2 s, 6 entries, temp_file) + segmented MP4 (5 s, faststart, segment list). Default.",
            Arguments = In.Concat(Maps).Concat(new[] { "-c", "copy" }).Concat(Hls2).Concat(Maps).Concat(new[] { "-c", "copy" }).Concat(Rec5).ToList() },
        new() { Id = "dual-transcode", Description = "Two independent libx264/AAC encodes → HLS (GOP 2 s) + segmented MP4 (GOP 5 s)",
            Arguments = In.Concat(Maps).Concat(new[] { "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-c:a", "aac" }).Concat(Hls2)
                .Concat(Maps).Concat(new[] { "-c:v", "libx264", "-preset", "veryfast", "-g", "125", "-keyint_min", "125", "-sc_threshold", "0", "-c:a", "aac" }).Concat(Rec5).ToList() },
        new() { Id = "original", Description = "Replica of the production command shape: -re, split=2/asplit=2, two libx264/AAC encodes, HLS list 3 + plain MP4 segments. Edit to your exact arguments.",
            Arguments = new List<string> { "-re", "-rtsp_transport", "tcp", "-i", "{inputUrl}", "-filter_complex", "[0:v]split=2[v1][v2];[0:a]asplit=2[a1][a2]",
                "-map", "[v1]", "-map", "[a1]", "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-c:a", "aac",
                "-f", "hls", "-hls_time", "2", "-hls_list_size", "3", "-hls_flags", "delete_segments", "-hls_segment_filename", "{hlsDir}/seg_%06d.ts", "{hlsDir}/stream.m3u8",
                "-map", "[v2]", "-map", "[a2]", "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-c:a", "aac",
                "-f", "segment", "-segment_time", "5", "-reset_timestamps", "1", "-segment_format", "mp4", "{recordingDir}/rec_%06d.mp4" } },
        new() { Id = "hls-copy", Description = "Stream copy → HLS only", Arguments = In.Concat(Maps).Concat(new[] { "-c", "copy" }).Concat(Hls2).ToList() },
        new() { Id = "rec-copy", Description = "Stream copy → segmented MP4 only", Arguments = In.Concat(Maps).Concat(new[] { "-c", "copy" }).Concat(Rec5).ToList() },
        new() { Id = "rec-fmp4-copy", Description = "Stream copy → fragmented MP4 segments (1 s fragments, recoverable after a crash)",
            Arguments = In.Concat(Maps).Concat(new[] { "-c", "copy", "-f", "segment", "-segment_time", "5", "-reset_timestamps", "1", "-segment_format", "mp4",
                "-segment_format_options", "movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=1000000:flush_packets=1",
                "-segment_list", "{segmentListPath}", "-segment_list_type", "csv", "{recordingDir}/rec_%06d.mp4" }).ToList() },
    };

    public static CommandTemplate Resolve(string id, IEnumerable<CommandTemplate> custom)
    {
        var key = Aliases.GetValueOrDefault(id) ?? id;
        return custom.LastOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || t.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? BuiltIn.FirstOrDefault(t => t.Id.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new TemplateException($"unknown profile '{id}'. Built-in: {string.Join(", ", BuiltIn.Select(t => t.Id))}; aliases: {string.Join(", ", Aliases.Keys)}");
    }

    /// <summary>Validates placeholders without expanding (unknown placeholders are rejected before launch).</summary>
    public static void Validate(CommandTemplate t)
    {
        if (t.Arguments.Count == 0) throw new TemplateException($"template '{t.Id}' has no arguments");
        for (int i = 0; i < t.Arguments.Count; i++)
            foreach (Match m in PlaceholderRx.Matches(t.Arguments[i].Replace("{{", "").Replace("}}", "")))
                if (!Placeholders.Contains(m.Groups[1].Value))
                    throw new TemplateException($"template '{t.Id}': unknown placeholder {{{m.Groups[1].Value}}} in argument [{i}] '{t.Arguments[i]}'. Allowed: {string.Join(" ", Placeholders.Select(p => "{" + p + "}"))}");
        if (!t.Arguments.Any(a => a.Contains("{inputUrl}"))) throw new TemplateException($"template '{t.Id}' never reads {{inputUrl}}");
        if (t.Arguments.Any(a => a.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) || a.Contains("powershell", StringComparison.OrdinalIgnoreCase)))
            throw new TemplateException($"template '{t.Id}': commands are argument arrays for FFmpeg; shells are not allowed");
    }

    /// <summary>Expands placeholders inside individual arguments ({{ and }} are literal braces).</summary>
    public static List<string> Expand(CommandTemplate t, IReadOnlyDictionary<string, string> values)
    {
        Validate(t);
        return t.Arguments.Select(a =>
        {
            var escaped = a.Replace("{{", "\u0001").Replace("}}", "\u0002");
            var x = PlaceholderRx.Replace(escaped, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : throw new TemplateException($"no value for {{{m.Groups[1].Value}}}"));
            return x.Replace('\u0001', '{').Replace('\u0002', '}');
        }).ToList();
    }

    /// <summary>
    /// Capability + instrumentation rules applied to every managed consumer:
    /// machine-readable progress, level-tagged logs, version-appropriate RTSP timeout, unsupported HLS flags removed.
    /// </summary>
    public static List<string> Instrument(List<string> args, Capabilities caps, bool injectRtspTimeout, List<string> notes)
    {
        var a = new List<string>(args);
        for (int i = 0; i < a.Count - 1; i++)
            if (a[i] == "-hls_flags" && a[i + 1].Contains("temp_file") && !caps.HlsTempFile)
            { a[i + 1] = string.Join('+', a[i + 1].Split('+').Where(f => f != "temp_file")); notes.Add("hls_flags temp_file unsupported by this FFmpeg build: removed"); }
        if (injectRtspTimeout && !a.Any(x => x is "-timeout" or "-stimeout" or "-rw_timeout"))
        {
            var iIdx = a.FindIndex(x => x == "-i");
            if (iIdx >= 0 && iIdx + 1 < a.Count && a[iIdx + 1].StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            { a.InsertRange(iIdx, caps.Major >= 5 ? new[] { "-timeout", "5000000" } : new[] { "-stimeout", "5000000" }); notes.Add($"RTSP socket timeout {(caps.Major >= 5 ? "-timeout" : "-stimeout")} 5 s inserted"); }
        }
        var pre = new List<string> { "-hide_banner", "-nostats", "-loglevel", "level+info", "-progress", "pipe:1" };
        if (caps.StatsPeriod) pre.AddRange(new[] { "-stats_period", "1" });
        pre.Add("-y");
        foreach (var flag in new[] { "-hide_banner", "-nostats", "-y" }) a.Remove(flag);
        RemovePair(a, "-loglevel"); RemovePair(a, "-v"); RemovePair(a, "-progress"); RemovePair(a, "-stats_period");
        return pre.Concat(a).ToList();
    }

    private static void RemovePair(List<string> a, string opt)
    {
        int i;
        while ((i = a.IndexOf(opt)) >= 0 && i + 1 < a.Count) a.RemoveRange(i, 2);
    }

    private static readonly HashSet<string> NoValueFlags = new() { "-y", "-n", "-nostats", "-stats", "-hide_banner", "-re", "-nostdin", "-shortest", "-an", "-vn", "-sn", "-dn",
        "-copyts", "-start_at_zero", "-ignore_unknown", "-benchmark", "-xerror", "-stdin", "-autorotate", "-noautorotate" };
    private static readonly string[] PathValueOptions = { "-hls_segment_filename", "-segment_list", "-hls_fmp4_init_filename", "-master_pl_name" };

    /// <summary>True when args[i] is a positional token (an output URL/path), i.e. not the value of a preceding option.</summary>
    public static bool IsPositional(IReadOnlyList<string> a, int i)
    {
        if (a[i].StartsWith('-')) return false;
        if (i == 0) return true;
        var prev = a[i - 1];
        if (!prev.StartsWith('-')) return true;
        if (double.TryParse(prev, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return true; // negative number value
        return NoValueFlags.Contains(prev);
    }

    /// <summary>Derives the outputs to monitor from an expanded command (HLS playlist, recording pattern, durations).</summary>
    public static OutputLayout Describe(IReadOnlyList<string> args)
    {
        string? playlist = null, recDir = null, recRegex = null, segList = null; double hlsTime = 2, segTime = 2; int listSize = 5; bool fmp4 = false; var recMode = "mp4";
        string? Val(int i) => i + 1 < args.Count ? args[i + 1] : null;
        double D(string? v, double def) => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
        var format = "";
        for (int i = 0; i < args.Count; i++)
        {
            var x = args[i];
            switch (x)
            {
                case "-f": format = Val(i) ?? ""; continue;
                case "-hls_time": hlsTime = D(Val(i), hlsTime); continue;
                case "-hls_list_size": listSize = (int)D(Val(i), listSize); continue;
                case "-hls_segment_type": fmp4 |= Val(i) == "fmp4"; continue;
                case "-segment_time": segTime = D(Val(i), segTime); continue;
                case "-segment_list": segList = Val(i); continue;
                case "-segment_format_options" or "-movflags":
                    var mv = Val(i) ?? "";
                    recMode = mv.Contains("frag_") ? "fmp4" : mv.Contains("faststart") ? "faststart" : recMode;
                    continue;
            }
            if (!IsPositional(args, i)) continue;
            if (format == "hls" || x.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) playlist ??= x;
            else if (format == "segment") { recDir ??= Path.GetDirectoryName(x); recRegex ??= Validation.RecordingMonitor.PatternToRegex(Path.GetFileName(x)); }
            format = "";
        }
        return new OutputLayout(playlist, hlsTime, listSize, fmp4, recDir, recRegex, segTime, recMode, segList);
    }

    /// <summary>All file outputs of an expanded command (positional outputs + path-valued options), for collision checks.</summary>
    public static IEnumerable<string> OutputPaths(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var x = args[i];
            if (PathValueOptions.Contains(x) && i + 1 < args.Count) { yield return Norm(args[i + 1]); i++; continue; }
            if (IsPositional(args, i) && !x.Contains("://") && x != "-") yield return Norm(x);
        }
        static string Norm(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
    }

    /// <summary>Rejects output-path collisions across (and within) instances before anything is launched.</summary>
    public static void CheckCollisions(IEnumerable<(string InstanceId, IReadOnlyList<string> Args)> instances)
    {
        var seen = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (id, args) in instances)
            foreach (var p in OutputPaths(args).Distinct())
            {
                if (seen.TryGetValue(p, out var other))
                    throw new TemplateException($"output path collision: '{p}' is written by both '{other}' and '{id}'. Use {{hlsDir}}/{{recordingDir}} placeholders so every instance gets isolated outputs.");
                seen[p] = id;
            }
    }
}
