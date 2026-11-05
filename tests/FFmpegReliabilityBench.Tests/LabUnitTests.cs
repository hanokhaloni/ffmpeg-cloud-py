using System.Net;
using System.Net.Sockets;
using FFmpegReliabilityBench.App;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Lab;
using FFmpegReliabilityBench.Core.Runner;
using FFmpegReliabilityBench.Core.Validation;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

public class CommandTemplateTests
{
    private static Capabilities Caps(int major, bool tempFile = true) => new() { Major = major, StatsPeriod = true, HlsTempFile = tempFile, HlsFmp4 = true };
    private static Dictionary<string, string> Values(string inst) => new()
    {
        ["inputUrl"] = "rtsp://127.0.0.1:8554/s01", ["streamId"] = "s01", ["profileId"] = "dual-copy", ["instanceId"] = inst, ["runDir"] = "/run",
        ["hlsDir"] = $"/run/consumers/{inst}/hls", ["recordingDir"] = $"/run/consumers/{inst}/rec", ["segmentListPath"] = $"/run/consumers/{inst}/segments.csv"
    };

    [Fact]
    public void Every_builtin_template_is_valid_and_describes_its_outputs()
    {
        foreach (var t in CommandTemplates.BuiltIn)
        {
            CommandTemplates.Validate(t);
            var args = CommandTemplates.Expand(t, Values("x"));
            Assert.DoesNotContain(args, a => a.Contains('{') && a.Contains('}') && !a.Contains("[0:"));
            var o = CommandTemplates.Describe(args);
            Assert.True(o.PlaylistPath != null || o.RecordingDir != null, t.Id);
        }
    }

    [Fact]
    public void Dual_copy_layout_matches_the_spec_defaults()
    {
        var args = CommandTemplates.Expand(CommandTemplates.Resolve("dual-copy", Array.Empty<CommandTemplate>()), Values("s01-dual-copy"));
        var o = CommandTemplates.Describe(args);
        Assert.Equal("/run/consumers/s01-dual-copy/hls/stream.m3u8", o.PlaylistPath);
        Assert.Equal(2, o.HlsTime); Assert.Equal(6, o.HlsListSize);
        Assert.Equal(5, o.SegmentTime);
        Assert.Equal("faststart", o.RecordingMode);
        Assert.Equal("/run/consumers/s01-dual-copy/segments.csv", o.SegmentListPath);
        Assert.Matches(o.RecordingFileRegex!, "rec_000042.mp4");
        Assert.Contains("copy", args);
    }

    [Fact]
    public void Aliases_resolve_and_unknown_profiles_are_rejected()
    {
        Assert.Equal("dual-copy", CommandTemplates.Resolve("controlled-copy", Array.Empty<CommandTemplate>()).Id);
        Assert.Equal("dual-transcode", CommandTemplates.Resolve("controlled-transcode", Array.Empty<CommandTemplate>()).Id);
        Assert.Throws<TemplateException>(() => CommandTemplates.Resolve("nope", Array.Empty<CommandTemplate>()));
    }

    [Fact]
    public void Unknown_placeholders_are_rejected_before_launch()
    {
        var t = new CommandTemplate { Id = "bad", Arguments = new() { "-i", "{inputUrl}", "{outputDir}/x.m3u8" } };
        var ex = Assert.Throws<TemplateException>(() => CommandTemplates.Validate(t));
        Assert.Contains("{outputDir}", ex.Message);
    }

    [Fact]
    public void Double_braces_are_literal()
    {
        var t = new CommandTemplate { Id = "t", Arguments = new() { "-i", "{inputUrl}", "-metadata", "title={{streamId}}={streamId}" } };
        Assert.Equal("title={streamId}=s01", CommandTemplates.Expand(t, Values("i"))[3]);
    }

    [Fact]
    public void Shells_are_not_allowed_in_templates()
        => Assert.Throws<TemplateException>(() => CommandTemplates.Validate(new CommandTemplate { Id = "x", Arguments = new() { "cmd.exe", "/c", "ffmpeg -i {inputUrl}" } }));

