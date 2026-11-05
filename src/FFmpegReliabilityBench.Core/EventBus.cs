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
    private readonly RuntimeEvent[] _ring = new RuntimeEvent[512]; private long _ringPos; private readonly object _ringLock = new();
    private long _warns, _fails, _total;
    public long WarnCount => Interlocked.Read(ref _warns);
    public long FailCount => Interlocked.Read(ref _fails);
    public long TotalCount => Interlocked.Read(ref _total);
    public int MaxEvents { get; init; } = 200_000;
    public event Action<RuntimeEvent>? Published;
    public double Now => _clock.Elapsed.TotalSeconds;

    public void Publish(EventLevel level, string category, string stream, string message)
    {
        var e = new RuntimeEvent(DateTime.UtcNow, _clock.Elapsed.TotalSeconds, level, category, stream, message);
        _all.Enqueue(e);
        while (_all.Count > MaxEvents) _all.TryDequeue(out _);
        lock (_ringLock) { _ring[_ringPos % _ring.Length] = e; _ringPos++; }
        Interlocked.Increment(ref _total);
        if (level == EventLevel.Warn) Interlocked.Increment(ref _warns); else if (level == EventLevel.Fail) Interlocked.Increment(ref _fails);
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
    /// <summary>Most recent events (bounded ring, O(n) regardless of total volume: safe under log floods).</summary>
    public IReadOnlyList<RuntimeEvent> Recent(int n)
    {
        lock (_ringLock)
        {
            var count = (int)Math.Min(Math.Min(n, _ring.Length), _ringPos);
            var res = new RuntimeEvent[count];
            for (int i = 0; i < count; i++) res[i] = _ring[(_ringPos - count + i) % _ring.Length];
            return res;
        }
    }
}
