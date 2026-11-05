using System.Text.RegularExpressions;

namespace FFmpegReliabilityBench.Core.Runner;

public enum FfErrorCategory
{
    None, RtspConnectionFailure, RtspTimeout, InputCorruption, TimestampDiscontinuity, NonMonotonicDts,
    DecoderError, EncoderError, MuxerError, FileAccessError, PermissionDenied, DiskFull, NetworkIoError,
    OutputBlocked, ProcessTermination, OtherError
}

/// <summary>Classified line; the raw text is always kept.</summary>
public sealed record ClassifiedLogLine(DateTime TsUtc, string Raw, string Level, FfErrorCategory Category)
{
    public bool IsProblem => Category != FfErrorCategory.None || Level is "error" or "fatal" or "panic";
}

public static class FFmpegLogParser
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    private static readonly (Regex Re, FfErrorCategory Cat)[] Rules =
    {
        (new Regex(@"No space left on device|disk full", O), FfErrorCategory.DiskFull),
        (new Regex(@"Permission denied|Access is denied", O), FfErrorCategory.PermissionDenied),
        (new Regex(@"non[- ]monoton\w* (increasing )?dts|Non-monotonous DTS", O), FfErrorCategory.NonMonotonicDts),
        (new Regex(@"Connection refused|method (DESCRIBE|SETUP|PLAY) failed|Server returned (4|5)\d\d|401 Unauthorized|404 Not Found|Unable to open RTSP", O), FfErrorCategory.RtspConnectionFailure),
        (new Regex(@"timed? ?out|Operation timed out", O), FfErrorCategory.RtspTimeout),
        (new Regex(@"RTP: missed \d+ packets|max delay reached|corrupt|Invalid data found|error while decoding MB|concealing|Packet mismatch|PES packet size mismatch", O), FfErrorCategory.InputCorruption),
        (new Regex(@"discontinuity|timestamp.*(jump|reset)|Invalid timestamps|DTS \d+.*out of order|pts has no value", O), FfErrorCategory.TimestampDiscontinuity),
        (new Regex(@"Error while decoding|decoding for stream .* failed|Error submitting packet to decoder", O), FfErrorCategory.DecoderError),
        (new Regex(@"Error (initializing output stream|while opening encoder)|Error encoding|Error submitting video frame to the encoder|encoder setup failed", O), FfErrorCategory.EncoderError),
        (new Regex(@"Error writing trailer|Could not write header|av_interleaved_write_frame|Error muxing|Error submitting a packet to the muxer|Slave .* failed", O), FfErrorCategory.MuxerError),
        (new Regex(@"Failed to open .* for writing|Could not open file|failed to rename|Failed to (open|rename|delete) |No such file or directory|Unable to (open|create)", O), FfErrorCategory.FileAccessError),
        (new Regex(@"Input/output error|Broken pipe|Connection reset by peer|Network is unreachable|End of file", O), FfErrorCategory.NetworkIoError),
        (new Regex(@"buffer (queue )?(overflow|full)|Too many packets buffered|Thread message queue blocking", O), FfErrorCategory.OutputBlocked),
        (new Regex(@"received signal|Conversion failed|Exiting normally|Terminating", O), FfErrorCategory.ProcessTermination),
    };
    private static readonly Regex LevelRx = new(@"\[(trace|debug|verbose|info|warning|error|fatal|panic)\]", O);

    public static ClassifiedLogLine Classify(string raw, DateTime? ts = null)
    {
        var lm = LevelRx.Match(raw);
        var level = lm.Success ? lm.Groups[1].Value.ToLowerInvariant() : "info";
        var cat = FfErrorCategory.None;
        // Informational lines are only classified when they are warnings/errors (avoids "encoder : Lavc" style false positives).
        if (level is "warning" or "error" or "fatal" or "panic" || !lm.Success)
        {
            foreach (var (re, c) in Rules)
                if (re.IsMatch(raw)) { cat = c; break; }
            if (cat == FfErrorCategory.None && level is "error" or "fatal" or "panic") cat = FfErrorCategory.OtherError;
        }
        if (!lm.Success && cat == FfErrorCategory.ProcessTermination) cat = FfErrorCategory.None;
        return new ClassifiedLogLine(ts ?? DateTime.UtcNow, raw, level, cat);
    }
}