    [Fact]
    public void Output_collisions_are_rejected()
    {
        var shared = new CommandTemplate { Id = "shared", Arguments = new() { "-i", "{inputUrl}", "-f", "hls", "/same/stream.m3u8" } };
        var a = CommandTemplates.Expand(shared, Values("a")); var b = CommandTemplates.Expand(shared, Values("b"));
        var ex = Assert.Throws<TemplateException>(() => CommandTemplates.CheckCollisions(new[] { ("a", (IReadOnlyList<string>)a), ("b", (IReadOnlyList<string>)b) }));
        Assert.Contains("collision", ex.Message);
        var ok = CommandTemplates.BuiltIn[0];
        CommandTemplates.CheckCollisions(new[] { ("a", (IReadOnlyList<string>)CommandTemplates.Expand(ok, Values("a"))), ("b", (IReadOnlyList<string>)CommandTemplates.Expand(ok, Values("b"))) });
    }

    [Fact]
    public void Instrumentation_applies_capability_rules()
    {
        var args = CommandTemplates.Expand(CommandTemplates.Resolve("dual-copy", Array.Empty<CommandTemplate>()), Values("i"));
        var notes = new List<string>();
        var v4 = CommandTemplates.Instrument(args, Caps(4, tempFile: false), true, notes);
        Assert.Equal(new[] { "-hide_banner", "-nostats", "-loglevel", "level+info", "-progress", "pipe:1", "-stats_period", "1", "-y" }, v4.Take(9));
        Assert.Contains("-stimeout", v4);
        Assert.DoesNotContain(v4, a => a.Contains("temp_file"));
        Assert.Contains(notes, n => n.Contains("temp_file"));
        var v7 = CommandTemplates.Instrument(args, Caps(7), true, new List<string>());
        Assert.Contains("-timeout", v7); Assert.Contains(v7, a => a.Contains("temp_file"));
        Assert.True(v7.IndexOf("-timeout") < v7.IndexOf("-i"));
    }

    [Fact]
    public void Positional_detection_distinguishes_values_from_outputs()
    {
        var a = new[] { "-y", "out0.mp4", "-ss", "-2", "out1.ts", "-hls_segment_filename", "seg_%d.ts", "pl.m3u8" };
        Assert.True(CommandTemplates.IsPositional(a, 1));
        Assert.False(CommandTemplates.IsPositional(a, 3));
        Assert.True(CommandTemplates.IsPositional(a, 4));
        Assert.False(CommandTemplates.IsPositional(a, 6));
        Assert.True(CommandTemplates.IsPositional(a, 7));
    }
}

public class LabConfigTests
{
    [Fact]
    public void Defaults_are_the_spec_lab_profile()
    {
        var r = ConfigLoader.Load(Array.Empty<string>(), new Dictionary<string, string?>());
        var l = r.Config.Lab;
        Assert.Equal("local-4-streams", l.Preset);
        Assert.Equal(4, l.Publishers.Count); Assert.Equal(1, l.Consumers.InstancesPerSource);
        Assert.Equal(new[] { "dual-copy" }, l.Consumers.Profiles); Assert.Equal("parallel", l.Consumers.Execution);
        Assert.Equal(600, l.DurationSeconds); Assert.Equal(500, l.Consumers.StaggerMilliseconds);
        Assert.Equal("127.0.0.1", l.Relay.ListenAddress); Assert.Equal(8554, l.Relay.RtspPort);
        Assert.Equal("127.0.0.1", l.Http.ListenAddress); Assert.Equal(8088, l.Http.Port);
        Assert.Equal("loop-file", l.Publishers.Mode); Assert.Equal("720p25-h264-aac", l.Publishers.FixturePreset);
        Assert.Equal(4, r.Config.Ui.RefreshHz); Assert.Equal(300, r.Config.Ui.HistorySeconds);
    }

