using System.Globalization;
using FFmpegReliabilityBench.Core;

namespace FFmpegReliabilityBench.App.Tui;

/// <summary>
/// Pure layout: (snapshot, view, size, glyphs) → canvas. Full layout targets 140×42, compact ≈100×30 (selected detail replaces
/// the lower panels), minimal below that. No I/O, no threads — unit-testable.
/// </summary>
public static class Renderer
{
    public const int FullW = 140, FullH = 42, CompactW = 100, CompactH = 30;
    public static readonly string[] TabNames = { "Overview", "Streams", "Checks", "Storage", "Logs", "Lab" };

    public static string LayoutFor(int w, int h) => w >= FullW && h >= FullH ? "full" : w >= CompactW && h >= CompactH ? "compact" : "minimal";

    /// <param name="terminalWidth">Real terminal width used for the layout decision (the canvas may be one column narrower so the
    /// bottom-right cell is never written).</param>
    public static Canvas Render(DashboardSnapshot s, ViewState v, int w, int h, Glyphs g, int? terminalWidth = null)
    {
        var c = new Canvas(w, h, g);
        var layout = LayoutFor(terminalWidth ?? w, h);
        if (layout == "minimal") { Minimal(c, s, v); return c; }
        c.Frame(Sty.Border);
        Title(c, s, v);
        var footerY = h - 4; // separator, tabs, keys, bottom border
        var top = 1;
        if (layout == "full")
        {
            ResourceArea(c, s, top, 3); top += 3;
        }
        else { ResourceCompact(c, s, top); top += 1; }

        switch (v.Tab)
        {
            case 2: StreamsTab(c, s, v, top, footerY, layout == "full"); break;
            case 3: ChecksTab(c, s, v, top, footerY); break;
            case 4: StorageTab(c, s, v, top, footerY); break;
            case 5: LogsTab(c, s, v, top, footerY); break;
            case 6: LabTab(c, s, v, top, footerY); break;
            default: if (layout == "full") OverviewFull(c, s, v, top, footerY); else OverviewCompact(c, s, v, top, footerY); break;
        }
        Footer(c, s, v, footerY);
        if (v.Overlay != Overlay.None) DrawOverlay(c, v);
        return c;
    }

    // ------------------------------------------------------------------ header / resources / footer

    private static void Title(Canvas c, DashboardSnapshot s, ViewState v)
    {
        var verdictLabel = s.Finished ? "FINAL" : "CURRENT";
        var parts = new List<string>
        {
            "StreamBench", $"{s.Mode}: {s.Preset}", $"{s.Topology}{(s.Strategy.Length > 0 ? " · " + s.Strategy : "")}{(s.BatchLabel.Length > 0 && s.BatchLabel != "all instances" ? " · " + s.BatchLabel : "")}",
            $"elapsed {Fmt(s.Elapsed)} / {Fmt(s.Planned)}", $"phase {s.Phase}"
        };
        var x = 2;
        foreach (var p in parts)
        {
            if (x + p.Length + 4 > c.W - 20) break;
            x += c.Text(x, 0, " " + p + " ", p == "StreamBench" ? Sty.Title : Sty.Bold);
            x += c.Text(x, 0, c.G.H.ToString(), Sty.Border);
        }
        var verdict = $" {verdictLabel}: {Label(s.Current)} ";
        c.Text(Math.Max(x, c.W - verdict.Length - 2), 0, verdict, StatusSty(s.Current));
        if (v.Paused) c.Text(Math.Max(2, c.W - verdict.Length - 16), 0, " PAUSED ", Sty.Warn);
    }

    private static void ResourceArea(Canvas c, DashboardSnapshot s, int y, int rows)
    {
        var r = s.Res; var x = 2;
        x += c.Text(x, y, "CPU ", Sty.Bold); x += Bar(c, x, y, r.CpuNow / 100, 12, Sty.G1); x += c.Text(x, y, $" {r.CpuNow,3:F0}%   ", Sty.Normal);
        x += c.Text(x, y, "RAM ", Sty.Bold); x += Bar(c, x, y, r.RamTotalGb > 0 ? r.RamUsedGb / r.RamTotalGb : 0, 12, Sty.G2); x += c.Text(x, y, $" {r.RamUsedGb:F1}/{r.RamTotalGb:F0} GB   ", Sty.Normal);
        x += c.Text(x, y, $"Streams {r.StreamsRunning}/{r.StreamsTotal}   ", Sty.Bold);
        x += c.Text(x, y, $"Owned procs {r.OwnedProcesses}   ", Sty.Normal);
        c.Text(x, y, $"Validation {r.ValidationQueued} queued, {r.ValidationRunning}/{r.ValidationMax} workers", r.ValidationQueued > 10 ? Sty.Warn : Sty.Normal, c.W - x - 2);
        // graphs: CPU, app write, net (history)
        var gw = Math.Max(10, (c.W - 4 - 60) / 3);
        x = 2;
        x += c.Text(x, y + 1, "CPU ", Sty.Dim); x += Spark(c, x, y + 1, r.CpuHistory, gw, 100, Sty.G1);
        x += c.Text(x, y + 1, "  Write ", Sty.Dim); x += Spark(c, x, y + 1, r.WriteHistory, gw, Math.Max(1, Max(r.WriteHistory)), Sty.G3); x += c.Text(x, y + 1, $" {r.WriteMBps,5:F1} MB/s", Sty.Normal);
        x += c.Text(x, y + 1, "  Net ", Sty.Dim); x += Spark(c, x, y + 1, r.NetHistory, gw, Math.Max(1, Max(r.NetHistory)), Sty.G4); c.Text(x, y + 1, $" {r.NetMbps,6:F1} Mb/s", Sty.Normal);
        if (rows < 3) return;
        x = 2; x += c.Text(x, y + 2, "Roles ", Sty.Dim);
        foreach (var t in r.Roles.Where(t => t.Started > 0))
        {
            var label = t.Role switch { Core.Lab.ProcessRole.Publisher => "Publishers(gen)", Core.Lab.ProcessRole.Consumer => "Consumers", Core.Lab.ProcessRole.Validator => "Validators", Core.Lab.ProcessRole.Player => "Players", _ => t.Role.ToString() };
            x += c.Text(x, y + 2, $"{label} {t.Live} {t.CpuPercent:F1}% {t.MemMb:F0}MB", t.Role == Core.Lab.ProcessRole.Publisher ? Sty.G3 : Sty.Normal, c.W - x - 2);
            x += c.Text(x, y + 2, " │ ".Replace('│', c.G.V), Sty.Border);
            if (x > c.W - 20) break;
        }
        var total = r.Roles.Sum(t => t.CpuPercent);
        c.Text(x, y + 2, $"Total {total:F1}%", Sty.Bold, c.W - x - 2);
    }

