using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FFmpegReliabilityBench.Core.Lab;

/// <summary>
/// Loopback-only HTTP server for the run directory (HLS + recordings), standing in for IIS/nginx.
/// Files are read with delete-sharing so serving never blocks FFmpeg's rename/delete on Windows; Range requests are supported.
/// </summary>
public sealed class LabHttpServer : IAsyncDisposable
{
    private WebApplication? _app; private string _root = "";
    private readonly ConcurrentQueue<string> _errors = new();
    public string BaseUrl { get; private set; } = "";
    public bool Running { get; private set; }
    public long Requests, NotFound, ServerErrors, BytesServed;
    public IReadOnlyList<string> RecentErrors => _errors.ToArray();

    private sealed class NoConsoleLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public async Task StartAsync(string root, string address, int port, CancellationToken ct)
    {
        _root = Path.GetFullPath(root);
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = _root, Args = Array.Empty<string>() });
        b.Logging.ClearProviders();                                             // never write to the terminal
        b.Services.AddSingleton<IHostLifetime, NoConsoleLifetime>();           // Ctrl+C belongs to StreamBench's ordered shutdown
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Parse(address), port));
        _app = b.Build();
        _app.MapMethods("/{**path}", new[] { "GET", "HEAD" }, (HttpContext ctx, string? path) => Serve(ctx, path ?? ""));
        await _app.StartAsync(ct);
        BaseUrl = $"http://{address}:{port}/";
        Running = true;
    }

    public string UrlFor(string fullPath) => BaseUrl + Path.GetRelativePath(_root, fullPath).Replace('\\', '/');

    private async Task Serve(HttpContext ctx, string rel)
    {
        Interlocked.Increment(ref Requests);
        try
        {
            var full = Path.GetFullPath(Path.Combine(_root, Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(_root, StringComparison.Ordinal) || !File.Exists(full)) { Interlocked.Increment(ref NotFound); ctx.Response.StatusCode = 404; return; }
            byte[] data;
            try
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = new byte[fs.Length]; fs.ReadExactly(data);
            }
            catch (FileNotFoundException) { Interlocked.Increment(ref NotFound); ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = Path.GetExtension(full).ToLowerInvariant() switch
            {
                ".m3u8" => "application/vnd.apple.mpegurl", ".ts" => "video/mp2t", ".m4s" => "video/iso.segment", ".mp4" => "video/mp4",
                ".csv" => "text/csv", ".json" => "application/json", ".html" => "text/html", _ => "application/octet-stream"
            };
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.AcceptRanges = "bytes";
            long start = 0, end = data.Length - 1;
            var range = ctx.Request.Headers.Range.ToString();
            if (range.StartsWith("bytes=") && data.Length > 0)
            {
                var parts = range[6..].Split(',')[0].Split('-');
                if (long.TryParse(parts[0], out var s1)) { start = s1; if (parts.Length > 1 && long.TryParse(parts[1], out var e1)) end = Math.Min(e1, data.Length - 1); }
                else if (parts.Length > 1 && long.TryParse(parts[1], out var suffix)) start = Math.Max(0, data.Length - suffix);
                if (start > end || start >= data.Length) { ctx.Response.StatusCode = 416; ctx.Response.Headers.ContentRange = $"bytes */{data.Length}"; return; }
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{data.Length}";
            }
            var len = data.Length == 0 ? 0 : end - start + 1;
            ctx.Response.ContentLength = len;
            if (HttpMethods.IsHead(ctx.Request.Method)) return;
            await ctx.Response.Body.WriteAsync(data.AsMemory((int)start, (int)len));
            Interlocked.Add(ref BytesServed, len);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref ServerErrors);
            _errors.Enqueue($"{DateTime.UtcNow:HH:mm:ss} {rel}: {ex.Message}");
            while (_errors.Count > 50) _errors.TryDequeue(out _);
            if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
        }
    }

    public async Task StopAsync()
    {
        if (_app == null) return;
        try { await _app.StopAsync(TimeSpan.FromSeconds(5).ToCancellationToken()); } catch { }
        Running = false;
    }

    public async ValueTask DisposeAsync() { await StopAsync(); if (_app != null) await _app.DisposeAsync(); }
}

internal static class TimeSpanExt
{
    public static CancellationToken ToCancellationToken(this TimeSpan t) => new CancellationTokenSource(t).Token;
}
