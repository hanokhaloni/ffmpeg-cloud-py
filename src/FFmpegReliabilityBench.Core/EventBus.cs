using System.Collections.Concurrent;
using System.Diagnostics;

namespace FFmpegReliabilityBench.Core;

public enum EventLevel { Info, Warn, Fail }

/// <summary>T = seconds since session start on a monotonic clock (Stopwatch), TsUtc = wall clock (UTC) for correlation with external logs.</summary>
public sealed record RuntimeEvent(DateTime TsUtc, double T, EventLevel Level, string Category, string Stream, string Message);

public sealed class EventBus
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<RuntimeEvent> _all = new();
    private readonly ConcurrentDictionary<string, double> _rate = new();
    public int MaxEvents { get; init; } = 200_000;
    public event Action<RuntimeEvent>? Published;
    public double Now => _clock.Elapsed.TotalSeconds;

    public void Publish(EventLevel level, string category, string stream, string message)
    {
        var e = new RuntimeEvent(DateTime.UtcNow, _clock.Elapsed.TotalSeconds, level, category, stream, message);
        _all.Enqueue(e);
        while (_all.Count > MaxEvents) _all.TryDequeue(out _);
        Published?.Invoke(e);
    }

    /// <summary>Publish at most once per <paramref name="seconds"/> for a given key (log-flood protection).</summary>
    public void PublishThrottled(string key, double seconds, EventLevel level, string category, string stream, string message)
    {
        var now = Now;
        if (_rate.TryGetValue(key, out var last) && now - last < seconds) return;
        _rate[key] = now;
        Publish(level, category, stream, message);
    }

    public IReadOnlyList<RuntimeEvent> Snapshot() => _all.ToArray();
    public IReadOnlyList<RuntimeEvent> Recent(int n) { var a = _all.ToArray(); return a.Skip(Math.Max(0, a.Length - n)).ToArray(); }
}
