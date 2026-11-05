using System.Text.Json;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using Spectre.Console;

namespace FFmpegReliabilityBench.App;

public static class Summary
{
    public static string Badge(TestStatus s) => s switch { TestStatus.Pass => "[bold green]PASS[/]", TestStatus.Warn => "[bold yellow]WARN[/]", TestStatus.Fail => "[bold red]FAIL[/]", _ => "[bold grey]UNKNOWN[/]" };

    /// <summary>Non-interactive mode (service, CI, redirected output): print events as they happen.</summary>
    public static async Task StreamEventsAsync(BenchState st, Task run)
    {
        st.Bus.Published += e => Console.WriteLine($"{e.TsUtc:HH:mm:ss.fff} {e.Level.ToString().ToUpperInvariant(),-4} {e.Category}/{e.Stream} {e.Message}");
        while (!run.IsCompleted) await Task.WhenAny(run, Task.Delay(1000));
    }

    public static void Print(BenchState st)
    {
        AnsiConsole.WriteLine();
        var t = new Table().Border(TableBorder.Rounded).Title($"[bold]{Markup.Escape(st.SessionId)}[/]").AddColumn("ID").AddColumn("Test").AddColumn("Status").AddColumn("Result");
        foreach (var r in st.Prerequisites.Concat(st.AllRuntimeResults))
            t.AddRow(Markup.Escape(r.Id), Markup.Escape(r.Name), Badge(r.Status), Markup.Escape(r.Diagnosis.Length > 160 ? r.Diagnosis[..160] + "…" : r.Diagnosis));
        AnsiConsole.Write(t);
        foreach (var s in st.Streams)
        {
            if (s.Hls != null) { var c = s.Hls.Coverage(); AnsiConsole.MarkupLine($"{Markup.Escape(s.Name)} HLS coverage [bold]{c.Percent:F2}%[/] ({c.VerifiedSeconds:F1}s / {c.ExpectedSeconds:F1}s; missing {c.Missing}, invalid {c.Invalid}, unverified {c.Unverified})"); }
            if (s.Rec != null) { var c = s.Rec.Coverage(); AnsiConsole.MarkupLine($"{Markup.Escape(s.Name)} recording coverage [bold]{c.Percent:F2}%[/] ({c.VerifiedSeconds:F1}s / {c.ExpectedSeconds:F1}s; missing {c.Missing}, invalid {c.Invalid}, unverified {c.Unverified})"); }
        }
        if (st.Lab is { } lab) AnsiConsole.MarkupLine($"Lab {Markup.Escape(lab.Topology)}: {Markup.Escape(lab.TopologyDetail)} · strategy {Markup.Escape(lab.Strategy)} ({Markup.Escape(lab.ScheduleText)}) · run directory {Markup.Escape(lab.RunDir)}");
        foreach (var h in st.Hypotheses.Take(5)) AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(h.Text)}[/]");
        if (st.FatalError != null) AnsiConsole.MarkupLine($"[red]FATAL:[/] {Markup.Escape(st.FatalError)}");
        AnsiConsole.MarkupLine($"Prerequisites {Badge(st.PrereqStatus)}  Runtime {Badge(st.RuntimeStatus)}  [bold]Overall[/] {Badge(st.Overall)}");
        AnsiConsole.MarkupLine($"Report: [link]{Markup.Escape(st.ReportHtml ?? "-")}[/]\nJSON:   {Markup.Escape(st.ReportJson ?? "-")}\nLogs:   {Markup.Escape(st.ReportDirectory)}");
    }

    public static int ShowLatest(BenchConfig cfg, string which)
    {
        var candidates = new[] { Path.Combine(cfg.Lab.RunRoot, "latest.txt"), Path.Combine(cfg.ReportDirectory, "latest.txt") }.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        if (candidates.Count == 0) { AnsiConsole.MarkupLine("[yellow]no report found[/]"); return 3; }
        var latest = candidates[0];
        var dir = File.ReadAllText(latest).Trim();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "report.json")));
        var root = doc.RootElement;
        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(root.GetProperty("session").GetProperty("SessionId").GetString() ?? "")}[/]  overall {Markup.Escape(root.GetProperty("status").GetProperty("overall").GetString() ?? "")}");
        var t = new Table().AddColumn("ID").AddColumn("Test").AddColumn("Status").AddColumn("Result");
        foreach (var section in new[] { "prerequisites", "runtimeResults" })
            foreach (var r in root.GetProperty(section).EnumerateArray())
            {
                var s = Enum.Parse<TestStatus>(r.GetProperty("Status").GetString()!);
                if (s == TestStatus.Pass) continue;
                t.AddRow(Markup.Escape(r.GetProperty("Id").GetString()!), Markup.Escape(r.GetProperty("Name").GetString()!), Badge(s), Markup.Escape(r.GetProperty("Diagnosis").GetString()!));
            }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine($"(non-PASS results only) {Markup.Escape(Path.Combine(dir, "report.html"))}");
        return 0;
    }
}
