using System.Diagnostics;
using System.Security.Cryptography;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Validation;

namespace FFmpegReliabilityBench.Core.Diagnostics;

/// <summary>Module B - tests the exact directories FFmpeg will write to, under the same identity, with a media-like workload.</summary>
public static class FilesystemTests
{
    private const string Cat = "Filesystem";

    public static async Task<List<TestResult>> RunAsync(string label, string dir, StorageCfg cfg, CancellationToken ct)
    {
        var r = new List<TestResult>();
        string Id(int n) => $"FS-{n:D3}";
        var suffix = $" ({label})";
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); r.Add(TestResult.Make(Id(1), Cat, "Directory exists" + suffix, TestStatus.Warn, $"{dir} did not exist and was created")); }
            catch (Exception ex) { r.Add(TestResult.Make(Id(1), Cat, "Directory exists" + suffix, TestStatus.Fail, $"{dir} missing and cannot be created: {ex.Message}")); return r; }
        }
        else r.Add(TestResult.Make(Id(1), Cat, "Directory exists" + suffix, TestStatus.Pass, dir));

        var work = Path.Combine(dir, $".frb_fs_{Guid.NewGuid():N}");
        try
        {
            // FS-009 subdirectory
            try { Directory.CreateDirectory(work); r.Add(Ok(Id(9), "Create subdirectory" + suffix, work)); }
            catch (Exception ex) { r.Add(Bad(Id(9), "Create subdirectory" + suffix, ex)); return r; }

            // FS-002..008 basic lifecycle with content verification
            var payload = RandomNumberGenerator.GetBytes(1024 * 1024);
            var f = Path.Combine(work, "basic.tmp"); var f2 = Path.Combine(work, "basic.renamed");
            var sw = Stopwatch.StartNew();
            try
            {
                await using (var fs = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    r.Add(Ok(Id(2), "Create file" + suffix, $"{sw.Elapsed.TotalMilliseconds:F1} ms"));
                    sw.Restart(); await fs.WriteAsync(payload, ct); r.Add(Ok(Id(3), "Write file" + suffix, $"1 MB in {sw.Elapsed.TotalMilliseconds:F1} ms"));
                    sw.Restart(); fs.Flush(flushToDisk: true);
                    r.Add(TestResult.Make(Id(4), Cat, "Flush to disk" + suffix, TestStatus.Pass, $"Flush(flushToDisk:true) {sw.Elapsed.TotalMilliseconds:F1} ms. Note: on SMB/NAS this proves server acknowledgment at best, not physical durability.", new() { ["flushMs"] = sw.Elapsed.TotalMilliseconds }));
                }
                sw.Restart(); var back = await File.ReadAllBytesAsync(f, ct);
                r.Add(Ok(Id(5), "Read file" + suffix, $"{back.Length} bytes in {sw.Elapsed.TotalMilliseconds:F1} ms"));
                var same = SHA256.HashData(back).AsSpan().SequenceEqual(SHA256.HashData(payload));
                r.Add(TestResult.Make(Id(6), Cat, "Verify content (SHA-256)" + suffix, same ? TestStatus.Pass : TestStatus.Fail, same ? "content matches" : "CONTENT MISMATCH - data corruption on read-back"));
                sw.Restart(); File.Move(f, f2); r.Add(Ok(Id(7), "Rename file" + suffix, $"{sw.Elapsed.TotalMilliseconds:F1} ms"));
                sw.Restart(); File.Delete(f2); r.Add(Ok(Id(8), "Delete file" + suffix, $"{sw.Elapsed.TotalMilliseconds:F1} ms"));
            }
            catch (UnauthorizedAccessException ex) { r.Add(TestResult.Make("FS-ERR", Cat, "Basic access" + suffix, TestStatus.Fail, "access denied: " + ex.Message)); return r; }
            catch (IOException ex) { r.Add(TestResult.Make("FS-ERR", Cat, "Basic access" + suffix, TestStatus.Fail, ex.GetType().Name + ": " + ex.Message)); return r; }

            // FS-011 small-file latency (create+write+close), P95/P99 - media workloads are many small files, not one big one
            var small = RandomNumberGenerator.GetBytes(64 * 1024);
            var lat = new List<double>(); var closeLat = new List<double>(); var renLat = new List<double>(); var delLat = new List<double>();
            for (int i = 0; i < cfg.SmallFileCount; i++)
            {
                var p = Path.Combine(work, $"s{i}.tmp");
                sw.Restart();
                var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await fs.WriteAsync(small, ct);
                var c0 = sw.Elapsed.TotalMilliseconds; await fs.DisposeAsync(); closeLat.Add(sw.Elapsed.TotalMilliseconds - c0);
                lat.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); File.Move(p, p + ".r"); renLat.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); File.Delete(p + ".r"); delLat.Add(sw.Elapsed.TotalMilliseconds);
            }
            r.Add(Latency(Id(11), "Small-file (64 KB) create/write/close" + suffix, lat, cfg));
            r.Add(Latency(Id(12), "File close latency" + suffix, closeLat, cfg));
            r.Add(Latency(Id(13), "Rename latency" + suffix, renLat, cfg));
            r.Add(Latency(Id(14), "Delete latency" + suffix, delLat, cfg));

            // FS-015 media-segment sized writes with durable flush; sustained throughput
            var seg = RandomNumberGenerator.GetBytes(cfg.SegmentFileSizeMb * 1024 * 1024);
            var segLat = new List<double>(); var flushLat = new List<double>(); var total = Stopwatch.StartNew();
            for (int i = 0; i < cfg.SegmentFileCount; i++)
            {
                var p = Path.Combine(work, $"seg{i}.tmp");
                sw.Restart();
                await using (var fs = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
                {
                    await fs.WriteAsync(seg, ct);
                    var t0 = sw.Elapsed.TotalMilliseconds; fs.Flush(true); flushLat.Add(sw.Elapsed.TotalMilliseconds - t0);
                }
                segLat.Add(sw.Elapsed.TotalMilliseconds);
            }
            var mbps = cfg.SegmentFileCount * cfg.SegmentFileSizeMb / total.Elapsed.TotalSeconds;
            var segRes = Latency(Id(15), $"Segment ({cfg.SegmentFileSizeMb} MB) write+flush+close" + suffix, segLat, cfg, cfg.LatencyFailMs * 4);
            segRes.Metrics["writeMBps"] = mbps;
            r.Add(segRes);
            r.Add(Latency(Id(16), "Durable flush latency" + suffix, flushLat, cfg, cfg.LatencyFailMs * 2));

            // FS-017 read immediately after close + sequential read throughput
            sw.Restart(); long read = 0;
            for (int i = 0; i < cfg.SegmentFileCount; i++) read += (await File.ReadAllBytesAsync(Path.Combine(work, $"seg{i}.tmp"), ct)).Length;
            var rmbps = read / 1048576.0 / sw.Elapsed.TotalSeconds;
            r.Add(TestResult.Make(Id(17), Cat, "Sequential read after close" + suffix, TestStatus.Pass, $"{rmbps:F0} MB/s (may be served from cache)", new() { ["readMBps"] = rmbps }));

            // FS-010 concurrent writers + readers (playback reading while recording writes)
            var errors = 0; var conLat = new System.Collections.Concurrent.ConcurrentBag<double>();
            var writers = Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
            {
                for (int k = 0; k < 5; k++)
                {
                    var s2 = Stopwatch.StartNew();
                    try
                    {
                        var p = Path.Combine(work, $"c{w}_{k}.tmp");
                        await File.WriteAllBytesAsync(p, small, ct);
                        await using var rd = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var buf = new byte[small.Length]; await rd.ReadExactlyAsync(buf, ct);
                        if (!buf.AsSpan().SequenceEqual(small)) Interlocked.Increment(ref errors);
                    }
                    catch { Interlocked.Increment(ref errors); }
                    conLat.Add(s2.Elapsed.TotalMilliseconds);
                }
            }, ct)).ToList();
            var readers = Enumerable.Range(0, 4).Select(rd => Task.Run(async () =>
            {
                for (int k = 0; k < 5; k++)
                    try { await using var fs = new FileStream(Path.Combine(work, $"seg{(rd + k) % cfg.SegmentFileCount}.tmp"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); await fs.CopyToAsync(Stream.Null, ct); }
                    catch { Interlocked.Increment(ref errors); }
            }, ct)).ToList();
            await Task.WhenAll(writers.Concat(readers));
            var cl = Latency(Id(10), "Concurrent writers/readers (8W+4R)" + suffix, conLat.ToList(), cfg);
            r.Add(errors > 0 ? TestResult.Make(Id(10), Cat, cl.Name, TestStatus.Fail, $"{errors} errors under concurrency; " + cl.Diagnosis, cl.Metrics) : cl);

            // FS-018 sharing semantics: a reader must be able to open a file another process is writing with FILE_SHARE_READ
            try
            {
                var p = Path.Combine(work, "share.tmp");
                await using var w = new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                await w.WriteAsync(small, ct); await w.FlushAsync(ct);
                await using var rd = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var exclusiveBlocked = false;
                try { using var ex = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { exclusiveBlocked = true; }
                r.Add(TestResult.Make(Id(18), Cat, "Sharing semantics" + suffix, TestStatus.Pass,
                    $"shared read while writing OK; open-without-write-sharing while a writer is active {(exclusiveBlocked ? "is blocked (Windows semantics: lock detection works)" : "is NOT blocked (POSIX/SMB semantics: 'file closed' detection falls back to size stability + successor file)")}"));
            }
            catch (Exception ex) { r.Add(TestResult.Make(Id(18), Cat, "Sharing semantics" + suffix, TestStatus.Fail, "reader could not open a file being written: " + ex.Message)); }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
        return r;
    }

    private static TestResult Ok(string id, string name, string d) => TestResult.Make(id, Cat, name, TestStatus.Pass, d);
    private static TestResult Bad(string id, string name, Exception ex) => TestResult.Make(id, Cat, name, TestStatus.Fail, ex.Message);

    private static TestResult Latency(string id, string name, IReadOnlyList<double> ms, StorageCfg cfg, double? failMs = null)
    {
        var p50 = Percentile.Of(ms, 50); var p95 = Percentile.Of(ms, 95); var p99 = Percentile.Of(ms, 99); var max = ms.Count > 0 ? ms.Max() : 0;
        var fail = failMs ?? cfg.LatencyFailMs; var warn = failMs.HasValue ? fail / 4 : cfg.LatencyWarnMs;
        var st = p99 > fail ? TestStatus.Fail : p99 > warn ? TestStatus.Warn : TestStatus.Pass;
        return TestResult.Make(id, Cat, name, st, $"n={ms.Count} p50={p50:F1} p95={p95:F1} p99={p99:F1} max={max:F1} ms (warn>{warn:F0}, fail>{fail:F0})",
            new() { ["p50Ms"] = p50, ["p95Ms"] = p95, ["p99Ms"] = p99, ["maxMs"] = max });
    }
}