    private static void ResourceCompact(Canvas c, DashboardSnapshot s, int y)
    {
        var r = s.Res; var x = 2;
        x += c.Text(x, y, "CPU ", Sty.Bold); x += Spark(c, x, y, r.CpuHistory, 14, 100, Sty.G1); x += c.Text(x, y, $" {r.CpuNow:F0}%  ", Sty.Normal);
        x += c.Text(x, y, $"RAM {r.RamUsedGb:F1}/{r.RamTotalGb:F0}G  ", Sty.Normal);
        x += c.Text(x, y, $"Streams {r.StreamsRunning}/{r.StreamsTotal}  ", Sty.Bold);
        var gen = r.Roles.FirstOrDefault(t => t.Role == Core.Lab.ProcessRole.Publisher); var con = r.Roles.FirstOrDefault(t => t.Role == Core.Lab.ProcessRole.Consumer);
        c.Text(x, y, $"gen {gen?.CpuPercent ?? 0:F1}% cons {con?.CpuPercent ?? 0:F1}%  valq {r.ValidationQueued}", Sty.Normal, c.W - x - 2);
    }

    private static void Footer(Canvas c, DashboardSnapshot s, ViewState v, int y)
    {
        c.Separator(y, null, Sty.Border);
        var x = 2;
        for (int i = 0; i < TabNames.Length; i++)
        {
            var t = $" {i + 1} {TabNames[i]} ";
            x += c.Text(x, y + 1, t, v.Tab == i + 1 ? Sty.Sel : Sty.Key);
            x += 1;
        }
        var status = new List<string>();
        if (v.Paused) status.Add("PAUSED (display only; collection continues)");
        if (v.FilterEditing) status.Add($"filter: {v.FilterDraft}_");
        else if (v.Filter.Length > 0) status.Add($"filter: {v.Filter}");
        status.Add($"sort: {v.Sort}{(v.SortDesc ? "↓" : "")}".Replace('↓', c.G.Down));
        if (v.Output != OutputFilter.All) status.Add($"output: {v.Output}");
        if (v.Severity != SeverityFilter.All) status.Add($"severity: {v.Severity}");
        if (v.Group != GroupBy.None) status.Add($"group: {v.Group}");
        c.Text(x + 1, y + 1, string.Join(" · ", status), v.Paused ? Sty.Warn : Sty.Dim, c.W - x - 3);
        var g = c.G;
        var keys = s.Finished
            ? $"Run finished: {Label(s.Current)} - report {s.ReportPath} - press q to exit"
            : $"{g.Up}{g.Down}/jk Select  Enter Details  / Filter  s Sort  o Output  v Severity  g Group  {g.Left}{g.Right} Scroll cmd  w Wrap  Space Pause UI  ? Help  q Stop & exit";
        c.Text(2, y + 2, keys, s.Finished ? StatusSty(s.Current) : Sty.Dim, c.W - 4);
    }

    // ------------------------------------------------------------------ overview

