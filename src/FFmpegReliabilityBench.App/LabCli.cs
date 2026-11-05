using System.Globalization;
using FFmpegReliabilityBench.Core.Config;

namespace FFmpegReliabilityBench.App;

/// <summary>Command-line options of the lab/run verbs, expressed as configuration keys (highest precedence layer).</summary>
public sealed class LabArgs
{
    public List<string> Configs { get; } = new();
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FaultDef> Faults { get; } = new();
    public bool NoTui { get; set; }
    public bool Help { get; set; }
}

public static class LabCli
{
    private static readonly Dictionary<string, string> Scalars = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--preset"] = "lab:preset", ["--streams"] = "lab:publishers:count", ["--consumers-per-stream"] = "lab:consumers:instancesPerSource",
        ["--execution"] = "lab:consumers:execution", ["--stagger"] = "lab:consumers:staggerMilliseconds", ["--group-duration"] = "lab:consumers:groupDurationSeconds",
        ["--stage-duration"] = "lab:consumers:stageDurationSeconds", ["--publisher-mode"] = "lab:publishers:mode", ["--fixture"] = "lab:publishers:fixturePreset",
        ["--relay"] = "lab:relay:mode", ["--relay-exe"] = "lab:relay:executable", ["--relay-url"] = "lab:relay:url", ["--rtsp-port"] = "lab:relay:rtspPort",
        ["--http-port"] = "lab:http:port", ["--run-root"] = "lab:runRoot", ["--workers"] = "lab:validation:maxDecodeWorkers",
        ["--ffmpeg"] = "ffmpeg:executable", ["--ffprobe"] = "ffmpeg:ffprobe", ["--refresh-hz"] = "ui:refreshHz", ["--history-seconds"] = "ui:historySeconds",
        ["--sample-every"] = "lab:validation:recordingHttpSampleEvery",
    };

    public static LabArgs Parse(IReadOnlyList<string> args)
    {
        var o = new LabArgs();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "-h": case "--help": o.Help = true; break;
                case "--config": o.Configs.Add(Next()); break;
                case "--no-tui": case "--headless": o.NoTui = true; o.Values["ui:mode"] = "headless"; break;
                case "--no-color": o.Values["ui:color"] = "false"; break;
                case "--ascii": o.Values["ui:asciiFallback"] = "on"; break;
                case "--no-http": o.Values["lab:http:enabled"] = "false"; break;
                case "--fail-fast": o.Values["lab:failFast"] = "true"; break;
                case "--no-player": o.Values["lab:validation:livePlayer"] = "false"; break;
                case "--duration": o.Values["lab:durationSeconds"] = ParseDuration(Next()).ToString(CultureInfo.InvariantCulture); break;
                case "--profiles": case "--profile": o.Lists["lab:consumers:profiles"] = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--stages": o.Lists["lab:consumers:stages"] = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                case "--source":
                    if (!o.Lists.TryGetValue("lab:sources", out var l)) o.Lists["lab:sources"] = l = new List<string>();
                    l.Add(Next()); break;
                case "--kill-publisher": o.Faults.Add(Fault("kill-publisher", Next())); break;
                case "--kill-consumer": o.Faults.Add(Fault("kill-consumer", Next())); break;
                default:
                    if (Scalars.TryGetValue(a, out var key)) { o.Values[key] = Next(); break; }
                    throw new ArgumentException($"unknown option {a} (see StreamBench help)");
            }
        }
        if (o.Values.ContainsKey("lab:relay:url") && !o.Values.ContainsKey("lab:relay:mode")) o.Values["lab:relay:mode"] = "external";
        return o;
    }

    /// <summary>"00:30:00", "1800", "30m", "90s", "2h".</summary>
    public static int ParseDuration(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs)) return secs;
        if (s.Contains(':') && TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts)) return (int)ts.TotalSeconds;
        if (s.Length > 1 && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return s[^1] switch { 's' => (int)v, 'm' => (int)(v * 60), 'h' => (int)(v * 3600), _ => throw new ArgumentException($"invalid duration '{s}'") };
        throw new ArgumentException($"invalid duration '{s}' (use hh:mm:ss, seconds, or 30m / 90s / 2h)");
    }

    private static FaultDef Fault(string type, string spec)
    {
        var at = spec.LastIndexOf('@');
        if (at <= 0) throw new ArgumentException($"{type} expects TARGET@SECONDS, e.g. s02@30");
        return new FaultDef { Type = type, Target = spec[..at], AtSeconds = ParseDuration(spec[(at + 1)..]) };
    }
}
