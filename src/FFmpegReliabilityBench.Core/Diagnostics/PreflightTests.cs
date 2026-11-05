using System.Runtime.InteropServices;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Diagnostics;

/// <summary>Module A - system prerequisites (SYS-xxx).</summary>
public static class PreflightTests
{
    private const string Cat = "System";

    public static async Task<List<TestResult>> RunAsync(BenchConfig cfg, Capabilities caps, CancellationToken ct)
    {
        var r = new List<TestResult>();
        r.Add(TestResult.Make("SYS-001", Cat, "OS version", OperatingSystem.IsWindows() ? TestStatus.Pass : TestStatus.Warn,
            RuntimeInformation.OSDescription + (OperatingSystem.IsWindows() ? "" : " (target platform is Windows Server; Windows-only checks will be UNKNOWN)")));
        r.Add(TestResult.Make("SYS-002", Cat, "Architecture", RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64 ? TestStatus.Pass : TestStatus.Warn,
            $"OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}"));
        var cpu0 = SystemInfo.SystemCpuPercent(); await Task.Delay(500, ct); var cpu = SystemInfo.SystemCpuPercent() ?? 0;
        var cpuStatus = Environment.ProcessorCount < 2 ? TestStatus.Warn : cpu > 85 ? TestStatus.Warn : TestStatus.Pass;
        r.Add(TestResult.Make("SYS-003", Cat, "CPU availability", cpuStatus, $"{Environment.ProcessorCount} logical processors, current load {cpu:F0}%",
            new() { ["logicalProcessors"] = Environment.ProcessorCount, ["loadPercent"] = cpu }));
        var (tot, avail) = SystemInfo.Memory();
        r.Add(TestResult.Make("SYS-004", Cat, "RAM", avail < 1024.0 * 1024 * 1024 ? TestStatus.Warn : TestStatus.Pass,
            $"{avail / 1073741824.0:F1} GB available of {tot / 1073741824.0:F1} GB", new() { ["availableGb"] = avail / 1073741824.0, ["totalGb"] = tot / 1073741824.0 }));

        foreach (var (label, dir) in new[] { ("HLS", cfg.Storage.HlsDirectory), ("Recording", cfg.Storage.RecordingDirectory) })
        {
            var ds = SystemInfo.DiskSpace(dir);
            if (ds == null) r.Add(TestResult.Make("SYS-005", Cat, $"Disk space ({label})", TestStatus.Unknown, $"cannot query free space for {dir}"));
            else
            {
                var gb = ds.Value.Free / 1073741824.0;
                r.Add(TestResult.Make("SYS-005", Cat, $"Disk space ({label})", gb < cfg.Storage.MinimumFreeSpaceGb ? TestStatus.Fail : gb < cfg.Storage.MinimumFreeSpaceGb * 3 ? TestStatus.Warn : TestStatus.Pass,
                    $"{gb:F1} GB free on volume of {dir} (minimum {cfg.Storage.MinimumFreeSpaceGb} GB)", new() { ["freeGb"] = gb }));
            }
            var kind = SystemInfo.PathKind(dir);
            r.Add(TestResult.Make("SYS-012", Cat, $"Path type ({label})", kind == "MappedNetworkDrive" && !Environment.UserInteractive ? TestStatus.Fail : kind == "MappedNetworkDrive" ? TestStatus.Warn : TestStatus.Pass,
                $"{dir} is {kind}" + (kind == "MappedNetworkDrive" ? " - mapped drives belong to an interactive logon and are usually NOT visible to services; prefer UNC paths" : "")));
        }
        r.Add(TestResult.Make("SYS-006", Cat, "Disk health", TestStatus.Unknown, OperatingSystem.IsWindows() ? "Use Get-PhysicalDisk / Get-StorageReliabilityCounter for SMART data (not queried)" : "not available on this OS"));

        r.Add(TestResult.Make("SYS-007", Cat, "FFmpeg executable", caps.VersionLine.Contains("ffmpeg version") ? TestStatus.Pass : TestStatus.Fail,
            caps.VersionLine.Length > 0 ? $"{caps.VersionLine} | {caps.ExecutablePath} | sha256 {caps.Sha256[..Math.Min(16, caps.Sha256.Length)]}…" : $"cannot execute '{cfg.Ffmpeg.Executable}'",
            new() { ["major"] = caps.Major, ["minor"] = caps.Minor }));
        r.Add(TestResult.Make("SYS-008", Cat, "FFprobe executable", caps.FfprobeVersionLine.Contains("ffprobe version") ? TestStatus.Pass : TestStatus.Fail,
            caps.FfprobeVersionLine.Length > 0 ? caps.FfprobeVersionLine : $"cannot execute '{cfg.Ffmpeg.Ffprobe}'"));
        if (caps.VersionLine.Length > 0 && caps.FfprobeVersionLine.Length > 0)
        {
            string V(string s) => s.Split(' ').ElementAtOrDefault(2) ?? "";
            if (V(caps.VersionLine) != V(caps.FfprobeVersionLine))
                r.Add(TestResult.Make("SYS-008b", Cat, "FFmpeg/FFprobe version match", TestStatus.Warn, $"ffmpeg {V(caps.VersionLine)} vs ffprobe {V(caps.FfprobeVersionLine)}"));
        }

        var needEnc = cfg.Benchmark.Profile == "copy" ? new[] { "aac" } : new[] { "libx264", "aac" };
        var missingEnc = needEnc.Where(e => !caps.Encoders.Contains(e)).ToList();
        r.Add(TestResult.Make("SYS-009", Cat, "FFmpeg encoders", missingEnc.Count == 0 ? TestStatus.Pass : TestStatus.Fail,
            missingEnc.Count == 0 ? $"available: {string.Join(", ", needEnc)} (hw encoders compiled into this build, not probed: {string.Join(", ", caps.Encoders.Where(e => e.Contains("nvenc") || e.Contains("qsv") || e.Contains("amf")).DefaultIfEmpty("none"))})" : "missing: " + string.Join(", ", missingEnc)));
        var needMux = new List<string> { "hls", "segment", "mp4" }; if (cfg.Benchmark.Profile == "tee") needMux.Add("tee");
        var missingMux = needMux.Where(m => !caps.Muxers.Contains(m)).ToList();
        r.Add(TestResult.Make("SYS-010", Cat, "FFmpeg muxers", missingMux.Count == 0 ? TestStatus.Pass : TestStatus.Fail,
            missingMux.Count == 0 ? $"available: {string.Join(", ", needMux)}; hls temp_file={(caps.HlsTempFile ? "yes" : "no")}, fmp4={(caps.HlsFmp4 ? "yes" : "no")}, -stats_period={(caps.StatsPeriod ? "yes" : "no")}" : "missing: " + string.Join(", ", missingMux)));

        var others = SystemInfo.OtherFfmpegProcesses();
        r.Add(TestResult.Make("SYS-013", Cat, "Concurrent FFmpeg processes", others > 0 ? TestStatus.Warn : others < 0 ? TestStatus.Unknown : TestStatus.Pass,
            others > 0 ? $"{others} other ffmpeg process(es) running - possible resource contention" : others == 0 ? "none" : "cannot enumerate", new() { ["count"] = Math.Max(0, others) }));
        r.Add(await DefenderAsync(cfg, ct));
        r.Add(TestResult.Make("SYS-015", Cat, "Identity", TestStatus.Pass, $"running as {SystemInfo.Identity()} - FFmpeg and all filesystem tests run under this identity"));
        return r;
    }

