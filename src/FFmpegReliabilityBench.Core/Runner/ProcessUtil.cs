using System.Diagnostics;

namespace FFmpegReliabilityBench.Core.Runner;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, TimeSpan Elapsed);

public static class ProcessUtil
{
    /// <summary>Runs a process with ArgumentList (no shell), reading both pipes concurrently to avoid the redirected-pipe deadlock.</summary>
    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = new System.Diagnostics.Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return new ProcResult(-1, "", ex.Message, false, sw.Elapsed); }
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
        return new ProcResult(timedOut ? -2 : p.ExitCode, so, se, timedOut, sw.Elapsed);
    }
}
