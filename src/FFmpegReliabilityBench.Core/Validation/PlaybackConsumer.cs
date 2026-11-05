using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Validation;

/// <summary>Independent consumer: a separate FFmpeg process that plays the live HLS output in real time (-re) and decodes it.</summary>
public sealed class PlaybackConsumer
{
    private readonly EventBus _bus; private readonly string _stream; private readonly double _stallThreshold;
    private CancellationTokenSource? _cts; private Task? _watch; private bool _inStall;

    public string Name { get; }
    public string Input { get; }
    public FFmpegSession? Session { get; private set; }
    public int StallEpisodes { get; private set; }
    public double MaxInterruptionSeconds { get; private set; }
    public int DecodeErrors, SkippedSegments, FailedSegmentOpens, Warnings, TimestampWarnings;
    public bool UnexpectedExit { get; private set; }
    public double? TimeToFirstFrameSeconds => Session?.TimeToFirstFrameSeconds;
    public long Frames => Session?.LastProgress?.Frame ?? 0;
    /// <summary>Decoded media seconds (works for both decode and copy).</summary>
    public bool Produced => (Session?.LastProgress?.Frame ?? 0) > 0 || (Session?.LastProgress?.OutTimeSeconds ?? 0) > 0;
    public double MediaSeconds => Session?.LastProgress?.OutTimeSeconds ?? 0;
    public List<string> Evidence { get; } = new();

    public PlaybackConsumer(string name, string stream, string input, double stallThreshold, EventBus bus)
    { Name = name; _stream = stream; Input = input; _stallThreshold = stallThreshold; _bus = bus; }

    public async Task StartAsync(FFmpegRunner runner, string ffmpeg, bool statsPeriod, string logFile, CancellationToken ct, Lab.ProcessRole role = Lab.ProcessRole.Player)
    {
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "level+warning", "-progress", "pipe:1" };
        if (statsPeriod) args.AddRange(new[] { "-stats_period", "1" });
        args.AddRange(new[] { "-re", "-i", Input, "-map", "0:v:0?", "-map", "0:a:0?", "-f", "null", "-" });
        Session = await runner.StartAsync(new FFmpegJob { Name = Name, Executable = ffmpeg, Arguments = args, LogFile = logFile, StallTimeoutSeconds = _stallThreshold, Role = role, Owner = _stream }, ct);
        Session.Line += (_, l) =>
        {
            if (FFprobeRunner.IsTimestampComplaint(l.Raw)) { Interlocked.Increment(ref TimestampWarnings); return; }
            if (l.Level is "error" or "fatal" or "panic") { Interlocked.Increment(ref DecodeErrors); Add(l.Raw); }
            else if (l.Level == "warning")
            {
                Interlocked.Increment(ref Warnings);
                if (l.Raw.Contains("skipping", StringComparison.OrdinalIgnoreCase) || l.Raw.Contains("expired", StringComparison.OrdinalIgnoreCase)) { Interlocked.Increment(ref SkippedSegments); Add(l.Raw); }
                else if (l.Raw.Contains("Failed to open segment", StringComparison.OrdinalIgnoreCase)) { Interlocked.Increment(ref FailedSegmentOpens); Add(l.Raw); }
            }
        };
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var s = Session;
        _watch = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested && !s.Process.HasExited)
            {
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
                var gap = s.SecondsSinceProgress;
                if (s.FirstFrameProgress == null) continue;
                MaxInterruptionSeconds = Math.Max(MaxInterruptionSeconds, gap);
                if (gap > _stallThreshold && !_inStall) { _inStall = true; StallEpisodes++; _bus.Publish(EventLevel.Warn, "Playback", _stream, $"{Name}: playback stalled ({gap:F1}s without decoded media)"); }
                else if (gap < 1.5 && _inStall) _inStall = false;
            }
            if (s.Process.HasExited && !s.StopRequested)
            { UnexpectedExit = true; _bus.Publish(EventLevel.Fail, "Playback", _stream, $"{Name}: consumer exited unexpectedly (code {s.Process.ExitCode})"); }
        });
    }

    private void Add(string l) { lock (Evidence) if (Evidence.Count < 30) Evidence.Add(l); }

    public async Task StopAsync(FFmpegRunner runner)
    {
        if (Session == null) return;
        Session.StopRequested = true;
        _cts?.Cancel();
        await runner.StopAsync(Session, CancellationToken.None);
        if (_watch != null) try { await _watch; } catch { }
    }
}
