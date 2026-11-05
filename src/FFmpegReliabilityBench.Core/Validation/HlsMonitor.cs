using System.Collections.Concurrent;
using System.Diagnostics;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Validation;

public sealed class HlsSegmentRecord
{
    public long Sequence { get; init; }
    public string Uri { get; init; } = "";
    public double PlaylistDuration { get; set; }
    public double ProbedDuration { get; set; }
    public long Size { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.Pending;
    public string Error { get; set; } = "";
    public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;
    public double ValidationSeconds { get; set; }
    public bool Discontinuity { get; init; }
    public bool WasGrowing { get; set; }
    public bool DurationAnomaly { get; set; }
    internal int MissingPolls;
    internal volatile bool Validating;
}

/// <summary>Independent HLS producer validation: polls the playlist, accounts for every sequence number, probes and decodes every segment.</summary>
public sealed class HlsMonitor
{
    private readonly string _stream, _playlist, _dir, _scratch;
    private readonly HlsCfg _cfg; private readonly FFprobeRunner _probe; private readonly EventBus _bus; private readonly bool _fmp4;
    private readonly SemaphoreSlim _gate = new(2);
    private readonly List<Task> _tasks = new();
    private readonly Stopwatch _sinceAdvance = new(), _sinceStart = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts; private Task? _loop; private bool _inStall, _warnedNoPlaylist; private volatile bool _producerExited;

    /// <summary>Once the producer is gone, a non-advancing playlist is a consequence (reported by PROC-001), not a separate stall.</summary>
    public void MarkProducerExited() => _producerExited = true;

    public ConcurrentDictionary<long, HlsSegmentRecord> Segments { get; } = new();
    public HashSet<string> PlaylistViolations { get; } = new();
    public int PlaylistReads, PlaylistReadErrors, StallEpisodes, GrowingReferences;
    public long MaxSeq { get; private set; } = -1;
    public double MaxPlaylistStallSeconds { get; private set; }
    public double? TimeToFirstPlaylistSeconds { get; private set; }
    public HlsPlaylist? LastPlaylist { get; private set; }
    public string? InitSegmentError { get; private set; }
    public bool PlaylistSeen => TimeToFirstPlaylistSeconds != null;
    public double SecondsSinceAdvance => _sinceAdvance.IsRunning ? _sinceAdvance.Elapsed.TotalSeconds : _sinceStart.Elapsed.TotalSeconds;
    public string PlaylistPath => _playlist;

