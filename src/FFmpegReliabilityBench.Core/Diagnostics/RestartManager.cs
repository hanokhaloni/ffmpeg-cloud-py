using System.Runtime.InteropServices;

namespace FFmpegReliabilityBench.Core.Diagnostics;

/// <summary>Windows Restart Manager: which processes hold a file. A lock is evidence, not proof that AV is responsible.</summary>
public static class RestartManager
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType; public uint AppStatus; public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint h, int flags, string key);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint h);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint h, uint nFiles, string[] files, uint nApps, [In] RM_UNIQUE_PROCESS[]? apps, uint nSvc, string[]? svcs);
    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint h, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint rebootReasons);

    public static List<string> GetLockingProcesses(string path)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;
        try
        {
            if (RmStartSession(out var h, 0, Guid.NewGuid().ToString()) != 0) return result;
            try
            {
                if (RmRegisterResources(h, 1, new[] { path }, 0, null, 0, null) != 0) return result;
                uint needed, count = 0, reasons = 0;
                var rc = RmGetList(h, out needed, ref count, null, ref reasons);
                if (rc == 234 /* ERROR_MORE_DATA */ && needed > 0)
                {
                    var info = new RM_PROCESS_INFO[needed]; count = needed;
                    if (RmGetList(h, out needed, ref count, info, ref reasons) == 0)
                        for (int i = 0; i < count; i++)
                            result.Add($"{info[i].strAppName} (pid {info[i].Process.dwProcessId}{(string.IsNullOrEmpty(info[i].strServiceShortName) ? "" : ", svc " + info[i].strServiceShortName)})");
                }
            }
            finally { RmEndSession(h); }
        }
        catch (Exception ex) { result.Add("RestartManager error: " + ex.Message); }
        return result;
    }
}
