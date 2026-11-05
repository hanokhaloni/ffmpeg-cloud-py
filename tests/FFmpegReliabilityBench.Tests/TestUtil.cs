using System.Net;
using System.Net.Sockets;
using FFmpegReliabilityBench.Core.Runner;
using Xunit;

namespace FFmpegReliabilityBench.Tests;

/// <summary>Tests that start FFmpeg run sequentially (one collection) so they do not starve each other of CPU.</summary>
[CollectionDefinition("ffmpeg")]
public class FfmpegCollection { }

public static class TestUtil
{
    /// <summary>FFmpeg-dependent tests return early (pass) when ffmpeg/ffprobe are not on PATH.</summary>
    public static bool HaveFfmpeg => Capabilities.ResolvePath("ffmpeg") != null && Capabilities.ResolvePath("ffprobe") != null;

    public static string TempDir(string prefix = "frbtest") => Directory.CreateTempSubdirectory(prefix).FullName;

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        return p;
    }

    public static async Task Ffmpeg(params string[] args)
    {
        var r = await ProcessUtil.RunAsync("ffmpeg", new[] { "-hide_banner", "-y", "-v", "error" }.Concat(args), TimeSpan.FromSeconds(120));
        Assert.True(r.ExitCode == 0, "ffmpeg failed: " + r.StdErr);
    }

    /// <summary>Writes a file atomically (temp + rename), like FFmpeg's hls muxer does for playlists.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "FFmpegReliabilityBench.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}

/// <summary>Minimal static file server (stands in for IIS/nginx) used to test HTTP HLS consumption.</summary>
public sealed class StaticHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _root;
    private readonly Task _loop;
    public string BaseUrl { get; }
    public int Requests;

    public StaticHttpServer(string root)
    {
        _root = root;
        BaseUrl = $"http://127.0.0.1:{TestUtil.FreePort()}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref Requests);
        try
        {
            var rel = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(_root, rel));
            if (!path.StartsWith(Path.GetFullPath(_root)) || !File.Exists(path)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[fs.Length]; fs.ReadExactly(bytes); }
            ctx.Response.ContentType = Path.GetExtension(path) switch { ".m3u8" => "application/vnd.apple.mpegurl", ".ts" => "video/mp2t", ".m4s" or ".mp4" => "video/mp4", _ => "application/octet-stream" };
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}
