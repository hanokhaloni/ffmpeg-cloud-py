using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Diagnostics;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Validation;

public sealed class RecordingRecord
{
    public int Index { get; init; }
    public string FileName { get; init; } = "";
    public string Path { get; init; } = "";
    public long Size { get; set; }
    public MediaStatus Status { get; set; } = MediaStatus.Pending;
    /// <summary>EXPECTED → CREATED → WRITING → SIZE STABLE → CLOSED → FFPROBE VALIDATED → DECODE VALIDATED → PLAYBACK VERIFIED. Last reached stage is kept on failure.</summary>
    public string Stage { get; set; } = "CREATED";
    public double Duration { get; set; }
    public string Error { get; set; } = "";
    public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;
    public double TimelineStart { get; set; }
    public bool DurationAnomaly { get; set; }
    public int SharingViolations { get; set; }
    public List<string> Lockers { get; set; } = new();
    public int SeekOk { get; set; }
    public int SeekFailed { get; set; }
    internal double LastChangeAt;
    internal volatile bool Validating;
}

/// <summary>Tracks every expected recording file through its lifecycle; detects gaps, unfinalized MP4s, locks and unplayable files.</summary>
public sealed class RecordingMonitor
{
    private readonly Regex NameRx;
    private readonly string? _segmentList;
    private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _stream, _dir; private readonly RecordingCfg _cfg; private readonly FFprobeRunner _probe; private readonly EventBus _bus;
    private readonly Stopwatch _clock = Stopwatch.StartNew(), _sinceNew = Stopwatch.StartNew();
    private readonly SemaphoreSlim _gate = new(2);
    private readonly List<Task> _tasks = new(); private readonly object _lock = new();
    private CancellationTokenSource? _cts; private Task? _loop; private volatile bool _producerExited; private bool _inStall;

    public ConcurrentDictionary<int, RecordingRecord> Files { get; } = new();
    public int StallEpisodes { get; private set; }
    public double MaxGapBetweenFilesSeconds { get; private set; }
    public double? TimeToFirstFileSeconds { get; private set; }

    /// <summary>Raised after a recording passed every validation stage (used e.g. for sampled HTTP playback).</summary>
    public event Action<RecordingRecord>? Validated;
    public int PendingCount => Files.Values.Count(f => f.Status == MediaStatus.Pending);
    public double SecondsSinceNewFile => _sinceNew.Elapsed.TotalSeconds;
    public string Directory => _dir;

