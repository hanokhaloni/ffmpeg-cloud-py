using System.Diagnostics;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Validation;

/// <summary>Consumes the playlist over the real HTTP endpoint (IIS/proxy): status codes, MIME types, content length, latency, decodability.</summary>
public sealed class HttpHlsConsumer
{
    private readonly string _url, _stream, _scratch; private readonly FFprobeRunner _probe; private readonly EventBus _bus;
    private readonly string? _localDir;
    public int HashMatches, HashMismatches, NotComparable, NotYetAvailable;
    private bool _playlistSeen;
    public string Url => _url;
    private readonly HashSet<long> _seen = new(); private CancellationTokenSource? _cts; private Task? _loop;
    public int PlaylistRequests, PlaylistFailures, SegmentRequests, SegmentFailures, LengthMismatches, DecodeFailures, MimeWarnings;
    public List<double> LatenciesMs { get; } = new();
    public List<string> Evidence { get; } = new();
    public long Bytes;

    /// <param name="localDir">If set, every segment served over HTTP is compared byte-for-byte (SHA-256) with the file the local
    /// monitor validates, instead of decoding it a second time.</param>
    public HttpHlsConsumer(string url, string stream, FFprobeRunner probe, EventBus bus, string scratch, string? localDir = null)
    { _url = url; _stream = stream; _probe = probe; _bus = bus; _scratch = scratch; _localDir = localDir; Directory.CreateDirectory(scratch); }

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            while (!_cts.IsCancellationRequested)
            {
                try { await PollAsync(http, _cts.Token); } catch (OperationCanceledException) { break; } catch (Exception ex) { Note($"poll error: {ex.Message}"); }
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { break; }
            }
        });
    }

    public async Task StopAsync() { _cts?.Cancel(); if (_loop != null) try { await _loop; } catch { } }

    private async Task PollAsync(HttpClient http, CancellationToken ct)
    {
        Interlocked.Increment(ref PlaylistRequests);
        var sw = Stopwatch.StartNew();
        using var resp = await http.GetAsync(_url, ct);
        lock (LatenciesMs) LatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
        if (!resp.IsSuccessStatusCode)
        {
            if (!_playlistSeen && resp.StatusCode == System.Net.HttpStatusCode.NotFound) { Interlocked.Increment(ref NotYetAvailable); return; } // producer not started yet
            Interlocked.Increment(ref PlaylistFailures); Note($"playlist HTTP {(int)resp.StatusCode}"); return;
        }
        _playlistSeen = true;
        var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!ctype.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)) { Interlocked.Increment(ref MimeWarnings); Note($"playlist Content-Type '{ctype}' (expected application/vnd.apple.mpegurl)"); }
        var pl = HlsPlaylist.Parse(await resp.Content.ReadAsStringAsync(ct));
        foreach (var s in pl.Segments)
        {
            if (!_seen.Add(s.Sequence)) continue;
            var segUrl = new Uri(new Uri(_url), s.Uri);
            Interlocked.Increment(ref SegmentRequests);
            var sw2 = Stopwatch.StartNew();
            using var r = await http.GetAsync(segUrl, ct);
            if (!r.IsSuccessStatusCode) { Interlocked.Increment(ref SegmentFailures); Note($"segment {s.Uri}: HTTP {(int)r.StatusCode}"); continue; }
            var bytes = await r.Content.ReadAsByteArrayAsync(ct);
            lock (LatenciesMs) LatenciesMs.Add(sw2.Elapsed.TotalMilliseconds);
            Interlocked.Add(ref Bytes, bytes.Length);
            if (r.Content.Headers.ContentLength is long cl && cl != bytes.Length) { Interlocked.Increment(ref LengthMismatches); Note($"segment {s.Uri}: Content-Length {cl} != received {bytes.Length}"); }
            if (_localDir != null)
            {
                var local = Path.Combine(_localDir, s.Uri);
                try
                {
                    byte[] lb;
                    using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { lb = new byte[fs.Length]; fs.ReadExactly(lb); }
                    if (System.Security.Cryptography.SHA256.HashData(lb).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(bytes))) Interlocked.Increment(ref HashMatches);
                    else { Interlocked.Increment(ref HashMismatches); Note($"segment {s.Uri}: HTTP bytes differ from the file on disk"); }
                }
                catch (IOException) { Interlocked.Increment(ref NotComparable); }
                continue;
            }
            if (pl.MapUri != null) continue; // fMP4 fragments need the init segment; covered by the local monitor
            var tmp = Path.Combine(_scratch, $"http_{_stream}_{s.Sequence}.ts");
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            var d = await _probe.DecodeAsync(tmp, ct: ct);
            try { File.Delete(tmp); } catch { }
            if (!d.Ok) { Interlocked.Increment(ref DecodeFailures); Note($"segment {s.Uri}: decode failed: {d.Error}"); }
        }
    }

    private void Note(string m)
    {
        lock (Evidence) if (Evidence.Count < 50) Evidence.Add(m);
        _bus.PublishThrottled("http" + _stream + m.GetHashCode(), 10, EventLevel.Warn, "HTTP", _stream, m);
    }
}
