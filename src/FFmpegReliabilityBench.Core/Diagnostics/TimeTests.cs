using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Runner;

namespace FFmpegReliabilityBench.Core.Diagnostics;

/// <summary>Module D - host clock vs NTP, camera clock vs host (ONVIF). UTC only; no evidence → UNKNOWN, never PASS.</summary>
public static class TimeTests
{
    private const string Cat = "Time";

    public static async Task<List<TestResult>> RunAsync(TimeCfg cfg, CancellationToken ct)
    {
        var r = new List<TestResult>();
        if (!cfg.Enabled)
        {
            r.Add(TestResult.Make("TIME-001", Cat, "Host clock vs NTP", TestStatus.Unknown, "time tests disabled (time.enabled=false)"));
            r.Add(TestResult.Make("TIME-002", Cat, "Camera clock vs host", TestStatus.Unknown, "time tests disabled"));
            return r;
        }
        // TIME-001: our own SNTP measurement (multiple samples, median/max abs offset), independent of what w32time claims
        var samples = new List<(double Offset, double Delay)>();
        string? err = null;
        for (int i = 0; i < cfg.SampleCount; i++)
        {
            try { samples.Add(await SntpAsync(cfg.NtpServer, ct)); } catch (Exception ex) { err = ex.Message; }
            await Task.Delay(200, ct);
        }
        double? hostOffset = null;
        if (samples.Count == 0) r.Add(TestResult.Make("TIME-001", Cat, "Host clock vs NTP", TestStatus.Unknown, $"NTP server {cfg.NtpServer} unreachable: {err}"));
        else
        {
            var offs = samples.Select(s => s.Offset).OrderBy(x => x).ToList();
            var med = offs[offs.Count / 2]; var maxAbs = offs.Max(Math.Abs);
            hostOffset = med;
            r.Add(TestResult.Make("TIME-001", Cat, "Host clock vs NTP", Math.Abs(med) > cfg.MaxClockDifferenceMs ? TestStatus.Fail : maxAbs > cfg.MaxClockDifferenceMs ? TestStatus.Warn : TestStatus.Pass,
                $"{cfg.NtpServer}: median offset {med:+0;-0} ms, max |offset| {maxAbs:F0} ms, median RTT {samples.Select(s => s.Delay).OrderBy(x => x).ElementAt(samples.Count / 2):F0} ms ({samples.Count} samples, limit ±{cfg.MaxClockDifferenceMs} ms)",
                new() { ["medianOffsetMs"] = med, ["maxAbsOffsetMs"] = maxAbs, ["samples"] = samples.Count }));
        }

        if (OperatingSystem.IsWindows())
        {
            var st = await ProcessUtil.RunAsync("w32tm", new[] { "/query", "/status" }, TimeSpan.FromSeconds(15), ct);
            var src = await ProcessUtil.RunAsync("w32tm", new[] { "/query", "/source" }, TimeSpan.FromSeconds(15), ct);
            var text = st.StdOut.Replace("\r", "");
            string F(string key) => Regex.Match(text, key + @":\s*(.*)").Groups[1].Value.Trim();
            r.Add(TestResult.Make("TIME-003", Cat, "Windows Time service", st.ExitCode == 0 ? TestStatus.Pass : TestStatus.Warn,
                st.ExitCode == 0 ? $"source={src.StdOut.Trim()} stratum={F("Stratum")} lastSync={F("Last Successful Sync Time")} (w32time state alone is not proof of ±{cfg.MaxClockDifferenceMs} ms; see TIME-001)" : "w32tm /query /status failed: " + st.StdErr.Trim()));
        }
        else r.Add(TestResult.Make("TIME-003", Cat, "Windows Time service", TestStatus.Unknown, "w32tm only available on Windows"));

        // TIME-002: camera UTC via ONVIF GetSystemDateAndTime
        if (string.IsNullOrWhiteSpace(cfg.OnvifUrl))
            r.Add(TestResult.Make("TIME-002", Cat, "Camera clock vs host", TestStatus.Unknown, "camera clock not verifiable: time.onvifUrl not configured (PTS continuity does not prove camera clock accuracy)"));
        else
        {
            try
            {
                var (camUtc, ntp, rtt) = await OnvifTimeAsync(cfg.OnvifUrl, ct);
                var hostNow = DateTime.UtcNow;
                var diff = (camUtc - hostNow).TotalMilliseconds; // camera reports whole seconds → ±1000 ms quantization
                var resolutionNote = "ONVIF reports whole seconds: resolution ±1 s, so a ±500 ms requirement cannot be proven PASS by ONVIF alone";
                var status = Math.Abs(diff) > cfg.MaxClockDifferenceMs + 1000 ? TestStatus.Fail : Math.Abs(diff) <= 1000 && cfg.MaxClockDifferenceMs < 1000 ? TestStatus.Warn : TestStatus.Pass;
                r.Add(TestResult.Make("TIME-002", Cat, "Camera clock vs host", status,
                    $"camera UTC {camUtc:O} vs host {hostNow:O}: {diff:+0;-0} ms (RTT {rtt:F0} ms, camera time type {ntp}). {resolutionNote}" + (hostOffset is double h ? $"; host vs NTP {h:+0;-0} ms → camera vs NTP ≈ {diff + h:+0;-0} ms" : ""),
                    new() { ["cameraMinusHostMs"] = diff }));
            }
            catch (Exception ex) { r.Add(TestResult.Make("TIME-002", Cat, "Camera clock vs host", TestStatus.Unknown, "ONVIF GetSystemDateAndTime failed: " + ex.Message)); }
        }
        return r;
    }