    private static void OverviewFull(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        var mid = c.W * 2 / 5;
        // prerequisites | storage/network
        c.Separator(top, "PREREQUISITES", Sty.Border, (mid, c.G.TT));
        c.Text(mid + 2, top, " STORAGE / NETWORK ", Sty.Title);
        const int panelRows = 6;
        for (int i = 0; i < panelRows; i++)
        {
            c.Put(mid, top + 1 + i, c.G.V, Sty.Border);
            if (i < s.Prereqs.Count) CheckLineAt(c, 2, top + 1 + i, s.Prereqs[i], mid - 3);
            if (i < s.StorageNet.Count) c.Text(mid + 2, top + 1 + i, s.StorageNet[i], Sty.Normal, c.W - mid - 4);
        }
        var tableTop = top + 1 + panelRows;
        // lower: selected | incidents (7 rows), command (3 rows)
        const int lowerRows = 7, cmdRows = 3;
        var cmdSep = footerY - cmdRows - 1;
        var lowerSep = cmdSep - lowerRows - 1;
        c.Separator(tableTop, "STREAM / COMMAND TABLE", Sty.Border, (mid, c.G.BT));
        var rows = VisibleRows(s, v);
        Table(c, rows, v, 2, tableTop + 1, c.W - 4, lowerSep - tableTop - 1, full: true);
        var sel = Selected(rows, v);
        if (v.Details && sel != null)
        {
            c.Separator(lowerSep, $"DETAILS: {sel.Id} (Enter/Esc closes, {c.G.Up}{c.G.Down} scroll)", Sty.Border);
            DetailLines(c, sel, s, 2, lowerSep + 1, c.W - 4, footerY - lowerSep - 1, v);
            return;
        }
        var split = c.W * 11 / 20;
        c.Separator(lowerSep, $"SELECTED: {sel?.Id ?? "-"}", Sty.Border, (split, c.G.TT));
        c.Text(split + 2, lowerSep, " INCIDENTS ", Sty.Title);
        for (int i = 0; i < lowerRows; i++) c.Put(split, lowerSep + 1 + i, c.G.V, Sty.Border);
        if (sel != null) SelectedSummary(c, sel, s, 2, lowerSep + 1, split - 3, lowerRows);
        Incidents(c, s, v, split + 2, lowerSep + 1, c.W - split - 4, lowerRows);
        c.Separator(cmdSep, $"COMMAND [redacted; {c.G.Left}{c.G.Right} scroll, w wrap]", Sty.Border, (split, c.G.BT));
        CommandLines(c, sel, v, 2, cmdSep + 1, c.W - 4, cmdRows);
    }

    private static void OverviewCompact(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        // one-line prerequisite strip
        var x = 2;
        foreach (var p in s.Prereqs)
        {
            x += c.Text(x, top, Label(p.Status, p.Id), StatusSty(p.Status));
            x += c.Text(x, top, " " + p.Name + "  ", Sty.Normal, c.W - x - 2);
        }
        var lowerRows = 6;
        var lowerSep = footerY - lowerRows - 1;
        c.Separator(top + 1, "STREAMS", Sty.Border);
        var rows = VisibleRows(s, v);
        Table(c, rows, v, 2, top + 2, c.W - 4, lowerSep - top - 2, full: false);
        var sel = Selected(rows, v);
        // compact: the selected detail replaces the lower panels
        c.Separator(lowerSep, v.Details ? $"DETAILS: {sel?.Id}" : $"SELECTED: {sel?.Id ?? "-"} · incidents {s.Incidents.Count} · {Shorten(s.Attribution, 40)}", Sty.Border);
        if (sel == null) return;
        if (v.Details) { DetailLines(c, sel, s, 2, lowerSep + 1, c.W - 4, footerY - lowerSep - 1, v); return; }
        SelectedSummary(c, sel, s, 2, lowerSep + 1, c.W - 4, lowerRows - 2);
        CommandLines(c, sel, v, 2, lowerSep + lowerRows - 1, c.W - 4, 2);
    }

    private static void Minimal(Canvas c, DashboardSnapshot s, ViewState v)
    {
        var y = 0;
        c.Text(0, y++, $"StreamBench {s.Mode} {s.Preset} {Fmt(s.Elapsed)}/{Fmt(s.Planned)} {s.Phase}", Sty.Title, c.W);
        c.Text(0, y++, $"{(s.Finished ? "FINAL" : "CURRENT")}: {Label(s.Current)}  {s.Topology}  CPU {s.Res.CpuNow:F0}%  streams {s.Res.StreamsRunning}/{s.Res.StreamsTotal}", StatusSty(s.Current), c.W);
        var rows = VisibleRows(s, v);
        var max = Math.Max(0, c.H - 4);
        var off = Offset(v.Selected, rows.Count, max);
        for (int i = off; i < rows.Count && y < c.H - 2; i++)
        {
            var r = rows[i];
            var line = $"{(i == v.Selected ? ">" : " ")}{r.Id} HLS {r.Hls} REC {r.Rec} {r.State}";
            c.Text(0, y, line, i == v.Selected ? Sty.Sel : StatusSty(r.StateStatus), c.W);
            y++;
        }
        if (v.Details && Selected(rows, v) is { } sel)
        {
            c.Text(0, y++, Shorten(sel.Detail.ConsumerCommand, 1000), Sty.Dim, c.W);
        }
        c.Text(0, c.H - 1, s.Finished ? "finished - q exits" : "jk select · Enter details · Space pause · q stop  (enlarge terminal for full view)", Sty.Dim, c.W);
        if (v.Overlay != Overlay.None) DrawOverlay(c, v);
    }

    // ------------------------------------------------------------------ tabs