    /// <param name="fileRegex">Recording file name pattern with one numeric capture group (index).</param>
    /// <param name="segmentListPath">FFmpeg -segment_list CSV: an entry proves the muxer closed that file.</param>
    public RecordingMonitor(string stream, string dir, RecordingCfg cfg, FFprobeRunner probe, EventBus bus, string fileRegex = @"^rec_(\d+)\.mp4$", string? segmentListPath = null)
    {
        _stream = stream; _dir = dir; _cfg = cfg; _probe = probe; _bus = bus; _segmentList = segmentListPath;
        NameRx = new Regex(fileRegex, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Turns an FFmpeg output pattern such as "rec_%06d.mp4" into a regex with one index group.</summary>
    public static string PatternToRegex(string fileNamePattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(fileNamePattern, @"%0?\d*d");
        if (!m.Success) return "^" + System.Text.RegularExpressions.Regex.Escape(fileNamePattern) + "$";
        return "^" + System.Text.RegularExpressions.Regex.Escape(fileNamePattern[..m.Index]) + @"(\d+)" + System.Text.RegularExpressions.Regex.Escape(fileNamePattern[(m.Index + m.Length)..]) + "$";
    }

    private void ReadSegmentList()
    {
        if (_segmentList == null || !File.Exists(_segmentList)) return;
        try
        {
            using var fs = new FileStream(_segmentList, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            string? line;
            while ((line = r.ReadLine()) != null)
            {
                var name = line.Split(',')[0].Trim().Trim('"');
                if (name.Length > 0) _listed.Add(System.IO.Path.GetFileName(name));
            }
        }
        catch { }
    }

    public void MarkProducerExited() => _producerExited = true;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { break; }
                try { Poll(); } catch (Exception ex) { _bus.PublishThrottled("recpoll" + _stream, 10, EventLevel.Warn, "Recording", _stream, "poll error: " + ex.Message); }
            }
        });
    }

    public async Task StopAsync()
    {
        _producerExited = true;
        _cts?.Cancel();
        if (_loop != null) try { await _loop; } catch { }
        try { Poll(); } catch { }
        Task[] t; lock (_lock) t = _tasks.ToArray();
        try { await Task.WhenAll(t).WaitAsync(TimeSpan.FromMinutes(10)); } catch { }
        foreach (var r in Files.Values.Where(r => r.Status == MediaStatus.Pending)) { r.Status = MediaStatus.Unverified; r.Error = "validation not completed"; }
        BuildTimeline();
    }

    private void Poll()
    {
        if (!System.IO.Directory.Exists(_dir)) return;
        var now = _clock.Elapsed.TotalSeconds;
        var present = new HashSet<int>();
        ReadSegmentList();
        foreach (var path in System.IO.Directory.EnumerateFiles(_dir))
        {
            var name = System.IO.Path.GetFileName(path);
            var m = NameRx.Match(name);
            if (!m.Success) continue;
            var idx = int.Parse(m.Groups[1].Value);
            present.Add(idx);
            long size; try { size = new FileInfo(path).Length; } catch { continue; }
            var isNew = false;
            var rec = Files.GetOrAdd(idx, _ => { isNew = true; return new RecordingRecord { Index = idx, FileName = name, Path = path, LastChangeAt = now }; });
            if (isNew)
            {
                MaxGapBetweenFilesSeconds = Math.Max(MaxGapBetweenFilesSeconds, Files.Count > 1 ? _sinceNew.Elapsed.TotalSeconds : 0);
                TimeToFirstFileSeconds ??= now;
                _sinceNew.Restart();
                if (_inStall) { _inStall = false; _bus.Publish(EventLevel.Info, "Recording", _stream, "recording files are being created again"); }
            }
            if (rec.Status == MediaStatus.Missing) { rec.Status = MediaStatus.Pending; rec.Error = ""; } // appeared late
            if (size != rec.Size) { rec.Size = size; rec.LastChangeAt = now; if (rec.Stage == "CREATED" && size > 0) rec.Stage = "WRITING"; }
        }
        if (present.Count == 0) return;
        var max = present.Max(); var min = present.Min();

        // Every expected interval must have a file: index holes are missing recordings.
        for (var i = Math.Min(min, Files.Keys.DefaultIfEmpty(min).Min()); i < max; i++)
            if (!present.Contains(i) && Files.TryAdd(i, new RecordingRecord { Index = i, FileName = $"#{i}", Status = MediaStatus.Missing, Stage = "EXPECTED", Error = "file never created", Duration = _cfg.SegmentDurationSeconds }))
                _bus.Publish(EventLevel.Fail, "Recording", _stream, $"recording #{i} missing (interval gap)");

        foreach (var rec in Files.Values)
        {
            if (rec.Status != MediaStatus.Pending || rec.Validating || !present.Contains(rec.Index)) continue;
            // Closed when FFmpeg listed it in the segment list, a successor exists (segment muxer opens N+1 after closing N), or the producer is gone.
            var closed = _listed.Contains(rec.FileName) || rec.Index < max || _producerExited;
            if (!closed) continue;
            if (now - rec.LastChangeAt < _cfg.StableSeconds && !_producerExited) continue;
            rec.Stage = "SIZE STABLE";
            rec.Validating = true;
            var isLast = rec.Index == max;
            var t = Task.Run(() => ValidateAsync(rec, isLast && _producerExited));
            lock (_lock) { _tasks.RemoveAll(x => x.IsCompleted); _tasks.Add(t); }
        }

        var limit = Math.Max(15, _cfg.SegmentDurationSeconds * 3);
        if (!_producerExited && !_inStall && _sinceNew.Elapsed.TotalSeconds > limit)
        { _inStall = true; StallEpisodes++; _bus.Publish(EventLevel.Fail, "Recording", _stream, $"no new recording file for {_sinceNew.Elapsed.TotalSeconds:F0}s (limit {limit:F0}s)"); }
    }

    private async Task ValidateAsync(RecordingRecord rec, bool lastFile)
    {
        await _gate.WaitAsync();
        try
        {
            // 1. Closed? An exclusive-write open fails while any writer (FFmpeg, AV, backup...) still holds it on Windows.
            var opened = false;
            for (int i = 0; i < 20 && !opened; i++)
            {
                try { using var fs = new FileStream(rec.Path, FileMode.Open, FileAccess.Read, FileShare.Read); opened = true; }
                catch (IOException ex) when (!(ex is FileNotFoundException))
                {
                    rec.SharingViolations++;
                    if (rec.SharingViolations == 1)
                    {
                        rec.Lockers = RestartManager.GetLockingProcesses(rec.Path);
                        _bus.Publish(EventLevel.Warn, "Storage", _stream, $"{rec.FileName}: sharing violation after close ({ex.Message.Trim()}) lockers=[{string.Join(", ", rec.Lockers)}]");
                    }
                    await Task.Delay(500);
                }
            }
            if (!opened) { Fail(rec, $"file remained locked ({rec.SharingViolations} sharing violations) lockers=[{string.Join(", ", rec.Lockers)}]"); return; }
            rec.Stage = "CLOSED";

            // 2. Container / streams / duration
            var p = await _probe.ProbeAsync(rec.Path);
            if (!p.Ok) { Fail(rec, "ffprobe: " + p.Error + (p.Error.Contains("moov", StringComparison.OrdinalIgnoreCase) ? " (MP4 not finalized - moov atom missing)" : "")); return; }
            if (p.Format == null || !(p.Format.Contains("mp4") || p.Format.Contains("mov"))) { Fail(rec, $"unexpected container '{p.Format}'"); return; }
            if (!p.HasVideo) { Fail(rec, "no video stream"); return; }
            rec.Duration = p.Duration;
            if (p.Duration <= 0.1) { Fail(rec, $"implausible duration {p.Duration:0.000}s"); return; }
            var firstFile = rec.Index == Files.Keys.DefaultIfEmpty(rec.Index).Min();
            if (!lastFile && !firstFile && Math.Abs(p.Duration - _cfg.SegmentDurationSeconds) > _cfg.DurationToleranceSeconds)
            {
                rec.DurationAnomaly = true;
                _bus.Publish(EventLevel.Warn, "Recording", _stream, $"{rec.FileName} duration {p.Duration:0.00}s outside {_cfg.SegmentDurationSeconds}±{_cfg.DurationToleranceSeconds}s");
            }
            rec.Stage = "FFPROBE VALIDATED";

            // 3. Decode (full or quick)
            var d = await _probe.DecodeAsync(rec.Path, null, _cfg.FullDecode ? null : 2.0);
            if (!d.Ok) { Fail(rec, "decode: " + d.Error); return; }
            rec.Stage = "DECODE VALIDATED";

            // 4. Playback: seek to 25/50/75/~end and decode a short window
            foreach (var f in new[] { 0.25, 0.5, 0.75, 0.95 }.Take(Math.Max(0, _cfg.SeekProbes)))
            {
                var s = await _probe.DecodeAsync(rec.Path, Math.Max(0, p.Duration * f - 0.05), 0.5);
                if (s.Ok) rec.SeekOk++; else { rec.SeekFailed++; Fail(rec, $"seek to {f:P0} failed: {s.Error}"); return; }
            }
            rec.Stage = "PLAYBACK VERIFIED";
            rec.Status = MediaStatus.Valid;
            try { Validated?.Invoke(rec); } catch { }
        }
        catch (FileNotFoundException) { Fail(rec, "file disappeared before validation"); }
        catch (Exception ex) { rec.Status = MediaStatus.Unverified; rec.Error = ex.Message; }
        finally { rec.Validating = false; _gate.Release(); }
    }

    private void Fail(RecordingRecord rec, string why)
    {
        rec.Status = MediaStatus.Invalid; rec.Error = why;
        _bus.Publish(EventLevel.Fail, "Recording", _stream, $"{rec.FileName} INVALID at stage {rec.Stage}: {why}");
    }

    private void BuildTimeline()
    {
        double t = 0;
        foreach (var r in Files.Values.OrderBy(r => r.Index))
        {
            r.TimelineStart = t;
            t += r.Duration > 0 ? r.Duration : _cfg.SegmentDurationSeconds;
        }
    }

    public CoverageReport Coverage(double? wallClock = null) =>
        CoverageReport.Compute(Files.Values.Select(r => (r.Status, r.Duration > 0 ? r.Duration : _cfg.SegmentDurationSeconds)), wallClock);
}
