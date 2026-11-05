using System.Collections.Concurrent;
using FFmpegReliabilityBench.Core;
using FFmpegReliabilityBench.Core.Config;
using FFmpegReliabilityBench.Core.Engine;

namespace FFmpegReliabilityBench.App.Tui;

/// <summary>
/// Single UI owner: a dedicated thread renders immutable snapshots at the configured rate and applies keyboard commands.
/// Confirmations are overlays inside the dashboard state machine (no nested console prompts). Rendering problems never
/// propagate into collection or supervision; pausing freezes the display only.
/// </summary>
public sealed class TuiController : ITuiController, IUiCommandDispatcher
{
    private readonly IDashboardStateProvider _provider; private readonly BenchState _st; private readonly UiCfg _cfg; private readonly Glyphs _g;
    private readonly TerminalPresenter _presenter;
    private readonly BlockingCollection<object> _queue = new();
    private readonly ViewState _v = new();
    private DashboardSnapshot? _snap;
    private bool _exit;

    public Func<(int W, int H)> SizeProvider { get; init; } = () => (Console.WindowWidth, Console.WindowHeight);
    public bool ReadConsoleKeys { get; init; } = true;
    public double ExitDelayAfterFinishSeconds { get; init; } = 2;
    public ViewState View => _v;
    public DashboardSnapshot? Snapshot => _snap;
    public long Frames;
    public Exception? LastRenderError { get; private set; }
    public Canvas? LastFrame { get; private set; }

    public TuiController(IDashboardStateProvider provider, BenchState st, UiCfg cfg, Glyphs g, TerminalPresenter presenter)
    { _provider = provider; _st = st; _cfg = cfg; _g = g; _presenter = presenter; }

