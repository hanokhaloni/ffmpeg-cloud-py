using System.Diagnostics;

namespace FFmpegReliabilityBench.Core.Diagnostics;

public sealed record ResourceSample(double T, DateTime TsUtc, double? SystemCpu, double AvailableMemMb, double BenchWorkingSetMb, double BenchManagedMb,
    Dictionary<string, double> FfmpegCpu, Dictionary<string, double> FfmpegMemMb, NetCounters? Net, Dictionary<string, double> FreeGb,
    double TotalMemMb = 0, double AppWriteMBps = 0, double NetMbps = 0, Dictionary<string, double>? RoleCpu = null, Dictionary<string, double>? RoleMemMb = null);

/// <summary>Samples system + per-FFmpeg + benchmark-self resources and network counters for the whole run.</summary>
public sealed class ResourceMonitor
{
    private readonly EventBus _bus; private readonly Func<IEnumerable<(string Name, double Cpu, double MemMb)>> _procs; private readonly Dictionary<string, string> _dirs;
    private CancellationTokenSource? _cts; private Task? _loop; private NetCounters? _prevNet;
    public List<ResourceSample> Samples { get; } = new();
    public double IntervalSeconds { get; init; } = 1;
    public int HistoryCapacity { get; init; } = 300;
    private readonly Queue<ResourceSample> _history = new();
    /// <summary>Last <see cref="HistoryCapacity"/> samples (UI graphs).</summary>
    public ResourceSample[] History() { lock (_history) return _history.ToArray(); }
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
            int highCpu = 0; long lastWrite = Lab.ProcessRegistry.Current.AppWriteBytes; var swRate = System.Diagnostics.Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), _cts.Token); } catch (OperationCanceledException) { break; }
                Lab.ProcessRegistry.Current.SampleAll();
                var cpu = SystemInfo.SystemCpuPercent();
                var (totalMem, avail) = SystemInfo.Memory();
                var dt = Math.Max(0.2, swRate.Elapsed.TotalSeconds); swRate.Restart();
                var wNow = Lab.ProcessRegistry.Current.AppWriteBytes; var writeMBps = (wNow - lastWrite) / 1048576.0 / dt; lastWrite = wNow;
                var roles = Lab.ProcessRegistry.Current.Totals();
                var self = Process.GetCurrentProcess();
                var p = _procs().ToList();
                var net = NetCounters.Sample();
                var free = new Dictionary<string, double>();
                foreach (var (k, d) in _dirs) if (SystemInfo.DiskSpace(d) is { } ds) free[k] = ds.Free / 1073741824.0;
                var netMbps = net != null && _prevNet != null ? Math.Max(0, (net.BytesReceived - _prevNet.BytesReceived + net.BytesSent - _prevNet.BytesSent) * 8 / 1e6 / dt) : 0;
                var s = new ResourceSample(_bus.Now, DateTime.UtcNow, cpu, avail / 1048576.0, self.WorkingSet64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0,
                    p.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Sum(x => x.Cpu)), p.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Sum(x => x.MemMb)), net, free,
                    totalMem / 1048576.0, writeMBps, netMbps, roles.ToDictionary(r => r.Role.ToString(), r => r.CpuPercent), roles.ToDictionary(r => r.Role.ToString(), r => r.MemMb));
                lock (_history) { _history.Enqueue(s); while (_history.Count > HistoryCapacity) _history.Dequeue(); }
                lock (Samples) Samples.Add(s);
                LastCpu = cpu ?? 0; LastAvailMb = avail / 1048576.0;
                if (cpu > CpuWarnPercent) { if (++highCpu == (int)(60 / IntervalSeconds)) _bus.Publish(EventLevel.Warn, "Resource", "*", $"system CPU above {CpuWarnPercent}% for 60s"); } else highCpu = 0;
                if (net != null && _prevNet != null)
                {
                    var resent = net.SegmentsResent - _prevNet.SegmentsResent; var resets = net.ResetConnections - _prevNet.ResetConnections;
                    var nicErr = net.InErrors - _prevNet.InErrors + net.InDiscards - _prevNet.InDiscards;
                    if (resent > 50) _bus.PublishThrottled("retrans", 10, EventLevel.Warn, "Network", "*", $"TCP retransmissions: {resent} segments in {dt:F1}s");
                    if (resets > 50) _bus.PublishThrottled("resets", 30, EventLevel.Warn, "Network", "*", $"TCP connection resets (host-wide): +{resets} in {dt:F1}s");
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