    [Fact]
    public void Precedence_is_defaults_then_preset_then_file_then_command_line_with_origins()
    {
        var dir = TestUtil.TempDir();
        var f = Path.Combine(dir, "site.json");
        File.WriteAllText(f, """{ "lab": { "preset": "mixed-4x3", "durationSeconds": 120, "publishers": { "count": 6 } } }""");
        var r = ConfigLoader.Load(new[] { f }, new Dictionary<string, string?> { ["lab:publishers:count"] = "8" });
        Assert.Equal("mixed-4x3", r.PresetName);
        Assert.Equal(3, r.Config.Lab.Consumers.InstancesPerSource);   // from preset
        Assert.Equal(120, r.Config.Lab.DurationSeconds);              // from file
        Assert.Equal(8, r.Config.Lab.Publishers.Count);               // CLI beats file and preset
        string O(string k) => r.Values.Single(v => v.Key.Equals(k, StringComparison.OrdinalIgnoreCase)).Origin;
        Assert.Equal("command line", O("lab:publishers:count"));
        Assert.Equal("config: site.json", O("lab:durationSeconds"));
        Assert.Equal("preset: mixed-4x3", O("lab:consumers:instancesPerSource"));
        Assert.Equal("default", O("lab:http:port"));
    }

    [Fact]
    public void Lists_are_replaced_not_merged_and_comparisons_default_to_sequential()
    {
        var r = ConfigLoader.Load(Array.Empty<string>(), new Dictionary<string, string?> { ["lab:preset"] = "profile-compare" },
            new Dictionary<string, List<string>> { ["lab:consumers:profiles"] = new() { "dual-copy" } });
        Assert.Equal(new[] { "dual-copy" }, r.Config.Lab.Consumers.Profiles);
        var cmp = ConfigLoader.Load(Array.Empty<string>(), new Dictionary<string, string?>(), new Dictionary<string, List<string>> { ["lab:consumers:profiles"] = new() { "original", "dual-copy" } });
        Assert.Equal("sequential", cmp.Config.Lab.Consumers.Execution); // never silently benchmark competing commands against each other
    }

    [Fact]
    public void Unknown_preset_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ConfigLoader.Load(Array.Empty<string>(), new Dictionary<string, string?> { ["lab:preset"] = "nope" }));

    [Theory]
    [InlineData("00:10:00", 600)] [InlineData("1800", 1800)] [InlineData("30m", 1800)] [InlineData("90s", 90)] [InlineData("2h", 7200)]
    public void Durations_parse(string s, int seconds) => Assert.Equal(seconds, LabCli.ParseDuration(s));

    [Fact]
    public void Lab_cli_maps_options_to_configuration_keys()
    {
        var o = LabCli.Parse(new[] { "--streams", "8", "--consumers-per-stream", "3", "--duration", "00:30:00", "--profiles", "original,dual-copy", "--execution", "sequential",
            "--relay-url", "rtsp://10.0.0.2:8554", "--kill-publisher", "s02@60", "--no-tui", "--config", "a.json" });
        Assert.Equal("8", o.Values["lab:publishers:count"]); Assert.Equal("3", o.Values["lab:consumers:instancesPerSource"]);
        Assert.Equal("1800", o.Values["lab:durationSeconds"]); Assert.Equal(new[] { "original", "dual-copy" }, o.Lists["lab:consumers:profiles"]);
        Assert.Equal("external", o.Values["lab:relay:mode"]);
        Assert.Equal(("kill-publisher", "s02", 60), (o.Faults[0].Type, o.Faults[0].Target, o.Faults[0].AtSeconds));
        Assert.True(o.NoTui); Assert.Equal(new[] { "a.json" }, o.Configs);
        Assert.Throws<ArgumentException>(() => LabCli.Parse(new[] { "--bogus" }));
    }
}

public class LabPlanningTests
{
    [Fact]
    public void Fan_out_is_never_labelled_as_independent_cameras()
    {
        var (label, detail) = LabRuntime.Classify(1, 0, 4, 1);
        Assert.Equal("FAN-OUT", label);
        Assert.Contains("not 4 independent cameras", detail);
        Assert.Equal("INDEPENDENT", LabRuntime.Classify(4, 0, 1, 1).Label);
        Assert.Equal("MIXED", LabRuntime.Classify(4, 0, 3, 1).Label);
        Assert.Equal("CAMERAS", LabRuntime.Classify(0, 2, 1, 1).Label);
        Assert.Equal("MIXED-SOURCES", LabRuntime.Classify(2, 2, 1, 1).Label);
    }

