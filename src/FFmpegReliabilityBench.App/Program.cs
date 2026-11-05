using FFmpegReliabilityBench.App;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Runner;
using Spectre.Console;

if (Console.IsOutputRedirected)
{
    // Non-interactive (service/CI/log file): plain text, wide layout, no ANSI.
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(Console.Out) });
    AnsiConsole.Profile.Width = 220;
    AnsiConsole.Profile.Height = 100_000;
}
CliOptions opts;
try { opts = Cli.Parse(args); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Cli.PrintHelp(); return 3; }
if (opts.Help) { Cli.PrintHelp(); return 0; }

BenchConfig cfg;
try { cfg = BenchConfig.Load(opts.Configs); }
catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Cannot load config:[/] {Markup.Escape(ex.Message)}"); return 3; }
try { Cli.ApplyOverrides(cfg, opts); }
catch (Exception ex) when (ex is ArgumentException or FormatException) { Console.Error.WriteLine(ex.Message); return 3; }
cfg.Source.Url = CredentialProvider.ApplyTo(cfg.Source.Url);

if (opts.ReportLatest) return Summary.ShowLatest(cfg);
if (opts.PrintCommand)
{
    var caps = await FFmpegReliabilityBench.Core.Runner.Capabilities.DiscoverAsync(cfg.Ffmpeg.Executable, cfg.Ffmpeg.Ffprobe);
    var cmd = CommandBuilder.Build(cfg, caps, Path.GetFullPath(cfg.Storage.HlsDirectory), Path.GetFullPath(cfg.Storage.RecordingDirectory), cfg.Hls.Enabled, cfg.Recording.Enabled);
    AnsiConsole.MarkupLine($"[grey]{Markup.Escape(caps.VersionLine)}[/]");
    Console.WriteLine(CommandText.Human(caps.ExecutablePath, cmd.Args));
    for (int i = 0; i < cmd.Args.Count; i++) Console.WriteLine($"  [{i}] {CommandText.Mask(cmd.Args[i])}");
    foreach (var n in cmd.Notes) AnsiConsole.MarkupLine($"[yellow]note:[/] {Markup.Escape(n)}");
    return 0;
}

var state = new BenchState { Config = cfg, Mode = opts.Mode };
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    if (!state.StopRequested) state.RequestStop(); // first Ctrl+C: graceful stop + final validation + report
    else cts.Cancel();                              // second Ctrl+C: abort
};

var run = Orchestrator.RunAsync(state, cts.Token);
if (opts.Tui && !Console.IsOutputRedirected && !Console.IsInputRedirected) await Dashboard.RunAsync(state, run);
else await Summary.StreamEventsAsync(state, run);
await run;
Summary.Print(state);
return state.Overall switch { TestStatus.Pass => 0, TestStatus.Warn => 1, _ => state.FatalError != null ? 3 : 2 };