    private static void StreamsTab(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY, bool full)
    {
        var procRows = Math.Min(Math.Max(4, s.Processes.Count + 2), (footerY - top) / 2);
        var procTop = footerY - procRows - 1;
        c.Separator(top, $"STREAMS ({s.Streams.Count}) · grouped by {v.Group}", Sty.Border);
        var rows = VisibleRows(s, v);
        Table(c, rows, v, 2, top + 1, c.W - 4, procTop - top - 1, full);
        c.Separator(procTop, $"PROCESSES BY ROLE (owned by StreamBench: {s.Processes.Count(p => p.State == "running")} running)", Sty.Border);
        var y = procTop + 1;
        c.Text(2, y++, $"{"Role",-10} {"Name",-26} {"Owner",-22} {"PID",7} {"CPU%",6} {"MEM MB",7}  {"State",-11} {"Age",8}", Sty.Header, c.W - 4);
        foreach (var p in s.Processes.Take(footerY - y))
            c.Text(2, y++, $"{p.Role,-10} {Shorten(p.Name, 26),-26} {Shorten(p.Owner, 22),-22} {p.Pid,7} {p.Cpu,6:F1} {p.MemMb,7:F0}  {p.State,-11} {Fmt(p.Age),8}",
                p.State == "running" ? Sty.Normal : Sty.Dim, c.W - 4);
    }

    private static void ChecksTab(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        var detailRows = 6;
        var listH = footerY - top - detailRows - 2;
        c.Separator(top, $"CHECKS ({s.Checks.Count}: {s.Checks.Count(x => x.Status == TestStatus.Fail)} fail, {s.Checks.Count(x => x.Status == TestStatus.Warn)} warn, {s.Checks.Count(x => x.Status == TestStatus.Unknown)} unknown)", Sty.Border);
        var list = s.Checks.Where(x => Match(v.Filter, x.Id, x.Name, x.Detail)).ToList();
        var sel = Math.Clamp(v.ListOffset, 0, Math.Max(0, list.Count - 1));
        var off = Offset(sel, list.Count, listH);
        for (int i = 0; i < listH && off + i < list.Count; i++)
        {
            var ck = list[off + i]; var y = top + 1 + i;
            var x = 2 + c.Text(2, y, Label(ck.Status), StatusSty(ck.Status));
            c.Text(x + 1, y, $"{ck.Id,-9} {ck.Name}: {ck.Detail}", off + i == sel ? Sty.Sel : Sty.Normal, c.W - x - 3);
        }
        var dy = footerY - detailRows - 1;
        c.Separator(dy, "CHECK DETAIL", Sty.Border);
        if (list.Count > 0)
        {
            var ck = list[sel];
            var lines = Wrap($"{ck.Id} {ck.Name} - {Label(ck.Status)}: {ck.Detail}", c.W - 4);
            for (int i = 0; i < detailRows && i < lines.Count; i++) c.Text(2, dy + 1 + i, lines[i], Sty.Normal, c.W - 4);
        }
    }

    private static void StorageTab(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        c.Separator(top, "STORAGE / NETWORK", Sty.Border);
        var y = top + 1;
        foreach (var l in s.StorageNet) c.Text(2, y++, l, Sty.Normal, c.W - 4);
        c.Text(2, y, "App write ", Sty.Dim); Spark(c, 12, y, s.Res.WriteHistory, c.W - 30, Math.Max(1, Max(s.Res.WriteHistory)), Sty.G3); c.Text(c.W - 17, y++, $"{s.Res.WriteMBps,6:F1} MB/s", Sty.Normal);
        c.Text(2, y, "Network   ", Sty.Dim); Spark(c, 12, y, s.Res.NetHistory, c.W - 30, Math.Max(1, Max(s.Res.NetHistory)), Sty.G4); c.Text(c.W - 17, y++, $"{s.Res.NetMbps,6:F1} Mb/s", Sty.Normal);
        c.Separator(y++, "STORAGE CHECKS (deep probe before streaming)", Sty.Border);
        var off = Math.Clamp(v.ListOffset, 0, Math.Max(0, s.StorageDetail.Count - 1));
        foreach (var l in s.StorageDetail.Skip(off))
        {
            if (y >= footerY) break;
            c.Text(2, y++, l, l.StartsWith("FAIL") ? Sty.Fail : l.StartsWith("WARN") ? Sty.Warn : Sty.Normal, c.W - 4);
        }
    }

    private static void LogsTab(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        var list = s.Events.Where(e => v.Severity switch { SeverityFilter.FailOnly => e.Level == EventLevel.Fail, SeverityFilter.WarnAndUp => e.Level != EventLevel.Info, _ => true })
            .Where(e => Match(v.Filter, e.Stream, e.Category, e.Text)).ToList();
        c.Separator(top, $"EVENTS (latest {list.Count} of {s.EventCount:N0}; child process logs go to files, never to this terminal)", Sty.Border);
        var h = footerY - top - 1;
        var end = Math.Max(0, list.Count - Math.Clamp(v.ListOffset, 0, Math.Max(0, list.Count - 1)));
        var start = Math.Max(0, end - h);
        var y = top + 1;
        for (int i = start; i < end; i++)
        {
            var e = list[i];
            var x = 2 + c.Text(2, y, $"{e.Ts:HH:mm:ss} ", Sty.Dim);
            x += c.Text(x, y, e.Level == EventLevel.Info ? "INFO " : e.Level == EventLevel.Warn ? "WARN " : "FAIL ", e.Level == EventLevel.Fail ? Sty.Fail : e.Level == EventLevel.Warn ? Sty.Warn : Sty.Dim);
            c.Text(x, y++, $"{e.Category}/{e.Stream} {e.Text}", Sty.Normal, c.W - x - 2);
        }
    }

