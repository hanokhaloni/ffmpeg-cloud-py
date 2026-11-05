using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FFmpegReliabilityBench.Core.Lab;

/// <summary>Every process StreamBench starts has a role; resources are accounted per role (generator vs consumer load).</summary>
public enum ProcessRole { Relay, Publisher, Consumer, Player, Validator, Tool }

public sealed class ManagedProcess
{
    public long Id { get; init; }
    public ProcessRole Role { get; init; }
    public string Name { get; init; } = "";
    public string Owner { get; init; } = "";
    public int Pid { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public string CommandLine { get; init; } = "";
    internal Process? Process { get; init; }
    public bool Exited { get; internal set; }
    public int? ExitCode { get; internal set; }
    public DateTime? ExitedUtc { get; internal set; }
    public double CpuPercent { get; internal set; }
    public double MemMb { get; internal set; }
    public double CpuSeconds { get; internal set; }
    public long WriteBytes { get; internal set; }
    internal TimeSpan LastCpu; internal long LastSampleTicks; internal long LastWriteBytes = -1;
    public TimeSpan Age => (ExitedUtc ?? DateTime.UtcNow) - StartedUtc;
}

public sealed record RoleTotals(ProcessRole Role, int Live, int Started, double CpuPercent, double MemMb, double CpuSeconds);

/// <summary>Registry of application-owned processes. Only these PIDs are ever stopped by StreamBench.</summary>
public sealed class ProcessRegistry
{
    public static ProcessRegistry Current { get; set; } = new();

    private readonly ConcurrentDictionary<long, ManagedProcess> _live = new();
    private readonly ConcurrentQueue<ManagedProcess> _exited = new();
    private readonly ConcurrentDictionary<ProcessRole, double> _cpuSecondsExited = new();
    private readonly ConcurrentDictionary<ProcessRole, int> _started = new();
    private long _next;
    private long _appWriteBytes;
    public long AppWriteBytes => Interlocked.Read(ref _appWriteBytes);

    public ManagedProcess Register(Process p, ProcessRole role, string name, string owner, string commandLine)
    {
        var mp = new ManagedProcess { Id = Interlocked.Increment(ref _next), Role = role, Name = name, Owner = owner, Pid = SafePid(p), CommandLine = commandLine, Process = p };
        _live[mp.Id] = mp;
        _started.AddOrUpdate(role, 1, (_, c) => c + 1);
        return mp;
    }

    public void MarkExited(ManagedProcess mp, int? exitCode)
    {
        if (mp.Exited) return;
        Sample(mp); // last chance to account CPU (works on Windows after exit; best effort elsewhere)
        mp.Exited = true; mp.ExitCode = exitCode; mp.ExitedUtc = DateTime.UtcNow; mp.CpuPercent = 0;
        _live.TryRemove(mp.Id, out _);
        _cpuSecondsExited.AddOrUpdate(mp.Role, mp.CpuSeconds, (_, v) => v + mp.CpuSeconds);
        _exited.Enqueue(mp);
        while (_exited.Count > 500) _exited.TryDequeue(out _);
    }

    public IReadOnlyList<ManagedProcess> Live => _live.Values.OrderBy(p => p.Id).ToList();
    public IReadOnlyList<ManagedProcess> RecentlyExited => _exited.ToArray();
    public int LiveCount => _live.Count;

    /// <summary>Samples CPU/memory/write bytes of all live owned processes. Called by one collector at ~1 Hz.</summary>
    public void SampleAll() { foreach (var p in _live.Values) Sample(p); }

    private void Sample(ManagedProcess mp)
    {
        var p = mp.Process;
        if (p == null) return;
        try
        {
            p.Refresh();
            var cpu = p.TotalProcessorTime; var now = Stopwatch.GetTimestamp();
            if (mp.LastSampleTicks > 0)
            {
                var dt = (now - mp.LastSampleTicks) / (double)Stopwatch.Frequency;
                if (dt > 0.05) mp.CpuPercent = Math.Max(0, (cpu - mp.LastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100.0);
            }
            mp.LastCpu = cpu; mp.LastSampleTicks = now; mp.CpuSeconds = cpu.TotalSeconds;
            mp.MemMb = p.WorkingSet64 / 1048576.0;
            var wb = ProcIo.WriteBytes(p);
            if (wb >= 0)
            {
                if (mp.LastWriteBytes >= 0 && wb >= mp.LastWriteBytes) Interlocked.Add(ref _appWriteBytes, wb - mp.LastWriteBytes);
                mp.LastWriteBytes = wb; mp.WriteBytes = wb;
            }
        }
        catch { /* exited between checks */ }
    }

    public IReadOnlyList<RoleTotals> Totals()
    {
        var live = _live.Values.ToList();
        return Enum.GetValues<ProcessRole>().Select(r =>
        {
            var l = live.Where(p => p.Role == r).ToList();
            return new RoleTotals(r, l.Count, _started.GetValueOrDefault(r), l.Sum(p => p.CpuPercent), l.Sum(p => p.MemMb), l.Sum(p => p.CpuSeconds) + _cpuSecondsExited.GetValueOrDefault(r));
        }).ToList();
    }

    private static int SafePid(Process p) { try { return p.Id; } catch { return -1; } }
}

/// <summary>Bytes written by a process (Linux /proc/pid/io wchar, Windows GetProcessIoCounters). -1 if unavailable.</summary>
internal static class ProcIo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);

    public static long WriteBytes(Process p)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return GetProcessIoCounters(p.Handle, out var c) ? (long)c.WriteTransferCount : -1;
            var path = $"/proc/{p.Id}/io";
            if (!File.Exists(path)) return -1;
            foreach (var l in File.ReadLines(path))
                if (l.StartsWith("wchar:")) return long.Parse(l[6..].Trim());
        }
        catch { }
        return -1;
    }
}
