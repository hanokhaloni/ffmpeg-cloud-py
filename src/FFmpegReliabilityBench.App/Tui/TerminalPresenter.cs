using System.Runtime.InteropServices;
using System.Text;

namespace FFmpegReliabilityBench.App.Tui;

/// <summary>Full-screen presenter: alternate screen, hidden cursor, only changed rows are rewritten, full redraw on resize.</summary>
public sealed class TerminalPresenter
{
    private readonly TextWriter _out; private readonly bool _color;
    private string[]? _prev; private int _w = -1, _h = -1; private bool _active;
    public TerminalPresenter(TextWriter output, bool color) { _out = output; _color = color; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>Windows needs VT processing enabled for ANSI sequences (Windows 10 / Server 2016+).</summary>
    public static bool EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            var h = GetStdHandle(-11);
            if (!GetConsoleMode(h, out var mode)) return false;
            return (mode & 4) != 0 || SetConsoleMode(h, mode | 4 /* ENABLE_VIRTUAL_TERMINAL_PROCESSING */);
        }
        catch { return false; }
    }

    public void Start()
    {
        _out.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H");
        _out.Flush();
        _active = true;
    }

    public void Invalidate() => _prev = null;

    public void Present(Canvas c)
    {
        if (!_active) return;
        var sb = new StringBuilder(c.W * c.H * 2);
        if (c.W != _w || c.H != _h) { sb.Append("\x1b[0m\x1b[2J"); _prev = null; _w = c.W; _h = c.H; }
        _prev ??= new string[c.H];
        for (int y = 0; y < c.H; y++)
        {
            var row = Styled(c, y);
            if (_prev[y] == row) continue;
            _prev[y] = row;
            sb.Append("\x1b[").Append(y + 1).Append(";1H").Append(row).Append("\x1b[0m");
        }
        if (sb.Length == 0) return;
        _out.Write(sb.ToString());
        _out.Flush();
    }

    /// <summary>Restores the terminal (main screen, cursor, attributes).</summary>
    public void Stop()
    {
        if (!_active) return;
        _active = false;
        try { _out.Write("\x1b[0m\x1b[?25h\x1b[?1049l"); _out.Flush(); } catch { }
    }

    private string Styled(Canvas c, int y)
    {
        var sb = new StringBuilder(c.W + 32);
        Sty? cur = null;
        for (int x = 0; x < c.W; x++)
        {
            var st = c.StyleAt(x, y);
            if (st != cur) { sb.Append("\x1b[0m"); var code = Code(st); if (code.Length > 0) sb.Append("\x1b[").Append(code).Append('m'); cur = st; }
            sb.Append(c.CharAt(x, y));
        }
        return sb.ToString();
    }

    public string Code(Sty s)
    {
        if (!_color) return s is Sty.Sel or Sty.Overlay ? "7" : s is Sty.Pass or Sty.Warn or Sty.Fail or Sty.Title ? "1" : "";
        return s switch
        {
            Sty.Dim => "90", Sty.Bold => "1", Sty.Border => "36", Sty.Title => "1;97", Sty.Pass => "1;32", Sty.Warn => "1;33", Sty.Fail => "1;31",
            Sty.Unknown => "1;37", Sty.Sel => "7", Sty.Key => "1;36", Sty.G1 => "32", Sty.G2 => "35", Sty.G3 => "33", Sty.G4 => "94", Sty.Header => "1;37",
            Sty.Overlay => "1;97;44", _ => ""
        };
    }
}