    private static void LabTab(Canvas c, DashboardSnapshot s, ViewState v, int top, int footerY)
    {
        var mid = c.W / 2;
        c.Separator(top, "STARTUP STATE MACHINE", Sty.Border, (mid, c.G.TT));
        c.Text(mid + 2, top, " TOPOLOGY / SOURCES ", Sty.Title);
        var phaseRows = Math.Max(s.Phases.Count, 8);
        var y = top + 1;
        for (int i = 0; i < s.Phases.Count; i++)
        {
            var p = s.Phases[i];
            var st = p.Status switch { "pass" => TestStatus.Pass, "warn" => TestStatus.Warn, "fail" => TestStatus.Fail, _ => TestStatus.Unknown };
            var label = p.Status switch { "pass" => "PASS", "warn" => "WARN", "fail" => "FAIL", "running" => "RUN ", "skipped" => "SKIP", _ => "    " };
            var x = 2 + c.Text(2, y + i, label, p.Status == "running" ? Sty.Key : StatusSty(st));
            c.Text(x + 1, y + i, $"{p.Name}{(p.Seconds is double d ? $" ({d:F1}s)" : "")}{(p.Detail.Length > 0 ? ": " + p.Detail : "")}", Sty.Normal, mid - x - 2);
        }
        var ry = top + 1;
        c.Text(mid + 2, ry++, $"{s.Topology}: {s.TopologyDetail}", s.Topology == "FAN-OUT" ? Sty.Warn : Sty.Bold, c.W - mid - 4);
        c.Text(mid + 2, ry++, $"strategy {s.Strategy} · {s.BatchLabel}", Sty.Normal, c.W - mid - 4);
        c.Text(mid + 2, ry++, $"{"Source",-7} {"Kind",-9} {"State",-9} {"PID",7} {"CPU%",5} {"Ready",6} {"Loops",5} {"Rdrs",4}", Sty.Header, c.W - mid - 4);
        foreach (var src in s.Sources)
        {
            if (ry >= top + 1 + phaseRows) break;
            c.Text(mid + 2, ry++, $"{src.Id,-7} {(src.Kind == "camera" ? "camera" : "synth/" + (src.Mode == "live-synthetic" ? "live" : "loop")),-9} {Shorten(src.State, 9),-9} {src.Pid,7} {src.Cpu,5:F1} {(src.ReadyAfter is double r ? r.ToString("F1") + "s" : "-"),6} {src.Loops,5} {(src.Readers >= 0 ? src.Readers.ToString() : "-"),4}",
                src.State.StartsWith("FAIL") || src.State.StartsWith("DOWN") ? Sty.Fail : Sty.Normal, c.W - mid - 4);
        }
        for (int i = top + 1; i < top + 1 + phaseRows; i++) c.Put(mid, i, c.G.V, Sty.Border);
        var cfgTop = top + 1 + phaseRows;
        c.Separator(cfgTop, $"EFFECTIVE CONFIGURATION ({s.Config.Count} values: built-in defaults < preset < config file < command line; {c.G.Up}{c.G.Down} scroll, / filter)", Sty.Border, (mid, c.G.BT));
        var list = s.Config.Where(x => Match(v.Filter, x.Key, x.Value, x.Origin)).ToList();
        var nonDefaultFirst = list.OrderBy(x => x.Origin == "default" ? 1 : 0).ThenBy(x => x.Key).ToList();
        var off = Math.Clamp(v.ListOffset, 0, Math.Max(0, nonDefaultFirst.Count - 1));
        var yy = cfgTop + 1;
        foreach (var cv in nonDefaultFirst.Skip(off))
        {
            if (yy >= footerY) break;
            var kx = 2 + c.Text(2, yy, $"{Shorten(cv.Key, 44),-44} ", Sty.Normal);
            kx += c.Text(kx, yy, $"{Shorten(Core.Runner.CommandText.Mask(cv.Value), Math.Max(10, c.W - kx - 30)),-10} ", Sty.Bold, c.W - kx - 28);
            c.Text(c.W - 26, yy, cv.Origin, cv.Origin == "default" ? Sty.Dim : Sty.Key, 24);
            yy++;
        }
    }

    // ------------------------------------------------------------------ table + detail pieces

    /// <summary>Rows after filter, output filter, grouping and sorting (the order the user sees).</summary>
    public static List<StreamRow> VisibleRows(DashboardSnapshot s, ViewState v)
    {
        IEnumerable<StreamRow> q = s.Streams.Where(r => Match(v.Filter, r.Id, r.Source, r.Profile, r.State));
        q = v.Output switch
        {
            OutputFilter.HlsIssues => q.Where(r => r.Hls is not ("PASS" or "-")),
            OutputFilter.RecIssues => q.Where(r => r.Rec is not ("PASS" or "-")),
            _ => q
        };
        Func<StreamRow, IComparable> key = v.Sort switch
        {
            SortKey.Cpu => r => r.Cpu,
            SortKey.State => r => Rank(r.StateStatus),
            SortKey.Latency => r => r.HlsAge,
            SortKey.Backlog => r => r.Backlog,
            _ => r => r.Id
        };
        var desc = v.SortDesc ^ (v.Sort is SortKey.Cpu or SortKey.State or SortKey.Latency or SortKey.Backlog); // "worst first" by default for metrics
        IOrderedEnumerable<StreamRow> ordered = v.Group switch
        {
            GroupBy.Source => q.OrderBy(r => r.Source),
            GroupBy.Profile => q.OrderBy(r => r.Profile),
            _ => q.OrderBy(_ => 0)
        };
        ordered = desc ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
        return ordered.ThenBy(r => r.Id).ToList();
    }

