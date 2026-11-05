using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Diagnostics;

/// <summary>Module C - camera connectivity (not just ping): DNS, TCP, RTSP OPTIONS/DESCRIBE, stream open via ffprobe.</summary>
public static class NetworkTests
{
    private const string Cat = "Network";

    public static async Task<List<TestResult>> RunAsync(BenchConfig cfg, CancellationToken ct)
    {
        var r = new List<TestResult>();
        if (!cfg.Source.Url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            r.Add(TestResult.Make("NET-000", Cat, "Camera connectivity", TestStatus.Pass, $"source is '{CommandText.Mask(cfg.Source.Url)}' (no camera/network in the path - infrastructure isolation mode)"));
            return r;
        }
        var uri = new Uri(cfg.Source.Url);
        var port = uri.Port > 0 ? uri.Port : 554;
        IPAddress[] addrs;
        var sw = Stopwatch.StartNew();
        try { addrs = await Dns.GetHostAddressesAsync(uri.Host, ct); r.Add(TestResult.Make("NET-001", Cat, "DNS resolution", TestStatus.Pass, $"{uri.Host} → {string.Join(", ", addrs.Select(a => a.ToString()))} in {sw.Elapsed.TotalMilliseconds:F0} ms")); }
        catch (Exception ex) { r.Add(TestResult.Make("NET-001", Cat, "DNS resolution", TestStatus.Fail, ex.Message)); return r; }

        var connectMs = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            using var c = new TcpClient(); sw.Restart();
            try { await c.ConnectAsync(addrs[0], port, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct); connectMs.Add(sw.Elapsed.TotalMilliseconds); }
            catch (Exception ex) { r.Add(TestResult.Make("NET-002", Cat, "TCP connectivity", TestStatus.Fail, $"{addrs[0]}:{port}: {ex.Message}")); return r; }
        }
        r.Add(TestResult.Make("NET-002", Cat, "TCP connectivity", TestStatus.Pass, $"{addrs[0]}:{port} reachable"));
        r.Add(TestResult.Make("NET-006", Cat, "Connection latency", connectMs.Max() > 200 ? TestStatus.Warn : TestStatus.Pass,
            $"TCP connect avg {connectMs.Average():F1} ms, max {connectMs.Max():F1} ms", new() { ["avgMs"] = connectMs.Average(), ["maxMs"] = connectMs.Max() }));

        foreach (var method in new[] { "OPTIONS", "DESCRIBE" })
        {
            try
            {
                using var c = new TcpClient();
                await c.ConnectAsync(addrs[0], port, ct);
                var s = c.GetStream(); s.ReadTimeout = 5000;
                var safeUrl = $"rtsp://{uri.Host}:{port}{uri.PathAndQuery}";
                var req = $"{method} {safeUrl} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: FFmpegReliabilityBench\r\n" + (method == "DESCRIBE" ? "Accept: application/sdp\r\n" : "") + "\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(req), ct);
                var buf = new byte[4096]; sw.Restart();
                var n = await s.ReadAsync(buf, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                var status = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n").FirstOrDefault() ?? "";
                var id = method == "OPTIONS" ? "NET-003" : "NET-004";
                var st = status.Contains(" 200 ") ? TestStatus.Pass : status.Contains(" 401 ") ? TestStatus.Pass : TestStatus.Warn;
                r.Add(TestResult.Make(id, Cat, $"RTSP {method}", st, $"'{status}' in {sw.Elapsed.TotalMilliseconds:F0} ms" + (status.Contains(" 401 ") ? " (authentication required - endpoint alive; credentials verified by NET-005)" : "")));
            }
            catch (Exception ex) { r.Add(TestResult.Make(method == "OPTIONS" ? "NET-003" : "NET-004", Cat, $"RTSP {method}", TestStatus.Fail, ex.Message)); }
        }

        // NET-005: actually open the stream with the configured transport
        sw.Restart();
        var probe = await ProcessUtil.RunAsync(cfg.Ffmpeg.Ffprobe, new[] { "-v", "error", "-rtsp_transport", cfg.Source.Transport, "-show_entries", "stream=codec_type,codec_name,width,height,avg_frame_rate", "-of", "compact", cfg.Source.Url }, TimeSpan.FromSeconds(20), ct);
        r.Add(TestResult.Make("NET-005", Cat, $"Stream availability (RTSP/{cfg.Source.Transport.ToUpperInvariant()})", probe.ExitCode == 0 ? TestStatus.Pass : TestStatus.Fail,
            probe.ExitCode == 0 ? $"opened in {sw.Elapsed.TotalSeconds:F1}s: {CommandText.Mask(probe.StdOut.Replace('\n', ' ').Trim())}" : CommandText.Mask(probe.StdErr.Trim()), new() { ["openSeconds"] = sw.Elapsed.TotalSeconds }));
        return r;
    }
}

/// <summary>TCP and NIC counters sampled throughout the run (retransmissions/resets/errors/discards), via .NET (Windows + Linux).</summary>
public sealed class NetCounters
{
    public long SegmentsResent { get; init; }
    public long SegmentsSent { get; init; }
    public long ResetsSent { get; init; }
    public long ResetConnections { get; init; }
    public long InErrors { get; init; }
    public long InDiscards { get; init; }
    public long OutErrors { get; init; }
    public long OutDiscards { get; init; }
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }

    private static long T(Func<long> f) { try { return f(); } catch { return 0; } } // some counters are not supported on every OS

    public static NetCounters? Sample()
    {
        try
        {
            var tcp = IPGlobalProperties.GetIPGlobalProperties().GetTcpIPv4Statistics();
            long ie = 0, id = 0, oe = 0, od = 0, br = 0, bs = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceStatistics st;
                try { st = nic.GetIPStatistics(); } catch { continue; }
                ie += T(() => st.IncomingPacketsWithErrors); id += T(() => st.IncomingPacketsDiscarded);
                oe += T(() => st.OutgoingPacketsWithErrors); od += T(() => st.OutgoingPacketsDiscarded);
                br += T(() => st.BytesReceived); bs += T(() => st.BytesSent);
            }
            return new NetCounters
            {
                SegmentsResent = T(() => tcp.SegmentsResent), SegmentsSent = T(() => tcp.SegmentsSent), ResetsSent = T(() => tcp.ResetsSent),
                ResetConnections = T(() => tcp.ResetConnections), InErrors = ie, InDiscards = id, OutErrors = oe, OutDiscards = od, BytesReceived = br, BytesSent = bs
            };
        }
        catch { return null; }
    }
}
