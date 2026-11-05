using System.Text;
using FFmpegReliabilityBench.App;
using FFmpegReliabilityBench.App.Tui;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;
using FFmpegReliabilityBench.Core.Lab;
using FFmpegReliabilityBench.Core.Runner;
using Spectre.Console;
using Capabilities = FFmpegReliabilityBench.Core.Runner.Capabilities;

if (Console.IsOutputRedirected)
{
    // Non-interactive (service/CI/log file): plain text, wide layout, no ANSI.
    AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No, Out = new AnsiConsoleOutput(Console.Out) });
    AnsiConsole.Profile.Width = 220;
    AnsiConsole.Profile.Height = 100_000;
}

if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?") { Cli.PrintHelp(); return 0; }
var verb = args[0].StartsWith('-') ? "bench" : args[0].ToLowerInvariant();
var rest = args[0].StartsWith('-') ? args : args.Skip(1).ToArray();
try
{
    switch (verb)
    {
        case "lab":
        case "run": return await RunLabAsync(verb, rest);
        case "print-commands": return await PrintCommandsAsync(rest);
        case "presets": PrintPresets(); return 0;
        case "bench": return await RunBenchAsync(rest);
        case "preflight": return await RunBenchAsync(new[] { "--preflight-only" }.Concat(rest).ToArray());
        case "report": return Summary.ShowLatest(BenchConfig.Load(Array.Empty<string>()), rest.FirstOrDefault(r => !r.StartsWith('-')) ?? "latest");
        default: Console.Error.WriteLine($"unknown command '{args[0]}'"); Cli.PrintHelp(); return 3;
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or FileNotFoundException or InvalidDataException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 3;
}

static async Task<int> RunLabAsync(string verb, string[] rest)
{
    var la = LabCli.Parse(rest);
    if (la.Help) { Cli.PrintHelp(); return 0; }
    var loaded = ConfigLoader.Load(la.Configs, la.Values, la.Lists, verb == "run" ? "cameras" : null);
    var cfg = loaded.Config;
    cfg.Lab.Faults.AddRange(la.Faults);
    var st = new BenchState { Config = cfg, Mode = RunMode.Benchmark };
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        if (!st.StopRequested) { st.RequestStop(); st.Bus.Publish(EventLevel.Info, "Session", "*", "Ctrl+C: graceful stop requested (press again to abort)"); }
        else cts.Cancel();
    };
    var run = LabOrchestrator.RunAsync(st, verb, loaded.Values, cts.Token);
    await PresentAsync(st, run, cfg.Ui, la.NoTui, cts.Token);
    await run;
    Summary.Print(st);
    return ExitCode(st);
}