    public ValueTask DispatchAsync(UiCommand command, CancellationToken cancellationToken) { _queue.Add(command, cancellationToken); return ValueTask.CompletedTask; }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { Loop(cancellationToken); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "streambench-tui" };
        t.Start();
        return tcs.Task;
    }

    private void Loop(CancellationToken ct)
    {
        _presenter.Start();
        if (ReadConsoleKeys) StartInputThread();
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(_cfg.RefreshHz <= 0 ? 4 : _cfg.RefreshHz, 1, 30));
        DateTime? finishedAt = null;
        try
        {
            while (!ct.IsCancellationRequested && !_exit)
            {
                if (!_v.Paused || _snap == null)
                {
                    try { _snap = _provider.GetSnapshot(); } catch (Exception ex) { LastRenderError = ex; }
                }
                RenderOnce();
                if (_st.Finished)
                {
                    finishedAt ??= DateTime.UtcNow;
                    if ((DateTime.UtcNow - finishedAt.Value).TotalSeconds >= ExitDelayAfterFinishSeconds) { _snap = SafeSnapshot(); RenderOnce(); break; }
                }
                if (_queue.TryTake(out var item, interval))
                {
                    Handle(item);
                    while (_queue.TryTake(out item)) Handle(item);
                    RenderOnce();
                }
            }
        }
        finally { _presenter.Stop(); }
    }

    private DashboardSnapshot? SafeSnapshot() { try { return _provider.GetSnapshot(); } catch { return _snap; } }

    public void RenderOnce()
    {
        if (_snap == null) return;
        try
        {
            var (tw, h) = SizeProvider();
            var w = Math.Max(20, tw - 1); h = Math.Max(5, h); // never write the bottom-right cell (avoids scrolling on legacy consoles)
            ClampSelection();
            var frame = Renderer.Render(_snap, _v, w, h, _g, tw);
            LastFrame = frame;
            _presenter.Present(frame);
            Interlocked.Increment(ref Frames);
        }
        catch (Exception ex) { LastRenderError = ex; _presenter.Invalidate(); }
    }

    private void ClampSelection()
    {
        if (_snap == null) return;
        var n = Renderer.VisibleRows(_snap, _v).Count;
        _v.Selected = Math.Clamp(_v.Selected, 0, Math.Max(0, n - 1));
        _v.ListOffset = Math.Max(0, _v.ListOffset);
    }

    private void StartInputThread()
    {
        new Thread(() =>
        {
            try { while (!_exit) _queue.Add(Console.ReadKey(intercept: true)); }
            catch { /* no console input */ }
        }) { IsBackground = true, Name = "streambench-input" }.Start();
    }

    private void Handle(object item)
    {
        if (item is ConsoleKeyInfo k) { if (KeyMap.Map(k, _v) is { } cmd) Apply(cmd); }
        else if (item is UiCommand c) Apply(c);
    }

    /// <summary>The dashboard state machine. Commands only change view state, except Confirm (requests the graceful stop).</summary>
    public void Apply(UiCommand c)
    {
        switch (_v.Overlay)
        {
            case Overlay.ConfirmQuit:
                if (c.Kind == UiCommandKind.ConfirmYes)
                {
                    _v.Overlay = Overlay.None; _v.QuitRequested = true;
                    if (!_st.StopRequested) { _st.RequestStop(); _st.Bus.Publish(EventLevel.Info, "Session", "*", "graceful stop requested from the dashboard"); }
                }
                else if (c.Kind is UiCommandKind.ConfirmNo or UiCommandKind.Escape) _v.Overlay = Overlay.None;
                return;
            case Overlay.Help:
                _v.Overlay = Overlay.None; return;
        }
        if (_v.FilterEditing)
        {
            switch (c.Kind)
            {
                case UiCommandKind.FilterChar: _v.FilterDraft += c.Ch; break;
                case UiCommandKind.FilterBackspace: if (_v.FilterDraft.Length > 0) _v.FilterDraft = _v.FilterDraft[..^1]; break;
                case UiCommandKind.FilterAccept: _v.Filter = _v.FilterDraft; _v.FilterEditing = false; _v.Selected = 0; _v.ListOffset = 0; break;
                case UiCommandKind.Escape: _v.FilterEditing = false; _v.FilterDraft = ""; _v.Filter = ""; break;
            }
            return;
        }
        var list = _v.Tab is 3 or 4 or 5 or 6;
        switch (c.Kind)
        {
            case UiCommandKind.Tab: _v.Tab = Math.Clamp(c.Arg, 1, 6); _v.ListOffset = 0; _v.Details = false; break;
            case UiCommandKind.NextTab: _v.Tab = _v.Tab % 6 + 1; _v.ListOffset = 0; _v.Details = false; break;
            case UiCommandKind.Down: Move(+1, list); break;
            case UiCommandKind.Up: Move(-1, list); break;
            case UiCommandKind.PageDown: Move(+10, list); break;
            case UiCommandKind.PageUp: Move(-10, list); break;
            case UiCommandKind.Home: if (_v.Details) _v.DetailScroll = 0; else if (list) _v.ListOffset = 0; else _v.Selected = 0; break;
            case UiCommandKind.End: if (list) _v.ListOffset = _v.Tab == 5 ? 0 : 100000; else _v.Selected = 100000; break;
            case UiCommandKind.ToggleDetails: if (_v.Tab is 1 or 2) { _v.Details = !_v.Details; _v.DetailScroll = 0; } break;
            case UiCommandKind.Escape: if (_v.Details) _v.Details = false; else _v.Filter = ""; break;
            case UiCommandKind.StartFilter: _v.FilterEditing = true; _v.FilterDraft = _v.Filter; break;
            case UiCommandKind.CycleSort: _v.Sort = (SortKey)(((int)_v.Sort + 1) % Enum.GetValues<SortKey>().Length); break;
            case UiCommandKind.ReverseSort: _v.SortDesc = !_v.SortDesc; break;
            case UiCommandKind.CycleOutputFilter: _v.Output = (OutputFilter)(((int)_v.Output + 1) % 3); _v.Selected = 0; break;
            case UiCommandKind.CycleSeverity: _v.Severity = (SeverityFilter)(((int)_v.Severity + 1) % 3); break;
            case UiCommandKind.CycleGroup: _v.Group = (GroupBy)(((int)_v.Group + 1) % 3); break;
            case UiCommandKind.ScrollLeft: _v.CmdScroll = Math.Max(0, _v.CmdScroll - 20); break;
            case UiCommandKind.ScrollRight: _v.CmdScroll += 20; break;
            case UiCommandKind.ToggleWrap: _v.Wrap = !_v.Wrap; _v.CmdScroll = 0; break;
            case UiCommandKind.TogglePause: _v.Paused = !_v.Paused; break;
            case UiCommandKind.Help: _v.Overlay = Overlay.Help; break;
            case UiCommandKind.Redraw: _presenter.Invalidate(); break;
            case UiCommandKind.RequestQuit:
                if (_st.Finished) _exit = true;
                else if (_v.QuitRequested) { /* already stopping */ }
                else _v.Overlay = Overlay.ConfirmQuit;
                break;
        }
    }

    private void Move(int d, bool list)
    {
        if (_v.Details) { _v.DetailScroll = Math.Max(0, _v.DetailScroll + d); return; }
        if (list) _v.ListOffset = Math.Max(0, _v.ListOffset + (_v.Tab == 5 ? -d : d)); // logs: up scrolls back in time
        else _v.Selected = Math.Max(0, _v.Selected + d);
    }
}

