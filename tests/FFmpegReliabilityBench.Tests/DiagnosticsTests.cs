using System.Net;
using System.Net.Sockets;
using System.Text;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Diagnostics;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

public class FilesystemDiagnosticsTests
{
    [Fact]
    public async Task Writable_local_directory_passes_all_storage_tests()
    {
        var dir = TestUtil.TempDir();
        var r = await FilesystemTests.RunAsync("T", dir, new StorageCfg { SmallFileCount = 10, SegmentFileCount = 3, SegmentFileSizeMb = 1 }, CancellationToken.None);
        Assert.DoesNotContain(r, x => x.Status == TestStatus.Fail);
        foreach (var id in new[] { "FS-001", "FS-002", "FS-003", "FS-004", "FS-005", "FS-006", "FS-007", "FS-008", "FS-009", "FS-010", "FS-011", "FS-015", "FS-016", "FS-017", "FS-018" })
            Assert.Contains(r, x => x.Id == id);
        Assert.Equal(TestStatus.Pass, r.Single(x => x.Id == "FS-006").Status); // SHA-256 read-back
        Assert.Empty(Directory.GetFileSystemEntries(dir));                    // cleans up after itself
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Missing_directory_is_created_and_reported()
    {
        var dir = Path.Combine(TestUtil.TempDir(), "does", "not", "exist");
        var r = await FilesystemTests.RunAsync("T", dir, new StorageCfg { SmallFileCount = 2, SegmentFileCount = 1, SegmentFileSizeMb = 1 }, CancellationToken.None);
        Assert.Equal(TestStatus.Warn, r.Single(x => x.Id == "FS-001").Status);
    }
}

/// <summary>Fake RTSP server: OPTIONS → 200, DESCRIBE → 401 (camera alive, credentials required).</summary>
[Collection("ffmpeg")]
public class NetworkDiagnosticsTests
{
    [Fact]
    public async Task Rtsp_checks_distinguish_reachable_endpoint_from_openable_stream()
    {
        if (!TestUtil.HaveFfmpeg) return;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c; try { c = await listener.AcceptTcpClientAsync(cts.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var cl = c; var s = cl.GetStream(); var buf = new byte[8192]; var acc = new StringBuilder();
                    try
                    {
                        while (true)
                        {
                            var n = await s.ReadAsync(buf); if (n == 0) return;
                            acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                            int end;
                            while ((end = acc.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                            {
                                var req = acc.ToString()[..end]; acc.Remove(0, end + 4);
                                var cseq = req.Split("\r\n").FirstOrDefault(l => l.StartsWith("CSeq", StringComparison.OrdinalIgnoreCase))?.Split(':')[1].Trim() ?? "1";
                                var resp = req.StartsWith("OPTIONS")
                                    ? $"RTSP/1.0 200 OK\r\nCSeq: {cseq}\r\nPublic: OPTIONS, DESCRIBE, SETUP, PLAY\r\n\r\n"
                                    : $"RTSP/1.0 401 Unauthorized\r\nCSeq: {cseq}\r\nWWW-Authenticate: Basic realm=\"cam\"\r\n\r\n";
                                await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
                            }
                        }
                    }
                    catch { }
                });
            }
        });
        var cfg = new BenchConfig(); cfg.Source.Url = $"rtsp://127.0.0.1:{port}/stream";
        var r = await NetworkTests.RunAsync(cfg, CancellationToken.None);
        cts.Cancel(); listener.Stop();

        Assert.Equal(TestStatus.Pass, r.Single(x => x.Id == "NET-001").Status);
        Assert.Equal(TestStatus.Pass, r.Single(x => x.Id == "NET-002").Status);
        Assert.Equal(TestStatus.Pass, r.Single(x => x.Id == "NET-003").Status);
        Assert.Contains("401", r.Single(x => x.Id == "NET-004").Diagnosis);
        Assert.Equal(TestStatus.Fail, r.Single(x => x.Id == "NET-005").Status); // ffprobe cannot open the stream
    }

    [Fact]
    public async Task Synthetic_source_skips_camera_checks()
    {
        var r = await NetworkTests.RunAsync(new BenchConfig(), CancellationToken.None);
        Assert.Single(r);
        Assert.Equal("NET-000", r[0].Id);
    }
}

