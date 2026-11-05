using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed class FFmpegJob
{
    public string Name { get; init; } = "ffmpeg";
    public string Executable { get; init; } = "ffmpeg";
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public string? LogFile { get; init; }
    public double StallTimeoutSeconds { get; init; } = 10;
    /// <summary>True for finite jobs (consumers, decoders): exit code 0 is success. False for live producers: any unrequested exit is a crash.</summary>
    public bool ExpectExit { get; init; }
}

public enum SessionState { Starting, Running, Stalled, Stopping, Exited, Crashed, Killed }

public sealed class FFmpegSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew(); // monotonic: immune to wall-clock adjustments
    private long _lastProgressMs = -1;
    private TimeSpan _lastCpu; private long _lastCpuSampleMs;

    public FFmpegJob Job { get; }
    public System.Diagnostics.Process Process { get; }
    public int Pid { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public SessionState State { get; internal set; } = SessionState.Starting;
    public ProgressSnapshot? LastProgress { get; private set; }
    public ProgressSnapshot? FirstFrameProgress { get; private set; }
    public double? TimeToFirstFrameSeconds { get; private set; }
    public int? ExitCode { get; internal set; }
    public bool StopRequested { get; internal set; }
    public bool KillRequested { get; internal set; }
    public int StallCount { get; internal set; }
    /// <summary>'q' was sent but FFmpeg did not exit in time and had to be killed (outputs may be unfinalized).</summary>
    public bool GracefulStopFailed { get; internal set; }
    public double MaxProgressGapSeconds { get; private set; }
    public double CpuPercent { get; private set; }
    public double WorkingSetMb { get; private set; }
    public double PeakWorkingSetMb { get; private set; }
    public int ThreadCount { get; private set; }
    public long LogLines;
    public ConcurrentDictionary<FfErrorCategory, int> ErrorCounts { get; } = new();
    public ConcurrentQueue<ClassifiedLogLine> RecentProblems { get; } = new();
    internal Task Completion { get; set; } = Task.CompletedTask;
    internal TaskCompletionSource ExitedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Exited => ExitedTcs.Task;
    private TimeSpan? _exitedAfter;
    internal void MarkExited() => _exitedAfter ??= _clock.Elapsed;
    public TimeSpan Uptime => _exitedAfter ?? _clock.Elapsed;
    public double SecondsSinceProgress => (_lastProgressMs < 0 ? _clock.ElapsedMilliseconds : _clock.ElapsedMilliseconds - _lastProgressMs) / 1000.0;

    public event Action<FFmpegSession, ClassifiedLogLine>? Problem;
    public event Action<FFmpegSession, ClassifiedLogLine>? Line;
    public event Action<FFmpegSession, string>? StateChanged;

    internal FFmpegSession(FFmpegJob job, System.Diagnostics.Process p) { Job = job; Process = p; Pid = p.Id; }

    internal void OnProgress(ProgressSnapshot s)
    {
        var now = _clock.ElapsedMilliseconds;
        if (_lastProgressMs >= 0) MaxProgressGapSeconds = Math.Max(MaxProgressGapSeconds, (now - _lastProgressMs) / 1000.0);
        // Only count as forward progress when media time or frames advance.
        if (LastProgress == null || s.Frame > LastProgress.Frame || s.OutTimeSeconds > LastProgress.OutTimeSeconds || s.End)
            _lastProgressMs = now;
        if (FirstFrameProgress == null && s.Frame > 0) { FirstFrameProgress = s; TimeToFirstFrameSeconds = _clock.Elapsed.TotalSeconds; }
        LastProgress = s;
        if (State is SessionState.Starting or SessionState.Stalled) SetState(SessionState.Running, State == SessionState.Stalled ? "progress resumed" : "first progress");
    }

    internal void OnLog(ClassifiedLogLine l)
    {
        Interlocked.Increment(ref LogLines);
        Line?.Invoke(this, l);
        if (!l.IsProblem) return;
        ErrorCounts.AddOrUpdate(l.Category, 1, (_, c) => c + 1);
        RecentProblems.Enqueue(l);
        while (RecentProblems.Count > 200) RecentProblems.TryDequeue(out _);
        Problem?.Invoke(this, l);
    }

    internal void SetState(SessionState s, string why) { State = s; StateChanged?.Invoke(this, $"{s}: {why}"); }

    internal void SampleResources()
    {
        try
        {
            Process.Refresh();
            var cpu = Process.TotalProcessorTime; var now = _clock.ElapsedMilliseconds;
            if (_lastCpuSampleMs > 0 && now > _lastCpuSampleMs)
                CpuPercent = (cpu - _lastCpu).TotalMilliseconds / (now - _lastCpuSampleMs) / Environment.ProcessorCount * 100.0;
            _lastCpu = cpu; _lastCpuSampleMs = now;
            WorkingSetMb = Process.WorkingSet64 / 1048576.0;
            PeakWorkingSetMb = Math.Max(PeakWorkingSetMb, WorkingSetMb);
            ThreadCount = Process.Threads.Count;
        }
        catch { /* process gone */ }
    }
}

