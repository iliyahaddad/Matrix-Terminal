using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MatrixTerminal;

public partial class MainWindow : Window
{
    private const int MaxScrollback = 5000;

    private ConPtyTerminal? _term;
    private VtScreen _screen = new(120, 30);
    private ConcurrentQueue<string> _incoming = new();
    private readonly List<VtCell[]> _pendingScroll = new();
    private readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    private readonly DispatcherTimer _timer;

    private string _theme = "matrix";
    private string _shell = "cmd.exe";
    private SolidColorBrush _themeFg = Brushes.LimeGreen;
    private SolidColorBrush _themeBg = Brushes.Black;

    private int _sessionId;
    private int _cols = 120, _rows = 30;
    private double _charW = 8, _lineH = 18;
    private int _sbCount, _screenBlocks;
    private bool _dirty, _stick = true, _started, _exited;

    public MainWindow()
    {
        InitializeComponent();
        Output.Document.PagePadding = new Thickness(0);

        // Output is produced on a background thread, queued, and applied here on the UI thread.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background, (_, _) => Pump(), Dispatcher);

        Loaded += OnLoaded;
        Closed += (_, _) => { _timer.Stop(); _sessionId++; _term?.Dispose(); _term = null; };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        Output.SizeChanged += (_, _) => OnViewportChanged();
    }

