using System.Diagnostics;
using FFmpegReliabilityBench.Core.Lab;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, TimeSpan Elapsed);

public static class ProcessUtil
{
    /// <summary>Runs a process with ArgumentList (no shell), reading both pipes concurrently to avoid the redirected-pipe deadlock.</summary>
    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default, ProcessRole role = ProcessRole.Tool, string owner = "", string? name = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        var argList = args.ToList();
        foreach (var a in argList) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, "", ex.Message, false, sw.Elapsed); }
        var mp = ProcessRegistry.Current.Register(p, role, name ?? Path.GetFileNameWithoutExtension(exe), owner, CommandText.Human(exe, argList));
        try { p.StandardInput.Close(); } catch { }
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        string so = "", se = "";
        try { so = await outTask.WaitAsync(TimeSpan.FromSeconds(5)); se = await errTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        int code; try { code = p.ExitCode; } catch { code = -3; }
        ProcessRegistry.Current.MarkExited(mp, code);
        return new ProcResult(timedOut ? -2 : code, so, se, timedOut, sw.Elapsed);
    }
}
