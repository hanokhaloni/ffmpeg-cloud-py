using System.Diagnostics;

namespace FFmpegReliabilityBench.Core.Diagnostics;

public sealed record ResourceSample(double T, DateTime TsUtc, double? SystemCpu, double AvailableMemMb, double BenchWorkingSetMb, double BenchManagedMb,
    Dictionary<string, double> FfmpegCpu, Dictionary<string, double> FfmpegMemMb, NetCounters? Net, Dictionary<string, double> FreeGb);

/// <summary>Samples system + per-FFmpeg + benchmark-self resources and network counters for the whole run.</summary>
public sealed class ResourceMonitor
{
    private readonly EventBus _bus; private readonly Func<IEnumerable<(string Name, double Cpu, double MemMb)>> _procs; private readonly Dictionary<string, string> _dirs;
    private CancellationTokenSource? _cts; private Task? _loop; private NetCounters? _prevNet;
    public List<ResourceSample> Samples { get; } = new();
    public double IntervalSeconds { get; init; } = 2;
    public double CpuWarnPercent { get; init; } = 85;
    public double LastCpu { get; private set; }
    public double LastAvailMb { get; private set; }

    public ResourceMonitor(EventBus bus, Func<IEnumerable<(string, double, double)>> procs, Dictionary<string, string> dirs) { _bus = bus; _procs = procs; _dirs = dirs; }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        SystemInfo.SystemCpuPercent();
        _prevNet = NetCounters.Sample();
        _loop = Task.Run(async () =>
        {
            int highCpu = 0;
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), _cts.Token); } catch (OperationCanceledException) { break; }
                var cpu = SystemInfo.SystemCpuPercent();
                var (_, avail) = SystemInfo.Memory();
                var self = Process.GetCurrentProcess();
                var p = _procs().ToList();
                var net = NetCounters.Sample();
                var free = new Dictionary<string, double>();
                foreach (var (k, d) in _dirs) if (SystemInfo.DiskSpace(d) is { } ds) free[k] = ds.Free / 1073741824.0;
                var s = new ResourceSample(_bus.Now, DateTime.UtcNow, cpu, avail / 1048576.0, self.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                    p.ToDictionary(x => x.Name, x => x.Cpu), p.ToDictionary(x => x.Name, x => x.MemMb), net, free);
                lock (Samples) Samples.Add(s);
                LastCpu = cpu ?? 0; LastAvailMb = avail / 1048576.0;
                if (cpu > CpuWarnPercent) { if (++highCpu == (int)(60 / IntervalSeconds)) _bus.Publish(EventLevel.Warn, "Resource", "*", $"system CPU above {CpuWarnPercent}% for 60s"); } else highCpu = 0;
                if (net != null && _prevNet != null)
                {
                    var resent = net.SegmentsResent - _prevNet.SegmentsResent; var resets = net.ResetConnections - _prevNet.ResetConnections;
                    var nicErr = net.InErrors - _prevNet.InErrors + net.InDiscards - _prevNet.InDiscards;
                    if (resent > 50) _bus.PublishThrottled("retrans", 10, EventLevel.Warn, "Network", "*", $"TCP retransmissions: {resent} segments in {IntervalSeconds}s");
                    if (resets > 0) _bus.PublishThrottled("resets", 10, EventLevel.Warn, "Network", "*", $"TCP connection resets: +{resets}");
                    if (nicErr > 0) _bus.PublishThrottled("nicerr", 10, EventLevel.Warn, "Network", "*", $"NIC errors/discards: +{nicErr}");
                }
                _prevNet = net ?? _prevNet;
                foreach (var (k, gb) in free) if (gb < 1) _bus.PublishThrottled("disk" + k, 30, EventLevel.Fail, "Storage", k, $"free space on {k} volume below 1 GB ({gb:F2} GB)");
            }
        });
    }

    public async Task StopAsync() { _cts?.Cancel(); if (_loop != null) try { await _loop; } catch { } }

    /// <summary>Least-squares slope (MB per hour) of a series — memory leak indicator.</summary>
    public static double SlopePerHour(IReadOnlyList<(double T, double V)> xs)
    {
        if (xs.Count < 5) return 0;
        var mt = xs.Average(x => x.T); var mv = xs.Average(x => x.V);
        var num = xs.Sum(x => (x.T - mt) * (x.V - mv)); var den = xs.Sum(x => (x.T - mt) * (x.T - mt));
        return den == 0 ? 0 : num / den * 3600;
    }
}