    // ------------------------------------------------------------ lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        MeasureCell();
        ApplyTheme(_theme);
        (_cols, _rows) = ComputeSize();
        _started = true;
        StartSession("cmd.exe");
        Output.Focus();
    }

    private void StartSession(string shell)
    {
        var old = _term;
        _term = null;
        var id = ++_sessionId;
        old?.Dispose();

        _incoming = new ConcurrentQueue<string>();
        var queue = _incoming;
        _pendingScroll.Clear();
        Output.Document.Blocks.Clear();
        _sbCount = 0; _screenBlocks = 0;
        _shell = shell; _exited = false; _stick = true;

        var screen = new VtScreen(_cols, _rows);
        screen.LineScrolledOff += row => _pendingScroll.Add(row);
        screen.Reply += text => { if (id == _sessionId) _term?.Write(text); };
        screen.TitleChanged += title => Title = string.IsNullOrWhiteSpace(title) ? "Matrix Terminal" : $"{title} — Matrix Terminal";
        _screen = screen;
        Title = "Matrix Terminal";

        var t = new ConPtyTerminal();
        t.OutputReceived += s => { if (id == _sessionId) queue.Enqueue(s); };
        t.Exited += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (id != _sessionId) return;
            Pump();
            _screen.Write("\u001b[0m\r\n[process exited]\r\n");
            _exited = true; _dirty = true;
            UpdateStatus();
        }));

        try
        {
            t.Start(shell, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), (short)_cols, (short)_rows);
            _term = t;
        }
        catch (Exception ex)
        {
            t.Dispose();
            _screen.Write($"[START ERROR] {ex.Message}\r\n");
        }
        _dirty = true;
        UpdateStatus();
    }

    // ------------------------------------------------------- output pipeline

    private void Pump()
    {
        var any = false;
        while (_incoming.TryDequeue(out var chunk)) { _screen.Write(chunk); any = true; }
        if (any) _dirty = true;
        if (_dirty) Render();
    }

    private void Render()
    {
        _dirty = false;
        var doc = Output.Document;
        var atBottom = _stick || Output.VerticalOffset + Output.ViewportHeight >= Output.ExtentHeight - _lineH * 1.5;
        _stick = false;

        // 1. drop the previously drawn screen area (always the tail of the document)
        for (var i = 0; i < _screenBlocks; i++) doc.Blocks.Remove(doc.Blocks.LastBlock);
        _screenBlocks = 0;

        // 2. append lines that scrolled off the top
        var skip = Math.Max(0, _pendingScroll.Count - MaxScrollback);
        for (var i = skip; i < _pendingScroll.Count; i++) { doc.Blocks.Add(MakeParagraph(_pendingScroll[i], -1)); _sbCount++; }
        _pendingScroll.Clear();
        while (_sbCount > MaxScrollback) { doc.Blocks.Remove(doc.Blocks.FirstBlock); _sbCount--; }

        // 3. draw the live screen
        var curRow = _screen.CursorVisible && !_exited ? _screen.CursorY : -1;
        for (var r = 0; r < _screen.Rows; r++)
            doc.Blocks.Add(MakeParagraph(_screen.Row(r), r == curRow ? _screen.CursorX : -1));
        _screenBlocks = _screen.Rows;

        if (atBottom) Output.ScrollToEnd();
    }

    private Paragraph MakeParagraph(VtCell[] row, int cursorCol)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = _lineH,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        var last = row.Length - 1;
        while (last >= 0 && row[last].IsBlank) last--;
        if (cursorCol > last) last = Math.Min(cursorCol, row.Length - 1);
        if (last < 0) { p.Inlines.Add(new Run(" ")); return p; }

        var sb = new StringBuilder();
        var i = 0;
        while (i <= last)
        {
            var first = row[i];
            var isCursor = i == cursorCol;
            var j = i;
            sb.Clear();
            while (j <= last && row[j].SameStyle(first) && (j == cursorCol) == isCursor)
            {
                sb.Append(row[j].Ch == '\0' ? ' ' : row[j].Ch);
                j++;
            }
            p.Inlines.Add(MakeRun(sb.ToString(), first, isCursor));
            i = j;
        }
        return p;
    }

    private Run MakeRun(string text, VtCell c, bool cursor)
    {
        var run = new Run(text);
        var inverse = ((c.Attr & VtCell.Inverse) != 0) ^ cursor;
        Brush fg;
        Brush? bg;
        if (inverse)
        {
            fg = c.Bg == 0 ? _themeBg : BrushFor(c.Bg);
            bg = c.Fg == 0 ? _themeFg : BrushFor(c.Fg);
        }
        else
        {
            fg = c.Fg == 0 ? _themeFg : BrushFor(c.Fg);
            bg = c.Bg == 0 ? null : BrushFor(c.Bg);
        }
        run.Foreground = fg;
        if (bg != null) run.Background = bg;
        if ((c.Attr & VtCell.Bold) != 0) run.FontWeight = FontWeights.Bold;
        if ((c.Attr & VtCell.Underline) != 0) run.TextDecorations = TextDecorations.Underline;
        return run;
    }

    private SolidColorBrush BrushFor(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
        {
            b = Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            _brushes[argb] = b;
        }
        return b;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------ sizing

    private void MeasureCell()
    {
        var ft = new FormattedText(
            new string('M', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Output.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            Output.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _charW = Math.Max(1, ft.WidthIncludingTrailingWhitespace / 20.0);
        _lineH = Math.Max(1, Math.Ceiling(ft.Height));
    }

    private (int cols, int rows) ComputeSize()
    {
        var w = Output.ActualWidth - Output.Padding.Left - Output.Padding.Right - SystemParameters.VerticalScrollBarWidth - 4;
        var h = Output.ActualHeight - Output.Padding.Top - Output.Padding.Bottom - 2;
        var cols = Math.Max(20, (int)(w / _charW) - 1);
        var rows = Math.Max(5, (int)(h / _lineH));
        return (cols, rows);
    }

    private void OnViewportChanged()
    {
        if (!_started) return;
        var (c, r) = ComputeSize();
        if (c == _cols && r == _rows) return;
        _cols = c; _rows = r;
        _screen.Resize(c, r);
        _term?.Resize((short)c, (short)r);
        _dirty = true;
        UpdateStatus();
    }

    // ------------------------------------------------------------- input

    private void Send(string s)
    {
        _stick = true;
        _term?.Write(s);
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var t = e.Text;
        if (string.IsNullOrEmpty(t) || t[0] < ' ') return;
        Send(t);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var alt = (mods & ModifierKeys.Alt) != 0;
        var shift = (mods & ModifierKeys.Shift) != 0;

        if (key == Key.F6) { ApplyTheme("matrix"); e.Handled = true; return; }
        if (key == Key.F7) { ApplyTheme("mikrotik"); e.Handled = true; return; }
        if (key == Key.F8) { ApplyTheme("classic"); e.Handled = true; return; }
        if (ctrl && alt) return; // AltGr: let the text input path deal with it

        if (ctrl && key == Key.L) { ClearScrollback(); e.Handled = true; return; }

        if ((ctrl && shift && key == Key.C) || (ctrl && !shift && key == Key.Insert))
        {
            CopySelection(); e.Handled = true; return;
        }
        if (ctrl && !shift && key == Key.C)
        {
            if (!Output.Selection.IsEmpty) CopySelection(); else Send("\u0003");
            e.Handled = true; return;
        }
        if ((ctrl && key == Key.V) || (shift && !ctrl && key == Key.Insert))
        {
            Paste(); e.Handled = true; return;
        }

        string? seq = key switch
        {
            Key.Enter => "\r",
            Key.Back => ctrl ? "\u0017" : "\u007f",
            Key.Tab => shift ? "\u001b[Z" : "\t",
            Key.Escape => "\u001b",
            Key.Up => "\u001b[A", Key.Down => "\u001b[B", Key.Right => "\u001b[C", Key.Left => "\u001b[D",
            Key.Home => "\u001b[H", Key.End => "\u001b[F",
            Key.Insert => "\u001b[2~", Key.Delete => "\u001b[3~",
            Key.PageUp => "\u001b[5~", Key.PageDown => "\u001b[6~",
            _ => null
        };

        if (ctrl && seq is { Length: 3 } && seq[1] == '[' && "ABCDHF".Contains(seq[2]))
            seq = "\u001b[1;5" + seq[2];
        else if (ctrl && key >= Key.A && key <= Key.Z)
            seq = ((char)(key - Key.A + 1)).ToString();

        if (seq != null) { Send(seq); e.Handled = true; }
    }

    private void CopySelection()
    {
        try
        {
            if (Output.Selection.IsEmpty) return;
            Clipboard.SetText(Output.Selection.Text);
            Output.Selection.Select(Output.Document.ContentEnd, Output.Document.ContentEnd);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void Paste()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
            Send(text);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    // ----------------------------------------------------------- UI actions

    private void ClearScrollback()
    {
        var doc = Output.Document;
        for (var i = 0; i < _sbCount; i++) doc.Blocks.Remove(doc.Blocks.FirstBlock);
        _sbCount = 0;
        _pendingScroll.Clear();
        _dirty = true;
    }

    private void UpdateStatus() =>
        Status.Text = $" {_theme.ToUpperInvariant()} | CONPTY | {_shell} | {_cols}x{_rows}{(_exited ? " | EXITED" : "")} ";

    private void ApplyTheme(string theme)
    {
        _theme = theme;
        Color fg, bg, win;
        switch (theme)
        {
            case "mikrotik": fg = Color.FromRgb(215, 230, 215); bg = Color.FromRgb(10, 12, 10); win = Color.FromRgb(20, 22, 20); break;
            case "classic": fg = Color.FromRgb(220, 220, 220); bg = Colors.Black; win = Colors.Black; break;
            default: fg = Color.FromRgb(65, 255, 105); bg = Color.FromRgb(2, 4, 2); win = Color.FromRgb(3, 7, 3); break;
        }
        _themeFg = Frozen(fg);
        _themeBg = Frozen(bg);
        Background = Frozen(win);
        Output.Background = _themeBg;
        Output.Foreground = _themeFg;
        _dirty = true;
        UpdateStatus();
    }

    private void Matrix_Click(object sender, RoutedEventArgs e) { ApplyTheme("matrix"); Output.Focus(); }
    private void Mikrotik_Click(object sender, RoutedEventArgs e) { ApplyTheme("mikrotik"); Output.Focus(); }
    private void Classic_Click(object sender, RoutedEventArgs e) { ApplyTheme("classic"); Output.Focus(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { ClearScrollback(); Output.Focus(); }
    private void NewCmd_Click(object sender, RoutedEventArgs e) { StartSession("cmd.exe"); Output.Focus(); }
    private void NewPowerShell_Click(object sender, RoutedEventArgs e) { StartSession("powershell.exe"); Output.Focus(); }
}