    public HlsMonitor(string stream, string playlist, HlsCfg cfg, FFprobeRunner probe, EventBus bus, bool fmp4, string scratchDir)
    {
        _stream = stream; _playlist = playlist; _dir = Path.GetDirectoryName(Path.GetFullPath(playlist))!;
        _cfg = cfg; _probe = probe; _bus = bus; _fmp4 = fmp4; _scratch = scratchDir;
        Directory.CreateDirectory(_scratch);
    }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sinceStart.Start();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
                try { Poll(final: false); } catch (Exception ex) { _bus.PublishThrottled("hlspoll" + _stream, 10, EventLevel.Warn, "HLS", _stream, "poll error: " + ex.Message); }
            }
        });
    }

    /// <summary>Stops polling, performs a final pass (picks up the last segment / ENDLIST), waits for validations.</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        try { Poll(final: true); } catch { }
        Task[] t; lock (_lock) t = _tasks.ToArray();
        try { await Task.WhenAll(t).WaitAsync(TimeSpan.FromMinutes(5)); } catch { }
        foreach (var r in Segments.Values.Where(r => r.Status == MediaStatus.Pending)) { r.Status = MediaStatus.Unverified; r.Error = "validation not completed"; }
    }

    private void Poll(bool final)
    {
        if (!File.Exists(_playlist))
        {
            if (!PlaylistSeen && !_warnedNoPlaylist && !_producerExited && _sinceStart.Elapsed.TotalSeconds > Math.Max(10, _cfg.MaxSegmentAgeSeconds * 2))
            { _warnedNoPlaylist = true; _bus.Publish(EventLevel.Fail, "HLS", _stream, $"playlist not created after {_sinceStart.Elapsed.TotalSeconds:F0}s: {_playlist}"); }
            return;
        }
        string text;
        try { text = ReadShared(_playlist); }
        catch (Exception ex) { Interlocked.Increment(ref PlaylistReadErrors); _bus.PublishThrottled("plread" + _stream, 5, EventLevel.Warn, "HLS", _stream, "playlist read failed: " + ex.Message); return; }
        if (text.Length == 0) { Interlocked.Increment(ref PlaylistReadErrors); return; }
        Interlocked.Increment(ref PlaylistReads);
        if (!PlaylistSeen) { TimeToFirstPlaylistSeconds = _sinceStart.Elapsed.TotalSeconds; _sinceAdvance.Restart(); _bus.Publish(EventLevel.Info, "HLS", _stream, $"playlist appeared after {TimeToFirstPlaylistSeconds:F1}s"); }

        var pl = HlsPlaylist.Parse(text);
        LastPlaylist = pl;
        foreach (var e in pl.Errors)
            lock (PlaylistViolations) if (PlaylistViolations.Count < 100 && PlaylistViolations.Add(e)) _bus.Publish(EventLevel.Fail, "HLS", _stream, "playlist violation: " + e);

        if (_fmp4 && pl.MapUri != null && InitSegmentError == null && !File.Exists(Path.Combine(_dir, pl.MapUri)))
        { InitSegmentError = $"EXT-X-MAP '{pl.MapUri}' not found"; _bus.Publish(EventLevel.Fail, "HLS", _stream, InitSegmentError); }

        var lastSeq = pl.Segments.Count > 0 ? pl.Segments[^1].Sequence : -1;
        if (lastSeq > MaxSeq)
        {
            if (_inStall) { _inStall = false; _bus.Publish(EventLevel.Info, "HLS", _stream, $"playlist advancing again after {_sinceAdvance.Elapsed.TotalSeconds:F1}s"); }
            _sinceAdvance.Restart();
        }
        else if (!final && !pl.EndList && !_producerExited)
        {
            var age = _sinceAdvance.Elapsed.TotalSeconds;
            MaxPlaylistStallSeconds = Math.Max(MaxPlaylistStallSeconds, age);
            if (age > _cfg.MaxSegmentAgeSeconds && !_inStall)
            { _inStall = true; StallEpisodes++; _bus.Publish(EventLevel.Fail, "HLS", _stream, $"playlist has not advanced for {age:F1}s (limit {_cfg.MaxSegmentAgeSeconds}s)"); }
        }

        // Sequence numbers that were never listed (window skipped while we were not looking / producer restarted)
        if (MaxSeq >= 0 && pl.Segments.Count > 0 && pl.Segments[0].Sequence > MaxSeq + 1)
            for (var s = MaxSeq + 1; s < pl.Segments[0].Sequence; s++)
                if (Segments.TryAdd(s, new HlsSegmentRecord { Sequence = s, Uri = "?", PlaylistDuration = _cfg.TargetDurationSeconds, Status = MediaStatus.Missing, Error = "sequence never observed in playlist" }))
                    _bus.Publish(EventLevel.Fail, "HLS", _stream, $"segment #{s} never observed in playlist");

        foreach (var e in pl.Segments)
        {
            var rec = Segments.GetOrAdd(e.Sequence, _ => new HlsSegmentRecord { Sequence = e.Sequence, Uri = e.Uri, PlaylistDuration = e.Duration, Discontinuity = e.Discontinuity });
            if (rec.Status != MediaStatus.Pending || rec.Validating) continue;
            var path = Path.Combine(_dir, e.Uri);
            if (!File.Exists(path))
            {
                rec.MissingPolls++;
                if (rec.MissingPolls >= 3 || final)
                { rec.Status = MediaStatus.Missing; rec.Error = "referenced by playlist but file not found"; _bus.Publish(EventLevel.Fail, "HLS", _stream, $"segment #{e.Sequence} '{e.Uri}' referenced but missing"); }
                continue;
            }
            rec.Validating = true;
            var isFinal = pl.EndList && ReferenceEquals(e, pl.Segments[^1]);
            var t = Task.Run(() => ValidateAsync(rec, path, pl.MapUri, isFinal));
            lock (_lock) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
        }
        MaxSeq = Math.Max(MaxSeq, lastSeq);
    }

    private async Task ValidateAsync(HlsSegmentRecord rec, string path, string? mapUri, bool isFinal)
    {
        await _gate.WaitAsync();
        var sw = Stopwatch.StartNew();
        var copy = Path.Combine(_scratch, $"{_stream}_{rec.Sequence}{(_fmp4 ? ".mp4" : ".ts")}");
        try
        {
            // A segment listed in the playlist must already be complete.
            long s1 = new FileInfo(path).Length; await Task.Delay(300); long s2 = new FileInfo(path).Length;
            if (s1 != s2)
            {
                rec.WasGrowing = true; Interlocked.Increment(ref GrowingReferences);
                _bus.Publish(EventLevel.Warn, "HLS", _stream, $"segment #{rec.Sequence} still growing while referenced by playlist (consider hls_flags temp_file)");
                for (int i = 0; i < 20 && s1 != s2; i++) { s1 = s2; await Task.Delay(250); s2 = new FileInfo(path).Length; }
            }
            rec.Size = s2;
            if (s2 < _cfg.MinSegmentSizeBytes) { Fail(rec, $"segment too small ({s2} bytes)"); return; }

            // Validate a private snapshot: never hold handles on producer files (on Windows that can block FFmpeg's rename/delete).
            Directory.CreateDirectory(_scratch);
            await using (var dst = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (_fmp4)
                {
                    if (mapUri == null) { Fail(rec, "fMP4 playlist without EXT-X-MAP"); return; }
                    await using var init = OpenShared(Path.Combine(_dir, mapUri));
                    await init.CopyToAsync(dst);
                }
                await using var src = OpenShared(path);
                await src.CopyToAsync(dst);
            }
            var probe = await _probe.ProbeAsync(copy);
            if (!probe.Ok) { Fail(rec, "ffprobe: " + probe.Error); return; }
            if (!probe.HasVideo) { Fail(rec, "no video stream"); return; }
            rec.ProbedDuration = probe.Duration;
            if (_cfg.DecodeSegments)
            {
                var d = await _probe.DecodeAsync(copy);
                if (!d.Ok) { Fail(rec, "decode: " + d.Error); return; }
            }
            // The last segment before #EXT-X-ENDLIST is legitimately short (cut at shutdown).
            if (!(isFinal && rec.PlaylistDuration < _cfg.TargetDurationSeconds) && Math.Abs(rec.PlaylistDuration - _cfg.TargetDurationSeconds) > _cfg.DurationToleranceSeconds)
            {
                rec.DurationAnomaly = true;
                _bus.Publish(EventLevel.Warn, "HLS", _stream, $"segment #{rec.Sequence} duration {rec.PlaylistDuration:0.000}s outside {_cfg.TargetDurationSeconds}±{_cfg.DurationToleranceSeconds}s");
            }
            rec.Status = MediaStatus.Valid;
        }
        catch (FileNotFoundException) { rec.Status = MediaStatus.Unverified; rec.Error = "deleted before validation (increase hls_delete_threshold)"; _bus.Publish(EventLevel.Warn, "HLS", _stream, $"segment #{rec.Sequence} deleted before validation"); }
        catch (Exception ex) { rec.Status = MediaStatus.Unverified; rec.Error = ex.Message; _bus.Publish(EventLevel.Warn, "HLS", _stream, $"segment #{rec.Sequence} could not be validated: {ex.Message}"); }
        finally
        {
            rec.ValidationSeconds = sw.Elapsed.TotalSeconds;
            try { File.Delete(copy); } catch { }
            rec.Validating = false;
            _gate.Release();
        }
    }

    private void Fail(HlsSegmentRecord rec, string why)
    {
        rec.Status = MediaStatus.Invalid; rec.Error = why;
        _bus.Publish(EventLevel.Fail, "HLS", _stream, $"segment #{rec.Sequence} '{rec.Uri}' INVALID: {why}");
    }

    internal static FileStream OpenShared(string p) => new(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    internal static string ReadShared(string p) { using var fs = OpenShared(p); using var r = new StreamReader(fs); return r.ReadToEnd(); }

    public CoverageReport Coverage(double? wallClock = null) =>
        CoverageReport.Compute(Segments.Values.Select(r => (r.Status, r.Status == MediaStatus.Valid ? (r.PlaylistDuration > 0 ? r.PlaylistDuration : r.ProbedDuration) : (r.PlaylistDuration > 0 ? r.PlaylistDuration : _cfg.TargetDurationSeconds))), wallClock);
}