    public static StreamRow? Selected(List<StreamRow> rows, ViewState v) => rows.Count == 0 ? null : rows[Math.Clamp(v.Selected, 0, rows.Count - 1)];

    private static void Table(Canvas c, List<StreamRow> rows, ViewState v, int x, int y, int w, int h, bool full)
    {
        if (h <= 0) return;
        var header = full
            ? $"  {"ID",-22} {"Source",-8} {"Kind",-5} {"Profile",-16} {"PID",7} {"CPU%",6} {"FPS",5} {"Speed",6} {"HLSseq",6} {"Age",5} {"HLS",-5} {"REC",-5} {"Bklg",4} {"State",-6}"
            : $"  {"ID",-18} {"Src",-5} {"Profile",-12} {"PID",7} {"CPU%",5} {"FPS",4} {"HLS",-5} {"REC",-5} {"State",-6}";
        c.Text(x, y, header, Sty.Header, w);
        var max = h - 1;
        var sel = Math.Clamp(v.Selected, 0, Math.Max(0, rows.Count - 1));
        var off = Offset(sel, rows.Count, max);
        string? lastGroup = null; var line = 0;
        for (int i = off; i < rows.Count && line < max; i++)
        {
            var r = rows[i];
            var grp = v.Group == GroupBy.Source ? r.Source : v.Group == GroupBy.Profile ? r.Profile : null;
            if (grp != null && grp != lastGroup)
            {
                lastGroup = grp;
                c.Text(x, y + 1 + line++, $"── {v.Group}: {grp}".Replace('─', c.G.H), Sty.Dim, w);
                if (line >= max) break;
            }
            var yy = y + 1 + line++;
            var kind = r.SourceKind == "camera" ? "cam" : "synth";
            var text = full
                ? $"{(i == sel ? ">" : " ")} {Shorten(r.Id, 22),-22} {Shorten(r.Source, 8),-8} {kind,-5} {Shorten(r.Profile, 16),-16} {(r.Pid > 0 ? r.Pid.ToString() : "-"),7} {r.Cpu,6:F1} {(r.Fps > 0 ? r.Fps.ToString("F0") : "-"),5} {r.Speed,5:F2}x {(r.HlsSeq >= 0 ? r.HlsSeq.ToString() : "-"),6} {r.HlsAge,4:F1}s "
                : $"{(i == sel ? ">" : " ")} {Shorten(r.Id, 18),-18} {Shorten(r.Source, 5),-5} {Shorten(r.Profile, 12),-12} {(r.Pid > 0 ? r.Pid.ToString() : "-"),7} {r.Cpu,5:F1} {(r.Fps > 0 ? r.Fps.ToString("F0") : "-"),4} ";
            var tx = x + c.Text(x, yy, text, i == sel ? Sty.Sel : Sty.Normal, w);
            tx += c.Text(tx, yy, $"{r.Hls,-5} ", OutSty(r.Hls), x + w - tx);
            tx += c.Text(tx, yy, $"{r.Rec,-5} ", OutSty(r.Rec), x + w - tx);
            if (full) tx += c.Text(tx, yy, $"{r.Backlog,4} ", r.Backlog > 6 ? Sty.Warn : Sty.Normal, x + w - tx);
            c.Text(tx, yy, $"{r.State,-6}", StatusSty(r.StateStatus), x + w - tx);
        }
        if (rows.Count == 0) c.Text(x, y + 1, v.Filter.Length > 0 || v.Output != OutputFilter.All ? "(no streams match the filter)" : "(no streams yet)", Sty.Dim, w);
    }

    private static void SelectedSummary(Canvas c, StreamRow r, DashboardSnapshot s, int x, int y, int w, int rows)
    {
        var d = r.Detail;
        var lines = new List<(string, Sty)>
        {
            ($"Input {d.InputUrl}", Sty.Normal),
            (d.HlsSummary, r.Hls == "FAIL" ? Sty.Fail : r.Hls == "LATE" ? Sty.Warn : Sty.Normal),
            (d.RecordingSummary, r.Rec == "FAIL" ? Sty.Fail : r.Rec == "LATE" ? Sty.Warn : Sty.Normal),
            (d.OutputCoverage, Sty.Normal),
            (d.SourceCompleteness, Sty.Unknown),
            (r.Attribution != null ? $"Attribution: {r.Attribution}" : d.PlayerSummary, r.Attribution != null ? Sty.Warn : Sty.Dim),
            (d.HttpUrl != null ? $"HTTP {d.HttpUrl}" : $"Outputs {d.HlsDir}", Sty.Dim),
        };
        for (int i = 0; i < rows && i < lines.Count; i++) c.Text(x, y + i, lines[i].Item1, lines[i].Item2, w);
    }