public static class KeyMap
{
    public static UiCommand? Map(ConsoleKeyInfo k, ViewState v)
    {
        if (v.Overlay == Overlay.ConfirmQuit)
            return k.KeyChar is 'y' or 'Y' ? new UiCommand(UiCommandKind.ConfirmYes) : k.KeyChar is 'n' or 'N' || k.Key == ConsoleKey.Escape ? new UiCommand(UiCommandKind.ConfirmNo) : null;
        if (v.Overlay == Overlay.Help) return new UiCommand(UiCommandKind.Escape);
        if (v.FilterEditing)
            return k.Key switch
            {
                ConsoleKey.Enter => new UiCommand(UiCommandKind.FilterAccept),
                ConsoleKey.Escape => new UiCommand(UiCommandKind.Escape),
                ConsoleKey.Backspace => new UiCommand(UiCommandKind.FilterBackspace),
                _ => char.IsControl(k.KeyChar) ? null : new UiCommand(UiCommandKind.FilterChar, Ch: k.KeyChar)
            };
        if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.L) return new UiCommand(UiCommandKind.Redraw);
        switch (k.Key)
        {
            case ConsoleKey.DownArrow: return new UiCommand(UiCommandKind.Down);
            case ConsoleKey.UpArrow: return new UiCommand(UiCommandKind.Up);
            case ConsoleKey.PageDown: return new UiCommand(UiCommandKind.PageDown);
            case ConsoleKey.PageUp: return new UiCommand(UiCommandKind.PageUp);
            case ConsoleKey.Home: return new UiCommand(UiCommandKind.Home);
            case ConsoleKey.End: return new UiCommand(UiCommandKind.End);
            case ConsoleKey.LeftArrow: return new UiCommand(UiCommandKind.ScrollLeft);
            case ConsoleKey.RightArrow: return new UiCommand(UiCommandKind.ScrollRight);
            case ConsoleKey.Enter: return new UiCommand(UiCommandKind.ToggleDetails);
            case ConsoleKey.Escape: return new UiCommand(UiCommandKind.Escape);
            case ConsoleKey.Tab: return new UiCommand(UiCommandKind.NextTab);
            case ConsoleKey.Spacebar: return new UiCommand(UiCommandKind.TogglePause);
        }
        return k.KeyChar switch
        {
            >= '1' and <= '6' => new UiCommand(UiCommandKind.Tab, k.KeyChar - '0'),
            'j' => new UiCommand(UiCommandKind.Down), 'k' => new UiCommand(UiCommandKind.Up),
            'h' => new UiCommand(UiCommandKind.ScrollLeft), 'l' => new UiCommand(UiCommandKind.ScrollRight),
            '/' => new UiCommand(UiCommandKind.StartFilter), 's' => new UiCommand(UiCommandKind.CycleSort), 'r' => new UiCommand(UiCommandKind.ReverseSort),
            'o' => new UiCommand(UiCommandKind.CycleOutputFilter), 'v' => new UiCommand(UiCommandKind.CycleSeverity), 'g' => new UiCommand(UiCommandKind.CycleGroup),
            'w' => new UiCommand(UiCommandKind.ToggleWrap), '?' => new UiCommand(UiCommandKind.Help), 'q' or 'Q' => new UiCommand(UiCommandKind.RequestQuit),
            ' ' => new UiCommand(UiCommandKind.TogglePause),
            _ => null
        };
    }
}
