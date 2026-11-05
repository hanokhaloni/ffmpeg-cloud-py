namespace FFmpegReliabilityBench.Core.Config;

/// <summary>Built-in multi-stream lab. Defaults are the "local-4-streams" preset: a clean launch works without editing JSON.</summary>
public sealed class LabCfg
{
    public string Preset { get; set; } = "local-4-streams";
    public int DurationSeconds { get; set; } = 600;
    public bool FailFast { get; set; }
    /// <summary>Application-owned run directories are created under this root (one per session).</summary>
    public string RunRoot { get; set; } = "streambench-runs";
    public RelayCfg Relay { get; set; } = new();
    public PublishersCfg Publishers { get; set; } = new();
    public ConsumersCfg Consumers { get; set; } = new();
    public LabHttpCfg Http { get; set; } = new();
    public ShutdownCfg Shutdown { get; set; } = new();
    public LabValidationCfg Validation { get; set; } = new();
    /// <summary>Explicit sources (cameras and/or synthetic). Empty = Publishers.Count synthetic sources.</summary>
    public List<SourceDef> Sources { get; set; } = new();
    /// <summary>Additional or overriding command templates (by id).</summary>
    public List<CommandTemplate> Templates { get; set; } = new();
    public List<FaultDef> Faults { get; set; } = new();
}

public sealed class RelayCfg
{
    /// <summary>managed (StreamBench starts/stops MediaMTX) | external (use RelayCfg.Url, never stopped) | none (consumers read sources directly)</summary>
    public string Mode { get; set; } = "managed";
    public string Executable { get; set; } = OperatingSystem.IsWindows() ? @".\tools\mediamtx\mediamtx.exe" : "./tools/mediamtx/mediamtx";
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int RtspPort { get; set; } = 8554;
    public int RtpPort { get; set; } = 8000;
    public int RtcpPort { get; set; } = 8001;
    /// <summary>MediaMTX control API (reader counts, bytes). 0 = disabled.</summary>
    public int ApiPort { get; set; } = 9997;
    /// <summary>External relay base URL, e.g. rtsp://127.0.0.1:8554</summary>
    public string Url { get; set; } = "";
    public int ReadyTimeoutSeconds { get; set; } = 15;
}

public sealed class PublishersCfg
{
    public int Count { get; set; } = 4;
    /// <summary>loop-file (stream-copy a prepared fixture: cheap) | live-synthetic (encode a test pattern live: measures generator load)</summary>
    public string Mode { get; set; } = "loop-file";
    public string FixturePreset { get; set; } = "720p25-h264-aac";
    public string FixtureDirectory { get; set; } = "streambench-fixtures";
    public int FixtureDurationSeconds { get; set; } = 60;
    /// <summary>{index} with optional format, e.g. "s{index:00}" → s01, s02 …</summary>
    public string PathTemplate { get; set; } = "s{index:00}";
    public int StartupTimeoutSeconds { get; set; } = 30;
    public string Transport { get; set; } = "tcp";
}

public sealed class ConsumersCfg
{
    public int InstancesPerSource { get; set; } = 1;
    /// <summary>Command template ids. Empty = ["dual-copy"].</summary>
    public List<string> Profiles { get; set; } = new();
    /// <summary>parallel | sequential | staged-load. Empty = parallel for one profile, sequential when comparing profiles.</summary>
    public string Execution { get; set; } = "";
    public int StaggerMilliseconds { get; set; } = 500;
    /// <summary>Sequential: seconds per profile group (0 = duration / groups).</summary>
    public int GroupDurationSeconds { get; set; }
    /// <summary>Staged load: active instance counts per stage (empty = 1, 2, 4 … up to all).</summary>
    public List<int> Stages { get; set; } = new();
    public int StageDurationSeconds { get; set; }
    /// <summary>Insert the version-appropriate RTSP socket timeout (-stimeout / -timeout) when a template has none.</summary>
    public bool InjectRtspTimeout { get; set; } = true;
}

public sealed class LabHttpCfg
{
    public bool Enabled { get; set; } = true;
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8088;
}

public sealed class ShutdownCfg
{
    public int GracefulTimeoutSeconds { get; set; } = 15;
    public bool PreserveOutputs { get; set; } = true;
}

public sealed class LabValidationCfg
{
    /// <summary>Bounded probe/decode workers shared by all streams (0 = max(2, cores/2)).</summary>
    public int MaxDecodeWorkers { get; set; }
    /// <summary>Play every Nth completed recording of each stream over HTTP (0 = off).</summary>
    public int RecordingHttpSampleEvery { get; set; } = 3;
    /// <summary>Real-time live HLS player over HTTP for one selected stream.</summary>
    public bool LivePlayer { get; set; } = true;
    /// <summary>Seconds after launch within which every consumer must produce its first valid outputs.</summary>
    public int FirstOutputTimeoutSeconds { get; set; } = 40;
}

public sealed class SourceDef
{
    public string Id { get; set; } = "";
    /// <summary>synthetic | camera</summary>
    public string Kind { get; set; } = "synthetic";
    /// <summary>Camera RTSP URL (credentials come from Credential Manager / environment, never config).</summary>
    public string Url { get; set; } = "";
    /// <summary>Synthetic only: loop-file | live-synthetic (default: Publishers.Mode)</summary>
    public string Mode { get; set; } = "";
}

public sealed class FaultDef
{
    /// <summary>kill-publisher | kill-consumer</summary>
    public string Type { get; set; } = "";
    public string Target { get; set; } = "";
    public int AtSeconds { get; set; }
}

/// <summary>A benchmark command as an argument array with placeholders (never a shell string).</summary>
public sealed class CommandTemplate
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExecutableReference { get; set; } = "ffmpeg-current";
    public List<string> Arguments { get; set; } = new();
}

public sealed class UiCfg
{
    /// <summary>full-screen | headless</summary>
    public string Mode { get; set; } = "full-screen";
    public double RefreshHz { get; set; } = 4;
    public int HistorySeconds { get; set; } = 300;
    public string Theme { get; set; } = "dark";
    public bool ShowCommand { get; set; } = true;
    /// <summary>auto | on | off</summary>
    public string AsciiFallback { get; set; } = "auto";
    public bool Color { get; set; } = true;
}