static async Task PresentAsync(BenchState st, Task run, UiCfg ui, bool noTui, CancellationToken ct)
{
    var tui = !noTui && ui.Mode != "headless" && !Console.IsOutputRedirected && !Console.IsInputRedirected && TerminalPresenter.EnableVirtualTerminal();
    if (!tui) { await Headless.RunAsync(st, run, Console.Out); return; }
    var ascii = ui.AsciiFallback switch { "on" => true, "off" => false, _ => !UnicodeCapable() };
    if (!ascii && OperatingSystem.IsWindows()) Console.OutputEncoding = Encoding.UTF8;
    var color = ui.Color && Environment.GetEnvironmentVariable("NO_COLOR") == null;
    var controller = new TuiController(new DashboardStateProvider(st), st, ui, ascii ? Glyphs.AsciiSet : Glyphs.Unicode, new TerminalPresenter(Console.Out, color));
    var uiTask = controller.RunAsync(ct);
    await Task.WhenAny(uiTask, Task.WhenAll(run, uiTask));
    await run;
    try { await uiTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
}

static bool UnicodeCapable()
{
    if (OperatingSystem.IsWindows()) return true;
    var lang = Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LC_CTYPE") ?? Environment.GetEnvironmentVariable("LANG") ?? "";
    return lang.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || lang.Contains("utf8", StringComparison.OrdinalIgnoreCase);
}

static int ExitCode(BenchState st) => st.FatalError != null ? 3 : st.Overall switch { TestStatus.Pass => 0, TestStatus.Warn => 1, _ => 2 };

static async Task<int> PrintCommandsAsync(string[] rest)
{
    var la = LabCli.Parse(rest);
    var verbIsRun = la.Values.GetValueOrDefault("lab:preset") == "cameras" || la.Lists.ContainsKey("lab:sources");
    var loaded = ConfigLoader.Load(la.Configs, la.Values, la.Lists, verbIsRun ? "cameras" : null);
    var cfg = loaded.Config;
    var caps = await Capabilities.DiscoverAsync(cfg.Ffmpeg.Executable, cfg.Ffmpeg.Ffprobe);
    var runDir = Path.GetFullPath(Path.Combine(cfg.Lab.RunRoot, "<session>"));
    Console.WriteLine($"# {caps.VersionLine}");
    Console.WriteLine($"# preset {cfg.Lab.Preset}, execution {cfg.Lab.Consumers.Execution}, run directory {runDir}");
    foreach (var (role, id, exe, a, notes) in LabOrchestrator.DescribeCommands(cfg, caps, runDir))
    {
        Console.WriteLine($"\n[{role}] {id}");
        Console.WriteLine("  " + CommandText.Human(exe, a));
        for (int i = 0; i < a.Count; i++) Console.WriteLine($"    [{i}] {CommandText.Mask(a[i])}");
        foreach (var n in notes) Console.WriteLine("  note: " + n);
    }
    return 0;
}

static void PrintPresets()
{
    Console.WriteLine("Presets (lab --preset NAME):");
    foreach (var p in ConfigLoader.Presets) Console.WriteLine($"  {p.Name,-18} {p.Description}");
    Console.WriteLine("\nCommand templates (lab --profiles a,b):");
    foreach (var t in CommandTemplates.BuiltIn) Console.WriteLine($"  {t.Id,-18} {t.Description}");
    Console.WriteLine("  aliases: controlled-copy → dual-copy, controlled-transcode → dual-transcode, original-command → original");
    Console.WriteLine($"\nPlaceholders: {string.Join(" ", CommandTemplates.Placeholders.Select(p => "{" + p + "}"))}");
    Console.WriteLine($"Fixture presets: {string.Join(", ", Fixture.Presets.Keys)}");
}

static async Task<int> RunBenchAsync(string[] rest)
{
    CliOptions opts;
    try { opts = Cli.Parse(rest); }
    catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); Cli.PrintHelp(); return 3; }
    if (opts.Help) { Cli.PrintHelp(); return 0; }
    BenchConfig cfg;
    try { cfg = BenchConfig.Load(opts.Configs); }
    catch (Exception ex) { AnsiConsole.MarkupLine($"[red]Cannot load config:[/] {Markup.Escape(ex.Message)}"); return 3; }
    try { Cli.ApplyOverrides(cfg, opts); }
    catch (Exception ex) when (ex is ArgumentException or FormatException) { Console.Error.WriteLine(ex.Message); return 3; }
    cfg.Source.Url = CredentialProvider.ApplyTo(cfg.Source.Url);
    if (opts.ReportLatest) return Summary.ShowLatest(cfg, "latest");
    if (opts.PrintCommand)
    {
        var caps = await Capabilities.DiscoverAsync(cfg.Ffmpeg.Executable, cfg.Ffmpeg.Ffprobe);
        var cmd = CommandBuilder.Build(cfg, caps, Path.GetFullPath(cfg.Storage.HlsDirectory), Path.GetFullPath(cfg.Storage.RecordingDirectory), cfg.Hls.Enabled, cfg.Recording.Enabled);
        Console.WriteLine($"# {caps.VersionLine}");
        Console.WriteLine(CommandText.Human(caps.ExecutablePath, cmd.Args));
        for (int i = 0; i < cmd.Args.Count; i++) Console.WriteLine($"  [{i}] {CommandText.Mask(cmd.Args[i])}");
        foreach (var n in cmd.Notes) Console.WriteLine("note: " + n);
        return 0;
    }
    var st = new BenchState { Config = cfg, Mode = opts.Mode };
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (!st.StopRequested) st.RequestStop(); else cts.Cancel(); };
    var run = Orchestrator.RunAsync(st, cts.Token);
    await PresentAsync(st, run, cfg.Ui, !opts.Tui, cts.Token);
    await run;
    Summary.Print(st);
    return ExitCode(st);
}
