using System.Globalization;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed class ProgressSnapshot
{
    public long Frame { get; init; }
    public double Fps { get; init; }
    public double BitrateKbps { get; init; }
    public long TotalSize { get; init; }
    public double OutTimeSeconds { get; init; }
    public long DupFrames { get; init; }
    public long DropFrames { get; init; }
    public double Speed { get; init; }
    public bool End { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Parses the key=value blocks produced by "-progress pipe:1". A block ends with "progress=continue|end".</summary>
public sealed class ProgressParser
{
    private readonly Dictionary<string, string> _cur = new(StringComparer.Ordinal);

    public ProgressSnapshot? Feed(string line)
    {
        var i = line.IndexOf('=');
        if (i <= 0) return null;
        var k = line[..i].Trim();
        var v = line[(i + 1)..].Trim();
        _cur[k] = v;
        if (k != "progress") return null;

        double outTime = double.NaN;
        // out_time_us is authoritative; out_time_ms is (historically) also microseconds.
        if (TryL("out_time_us", out var us)) outTime = us / 1e6;
        else if (TryL("out_time_ms", out var ms)) outTime = ms / 1e6;
        else if (_cur.TryGetValue("out_time", out var ot) && TimeSpan.TryParse(ot.Split('.')[0], CultureInfo.InvariantCulture, out var ts)) outTime = ts.TotalSeconds;

        var s = new ProgressSnapshot
        {
            Frame = L("frame"), Fps = D("fps"),
            BitrateKbps = D("bitrate", "kbits/s"), TotalSize = L("total_size"),
            OutTimeSeconds = double.IsNaN(outTime) ? 0 : Math.Max(0, outTime),
            DupFrames = L("dup_frames"), DropFrames = L("drop_frames"),
            Speed = D("speed", "x"), End = v == "end", ReceivedUtc = DateTime.UtcNow
        };
        _cur.Clear();
        return s;
    }

    private bool TryL(string k, out long val)
    {
        val = 0;
        return _cur.TryGetValue(k, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out val);
    }
    private long L(string k) => TryL(k, out var v) ? v : 0;
    private double D(string k, string? suffix = null)
    {
        if (!_cur.TryGetValue(k, out var s)) return 0;
        if (suffix != null && s.EndsWith(suffix, StringComparison.Ordinal)) s = s[..^suffix.Length];
        return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
