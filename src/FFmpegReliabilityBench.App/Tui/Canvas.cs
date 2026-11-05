namespace FFmpegReliabilityBench.App.Tui;

public enum Sty : byte { Normal, Dim, Bold, Border, Title, Pass, Warn, Fail, Unknown, Sel, Key, G1, G2, G3, G4, Header, Overlay }

/// <summary>Box/bar/graph glyphs. ASCII fallback for consoles without Unicode.</summary>
public sealed class Glyphs
{
    public char TL, TR, BL, BR, H, V, LT, RT, TT, BT, X, Full, Empty, Ellipsis, Up, Down, Left, Right;
    public string Spark = "";
    public bool Ascii;

    public static readonly Glyphs Unicode = new()
    {
        TL = '┌', TR = '┐', BL = '└', BR = '┘', H = '─', V = '│', LT = '├', RT = '┤', TT = '┬', BT = '┴', X = '┼',
        Full = '█', Empty = '░', Ellipsis = '…', Spark = "▁▂▃▄▅▆▇█", Up = '↑', Down = '↓', Left = '←', Right = '→'
    };
    public static readonly Glyphs AsciiSet = new()
    {
        TL = '+', TR = '+', BL = '+', BR = '+', H = '-', V = '|', LT = '+', RT = '+', TT = '+', BT = '+', X = '+',
        Full = '#', Empty = '.', Ellipsis = '~', Spark = "_.:-=+*#", Up = '^', Down = 'v', Left = '<', Right = '>', Ascii = true
    };
}

/// <summary>Clipped character/style frame buffer. Drawing outside the canvas is ignored (never throws).</summary>
public sealed class Canvas
{
    private readonly char[] _c; private readonly Sty[] _s;
    public int W { get; }
    public int H { get; }
    public Glyphs G { get; }

    public Canvas(int w, int h, Glyphs g)
    {
        W = Math.Max(1, w); H = Math.Max(1, h); G = g;
        _c = new char[W * H]; _s = new Sty[W * H];
        Array.Fill(_c, ' ');
    }

    public void Put(int x, int y, char ch, Sty st)
    {
        if (x < 0 || y < 0 || x >= W || y >= H) return;
        if (char.IsControl(ch)) ch = ' ';
        if (G.Ascii && ch > 127) ch = ToAscii(ch);
        _c[y * W + x] = ch; _s[y * W + x] = st;
    }

    /// <summary>Writes text clipped to <paramref name="max"/> columns (ellipsis when truncated). Returns columns used.</summary>
    public int Text(int x, int y, string? s, Sty st, int max = int.MaxValue)
    {
        if (string.IsNullOrEmpty(s) || y < 0 || y >= H) return 0;
        max = Math.Min(max, W - x);
        if (max <= 0) return 0;
        var t = s.Length > max ? (max > 1 ? s[..(max - 1)] + G.Ellipsis : s[..max]) : s;
        for (int i = 0; i < t.Length; i++) Put(x + i, y, t[i], st);
        return t.Length;
    }

    public void Fill(int x, int y, int w, int h, char ch, Sty st)
    {
        for (int j = y; j < y + h; j++) for (int i = x; i < x + w; i++) Put(i, j, ch, st);
    }

    public void StyleRange(int x, int y, int w, Sty st)
    {
        if (y < 0 || y >= H) return;
        for (int i = Math.Max(0, x); i < Math.Min(W, x + w); i++) _s[y * W + i] = st;
    }

    public void HLine(int x, int y, int w, Sty st) { for (int i = x; i < x + w; i++) Put(i, y, G.H, st); }
    public void VLine(int x, int y, int h, Sty st) { for (int j = y; j < y + h; j++) Put(x, j, G.V, st); }

    /// <summary>Separator row across the frame: ├────┤ with an optional title and optional column junctions.</summary>
    public void Separator(int y, string? title, Sty st, params (int X, char Ch)[] junctions)
    {
        Put(0, y, G.LT, st); HLine(1, y, W - 2, st); Put(W - 1, y, G.RT, st);
        foreach (var (x, ch) in junctions) Put(x, y, ch, st);
        if (!string.IsNullOrEmpty(title)) Text(2, y, $" {title} ", Sty.Title, W - 4);
    }

    public void Frame(Sty st)
    {
        Put(0, 0, G.TL, st); HLine(1, 0, W - 2, st); Put(W - 1, 0, G.TR, st);
        for (int y = 1; y < H - 1; y++) { Put(0, y, G.V, st); Put(W - 1, y, G.V, st); }
        Put(0, H - 1, G.BL, st); HLine(1, H - 1, W - 2, st); Put(W - 1, H - 1, G.BR, st);
    }

    /// <summary>ASCII fallback for any text content (labels, data, commands), not only box glyphs.</summary>
    public static char ToAscii(char ch) => ch switch
    {
        '·' or '•' or '─' or '━' or '–' or '—' => '-', '│' or '┃' => '|', '×' => 'x', '→' or '›' or '≥' => '>', '←' or '‹' or '≤' => '<',
        '↑' => '^', '↓' => 'v', '…' => '~', '▶' => '>', '█' => '#', '░' => '.', '±' => '+', '°' => 'o',
        _ when ch is >= '┌' and <= '╿' => '+',
        _ when ch is >= '▁' and <= '▇' => '=',
        _ => '?'
    };

    public char CharAt(int x, int y) => _c[y * W + x];
    public Sty StyleAt(int x, int y) => _s[y * W + x];
    public string Row(int y) => new(_c, y * W, W);
    public IEnumerable<string> Lines() { for (int y = 0; y < H; y++) yield return Row(y); }
    public override string ToString() => string.Join("\n", Lines());
}
