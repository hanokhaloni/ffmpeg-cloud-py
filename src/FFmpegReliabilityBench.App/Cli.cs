using System.Globalization;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.App;

public sealed class CliOptions
{
    public List<string> Configs = new(); public bool Help, Tui = true, ReportLatest, PrintCommand;
    public RunMode Mode = RunMode.Benchmark;
    public Dictionary<string, string> Overrides = new();
}

public static class Cli
{
    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "-h": case "--help": o.Help = true; break;
                case "--config": o.Configs.Add(Next()); break;
                case "--no-tui": o.Tui = false; break;
                case "--preflight-only": o.Mode = RunMode.PreflightOnly; break;
                case "--filesystem-only": o.Mode = RunMode.FilesystemOnly; break;
                case "--hls-only": o.Mode = RunMode.HlsOnly; break;
                case "--recording-only": o.Mode = RunMode.RecordingOnly; break;
                case "--benchmark": o.Mode = RunMode.Benchmark; break;
                case "--print-command": o.PrintCommand = true; break;
                case "--report": var v = Next(); if (v == "latest") o.ReportLatest = true; else throw new ArgumentException("--report supports 'latest'"); break;
                default:
                    if (args[i].StartsWith("--")) o.Overrides[args[i][2..]] = Next();
                    else throw new ArgumentException($"unknown argument {args[i]}");
                    break;
            }
        }
        return o;
    }

    public static void ApplyOverrides(BenchConfig c, CliOptions o)
    {
        foreach (var (k, v) in o.Overrides)
        {
            double D() => double.Parse(v, CultureInfo.InvariantCulture);
            int I() => int.Parse(v, CultureInfo.InvariantCulture);
            switch (k)
            {
                case "source": c.Source.Url = v; break;
                case "transport": c.Source.Transport = v; break;
                case "fps": c.Source.ExpectedFps = I(); break;
                case "duration": c.Benchmark.DurationSeconds = I(); break;
                case "profile": c.Benchmark.Profile = v; break;
                case "streams": c.Benchmark.ConcurrentStreams = I(); break;
                case "kill-at": c.Benchmark.InjectKillAtSeconds = I(); break;
                case "hls-dir": c.Storage.HlsDirectory = v; break;
                case "rec-dir": c.Storage.RecordingDirectory = v; break;
                case "recording-mode": c.Recording.Mode = v; break;
                case "hls-time": c.Hls.TargetDurationSeconds = D(); break;
                case "segment-time": c.Recording.SegmentDurationSeconds = D(); break;
                case "hls-flags": c.Hls.Flags = v; break;
                case "http-url": c.Hls.HttpUrl = v; break;
                case "ffmpeg": c.Ffmpeg.Executable = v; break;
                case "ffprobe": c.Ffmpeg.Ffprobe = v; break;
                case "ntp": c.Time.Enabled = true; c.Time.NtpServer = v; break;
                case "onvif": c.Time.Enabled = true; c.Time.OnvifUrl = v; break;
                case "max-clock-ms": c.Time.MaxClockDifferenceMs = D(); break;
                case "reports": c.ReportDirectory = v; break;
                case "full-decode": c.Recording.FullDecode = bool.Parse(v); break;
                default: throw new ArgumentException($"unknown option --{k}");
            }
        }
    }

    public static void PrintHelp() => Console.WriteLine($"""
        FFmpegReliabilityBench - end-to-end FFmpeg stream & recording reliability tester

        Usage: FFmpegReliabilityBench [mode] [--config file.json [--config profile.json ...]] [overrides] [--no-tui]

        Modes:
          --benchmark (default)  preflight + filesystem + network + time + live HLS/recording validation
          --preflight-only       system/ffmpeg/filesystem/network/time checks, no FFmpeg run
          --filesystem-only      storage diagnostics for the HLS and recording directories
          --hls-only | --recording-only
          --print-command        show the exact FFmpeg command/ArgumentList for the profile and exit
          --report latest        summary of the latest report

        Overrides:
          --source <synthetic|rtsp://host/path|file>   --transport tcp|udp   --fps 25
          --profile {string.Join("|", CommandBuilder.Profiles)}   --recording-mode mp4|faststart|fmp4
          --duration <s>  --streams <n>  --kill-at <s> (fault injection: forced termination)
          --hls-dir <dir> --rec-dir <dir> --hls-time <s> --segment-time <s> --hls-flags <flags>
          --http-url <url of playlist; {"{session}"} and {"{stream}"} placeholders>
          --ntp <server> --onvif <http://cam/onvif/device_service> --max-clock-ms 500
          --ffmpeg <path> --ffprobe <path> --reports <dir> --full-decode true|false

        Credentials: never put passwords in config. Use Windows Credential Manager
          cmdkey /generic:FFmpegReliabilityBench:<camera-host> /user:<user> /pass:<password>
        or FRB_RTSP_USER / FRB_RTSP_PASSWORD environment variables.

        Keys (TUI): 1 overview 2 tests 3 ffmpeg 4 hls 5 recording 6 resources 0 events  S save report  Q graceful stop
        Exit codes: 0 PASS, 1 WARN, 2 FAIL, 3 fatal
        """);
}
