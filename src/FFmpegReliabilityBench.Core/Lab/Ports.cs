using System.Net;
using System.Net.Sockets;

namespace FFmpegReliabilityBench.Core.Lab;

/// <summary>Port availability checks. A busy port is reported clearly; its owner is never touched.</summary>
public static class Ports
{
    public static string? TcpInUse(string address, int port)
    {
        if (port <= 0) return null;
        try
        {
            using var c = new TcpClient();
            if (c.ConnectAsync(IPAddress.Parse(address), port).Wait(300) && c.Connected)
                return $"TCP {address}:{port} is already in use (something is listening)";
        }
        catch { /* refused = free */ }
        try
        {
            var l = new TcpListener(IPAddress.Parse(address), port);
            l.Start(); l.Stop();
            return null;
        }
        catch (SocketException ex) { return $"TCP {address}:{port} is already in use ({ex.SocketErrorCode})"; }
    }

    public static string? UdpInUse(string address, int port)
    {
        if (port <= 0) return null;
        try { using var u = new UdpClient(new IPEndPoint(IPAddress.Parse(address), port)); return null; }
        catch (SocketException ex) { return $"UDP {address}:{port} is already in use ({ex.SocketErrorCode})"; }
    }

    public static bool CanConnect(string host, int port, int timeoutMs = 1000)
    {
        try { using var c = new TcpClient(); return c.ConnectAsync(host, port).Wait(timeoutMs) && c.Connected; }
        catch { return false; }
    }
}