    private static void Incidents(Canvas c, DashboardSnapshot s, ViewState v, int x, int y, int w, int rows)
    {
        var list = s.Incidents.Where(e => v.Severity == SeverityFilter.FailOnly ? e.Level == EventLevel.Fail : true).ToList();
        var show = Math.Max(0, rows - 1);
        if (list.Count == 0) c.Text(x, y, "no warnings or failures", Sty.Pass, w);
        for (int i = 0; i < show && i < list.Count; i++)
        {
            var e = list[i];
            var tx = x + c.Text(x, y + i, $"{e.Ts:HH:mm:ss} ", Sty.Dim);
            c.Text(tx, y + i, $"{(e.Stream == "*" ? e.Category : e.Stream)} {e.Text}", e.Level == EventLevel.Fail ? Sty.Fail : Sty.Warn, w - (tx - x));
        }
        c.Text(x, y + rows - 1, $"Attribution: {s.Attribution}", Sty.Dim, w);
    }

    private static void CommandLines(Canvas c, StreamRow? r, ViewState v, int x, int y, int w, int rows)
    {
        if (r == null) return;
        var cmds = new List<(string Label, string Cmd)> { ("consumer ", r.Detail.ConsumerCommand), ("publisher", r.Detail.PublisherCommand) };
        if (v.Wrap)
        {
            var lines = Wrap("consumer: " + r.Detail.ConsumerCommand, w).Concat(Wrap("publisher: " + r.Detail.PublisherCommand, w)).ToList();
            var start = Math.Clamp(v.CmdScroll / Math.Max(1, w), 0, Math.Max(0, lines.Count - rows));
            for (int i = 0; i < rows && start + i < lines.Count; i++) c.Text(x, y + i, lines[start + i], Sty.Normal, w);
            return;
        }
        for (int i = 0; i < rows && i < cmds.Count; i++)
        {
            var lx = x + c.Text(x, y + i, cmds[i].Label + " ", Sty.Dim);
            var cmd = cmds[i].Cmd ?? "";
            var off = Math.Clamp(v.CmdScroll, 0, Math.Max(0, cmd.Length - 1));
            var visible = cmd.Length > off ? cmd[off..] : "";
            c.Text(lx, y + i, (off > 0 ? c.G.Left.ToString() : "") + visible, Sty.Normal, w - (lx - x));
        }
        if (rows > 2) c.Text(x, y + 2, $"offset {v.CmdScroll}/{Math.Max(r.Detail.ConsumerCommand.Length, r.Detail.PublisherCommand.Length)} · Enter shows the exact ArgumentList", Sty.Dim, w);
    }

    private static void DetailLines(Canvas c, StreamRow r, DashboardSnapshot s, int x, int y, int w, int h, ViewState v)
    {
        var d = r.Detail;
        var lines = new List<(string, Sty)>
        {
            ($"{r.Id} · source {r.Source} ({r.SourceKind}) · profile {r.Profile} · pid {r.Pid} · state {r.State}", Sty.Bold),
            ($"Input: {d.InputUrl}", Sty.Normal),
            (d.HlsSummary, Sty.Normal), (d.RecordingSummary, Sty.Normal), (d.OutputCoverage, Sty.Normal), (d.SourceCompleteness, Sty.Unknown),
        };
        if (r.Attribution != null) lines.Add(($"Attribution: {r.Attribution}", Sty.Warn));
        if (d.PlayerSummary.Length > 0) lines.Add((d.PlayerSummary, Sty.Normal));
        lines.Add(($"HLS dir: {d.HlsDir}   Recording dir: {d.RecDir}", Sty.Dim));
        if (d.HttpUrl != null) lines.Add(($"HTTP: {d.HttpUrl}", Sty.Dim));
        lines.Add(("Publisher command (redacted):", Sty.Title));
        lines.AddRange(Wrap(d.PublisherCommand, w).Select(l => (l, Sty.Normal)));
        lines.Add(("Consumer command (redacted):", Sty.Title));
        lines.AddRange(Wrap(d.ConsumerCommand, w).Select(l => (l, Sty.Normal)));
        lines.Add(("Exact ArgumentList passed to ProcessStartInfo:", Sty.Title));
        lines.AddRange(d.ConsumerArgs.Select((a, i) => ($"  [{i}] {a}", Sty.Dim)));
        lines.Add(("Evidence (recent classified FFmpeg log lines):", Sty.Title));
        lines.AddRange(d.Evidence.Count == 0 ? new[] { ("  none", Sty.Dim) } : d.Evidence.Select(e => ("  " + e, Sty.Warn)));
        var start = Math.Clamp(v.DetailScroll, 0, Math.Max(0, lines.Count - h));
        for (int i = 0; i < h && start + i < lines.Count; i++) c.Text(x, y + i, lines[start + i].Item1, lines[start + i].Item2, w);
    }