public class TimeDiagnosticsTests
{
    [Fact]
    public async Task Disabled_time_checks_are_unknown_never_pass()
    {
        var r = await TimeTests.RunAsync(new TimeCfg { Enabled = false }, CancellationToken.None);
        Assert.All(r, x => Assert.Equal(TestStatus.Unknown, x.Status));
    }

    [Theory]
    [InlineData(0, TestStatus.Warn)]   // within ONVIF's 1 s resolution: cannot prove ±500 ms
    [InlineData(-5, TestStatus.Fail)]  // camera 5 s behind
    public async Task Onvif_camera_clock_is_compared_with_host(int cameraOffsetSeconds, TestStatus expected)
    {
        var url = $"http://127.0.0.1:{TestUtil.FreePort()}/onvif/device_service/";
        using var http = new HttpListener(); http.Prefixes.Add(url); http.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await http.GetContextAsync();
                var t = DateTime.UtcNow.AddSeconds(cameraOffsetSeconds);
                var xml = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:tt=\"http://www.onvif.org/ver10/schema\" xmlns:tds=\"http://www.onvif.org/ver10/device/wsdl\"><s:Body><tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>" +
                          $"<tt:DateTimeType>NTP</tt:DateTimeType><tt:DaylightSavings>false</tt:DaylightSavings><tt:UTCDateTime><tt:Time><tt:Hour>{t.Hour}</tt:Hour><tt:Minute>{t.Minute}</tt:Minute><tt:Second>{t.Second}</tt:Second></tt:Time>" +
                          $"<tt:Date><tt:Year>{t.Year}</tt:Year><tt:Month>{t.Month}</tt:Month><tt:Day>{t.Day}</tt:Day></tt:Date></tt:UTCDateTime></tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse></s:Body></s:Envelope>";
                var b = Encoding.UTF8.GetBytes(xml);
                ctx.Response.ContentType = "application/soap+xml"; ctx.Response.ContentLength64 = b.Length;
                await ctx.Response.OutputStream.WriteAsync(b); ctx.Response.Close();
            }
            catch { }
        });
        var r = await TimeTests.RunAsync(new TimeCfg { Enabled = true, NtpServer = "127.0.0.1", SampleCount = 1, OnvifUrl = url }, CancellationToken.None);
        var cam = r.Single(x => x.Id == "TIME-002");
        Assert.Equal(expected, cam.Status);
        Assert.InRange(cam.Metrics["cameraMinusHostMs"], cameraOffsetSeconds * 1000 - 1500, cameraOffsetSeconds * 1000 + 1500);
        Assert.Equal(TestStatus.Unknown, r.Single(x => x.Id == "TIME-001").Status); // no NTP server on localhost
    }
}

public class CorrelationAndEventTests
{
    [Fact]
    public void Failures_are_correlated_with_nearby_infrastructure_events_as_hypotheses()
    {
        var ev = new List<RuntimeEvent>
        {
            new(DateTime.UtcNow, 10.0, EventLevel.Warn, "Storage", "*", "NAS write latency 850 ms"),
            new(DateTime.UtcNow, 11.0, EventLevel.Fail, "Recording", "cam1", "no new recording file for 15s"),
            new(DateTime.UtcNow, 60.0, EventLevel.Fail, "HLS", "cam1", "playlist has not advanced"),
            new(DateTime.UtcNow, 61.0, EventLevel.Fail, "HLS", "cam2", "playlist has not advanced"),
        };
        var h = Correlation.Analyze(ev);
        Assert.Contains(h, x => x.Text.StartsWith("HYPOTHESIS") && x.Text.Contains("Storage") && x.Symptom.Contains("recording"));
        Assert.Contains(h, x => x.Text.Contains("shared resource"));
    }

    [Fact]
    public void Throttled_events_are_rate_limited()
    {
        var bus = new EventBus();
        for (int i = 0; i < 50; i++) bus.PublishThrottled("k", 60, EventLevel.Warn, "Process", "s", "flood");
        Assert.Single(bus.Snapshot());
    }
}
