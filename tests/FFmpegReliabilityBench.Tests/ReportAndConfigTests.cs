using FFmpegReliabilityBench.App;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Reporting;
using FFmpegReliabilityBench.Core.Runner;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

public class ReporterTests
{
    [Fact]
    public void Reports_are_written_and_never_contain_credentials()
    {
        var root = TestUtil.TempDir();
        var cfg = new BenchConfig { ReportDirectory = root };
        cfg.Source.Url = "rtsp://admin:S3cretPassw0rd@10.0.0.5/stream";
        var st = new BenchState { Config = cfg, Mode = RunMode.PreflightOnly };
        st.ReportDirectory = Path.Combine(root, st.SessionId);
        st.Prerequisites.Add(TestResult.Make("NET-005", "Network", "Stream availability", TestStatus.Fail, CommandText.Mask(cfg.Source.Url) + " unreachable"));
        st.Bus.Publish(EventLevel.Fail, "Network", "*", "cannot open " + CommandText.Mask(cfg.Source.Url));

        Reporter.WriteAll(st);

        foreach (var f in Directory.GetFiles(st.ReportDirectory))
            Assert.DoesNotContain("S3cretPassw0rd", File.ReadAllText(f));
        var json = File.ReadAllText(st.ReportJson!);
        Assert.Contains("admin:***@10.0.0.5", json);
        Assert.Contains("\"Fail\"", json); // enums as strings
        Assert.Contains("FFmpeg Reliability Benchmark", File.ReadAllText(st.ReportHtml!));
        Assert.StartsWith("phase,id,category", File.ReadAllText(Path.Combine(st.ReportDirectory, "results.csv")));
        Assert.Equal(st.ReportDirectory, File.ReadAllText(Path.Combine(root, "latest.txt")).Trim());
        Directory.Delete(root, true);
    }

    [Fact]
    public void Session_ids_are_unique_even_when_started_in_the_same_second()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => new BenchState().SessionId).ToHashSet();
        Assert.True(ids.Count >= 49); // 16-bit random suffix; a collision in 50 draws is ~2%
    }
}

public class ConfigAndCliTests
{
    [Fact]
    public void Config_files_are_layered_later_wins()
    {
        var dir = TestUtil.TempDir();
        var a = Path.Combine(dir, "a.json"); var b = Path.Combine(dir, "b.json");
        File.WriteAllText(a, """{ "hls": { "targetDurationSeconds": 4 }, "benchmark": { "profile": "tee" } }""");
        File.WriteAllText(b, """{ "benchmark": { "profile": "baseline", "concurrentStreams": 3 } }""");
        var c = BenchConfig.Load(new[] { a, b });
        Assert.Equal(4, c.Hls.TargetDurationSeconds);
        Assert.Equal("baseline", c.Benchmark.Profile);
        Assert.Equal(3, c.Benchmark.ConcurrentStreams);
        Assert.Equal(5, c.Recording.SegmentDurationSeconds); // untouched defaults survive
    }

    [Fact]
    public void Cli_parses_modes_and_overrides()
    {
        var o = Cli.Parse(new[] { "--preflight-only", "--config", "x.json", "--duration", "90", "--profile", "tee", "--streams", "4", "--kill-at", "30", "--no-tui" });
        Assert.Equal(RunMode.PreflightOnly, o.Mode);
        Assert.Equal(new[] { "x.json" }, o.Configs);
        Assert.False(o.Tui);
        var c = new BenchConfig();
        Cli.ApplyOverrides(c, o);
        Assert.Equal(90, c.Benchmark.DurationSeconds);
        Assert.Equal("tee", c.Benchmark.Profile);
        Assert.Equal(4, c.Benchmark.ConcurrentStreams);
        Assert.Equal(30, c.Benchmark.InjectKillAtSeconds);
    }

    [Fact]
    public void Cli_rejects_unknown_arguments()
    {
        Assert.Throws<ArgumentException>(() => Cli.Parse(new[] { "positional" }));
        Assert.Throws<ArgumentException>(() => Cli.ApplyOverrides(new BenchConfig(), Cli.Parse(new[] { "--no-such-option", "1" })));
    }

    [Fact]
    public void Credentials_come_from_the_environment_never_from_config()
    {
        Environment.SetEnvironmentVariable("FRB_RTSP_USER", "viewer");
        Environment.SetEnvironmentVariable("FRB_RTSP_PASSWORD", "p@ss");
        try
        {
            if (!OperatingSystem.IsWindows()) // on Windows a Credential Manager entry could take precedence
                Assert.Equal("rtsp://viewer:p%40ss@cam.local/stream", CredentialProvider.ApplyTo("rtsp://cam.local/stream"));
            Assert.Equal("rtsp://other:x@cam.local/stream", CredentialProvider.ApplyTo("rtsp://other:x@cam.local/stream")); // explicit wins
            Assert.Equal("synthetic", CredentialProvider.ApplyTo("synthetic"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRB_RTSP_USER", null);
            Environment.SetEnvironmentVariable("FRB_RTSP_PASSWORD", null);
        }
    }

    [Fact]
    public void Every_shipped_config_and_profile_loads_and_builds_a_command()
    {
        var root = TestUtil.RepoRoot();
        var caps = new Capabilities { Major = 6, StatsPeriod = true, HlsTempFile = true, HlsFmp4 = true, VersionLine = "ffmpeg version test" };
        var files = Directory.GetFiles(Path.Combine(root, "config"), "*.json").Concat(Directory.GetFiles(Path.Combine(root, "profiles"), "*.json")).ToList();
        Assert.True(files.Count >= 10);
        foreach (var f in files)
        {
            var c = BenchConfig.Load(new[] { Path.Combine(root, "config", "default.json"), f });
            if (c.Benchmark.Profile == "copy") c.Source.Url = "rtsp://cam/stream"; // copy needs an encoded source
            var cmd = CommandBuilder.Build(c, caps, "/hls", "/rec");
            Assert.Contains("-progress", cmd.Args);
        }
    }
}