    private static void DrawOverlay(Canvas c, ViewState v)
    {
        var g = c.G;
        var lines = v.Overlay == Overlay.ConfirmQuit
            ? new[] { "Stop the run and exit?", "", "Consumers stop gracefully, completed outputs are reconciled,", "publishers and the managed relay are stopped, and the report is written.", "Only processes started by StreamBench are stopped.", "", "[y] yes, stop      [n] no, keep running" }
            : new[] { "Keys", "", "1-6 / Tab   switch view (Overview, Streams, Checks, Storage, Logs, Lab)", $"{g.Up}{g.Down} j k        select stream / scroll list; PgUp PgDn Home End", "Enter       details of the selected stream (commands, ArgumentList, evidence)",
                "/           filter by text (Enter apply, Esc clear)", "s / r       cycle sort (ID, CPU, State, Latency, Backlog) / reverse", "o / v / g   output filter / incident severity / group by source|profile",
                $"{g.Left}{g.Right} h l      scroll the command horizontally; w wrap", "Space       pause the display (collection, recording and validation continue)", "q           stop & exit (confirmation); Ctrl+C stops immediately (graceful)", "", "Esc closes this help" };
        var w = Math.Min(c.W - 4, lines.Max(l => l.Length) + 6); var h = Math.Min(c.H - 2, lines.Length + 2);
        var x0 = (c.W - w) / 2; var y0 = (c.H - h) / 2;
        c.Fill(x0, y0, w, h, ' ', Sty.Overlay);
        c.Put(x0, y0, g.TL, Sty.Overlay); c.Put(x0 + w - 1, y0, g.TR, Sty.Overlay); c.Put(x0, y0 + h - 1, g.BL, Sty.Overlay); c.Put(x0 + w - 1, y0 + h - 1, g.BR, Sty.Overlay);
        for (int i = 1; i < w - 1; i++) { c.Put(x0 + i, y0, g.H, Sty.Overlay); c.Put(x0 + i, y0 + h - 1, g.H, Sty.Overlay); }
        for (int j = 1; j < h - 1; j++) { c.Put(x0, y0 + j, g.V, Sty.Overlay); c.Put(x0 + w - 1, y0 + j, g.V, Sty.Overlay); }
        for (int i = 0; i < lines.Length && i < h - 2; i++) c.Text(x0 + 3, y0 + 1 + i, lines[i], Sty.Overlay, w - 6);
    }

    // ------------------------------------------------------------------ primitives

    private static int Bar(Canvas c, int x, int y, double frac, int w, Sty st)
    {
        frac = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
        var full = (int)Math.Round(frac * w);
        for (int i = 0; i < w; i++) c.Put(x + i, y, i < full ? c.G.Full : c.G.Empty, i < full ? st : Sty.Dim);
        return w;
    }

    /// <summary>Sparkline of the last <paramref name="w"/> samples, scaled to <paramref name="max"/>.</summary>
    public static int Spark(Canvas c, int x, int y, double[] values, int w, double max, Sty st)
    {
        if (w <= 0) return 0;
        var levels = c.G.Spark;
        var start = Math.Max(0, values.Length - w); var pad = w - (values.Length - start);
        for (int i = 0; i < pad; i++) c.Put(x + i, y, ' ', Sty.Dim);
        for (int i = start; i < values.Length; i++)
        {
            var f = max <= 0 ? 0 : Math.Clamp(values[i] / max, 0, 1);
            c.Put(x + pad + i - start, y, levels[(int)Math.Round(f * (levels.Length - 1))], st);
        }
        return w;
    }

    private static void CheckLineAt(Canvas c, int x, int y, CheckLine l, int w)
    {
        var lx = x + c.Text(x, y, Label(l.Status, l.Id), StatusSty(l.Status));
        c.Text(lx + 1, y, $"{l.Name}  {l.Detail}", Sty.Normal, w - (lx - x) - 1);
    }

    public static string Label(TestStatus s, string? special = null) =>
        special == "N/A" ? "N/A " : s switch { TestStatus.Pass => "PASS", TestStatus.Warn => "WARN", TestStatus.Fail => "FAIL", _ => "UNKN" };

    public static Sty StatusSty(TestStatus s) => s switch { TestStatus.Pass => Sty.Pass, TestStatus.Warn => Sty.Warn, TestStatus.Fail => Sty.Fail, _ => Sty.Unknown };
    private static Sty OutSty(string s) => s switch { "PASS" => Sty.Pass, "FAIL" => Sty.Fail, "LATE" or "BKLG" => Sty.Warn, _ => Sty.Dim };
    private static int Rank(TestStatus s) => s switch { TestStatus.Fail => 3, TestStatus.Warn => 2, TestStatus.Unknown => 1, _ => 0 };
    private static double Max(double[] xs) => xs.Length == 0 ? 0 : xs.Max();
    public static string Fmt(TimeSpan t) => t.TotalHours >= 100 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    private static string Shorten(string? s, int n) => s == null ? "" : s.Length <= n ? s : n <= 1 ? s[..n] : s[..(n - 1)] + "~";
    private static bool Match(string filter, params string?[] fields) => filter.Length == 0 || fields.Any(f => f != null && f.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public static int Offset(int selected, int count, int visible)
    {
        if (visible <= 0 || count <= visible) return 0;
        return Math.Clamp(selected - visible / 2, 0, count - visible);
    }

    public static List<string> Wrap(string text, int width)
    {
        var res = new List<string>();
        if (width < 8) width = 8;
        foreach (var para in (text ?? "").Split('\n'))
        {
            var rest = para;
            while (rest.Length > width)
            {
                var cut = rest.LastIndexOf(' ', width);
                if (cut < width / 3) cut = width;
                res.Add(rest[..cut]);
                rest = rest[cut..].TrimStart();
            }
            res.Add(rest);
        }
        return res;
    }

    public static string Num(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
}