    [Fact]
    public void Schedules_for_each_execution_strategy()
    {
        var src = new[] { "s01", "s02" }; var prof = new[] { "a", "b" };
        var par = LabRuntime.Schedule(src, prof, 1, "parallel", Array.Empty<int>());
        Assert.Single(par); Assert.Equal(4, par[0].Count);
        var seq = LabRuntime.Schedule(src, prof, 1, "sequential", Array.Empty<int>());
        Assert.Equal(2, seq.Count); Assert.All(seq[0], p => Assert.Equal("a", p.ProfileId)); Assert.All(seq[1], p => Assert.Equal("b", p.ProfileId));
        var staged = LabRuntime.Schedule(new[] { "s1", "s2", "s3", "s4", "s5", "s6", "s7", "s8" }, new[] { "a" }, 1, "staged-load", Array.Empty<int>());
        Assert.Equal(new[] { 1, 1, 2, 4 }, staged.Select(b => b.Count));               // 1 → 2 → 4 → 8 active
        Assert.Equal(8, staged.SelectMany(b => b).Select(p => p.InstanceId).Distinct().Count());
        var fan = LabRuntime.Schedule(new[] { "s01" }, new[] { "a" }, 3, "parallel", Array.Empty<int>());
        Assert.Equal(new[] { "s01-a-1", "s01-a-2", "s01-a-3" }, fan[0].Select(p => p.InstanceId));
        Assert.Throws<ArgumentException>(() => LabRuntime.Schedule(src, prof, 1, "random", Array.Empty<int>()));
    }

    [Theory]
    [InlineData("s{index:00}", 3, "s03")] [InlineData("cam{index}", 12, "cam12")] [InlineData("path-{index:000}", 7, "path-007")]
    public void Path_templates(string t, int i, string expected) => Assert.Equal(expected, LabOrchestrator.FormatPath(t, i));
}

public class PortAndProcessTests
{
    [Fact]
    public void Busy_tcp_port_is_reported_and_its_owner_keeps_running()
    {
        var owner = new TcpListener(IPAddress.Loopback, 0); owner.Start();
        var port = ((IPEndPoint)owner.LocalEndpoint).Port;
        Assert.Contains("already in use", Ports.TcpInUse("127.0.0.1", port));
        using (var c = new TcpClient()) { c.Connect(IPAddress.Loopback, port); Assert.True(c.Connected); } // owner untouched
        owner.Stop();
        Assert.Null(Ports.TcpInUse("127.0.0.1", port));
    }

    [Fact]
    public void Relay_rejects_odd_rtp_ports()
    {
        var r = new RelayManager(new RelayCfg { RtpPort = 8001, RtcpPort = 8002, ApiPort = 0, RtspPort = TestUtil.FreePort() }, new EventBus());
        Assert.Contains(r.PortProblems(), p => p.Contains("even"));
    }

    [Fact]
    public async Task Registry_accounts_processes_by_role()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var before = ProcessRegistry.Current.Totals().First(t => t.Role == ProcessRole.Validator).Started;
        var r = await ProcessUtil.RunAsync("ffmpeg", new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(30), default, ProcessRole.Validator, "unit");
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(before + 1, ProcessRegistry.Current.Totals().First(t => t.Role == ProcessRole.Validator).Started);
        Assert.Contains(ProcessRegistry.Current.RecentlyExited, p => p.Owner == "unit" && p.Role == ProcessRole.Validator && p.ExitCode == 0);
    }

    [Fact]
    public void Misc_helpers()
    {
        Assert.Equal(@"^cam_(\d+)\.mp4$", RecordingMonitor.PatternToRegex("cam_%05d.mp4"));
        Assert.True(FFprobeRunner.IsTimestampComplaint("[null @ 0x1] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 25 >= 25"));
        Assert.False(FFprobeRunner.IsTimestampComplaint("[h264 @ 0x1] error while decoding MB 1 2"));
        Assert.EndsWith("-v2.mp4", Fixture.FileName("720p25-h264-aac", 60));
    }
}
