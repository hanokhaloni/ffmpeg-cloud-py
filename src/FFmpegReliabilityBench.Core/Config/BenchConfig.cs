using Microsoft.Extensions.Configuration;

namespace FFmpegReliabilityBench.Core.Config;

public sealed class BenchConfig
{
    public FfmpegCfg Ffmpeg { get; set; } = new();
    public SourceCfg Source { get; set; } = new();
    public StorageCfg Storage { get; set; } = new();
    public HlsCfg Hls { get; set; } = new();
    public RecordingCfg Recording { get; set; } = new();
    public TimeCfg Time { get; set; } = new();
    public BenchmarkCfg Benchmark { get; set; } = new();
    public string ReportDirectory { get; set; } = "reports";

    /// <summary>Later files override earlier ones (e.g. config/site.json + profiles/fragmented-mp4.json).</summary>
    public static BenchConfig Load(IEnumerable<string> paths)
    {
        var b = new ConfigurationBuilder();
        foreach (var path in paths) b.AddJsonFile(Path.GetFullPath(path), optional: false);
        var cfg = new BenchConfig();
        b.Build().Bind(cfg);
        return cfg;
    }
}

public sealed class FfmpegCfg
{
    public string Executable { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public string LogLevel { get; set; } = "info";
    public double ProcessStallTimeoutSeconds { get; set; } = 10;
    public double StartupTimeoutSeconds { get; set; } = 30;
}

public sealed class SourceCfg
{
    /// <summary>"synthetic" (lavfi test pattern, no camera) or an rtsp:// URL.</summary>
    public string Url { get; set; } = "synthetic";
    public string Transport { get; set; } = "tcp";
    public int ExpectedFps { get; set; } = 25;
    public bool UseReadrate { get; set; } = false; // -re; discouraged for live RTSP
    public bool IsSynthetic => string.IsNullOrWhiteSpace(Url) || Url.Equals("synthetic", StringComparison.OrdinalIgnoreCase);
}

public sealed class StorageCfg
{
    public string HlsDirectory { get; set; } = "out/hls";
    public string RecordingDirectory { get; set; } = "out/rec";
    public double MinimumFreeSpaceGb { get; set; } = 1;
    public int SmallFileCount { get; set; } = 50;
    public int SegmentFileCount { get; set; } = 10;
    public int SegmentFileSizeMb { get; set; } = 4;
    public double LatencyWarnMs { get; set; } = 250;
    public double LatencyFailMs { get; set; } = 1000;
}

public sealed class HlsCfg
{
    public bool Enabled { get; set; } = true;
    public double TargetDurationSeconds { get; set; } = 2;
    public int PlaylistSize { get; set; } = 3;
    public double DurationToleranceSeconds { get; set; } = 0.5;
    public double MaxSegmentAgeSeconds { get; set; } = 8;
    public long MinSegmentSizeBytes { get; set; } = 1024;
    public bool DecodeSegments { get; set; } = true;
    public string Flags { get; set; } = "delete_segments+independent_segments";
    public int DeleteThreshold { get; set; } = 6;
    public string PlaylistName { get; set; } = "stream.m3u8";
    public bool PlaybackTest { get; set; } = true;
    /// <summary>Optional: the real HTTP URL of the playlist (IIS / reverse proxy) for the HTTP consumption test.</summary>
    public string HttpUrl { get; set; } = "";
}

public sealed class RecordingCfg
{
    public bool Enabled { get; set; } = true;
    public double SegmentDurationSeconds { get; set; } = 5;
    /// <summary>mp4 | faststart | fmp4</summary>
    public string Mode { get; set; } = "faststart";
    public bool FullDecode { get; set; } = true;
    public double StableSeconds { get; set; } = 1.5;
    public double DurationToleranceSeconds { get; set; } = 1.5;
    public int SeekProbes { get; set; } = 3;
}

public sealed class TimeCfg
{
    public bool Enabled { get; set; } = false;
    public string NtpServer { get; set; } = "pool.ntp.org";
    public int SampleCount { get; set; } = 5;
    public double MaxClockDifferenceMs { get; set; } = 500;
    public string OnvifUrl { get; set; } = ""; // e.g. http://192.168.1.10/onvif/device_service
}

public sealed class BenchmarkCfg
{
    public int DurationSeconds { get; set; } = 60;
    /// <summary>baseline | copy | fmp4</summary>
    public string Profile { get; set; } = "baseline";
    public int ConcurrentStreams { get; set; } = 1;
    /// <summary>Fault injection: forcibly kill FFmpeg after N seconds (0 = off).</summary>
    public int InjectKillAtSeconds { get; set; } = 0;
    public bool ValidateOnlyCompletedHlsSegments { get; set; } = true;
}
