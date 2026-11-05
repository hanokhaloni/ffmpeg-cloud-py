using FFmpegReliabilityBench.App.Tui;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Engine;

namespace FFmpegReliabilityBench.App;

/// <summary>--no-tui / redirected output: same orchestrator, same evidence and verdicts; plain lines instead of a dashboard.</summary>
public static class Headless
{
    public static async Task RunAsync(BenchState st, Task run, TextWriter o, double statusIntervalSeconds = 10)
    {
        var provider = new DashboardStateProvider(st);
        void Print(RuntimeEvent e)
        {
            if (e.Level == EventLevel.Info && e.Category is not ("Lab" or "Session" or "Process" or "Publisher" or "Relay" or "Playback" or "Preflight" or "Config" or "Fixture")) return;
            lock (o) o.WriteLine($"{e.TsUtc:HH:mm:ss.fff} {e.Level.ToString().ToUpperInvariant(),-4} {e.Category}/{e.Stream} {e.Message}");
        }
        st.Bus.Published += Print;
        var last = DateTime.UtcNow;
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(500));
            if ((DateTime.UtcNow - last).TotalSeconds < statusIntervalSeconds) continue;
            last = DateTime.UtcNow;
            try
            {
                var s = provider.GetSnapshot();
                lock (o) o.WriteLine($"[status T+{Renderer.Fmt(s.Elapsed)}] phase {s.Phase} · CURRENT {Renderer.Label(s.Current)} · {s.Topology} · streams {s.Res.StreamsRunning}/{s.Res.StreamsTotal} · " +
                                     $"HLS ok {s.Streams.Sum(r => r.HlsValid)} bad {s.Streams.Sum(r => r.HlsBad)} · REC ok {s.Streams.Sum(r => r.RecValid)} bad {s.Streams.Sum(r => r.RecBad)} · " +
                                     $"backlog {s.Streams.Sum(r => r.Backlog)} · CPU {s.Res.CpuNow:F0}% · owned procs {s.Res.OwnedProcesses}");
            }
            catch { }
        }
        st.Bus.Published -= Print;
    }
}