    /// <summary>SYS-014: Defender status + exclusions. Diagnose only; never disables AV or adds exclusions.</summary>
    private static async Task<TestResult> DefenderAsync(BenchConfig cfg, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return TestResult.Make("SYS-014", Cat, "Antivirus", TestStatus.Unknown, "Windows Defender check only available on Windows");
        var ps = await ProcessUtil.RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$s=Get-MpComputerStatus; $p=Get-MpPreference; \"RTP=$($s.RealTimeProtectionEnabled)\"; \"EXCL=\" + (($p.ExclusionPath) -join '|'); \"PROCEXCL=\" + (($p.ExclusionProcess) -join '|')" }, TimeSpan.FromSeconds(30), ct);
        if (ps.ExitCode != 0) return TestResult.Make("SYS-014", Cat, "Antivirus", TestStatus.Unknown, "Get-MpComputerStatus failed (Defender absent, 3rd-party AV, or insufficient rights): " + ps.StdErr.Trim());
        var lines = ps.StdOut.Split('\n').Select(l => l.Trim()).ToList();
        var rtp = lines.FirstOrDefault(l => l.StartsWith("RTP="))?[4..] ?? "?";
        var excl = (lines.FirstOrDefault(l => l.StartsWith("EXCL="))?[5..] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        var procExcl = (lines.FirstOrDefault(l => l.StartsWith("PROCEXCL="))?[9..] ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        bool Covered(string d) => excl.Any(e => Path.GetFullPath(d).StartsWith(e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var uncovered = new[] { cfg.Storage.HlsDirectory, cfg.Storage.RecordingDirectory }.Where(d => !Covered(d)).ToList();
        var status = rtp.Equals("True", StringComparison.OrdinalIgnoreCase) && uncovered.Count > 0 ? TestStatus.Warn : TestStatus.Pass;
        return TestResult.Make("SYS-014", Cat, "Antivirus", status,
            $"Defender real-time protection={rtp}; output dirs without path exclusion: {(uncovered.Count == 0 ? "none" : string.Join(", ", uncovered))}; process exclusions: {(procExcl.Length == 0 ? "none" : string.Join(", ", procExcl))}. Recommendation only - investigate scanning of media output, do not disable AV blindly.");
    }
}
