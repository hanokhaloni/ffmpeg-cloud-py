using System.Text;
using System.Text.RegularExpressions;

namespace FFmpegReliabilityBench.Core.Runner;

public static class CommandText
{
    private static readonly Regex Cred = new(@"(?<=://)([^:/@\s]+):([^@/\s]+)@", RegexOptions.Compiled);

    /// <summary>Masks credentials in URLs (rtsp://user:pass@host → rtsp://user:***@host). Never log passwords.</summary>
    public static string Mask(string s) => Cred.Replace(s, m => $"{m.Groups[1].Value}:***@");

    /// <summary>Human-readable, copy-pasteable command (credentials masked).</summary>
    public static string Human(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(Mask(a)));
        return sb.ToString();
    }

    private static string Quote(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '|', '[', ']', ';', '&', '(', ')' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