public interface IFFmpegRunner
{
    Task<FFmpegSession> StartAsync(FFmpegJob job, CancellationToken cancellationToken);
    Task StopAsync(FFmpegSession session, CancellationToken cancellationToken);
}

public sealed class FFmpegRunner : IFFmpegRunner
{
    public double GracefulStopTimeoutSeconds { get; init; } = 15;

    public Task<FFmpegSession> StartAsync(FFmpegJob job, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(job.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in job.Arguments) psi.ArgumentList.Add(a); // no shell, no quoting problems
        var p = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        var s = new FFmpegSession(job, p);

        StreamWriter? log = null;
        if (job.LogFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.LogFile))!);
            log = new StreamWriter(new FileStream(job.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            log.WriteLine($"# {DateTime.UtcNow:O} START pid={p.Id} exe={job.Executable}");
            foreach (var a in job.Arguments) log.WriteLine($"#   arg: {CommandText.Mask(a)}");
        }
        var logLock = new object();

        var stdout = Task.Run(async () =>
        {
            var parser = new ProgressParser();
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                var snap = parser.Feed(line);
                if (snap != null) s.OnProgress(snap);
            }
        });
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardError.ReadLineAsync()) != null)
            {
                var now = DateTime.UtcNow;
                if (log != null) lock (logLock) log.WriteLine($"{now:HH:mm:ss.fff} {CommandText.Mask(line)}"); // original line kept
                s.OnLog(FFmpegLogParser.Classify(line, now));
            }
        });
        var watchdog = Task.Run(async () =>
        {
            while (!p.HasExited)
            {
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                s.SampleResources();
                if (s.State is SessionState.Running or SessionState.Starting && !s.StopRequested)
                {
                    var limit = s.State == SessionState.Starting ? Math.Max(job.StallTimeoutSeconds * 3, 30) : job.StallTimeoutSeconds;
                    if (s.SecondsSinceProgress > limit) { s.StallCount++; s.SetState(SessionState.Stalled, $"no forward progress for {s.SecondsSinceProgress:F1}s"); }
                }
            }
        });
        s.Completion = Task.Run(async () =>
        {
            await p.WaitForExitAsync(CancellationToken.None);
            s.MarkExited();
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            s.MarkExited();
            s.ExitCode = p.ExitCode;
            var st = s.KillRequested ? SessionState.Killed
                : s.StopRequested ? SessionState.Exited
                : job.ExpectExit && p.ExitCode == 0 ? SessionState.Exited
                : SessionState.Crashed; // unexpected exit of a live producer is always a failure
            s.SetState(st, $"exit code {p.ExitCode}");
            if (log != null) lock (logLock) { log.WriteLine($"# {DateTime.UtcNow:O} EXIT code={p.ExitCode} state={st}"); log.Dispose(); }
            s.ExitedTcs.TrySetResult();
        });
        return Task.FromResult(s);
    }

    /// <summary>Graceful stop: send 'q' so muxers write trailers (MP4 moov); kill the tree on timeout.</summary>
    public async Task StopAsync(FFmpegSession s, CancellationToken ct)
    {
        if (s.Process.HasExited) { await s.Exited; return; }
        s.StopRequested = true;
        s.SetState(SessionState.Stopping, "graceful stop requested ('q')");
        try { await s.Process.StandardInput.WriteAsync("q"); await s.Process.StandardInput.FlushAsync(); } catch { }
        try { await s.Exited.WaitAsync(TimeSpan.FromSeconds(GracefulStopTimeoutSeconds), ct); }
        catch (TimeoutException)
        {
            s.GracefulStopFailed = true;
            Kill(s);
            try { await s.Exited.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); } catch (TimeoutException) { }
        }
    }

    /// <summary>Fault injection / last resort: terminate without muxer finalization.</summary>
    public static void Kill(FFmpegSession s)
    {
        s.KillRequested = true;
        try { s.Process.Kill(entireProcessTree: true); } catch { }
    }
}
