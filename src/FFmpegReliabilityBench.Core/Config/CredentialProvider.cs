using System.Runtime.InteropServices;
using System.Text;

namespace FFmpegReliabilityBench.Core.Config;

/// <summary>
/// RTSP credentials are never stored in config files or reports. Lookup order:
/// 1. Windows Credential Manager generic credential "FFmpegReliabilityBench:&lt;host&gt;" (cmdkey /generic:FFmpegReliabilityBench:host /user:u /pass:p)
/// 2. Environment variables FRB_RTSP_USER / FRB_RTSP_PASSWORD
/// </summary>
public static class CredentialProvider
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags, Type; public IntPtr TargetName, Comment; public long LastWritten;
        public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr cred);

    public static string ApplyTo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !u.Scheme.StartsWith("rtsp") || !string.IsNullOrEmpty(u.UserInfo)) return url;
        var cred = ReadWindows("FFmpegReliabilityBench:" + u.Host);
        if (cred == null)
        {
            var user = Environment.GetEnvironmentVariable("FRB_RTSP_USER"); var pass = Environment.GetEnvironmentVariable("FRB_RTSP_PASSWORD");
            if (!string.IsNullOrEmpty(user)) cred = (user, pass ?? "");
        }
        if (cred == null) return url;
        var b = new UriBuilder(u) { UserName = Uri.EscapeDataString(cred.Value.User), Password = Uri.EscapeDataString(cred.Value.Pass) };
        return b.Uri.ToString();
    }

    private static (string User, string Pass)? ReadWindows(string target)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (!CredRead(target, 1 /* CRED_TYPE_GENERIC */, 0, out var p)) return null;
            try
            {
                var c = Marshal.PtrToStructure<CREDENTIAL>(p);
                var user = Marshal.PtrToStringUni(c.UserName) ?? "";
                var pass = c.CredentialBlobSize > 0 ? Encoding.Unicode.GetString(GetBytes(c.CredentialBlob, c.CredentialBlobSize)) : "";
                return (user, pass);
            }
            finally { CredFree(p); }
        }
        catch { return null; }
    }

    private static byte[] GetBytes(IntPtr p, int n) { var b = new byte[n]; Marshal.Copy(p, b, 0, n); return b; }
}
