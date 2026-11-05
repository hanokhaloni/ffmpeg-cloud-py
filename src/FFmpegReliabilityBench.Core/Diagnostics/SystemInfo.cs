using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FFmpegReliabilityBench.Core.Diagnostics;

public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string dir, out ulong freeForCaller, out ulong total, out ulong totalFree);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>(total, available) physical memory in bytes.</summary>
    public static (double Total, double Available) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref m)) return (m.ullTotalPhys, m.ullAvailPhys);
        }
        else if (File.Exists("/proc/meminfo"))
        {
            double total = 0, avail = 0;
            foreach (var l in File.ReadLines("/proc/meminfo"))
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                if (p[0] == "MemTotal:") total = double.Parse(p[1]) * 1024;
                if (p[0] == "MemAvailable:") avail = double.Parse(p[1]) * 1024;
            }
            return (total, avail);
        }
        var gc = GC.GetGCMemoryInfo();
        return (gc.TotalAvailableMemoryBytes, gc.TotalAvailableMemoryBytes - gc.MemoryLoadBytes);
    }

    /// <summary>Free bytes for the caller on the volume holding <paramref name="dir"/> (works for UNC paths on Windows).</summary>
    public static (double Free, double Total)? DiskSpace(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            if (OperatingSystem.IsWindows())
            {
                var probe = full; while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is string parent) probe = parent;
                if (GetDiskFreeSpaceEx(probe, out var free, out var total, out _)) return (free, total);
                return null;
            }
            var probeDir = full; while (!Directory.Exists(probeDir) && Path.GetDirectoryName(probeDir) is string pp) probeDir = pp;
            var di = new DriveInfo(probeDir);
            // DriveInfo on Unix resolves to the mount containing the path
            return (di.AvailableFreeSpace, di.TotalSize);
        }
        catch { return null; }
    }

    public static string PathKind(string dir)
    {
        if (dir.StartsWith(@"\\")) return "UNC";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(dir));
                if (root != null && new DriveInfo(root).DriveType == DriveType.Network) return "MappedNetworkDrive";
            }
            catch { }
        }
        return "Local";
    }

    private static (long Idle, long Total)? _lastCpu;
    /// <summary>System-wide CPU utilization since the previous call (0..100).</summary>
    public static double? SystemCpuPercent()
    {
        long idle, total;
        if (OperatingSystem.IsWindows())
        {
            if (!GetSystemTimes(out idle, out var kernel, out var user)) return null;
            total = kernel + user; // kernel includes idle
        }
        else if (File.Exists("/proc/stat"))
        {
            var p = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            idle = p[3] + (p.Length > 4 ? p[4] : 0); total = p.Sum();
        }
        else return null;
        var prev = _lastCpu; _lastCpu = (idle, total);
        if (prev == null || total == prev.Value.Total) return null;
        return Math.Clamp(100.0 * (1 - (double)(idle - prev.Value.Idle) / (total - prev.Value.Total)), 0, 100);
    }

    public static int OtherFfmpegProcesses()
    {
        try { return Process.GetProcessesByName("ffmpeg").Length; } catch { return -1; }
    }

    public static string Identity()
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        return Environment.UserInteractive ? user + " (interactive)" : user + " (service/non-interactive)";
    }
}
