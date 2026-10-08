using System.Text;

namespace MatrixTerminal;

internal struct VtCell
{
    public const int Bold = 1, Inverse = 2, Underline = 4;

    public char Ch;
    public uint Fg;   // 0 = theme default, otherwise 0xFFRRGGBB
    public uint Bg;   // 0 = theme default, otherwise 0xFFRRGGBB
    public int Attr;

    public readonly bool IsBlank => (Ch == ' ' || Ch == '\0') && Bg == 0 && (Attr & Inverse) == 0 && (Attr & Underline) == 0;
    public readonly bool SameStyle(VtCell o) => Fg == o.Fg && Bg == o.Bg && Attr == o.Attr;
}

/// <summary>
/// Minimal VT100/xterm screen model. ConPTY does not stream plain text: it repaints a screen using
/// cursor-positioning, erase and scroll sequences, so a real cell grid is required for correct output.
/// All members must be used from a single (UI) thread.
/// </summary>
internal sealed class VtScreen
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX => _cx;
    public int CursorY => _cy;
    public bool CursorVisible { get; private set; } = true;
    public string Title { get; private set; } = "";

    public event Action<VtCell[]>? LineScrolledOff;
    public event Action<string>? Reply;
    public event Action<string>? TitleChanged;

    private VtCell[][] _grid;
    private VtCell[][]? _mainGrid;
    private int _cx, _cy, _sx, _sy, _top, _bottom, _mainCx, _mainCy;
    private bool _wrapPending, _alt;
    private VtCell _pen = new() { Ch = ' ' };
    private enum State { Ground, Esc, EscSkip, Csi, Osc, OscEsc, Str, StrEsc }
    private State _state = State.Ground;
    private readonly StringBuilder _buf = new();

    private static readonly uint[] Palette =
    {
        0xFF0C0C0C, 0xFFC50F1F, 0xFF13A10E, 0xFFC19C00, 0xFF0037DA, 0xFF881798, 0xFF3A96DD, 0xFFCCCCCC,
        0xFF767676, 0xFFE74856, 0xFF16C60C, 0xFFF9F1A5, 0xFF3B78FF, 0xFFB4009E, 0xFF61D6D6, 0xFFF2F2F2,
    };

    public VtScreen(int cols, int rows)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _top = 0; _bottom = Rows - 1;
        _grid = NewGrid();
    }

    public VtCell[] Row(int r) => _grid[r];

    // ---------------------------------------------------------------- input

    public void Write(string s)
    {
        foreach (var c in s) Feed(c);
    }

    private void Feed(char c)
    {
        switch (_state)
        {
            case State.Ground: Ground(c); break;
            case State.Esc: EscChar(c); break;
            case State.EscSkip: _state = State.Ground; break;
            case State.Csi: CsiChar(c); break;
            case State.Osc:
                if (c == '\a') EndOsc();
                else if (c == '\u001b') _state = State.OscEsc;
                else if (_buf.Length < 2048) _buf.Append(c);
                break;
            case State.OscEsc: EndOsc(); break; // ESC \ (ST)
            case State.Str:
                if (c == '\a') _state = State.Ground;
                else if (c == '\u001b') _state = State.StrEsc;
                break;
            case State.StrEsc: _state = State.Ground; break;
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\u001b': _state = State.Esc; return;
            case '\r': _cx = 0; _wrapPending = false; return;
            case '\n': case '\u000b': case '\u000c': LineFeed(); _wrapPending = false; return;
            case '\b': if (_cx > 0) _cx--; _wrapPending = false; return;
            case '\t': _cx = Math.Min(((_cx / 8) + 1) * 8, Cols - 1); _wrapPending = false; return;
        }
        if (c < ' ' || c == '\u007f') return;
        Put(c);
    }

    private void EscChar(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': _buf.Clear(); _state = State.Csi; break;
            case ']': _buf.Clear(); _state = State.Osc; break;
            case 'P': case 'X': case '^': case '_': _state = State.Str; break;
            case '(': case ')': case '*': case '+': case '#': case '%': case ' ': _state = State.EscSkip; break;
            case '7': _sx = _cx; _sy = _cy; break;
            case '8': _cx = Math.Clamp(_sx, 0, Cols - 1); _cy = Math.Clamp(_sy, 0, Rows - 1); _wrapPending = false; break;
            case 'D': LineFeed(); break;
            case 'E': _cx = 0; LineFeed(); break;
            case 'M': if (_cy == _top) ScrollDown(1); else if (_cy > 0) _cy--; break;
            case 'c': Reset(); break;
            case '\u001b': _state = State.Esc; break;
        }
    }

    private void EndOsc()
    {
        _state = State.Ground;
        var s = _buf.ToString();
        _buf.Clear();
        var i = s.IndexOf(';');
        if (i > 0 && (s[..i] == "0" || s[..i] == "2"))
        {
            Title = s[(i + 1)..];
            TitleChanged?.Invoke(Title);
        }
    }

    private void CsiChar(char c)
    {
        if (c >= '@' && c <= '~') { _state = State.Ground; ExecCsi(c); }
        else if (c == '\u001b') { _buf.Clear(); _state = State.Esc; }
        else if (c >= ' ' && c <= '?') { if (_buf.Length < 256) _buf.Append(c); }
    }

    // ------------------------------------------------------------- CSI

    private void ExecCsi(char final)
    {
        var raw = _buf.ToString();
        _buf.Clear();

        var priv = '\0';
        if (raw.Length > 0 && (raw[0] is '?' or '>' or '=' or '<')) { priv = raw[0]; raw = raw[1..]; }
        foreach (var ch in raw) if (ch < '0') return; // sequences with intermediates (DECSCUSR, soft reset...) are ignored

        var args = new List<int>();
        if (raw.Length > 0)
            foreach (var part in raw.Replace(':', ';').Split(';'))
                args.Add(part.Length == 0 ? -1 : (int.TryParse(part, out var v) ? v : -1));

        int Arg(int i, int def) => i < args.Count && args[i] >= 0 ? args[i] : def;
        int N(int i) => Math.Max(1, Arg(i, 1));

        if (priv != '\0')
        {
            if (final is 'h' or 'l') SetModes(args, final == 'h');
            return;
        }

        if (final != 'm') _wrapPending = false;

        switch (final)
        {
            case 'A': _cy = Math.Max(0, _cy - N(0)); break;
            case 'B': _cy = Math.Min(Rows - 1, _cy + N(0)); break;
            case 'C': _cx = Math.Min(Cols - 1, _cx + N(0)); break;
            case 'D': _cx = Math.Max(0, _cx - N(0)); break;
            case 'E': _cx = 0; _cy = Math.Min(Rows - 1, _cy + N(0)); break;
            case 'F': _cx = 0; _cy = Math.Max(0, _cy - N(0)); break;
            case 'G': case '`': _cx = Math.Clamp(N(0) - 1, 0, Cols - 1); break;
            case 'H': case 'f': _cy = Math.Clamp(N(0) - 1, 0, Rows - 1); _cx = Math.Clamp(N(1) - 1, 0, Cols - 1); break;
            case 'd': _cy = Math.Clamp(N(0) - 1, 0, Rows - 1); break;
            case 'J':
                switch (Arg(0, 0))
                {
                    case 0:
                        Fill(_grid[_cy], _cx, Cols);
                        for (var r = _cy + 1; r < Rows; r++) _grid[r] = NewRow();
                        break;
                    case 1:
                        for (var r = 0; r < _cy; r++) _grid[r] = NewRow();
                        Fill(_grid[_cy], 0, _cx + 1);
                        break;
                    default:
                        for (var r = 0; r < Rows; r++) _grid[r] = NewRow();
                        break;
                }
                break;
            case 'K':
                switch (Arg(0, 0))
                {
                    case 0: Fill(_grid[_cy], _cx, Cols); break;
                    case 1: Fill(_grid[_cy], 0, _cx + 1); break;
                    default: Fill(_grid[_cy], 0, Cols); break;
                }
                break;
            case 'L':
                if (_cy >= _top && _cy <= _bottom)
                {
                    for (var k = Math.Min(N(0), _bottom - _cy + 1); k > 0; k--)
                    {
                        for (var r = _bottom; r > _cy; r--) _grid[r] = _grid[r - 1];
                        _grid[_cy] = NewRow();
                    }
                    _cx = 0;
                }
                break;
            case 'M':
                if (_cy >= _top && _cy <= _bottom)
                {
                    for (var k = Math.Min(N(0), _bottom - _cy + 1); k > 0; k--)
                    {
                        for (var r = _cy; r < _bottom; r++) _grid[r] = _grid[r + 1];
                        _grid[_bottom] = NewRow();
                    }
                    _cx = 0;
                }
                break;
            case '@':
            {
                var n = Math.Min(N(0), Cols - _cx);
                var row = _grid[_cy];
                Array.Copy(row, _cx, row, _cx + n, Cols - _cx - n);
                Fill(row, _cx, _cx + n);
                break;
            }
            case 'P':
            {
                var n = Math.Min(N(0), Cols - _cx);
                var row = _grid[_cy];
                Array.Copy(row, _cx + n, row, _cx, Cols - _cx - n);
                Fill(row, Cols - n, Cols);
                break;
            }
            case 'X': Fill(_grid[_cy], _cx, Math.Min(Cols, _cx + N(0))); break;
            case 'S': ScrollUp(N(0)); break;
            case 'T': ScrollDown(N(0)); break;
            case 'm': Sgr(args); break;
            case 'r':
            {
                var t = Math.Max(0, Arg(0, 1) - 1);
                var b = Math.Min(Rows - 1, Arg(1, Rows) - 1);
                if (t < b) { _top = t; _bottom = b; } else { _top = 0; _bottom = Rows - 1; }
                _cx = 0; _cy = 0;
                break;
            }
            case 's': _sx = _cx; _sy = _cy; break;
            case 'u': _cx = Math.Clamp(_sx, 0, Cols - 1); _cy = Math.Clamp(_sy, 0, Rows - 1); break;
            case 'n':
                if (Arg(0, 0) == 6) Reply?.Invoke($"\u001b[{_cy + 1};{_cx + 1}R"); // ConPTY asks for the cursor position
                else if (Arg(0, 0) == 5) Reply?.Invoke("\u001b[0n");
                break;
            case 'c': Reply?.Invoke("\u001b[?1;0c"); break;
        }
    }

    private void SetModes(List<int> args, bool on)
    {
        foreach (var m in args)
        {
            switch (m)
            {
                case 25: CursorVisible = on; break;
                case 47: case 1047: case 1049: if (on) EnterAlt(); else ExitAlt(); break;
            }
        }
    }

    private void Sgr(List<int> args)
    {
        if (args.Count == 0) { _pen = new VtCell { Ch = ' ' }; return; }
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i] < 0 ? 0 : args[i];
            switch (a)
            {
                case 0: _pen = new VtCell { Ch = ' ' }; break;
                case 1: _pen.Attr |= VtCell.Bold; break;
                case 22: _pen.Attr &= ~VtCell.Bold; break;
                case 4: _pen.Attr |= VtCell.Underline; break;
                case 24: _pen.Attr &= ~VtCell.Underline; break;
                case 7: _pen.Attr |= VtCell.Inverse; break;
                case 27: _pen.Attr &= ~VtCell.Inverse; break;
                case >= 30 and <= 37: _pen.Fg = Palette[a - 30]; break;
                case 39: _pen.Fg = 0; break;
                case >= 40 and <= 47: _pen.Bg = Palette[a - 40]; break;
                case 49: _pen.Bg = 0; break;
                case >= 90 and <= 97: _pen.Fg = Palette[a - 90 + 8]; break;
                case >= 100 and <= 107: _pen.Bg = Palette[a - 100 + 8]; break;
                case 38:
                case 48:
                {
                    i = ReadExtColor(args, i, out var col);
                    if (col != 0) { if (a == 38) _pen.Fg = col; else _pen.Bg = col; }
                    break;
                }
            }
        }
    }

    private static int ReadExtColor(List<int> args, int i, out uint color)
    {
        color = 0;
        if (i + 1 >= args.Count) return args.Count;
        var mode = args[i + 1];
        if (mode == 5 && i + 2 < args.Count) { color = Xterm256(Math.Max(0, args[i + 2])); return i + 2; }
        if (mode == 2 && i + 4 < args.Count)
        {
            static uint C(int v) => (uint)Math.Clamp(v, 0, 255);
            color = 0xFF000000u | (C(args[i + 2]) << 16) | (C(args[i + 3]) << 8) | C(args[i + 4]);
            return i + 4;
        }
        return args.Count;
    }

    private static uint Xterm256(int n)
    {
        if (n < 16) return Palette[n];
        if (n >= 232) { var g = (uint)Math.Min(255, 8 + 10 * (n - 232)); return 0xFF000000u | (g << 16) | (g << 8) | g; }
        n -= 16;
        static uint L(int v) => v == 0 ? 0u : (uint)(55 + 40 * v);
        return 0xFF000000u | (L(n / 36) << 16) | (L(n / 6 % 6) << 8) | L(n % 6);
    }

    // ---------------------------------------------------------- grid ops

    private VtCell Blank() => new() { Ch = ' ', Bg = _pen.Bg };

    private VtCell[] NewRow()
    {
        var row = new VtCell[Cols];
        Array.Fill(row, Blank());
        return row;
    }

    private VtCell[][] NewGrid()
    {
        var g = new VtCell[Rows][];
        for (var r = 0; r < Rows; r++) g[r] = NewRow();
        return g;
    }

    private void Fill(VtCell[] row, int from, int toExclusive)
    {
        var blank = Blank();
        for (var i = Math.Max(0, from); i < Math.Min(row.Length, toExclusive); i++) row[i] = blank;
    }

    private void Put(char c)
    {
        if (_wrapPending) { _cx = 0; LineFeed(); _wrapPending = false; }
        var cell = _pen;
        cell.Ch = c;
        _grid[_cy][_cx] = cell;
        if (_cx >= Cols - 1) _wrapPending = true; else _cx++;
    }

    private void LineFeed()
    {
        if (_cy == _bottom) ScrollUp(1);
        else if (_cy < Rows - 1) _cy++;
    }

    private void ScrollUp(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            if (_top == 0 && !_alt) LineScrolledOff?.Invoke(_grid[_top]);
            for (var r = _top; r < _bottom; r++) _grid[r] = _grid[r + 1];
            _grid[_bottom] = NewRow();
        }
    }

    private void ScrollDown(int n)
    {
        n = Math.Min(n, _bottom - _top + 1);
        for (var i = 0; i < n; i++)
        {
            for (var r = _bottom; r > _top; r--) _grid[r] = _grid[r - 1];
            _grid[_top] = NewRow();
        }
    }

    private void EnterAlt()
    {
        if (_alt) return;
        _alt = true;
        _mainGrid = _grid; _mainCx = _cx; _mainCy = _cy;
        _grid = NewGrid();
        _top = 0; _bottom = Rows - 1;
    }

    private void ExitAlt()
    {
        if (!_alt) return;
        _alt = false;
        _grid = _mainGrid ?? NewGrid();
        _mainGrid = null;
        _cx = Math.Clamp(_mainCx, 0, Cols - 1);
        _cy = Math.Clamp(_mainCy, 0, Rows - 1);
        _top = 0; _bottom = Rows - 1;
        _wrapPending = false;
    }

    private void Reset()
    {
        _pen = new VtCell { Ch = ' ' };
        _alt = false; _mainGrid = null;
        _top = 0; _bottom = Rows - 1;
        _cx = _cy = _sx = _sy = 0;
        _wrapPending = false;
        CursorVisible = true;
        _grid = NewGrid();
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols); rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        var shift = 0;
        if (!_alt && _cy >= rows) shift = _cy - rows + 1; // keep the cursor line visible; older lines go to scrollback
        for (var i = 0; i < shift; i++) LineScrolledOff?.Invoke(_grid[i]);

        _grid = Reflow(_grid, cols, rows, shift);
        if (_mainGrid != null) _mainGrid = Reflow(_mainGrid, cols, rows, 0);

        Cols = cols; Rows = rows;
        _cy = Math.Clamp(_cy - shift, 0, rows - 1);
        _cx = Math.Clamp(_cx, 0, cols - 1);
        _sx = Math.Clamp(_sx, 0, cols - 1); _sy = Math.Clamp(_sy, 0, rows - 1);
        _mainCx = Math.Clamp(_mainCx, 0, cols - 1); _mainCy = Math.Clamp(_mainCy, 0, rows - 1);
        _top = 0; _bottom = rows - 1;
        _wrapPending = false;
    }

    private static VtCell[][] Reflow(VtCell[][] old, int cols, int rows, int dropTop)
    {
        var g = new VtCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            var row = new VtCell[cols];
            Array.Fill(row, new VtCell { Ch = ' ' });
            var src = r + dropTop;
            if (src < old.Length) Array.Copy(old[src], row, Math.Min(cols, old[src].Length));
            g[r] = row;
        }
        return g;
    }
}
