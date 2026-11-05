using System.Globalization;

namespace FFmpegReliabilityBench.Core.Validation;

public sealed record HlsSegmentEntry(long Sequence, string Uri, double Duration, bool Discontinuity);

/// <summary>Minimal RFC 8216 media playlist parser + validator.</summary>
public sealed class HlsPlaylist
{
    public int Version { get; private set; }
    public double TargetDuration { get; private set; } = -1;
    public long MediaSequence { get; private set; }
    public bool EndList { get; private set; }
    public bool IndependentSegments { get; private set; }
    public string? MapUri { get; private set; }
    public List<HlsSegmentEntry> Segments { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public static HlsPlaylist Parse(string text)
    {
        var p = new HlsPlaylist();
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0] != "#EXTM3U") { p.Errors.Add("missing #EXTM3U header"); if (lines.Count == 0) return p; }
        double? pendingDur = null; bool disc = false; long seq = 0; bool seqSet = false;
        var seen = new HashSet<string>();
        foreach (var l in lines.Skip(1))
        {
            if (l.StartsWith("#EXT-X-VERSION:")) p.Version = (int)Num(l[15..]);
            else if (l.StartsWith("#EXT-X-TARGETDURATION:")) p.TargetDuration = Num(l[22..]);
            else if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) { p.MediaSequence = (long)Num(l[22..]); seq = p.MediaSequence; seqSet = true; }
            else if (l == "#EXT-X-DISCONTINUITY") disc = true;
            else if (l == "#EXT-X-ENDLIST") p.EndList = true;
            else if (l == "#EXT-X-INDEPENDENT-SEGMENTS") p.IndependentSegments = true;
            else if (l.StartsWith("#EXT-X-MAP:"))
            {
                var i = l.IndexOf("URI=\"", StringComparison.Ordinal);
                if (i >= 0) { var j = l.IndexOf('"', i + 5); if (j > i) p.MapUri = l[(i + 5)..j]; }
            }
            else if (l.StartsWith("#EXTINF:"))
            {
                var v = l[8..]; var comma = v.IndexOf(',');
                pendingDur = Num(comma >= 0 ? v[..comma] : v);
                if (pendingDur < 0 || double.IsNaN(pendingDur.Value)) { p.Errors.Add($"invalid EXTINF '{l}'"); pendingDur = 0; }
            }
            else if (l.StartsWith('#')) { /* other tags ignored */ }
            else
            {
                if (pendingDur == null) p.Errors.Add($"segment '{l}' without #EXTINF");
                if (!seen.Add(l)) p.Warnings.Add($"duplicate segment URI '{l}'");
                p.Segments.Add(new HlsSegmentEntry(seq++, l, pendingDur ?? 0, disc));
                pendingDur = null; disc = false;
            }
        }
        if (p.TargetDuration < 0) p.Errors.Add("missing #EXT-X-TARGETDURATION");
        if (!seqSet) p.Warnings.Add("missing #EXT-X-MEDIA-SEQUENCE (defaults to 0)");
        if (p.TargetDuration >= 0)
            foreach (var s in p.Segments)
                if (Math.Round(s.Duration, MidpointRounding.AwayFromZero) > p.TargetDuration)
                    p.Errors.Add($"EXTINF {s.Duration:0.###}s of '{s.Uri}' exceeds TARGETDURATION {p.TargetDuration} (RFC 8216 4.3.3.1)");
        return p;
    }

    private static double Num(string s) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}
