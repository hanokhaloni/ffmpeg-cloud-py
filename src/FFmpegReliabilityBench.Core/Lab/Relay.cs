using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Lab;

public sealed record RelayPathStats(string Name, bool Ready, int Readers, long BytesIn, long BytesOut);

/// <summary>
/// RTSP relay. managed: StreamBench starts/stops its own MediaMTX (minimal YAML + MTX_* environment overrides, so the same
/// config works across MediaMTX versions). external: an existing relay is used and never stopped. none: sources are read directly.
/// </summary>
public sealed class RelayManager
{
    private readonly RelayCfg _cfg; private readonly EventBus _bus;
    private Process? _p; private StreamWriter? _log; private readonly StringBuilder _tail = new();
    public string Mode => _cfg.Mode.ToLowerInvariant();
    public string BaseUrl { get; private set; } = "";
    public string? Version { get; private set; }
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "not started";
    public string? Error { get; private set; }
    public double? ReadyAfterSeconds { get; private set; }
    public string? ResolvedExecutable { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? LogPath { get; private set; }
    public ManagedProcess? Managed { get; private set; }
    public int? Pid => Managed?.Pid;
    public bool Exited => _p?.HasExited ?? false;

    public RelayManager(RelayCfg cfg, EventBus bus) { _cfg = cfg; _bus = bus; }

    public string PathUrl(string path) => Mode == "none" ? "" : $"{BaseUrl}/{path}";

    /// <summary>Configured path relative to the working directory, then to the application folder and its parent
    /// (dist\app\StreamBench.exe + dist\tools\mediamtx), then PATH.</summary>
    public static string? ResolveExecutable(string configured)
    {
        if (Path.IsPathRooted(configured)) return File.Exists(configured) ? configured : null;
        var candidates = new[]
        {
            Path.GetFullPath(configured),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", configured)),
        };
        return candidates.FirstOrDefault(File.Exists) ?? Capabilities.ResolvePath("mediamtx");
    }

    /// <summary>Ports a managed relay needs (checked before anything starts).</summary>
    public IEnumerable<string> PortProblems()
    {
        if (Mode != "managed") yield break;
        if (_cfg.RtpPort % 2 != 0 || _cfg.RtcpPort != _cfg.RtpPort + 1)
            yield return $"relay RTP/RTCP ports must be an even port and the next one (got {_cfg.RtpPort}/{_cfg.RtcpPort}); MediaMTX requires this";
        if (Ports.TcpInUse(_cfg.ListenAddress, _cfg.RtspPort) is string a) yield return a + " - needed for the RTSP relay (use --rtsp-port, or --relay external --relay-url …)";
        if (Ports.UdpInUse(_cfg.ListenAddress, _cfg.RtpPort) is string b) yield return b + " - needed for relay RTP";
        if (Ports.UdpInUse(_cfg.ListenAddress, _cfg.RtcpPort) is string c) yield return c + " - needed for relay RTCP";
        if (_cfg.ApiPort > 0 && Ports.TcpInUse(_cfg.ListenAddress, _cfg.ApiPort) is string d) yield return d + " - needed for the relay API (set lab.relay.apiPort 0 to disable)";
    }

    public async Task<bool> StartAsync(string logDir, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        switch (Mode)
        {
            case "none":
                Ready = true; Status = "not used (sources are read directly)"; return true;
            case "external":
                if (!Uri.TryCreate(_cfg.Url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("rtsp"))
                    return Fail($"external relay URL invalid: '{_cfg.Url}' (expected rtsp://host:port)");
                BaseUrl = _cfg.Url.TrimEnd('/');
                if (!Ports.CanConnect(u.Host, u.Port > 0 ? u.Port : 554, 3000)) return Fail($"external relay {BaseUrl} is not reachable");
                Ready = true; ReadyAfterSeconds = sw.Elapsed.TotalSeconds; Status = $"external relay {BaseUrl} reachable (not managed, never stopped)";
                return true;
            case "managed":
                break;
            default:
                return Fail($"unknown relay mode '{_cfg.Mode}' (managed | external | none)");
        }

        ResolvedExecutable = ResolveExecutable(_cfg.Executable);
        if (ResolvedExecutable == null)
            return Fail($"MediaMTX executable not found: {Path.GetFullPath(_cfg.Executable)} (also searched PATH for 'mediamtx'). " +
                        "Download it from https://github.com/bluenviron/mediamtx/releases and place it there, pass --relay-exe <path>, or use --relay external --relay-url rtsp://host:8554. Nothing was downloaded or installed.");
        Directory.CreateDirectory(logDir);
        ConfigPath = Path.Combine(logDir, "mediamtx.yml");
        LogPath = Path.Combine(logDir, "relay-mediamtx.log");
        // Minimal YAML (unknown YAML keys are fatal in MediaMTX); everything else via MTX_* environment variables,
        // which MediaMTX versions that do not know a key simply ignore. The regex path accepts any published path.
        await File.WriteAllTextAsync(ConfigPath, "logLevel: info\nlogDestinations: [stdout]\npaths:\n  \"~^.*$\":\n", ct);
        var psi = new ProcessStartInfo(ResolvedExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = logDir };
        psi.ArgumentList.Add(ConfigPath);
        var a = _cfg.ListenAddress;
        var env = new Dictionary<string, string>
        {
            ["MTX_RTSP"] = "yes", ["MTX_RTSPADDRESS"] = $"{a}:{_cfg.RtspPort}", ["MTX_RTPADDRESS"] = $"{a}:{_cfg.RtpPort}", ["MTX_RTCPADDRESS"] = $"{a}:{_cfg.RtcpPort}",
            ["MTX_RTSPTRANSPORTS"] = "tcp,udp", ["MTX_RTSPENCRYPTION"] = "no",
            ["MTX_RTMP"] = "no", ["MTX_HLS"] = "no", ["MTX_WEBRTC"] = "no", ["MTX_SRT"] = "no", ["MTX_MOQ"] = "no",
            ["MTX_RTMPDISABLE"] = "yes", ["MTX_HLSDISABLE"] = "yes", ["MTX_WEBRTCDISABLE"] = "yes",
            ["MTX_METRICS"] = "no", ["MTX_PPROF"] = "no", ["MTX_PLAYBACK"] = "no",
            ["MTX_API"] = _cfg.ApiPort > 0 ? "yes" : "no", ["MTX_APIADDRESS"] = $"{a}:{(_cfg.ApiPort > 0 ? _cfg.ApiPort : 9997)}",
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;
        _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        _log.WriteLine($"# {DateTime.UtcNow:O} {ResolvedExecutable} {ConfigPath}");
        foreach (var (k, v) in env) _log.WriteLine($"#   {k}={v}");
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { _p.Start(); }
        catch (Exception ex) { return Fail($"cannot start MediaMTX {ResolvedExecutable}: {ex.Message}"); }
        Managed = ProcessRegistry.Current.Register(_p, ProcessRole.Relay, "mediamtx", "relay", CommandText.Human(ResolvedExecutable, new[] { ConfigPath }));
        _ = Pump(_p.StandardOutput); _ = Pump(_p.StandardError);
        _p.Exited += (_, _) =>
        {
            ProcessRegistry.Current.MarkExited(Managed!, SafeExit());
            if (Ready && !_stopping) _bus.Publish(EventLevel.Fail, "Relay", "relay", $"managed MediaMTX exited unexpectedly (code {SafeExit()})");
        };
        BaseUrl = $"rtsp://{a}:{_cfg.RtspPort}";
        Status = "starting";
        while (sw.Elapsed.TotalSeconds < _cfg.ReadyTimeoutSeconds && !ct.IsCancellationRequested)
        {
            if (_p.HasExited) return Fail($"MediaMTX exited during startup (code {SafeExit()}): {LastLines()}");
            if (Ports.CanConnect(a, _cfg.RtspPort, 500))
            {
                Ready = true; ReadyAfterSeconds = sw.Elapsed.TotalSeconds;
                Version = Regex.Match(LastLines(50), @"MediaMTX (v[\w.\-]+)").Groups[1].Value is { Length: > 0 } v ? v : null;
                Status = $"managed MediaMTX {Version} pid {Managed.Pid} on {BaseUrl}";
                return true;
            }
            await Task.Delay(200, ct);
        }
        return Fail($"MediaMTX did not accept RTSP connections within {_cfg.ReadyTimeoutSeconds}s: {LastLines()}");
    }

    private volatile bool _stopping;
    /// <summary>Stops only the relay this application started. External relays are never touched.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        if (Mode != "managed" || _p == null) return;
        try { if (!_p.HasExited) { _p.Kill(entireProcessTree: true); await _p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } } catch { }
        Status = "stopped";
        try { _log?.Flush(); } catch { }
    }

    /// <summary>Per-path readers and bytes from the managed relay's API (null if unavailable).</summary>
    public async Task<IReadOnlyList<RelayPathStats>?> PathStatsAsync(CancellationToken ct)
    {
        if (Mode != "managed" || _cfg.ApiPort <= 0 || !Ready) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var json = await http.GetStringAsync($"http://{_cfg.ListenAddress}:{_cfg.ApiPort}/v3/paths/list", ct);
            using var doc = JsonDocument.Parse(json);
            var res = new List<RelayPathStats>();
            foreach (var it in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                long L(params string[] names) { foreach (var n in names) if (it.TryGetProperty(n, out var v) && v.TryGetInt64(out var x)) return x; return 0; }
                res.Add(new RelayPathStats(it.GetProperty("name").GetString() ?? "", it.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True,
                    it.TryGetProperty("readers", out var rd) && rd.ValueKind == JsonValueKind.Array ? rd.GetArrayLength() : 0,
                    L("inboundBytes", "bytesReceived"), L("outboundBytes", "bytesSent")));
            }
            return res;
        }
        catch { return null; }
    }

    private async Task Pump(StreamReader r)
    {
        string? line;
        try
        {
            while ((line = await r.ReadLineAsync()) != null)
            {
                lock (_tail) { _tail.AppendLine(line); if (_tail.Length > 20000) _tail.Remove(0, 10000); }
                try { _log?.WriteLine(line); } catch { }
                if (line.Contains(" ERR ")) _bus.PublishThrottled("relay-err", 5, EventLevel.Warn, "Relay", "relay", line);
            }
        }
        catch { }
    }

    private string LastLines(int n = 5) { lock (_tail) return string.Join(" | ", _tail.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(n).Select(l => l.Trim())); }
    private int? SafeExit() { try { return _p?.ExitCode; } catch { return null; } }
    private bool Fail(string m) { Error = m; Status = "FAILED: " + m; Ready = false; return false; }
}