    /// <summary>Minimal SNTP (RFC 4330). Returns (offset ms = server - local, round-trip delay ms).</summary>
    public static async Task<(double Offset, double Delay)> SntpAsync(string server, CancellationToken ct)
    {
        var addr = (await Dns.GetHostAddressesAsync(server, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Connect(addr, 123);
        var req = new byte[48]; req[0] = 0x1B; // LI=0 VN=3 Mode=3
        var t1 = DateTime.UtcNow;
        await udp.SendAsync(req, ct);
        var res = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
        var t4 = DateTime.UtcNow;
        var b = res.Buffer;
        if (b.Length < 48) throw new InvalidDataException("short NTP reply");
        DateTime Ts(int o)
        {
            ulong sec = (ulong)b[o] << 24 | (ulong)b[o + 1] << 16 | (ulong)b[o + 2] << 8 | b[o + 3];
            ulong frac = (ulong)b[o + 4] << 24 | (ulong)b[o + 5] << 16 | (ulong)b[o + 6] << 8 | b[o + 7];
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(sec * 1000.0 + frac * 1000.0 / 0x1_0000_0000);
        }
        var t2 = Ts(32); var t3 = Ts(40);
        var offset = ((t2 - t1) + (t3 - t4)).TotalMilliseconds / 2;
        var delay = ((t4 - t1) - (t3 - t2)).TotalMilliseconds;
        return (offset, delay);
    }

    /// <summary>ONVIF Device GetSystemDateAndTime (usually allowed without authentication).</summary>
    public static async Task<(DateTime Utc, string DateTimeType, double RttMs)> OnvifTimeAsync(string url, CancellationToken ct)
    {
        const string body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><GetSystemDateAndTime xmlns=\"http://www.onvif.org/ver10/device/wsdl\"/></s:Body></s:Envelope>";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/soap+xml"), ct);
        var rtt = sw.Elapsed.TotalMilliseconds;
        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var utc = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "UTCDateTime") ?? throw new InvalidDataException("no UTCDateTime in response");
        int V(string parent, string name) => int.Parse(utc.Descendants().First(e => e.Name.LocalName == parent).Elements().First(e => e.Name.LocalName == name).Value, CultureInfo.InvariantCulture);
        var dt = new DateTime(V("Date", "Year"), V("Date", "Month"), V("Date", "Day"), V("Time", "Hour"), V("Time", "Minute"), V("Time", "Second"), DateTimeKind.Utc);
        var type = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "DateTimeType")?.Value ?? "?";
        return (dt.AddMilliseconds(rtt / 2), type, rtt);
    }
}
