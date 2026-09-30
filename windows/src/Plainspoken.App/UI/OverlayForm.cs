using System.Drawing.Drawing2D;
using Plainspoken.App.Platform;

namespace Plainspoken.App.UI;

internal enum PillMode
{
    Hidden,
    Mini,
    Recording,
    Transcribing,
    Hint,
    Error,
}

/// <summary>
/// The on-screen pill: a small idle "mini button" (click to dictate, drag to move), which grows into the
/// "Listening / Transcribing…" pill while dictating, and shows short hints and errors.
/// It must NEVER take focus (that would break pasting into the user's app):
/// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST, ShowWithoutActivation, WM_MOUSEACTIVATE → MA_NOACTIVATE,
/// no focusable child controls (everything is painted), and topmost is set with SWP_NOACTIVATE.
/// </summary>
internal sealed class OverlayForm : Form
{
    // Logical sizes at 96 DPI.
    private const int BaseWidth = 264;
    private const int PillHeight = 44;
    private const int MiniWidth = 44;
    private const int MiniHeight = 18;
    private const int MiniHoverHeight = 30;
    private const int BottomMargin = 14;
    private const int MaxWidth = 600;
    private const int TextLeft = 38;
    private const int Button = 28;

    private static readonly Color Background = Color.FromArgb(32, 33, 36);
    private static readonly Color Border = Color.FromArgb(70, 72, 78);
    private static readonly Color Foreground = Color.FromArgb(248, 249, 250);
    private static readonly Color Muted = Color.FromArgb(189, 193, 198);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);
    private static readonly Color Amber = Color.FromArgb(245, 158, 11);
    private static readonly Color Blue = Color.FromArgb(96, 165, 250);
    private static readonly Color Green = Color.FromArgb(52, 211, 153);
    private static readonly Color TickGreen = Color.FromArgb(22, 163, 74);
    private static readonly Color TickGreenHover = Color.FromArgb(34, 197, 94);
    private static readonly Color BarOff = Color.FromArgb(75, 85, 99);
    private static readonly Color Hover = Color.FromArgb(60, 64, 67);

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly Func<TimeSpan> _elapsed;
    private readonly Func<float> _level;
    private readonly Func<string> _hotkeyLabel;

    private PillMode _mode = PillMode.Hidden;
    private string _text = string.Empty;
    private string? _warning;
    private DateTime _warningUntil;
    private DateTime _hideAt = DateTime.MaxValue;
    private float _meter;
    private int _frame;
    private int _dpi = 96;
    private Font _font;
    private Font _smallFont;
    private Rectangle _doneRect;
    private Rectangle _cancelRect;
    private Rectangle _hoverRect;
    private bool _miniHover;
    private bool _suppressed; // mini hidden because a full-screen app is in front
    private Screen? _lastScreen;

    // Dragging.
    private bool _pressed;
    private bool _dragging;
    private Point _pressScreen;
    private Point _pressLocation;

    public OverlayForm(Func<TimeSpan> elapsed, Func<float> level, Func<string> hotkeyLabel)
    {
        _elapsed = elapsed;
        _level = level;
        _hotkeyLabel = hotkeyLabel;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        Text = "Plainspoken";
        AccessibleName = "Plainspoken dictation button";
        BackColor = Background;
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _font = MakeFont(96, 13);
        _smallFont = MakeFont(96, 12);
        _timer.Tick += OnTimer;
    }

    /// <summary>The ✓ button: stop and insert the text.</summary>
    public event EventHandler? DoneClicked;

    public event EventHandler? CancelClicked;

    /// <summary>User clicked an error pill (the tray context runs its action).</summary>
    public event EventHandler? ErrorClicked;

    /// <summary>User clicked the idle mini button.</summary>
    public event EventHandler? MiniClicked;

    /// <summary>User right-clicked the idle mini button (screen coordinates).</summary>
    public event EventHandler<Point>? MiniMenuRequested;

    /// <summary>User dragged the pill; the new bottom-centre point in screen pixels.</summary>
    public event EventHandler<Point>? AnchorChanged;

    /// <summary>Raised every 100 ms while recording or transcribing (on the UI thread).</summary>
    public event EventHandler? Tick;

    public PillMode Mode => _mode;

    /// <summary>Show the mini button while idle.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool MiniEnabled { get; set; }

    /// <summary>Bottom-centre point chosen by dragging; null = bottom-centre of the active screen.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Point? CustomAnchor { get; set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ShowRecording()
    {
        _mode = PillMode.Recording;
        _text = "Listening";
        _warning = null;
        _hideAt = DateTime.MaxValue;
        _meter = 0;
        Place();
    }

    public void ShowTranscribing(string text)
    {
        _mode = PillMode.Transcribing;
        _text = text;
        _warning = null;
        _hideAt = DateTime.MaxValue;
        Place();
    }

    public void ShowWarning(string text)
    {
        _warning = text;
        _warningUntil = DateTime.UtcNow.AddSeconds(4);
        Invalidate();
    }

    /// <summary>A short note: shown inside the active pill while busy, otherwise as a hint pill.</summary>
    public void ShowNote(string text)
    {
        if (_mode is PillMode.Recording or PillMode.Transcribing)
        {
            _warning = text;
            _warningUntil = DateTime.UtcNow.AddSeconds(2.5);
            Invalidate();
            return;
        }

        ShowHint(text, error: false);
    }

    public void ShowHint(string text, bool error)
    {
        ArgumentNullException.ThrowIfNull(text);
        _mode = error ? PillMode.Error : PillMode.Hint;
        _text = text;
        _warning = null;
        // Longer messages stay up a little longer so they can be read.
        var seconds = Math.Min(error ? 9 : 6, (error ? 5 : 2.5) + (text.Length * 0.04));
        _hideAt = DateTime.UtcNow.AddSeconds(seconds);
        Place();
    }

    /// <summary>
    /// Dictation went back to idle. A recording/transcribing pill either turns its pending note into a
    /// hint (e.g. "Didn't catch that", "Cancelled") or returns to idle. Hint and error pills are left alone.
    /// </summary>
    public void EndBusy()
    {
        if (_mode is not (PillMode.Recording or PillMode.Transcribing))
        {
            return;
        }

        if (_warning is not null && DateTime.UtcNow < _warningUntil)
        {
            ShowHint(_warning, error: false);
        }
        else
        {
            GoIdle();
        }
    }

    /// <summary>Back to the idle look: the mini button if enabled, otherwise nothing on screen.</summary>
    public void GoIdle()
    {
        _warning = null;
        _miniHover = false;
        _hoverRect = Rectangle.Empty;
        if (MiniEnabled)
        {
            _mode = PillMode.Mini;
            Place();
        }
        else
        {
            HideCompletely();
        }
    }

    public void HideCompletely()
    {
        _mode = PillMode.Hidden;
        _timer.Stop();
        if (Visible)
        {
            Hide();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            m.Result = NativeMethods.MA_NOACTIVATE;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        // We size ourselves; don't let WinForms rescale to the suggested rectangle.
        e.Cancel = true;
        base.OnDpiChanged(e);
        if (_mode != PillMode.Hidden && !_dragging)
        {
            Place();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var w = ClientSize.Width;
        var h = ClientSize.Height;

        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, w - 1.5f, h - 1.5f), (h - 1.5f) / 2f))
        using (var fill = new SolidBrush(_mode == PillMode.Mini && _miniHover ? Color.FromArgb(44, 46, 50) : Background))
        using (var pen = new Pen(Border, 1))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var cy = h / 2f;
        if (_mode == PillMode.Mini)
        {
            if (_miniHover)
            {
                DrawWave(g, S(16), cy, S(13));
                TextRenderer.DrawText(g, $"Click or {_hotkeyLabel()} to dictate", _smallFont,
                    new Rectangle(S(28), 0, Math.Max(0, w - S(36)), h), Foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }
            else
            {
                DrawWave(g, w / 2f, cy, S(10));
            }

            return;
        }

        // Status dot / spinner.
        var dot = S(12);
        switch (_mode)
        {
            case PillMode.Recording:
                var pulse = 0.65f + (0.35f * (float)Math.Abs(Math.Sin(_frame / 6.0)));
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * pulse), Red)))
                {
                    g.FillEllipse(b, S(16), cy - (dot / 2f), dot, dot);
                }

                break;
            case PillMode.Transcribing:
                for (var i = 0; i < 3; i++)
                {
                    var on = (_frame / 3 % 3) == i;
                    using var b = new SolidBrush(on ? Amber : Color.FromArgb(120, Amber));
                    var d = S(5);
                    g.FillEllipse(b, S(12) + (i * S(7)), cy - (d / 2f), d, d);
                }

                break;
            default:
                using (var b = new SolidBrush(_mode == PillMode.Error ? Amber : Blue))
                {
                    g.FillEllipse(b, S(16), cy - (dot / 2f), dot, dot);
                }

                break;
        }

        var right = w - S(12);
        if (_mode == PillMode.Recording)
        {
            DrawCancel(g, _cancelRect);
            DrawTick(g, _doneRect);
            DrawMeter(g, new Rectangle(_doneRect.Left - S(42), 0, S(32), h));
            var time = _elapsed();
            var timeRect = new Rectangle(_doneRect.Left - S(92), 0, S(44), h);
            TextRenderer.DrawText(g, $"{(int)time.TotalMinutes}:{time.Seconds:00}", _font, timeRect, Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            right = timeRect.Left - S(4);
        }
        else if (_mode == PillMode.Transcribing)
        {
            DrawCancel(g, _cancelRect);
            right = _cancelRect.Left - S(4);
        }

        var showWarning = _warning is not null && DateTime.UtcNow < _warningUntil && _mode is PillMode.Recording or PillMode.Transcribing;
        var textRect = new Rectangle(S(TextLeft), 0, Math.Max(0, right - S(TextLeft)), h);
        TextRenderer.DrawText(g, showWarning ? _warning : _text, _font, textRect, showWarning ? Amber : Foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitButton(e.Location) == Rectangle.Empty)
        {
            _pressed = true;
            _dragging = false;
            _pressScreen = Cursor.Position;
            _pressLocation = Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        if (_pressed)
        {
            var now = Cursor.Position;
            var dx = now.X - _pressScreen.X;
            var dy = now.Y - _pressScreen.Y;
            if (!_dragging && (Math.Abs(dx) > SystemInformation.DragSize.Width || Math.Abs(dy) > SystemInformation.DragSize.Height))
            {
                _dragging = true;
                Cursor = Cursors.SizeAll;
            }

            if (_dragging)
            {
                Location = new Point(_pressLocation.X + dx, _pressLocation.Y + dy); // SetWindowPos with SWP_NOACTIVATE
                return;
            }
        }

        var hover = HitButton(e.Location);
        Cursor = hover == Rectangle.Empty ? (_mode == PillMode.Mini ? Cursors.Hand : Cursors.Default) : Cursors.Hand;
        if (hover != _hoverRect)
        {
            _hoverRect = hover;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            _hoverRect = Rectangle.Empty;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);
        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        Cursor = Cursors.Default;

        if (wasDragging)
        {
            var anchor = new Point(Left + (Width / 2), Bottom);
            CustomAnchor = anchor;
            AnchorChanged?.Invoke(this, anchor);
            Place(); // clamp inside the screen and adopt that screen's DPI
            return;
        }

        if (e.Button == MouseButtons.Right && _mode == PillMode.Mini)
        {
            MiniMenuRequested?.Invoke(this, Cursor.Position);
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = HitButton(e.Location);
        if (hit == _doneRect && hit != Rectangle.Empty)
        {
            DoneClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (hit == _cancelRect && hit != Rectangle.Empty)
        {
            CancelClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (_mode == PillMode.Mini)
        {
            MiniClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (_mode == PillMode.Error)
        {
            GoIdle();
            ErrorClicked?.Invoke(this, EventArgs.Empty);
        }
        else if (_mode == PillMode.Hint)
        {
            GoIdle();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _font.Dispose();
            _smallFont.Dispose();
        }

        base.Dispose(disposing);
    }

    private Rectangle HitButton(Point p)
    {
        if (_mode == PillMode.Recording && _doneRect.Contains(p))
        {
            return _doneRect;
        }

        if (_mode is PillMode.Recording or PillMode.Transcribing && _cancelRect.Contains(p))
        {
            return _cancelRect;
        }

        return Rectangle.Empty;
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _frame++;
        if (_mode is PillMode.Hint or PillMode.Error && DateTime.UtcNow >= _hideAt)
        {
            GoIdle();
            return;
        }

        if (_mode is PillMode.Recording or PillMode.Transcribing)
        {
            // Smooth the meter a little so it doesn't jitter.
            _meter = Math.Max(_level(), _meter * 0.7f);
            Tick?.Invoke(this, EventArgs.Empty);
            Invalidate();
            return;
        }

        if (_mode == PillMode.Mini && !_dragging)
        {
            UpdateMini();
            return;
        }

        if (_mode != PillMode.Hidden)
        {
            Invalidate();
        }
    }

    /// <summary>Idle housekeeping: hover expand/collapse, hide during full-screen apps, follow the active screen.</summary>
    private void UpdateMini()
    {
        var hover = Visible && Bounds.Contains(Cursor.Position);
        if (hover != _miniHover)
        {
            _miniHover = hover;
            Place();
            return;
        }

        if (_frame % 10 != 0)
        {
            return; // the checks below run about once a second
        }

        var fg = NativeMethods.GetForegroundWindow();
        var fullscreen = NativeMethods.IsFullscreen(fg);
        var screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen;
        if (fullscreen != _suppressed || (CustomAnchor is null && !Equals(screen, _lastScreen)))
        {
            _suppressed = fullscreen;
            Place();
        }
        else if (Visible)
        {
            // Stay above other topmost windows that appeared since.
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Sizes and positions the pill, without activating it. The pill grows around its bottom-centre anchor:
    /// the dragged position if any, otherwise bottom-centre of the screen with the foreground window.
    /// </summary>
    private void Place()
    {
        if (_mode == PillMode.Hidden)
        {
            return;
        }

        var fg = NativeMethods.GetForegroundWindow();
        if (_mode == PillMode.Mini && NativeMethods.IsFullscreen(fg))
        {
            // Don't sit on top of full-screen videos, games or presentations.
            _suppressed = true;
            if (Visible)
            {
                Hide();
            }

            _timer.Start();
            return;
        }

        _suppressed = false;
        Screen screen;
        Point anchor;
        if (CustomAnchor is { } custom && Screen.AllScreens.FirstOrDefault(s => s.WorkingArea.Contains(new Point(custom.X, custom.Y - 1))) is { } chosen)
        {
            screen = chosen;
            anchor = custom;
        }
        else
        {
            screen = fg != IntPtr.Zero ? Screen.FromHandle(fg) : Screen.PrimaryScreen!;
            anchor = Point.Empty; // computed below once we know the DPI
        }

        _lastScreen = screen;
        var dpi = MonitorDpi(screen);
        if (dpi != _dpi)
        {
            _dpi = dpi;
            _font.Dispose();
            _smallFont.Dispose();
            _font = MakeFont(dpi, 13);
            _smallFont = MakeFont(dpi, 12);
        }

        var wa = screen.WorkingArea;
        if (anchor == Point.Empty)
        {
            anchor = new Point(wa.Left + (wa.Width / 2), wa.Bottom - S(BottomMargin));
        }

        var (w, h) = DesiredSize();
        w = Math.Min(w, (int)(wa.Width * 0.9));
        var x = Math.Clamp(anchor.X - (w / 2), wa.Left + S(4), Math.Max(wa.Left + S(4), wa.Right - w - S(4)));
        var y = Math.Clamp(anchor.Y - h, wa.Top + S(4), Math.Max(wa.Top + S(4), wa.Bottom - h));

        _cancelRect = new Rectangle(w - S(8) - S(Button), (h - S(Button)) / 2, S(Button), S(Button));
        _doneRect = new Rectangle(_cancelRect.Left - S(4) - S(Button), _cancelRect.Top, S(Button), S(Button));

        if (Bounds != new Rectangle(x, y, w, h))
        {
            Bounds = new Rectangle(x, y, w, h);
            using var path = RoundedRect(new RectangleF(0, 0, w, h), h / 2f);
            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        if (!Visible)
        {
            Show(); // ShowWithoutActivation → SW_SHOWNOACTIVATE
        }

        // Re-assert topmost without activating (the WinForms TopMost setter may activate).
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        _timer.Start();
        Invalidate();
    }

    private (int W, int H) DesiredSize()
    {
        switch (_mode)
        {
            case PillMode.Mini when _miniHover:
                var hint = TextRenderer.MeasureText($"Click or {_hotkeyLabel()} to dictate", _smallFont, System.Drawing.Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                return (S(28) + hint + S(14), S(MiniHoverHeight));
            case PillMode.Mini:
                return (S(MiniWidth), S(MiniHeight));
            case PillMode.Recording:
                return (S(BaseWidth), S(PillHeight));
            default:
                var reserve = _mode == PillMode.Transcribing ? S(Button + 20) : S(18);
                var textWidth = TextRenderer.MeasureText(_text, _font, System.Drawing.Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                return (Math.Clamp(S(TextLeft) + textWidth + reserve, S(BaseWidth), S(MaxWidth)), S(PillHeight));
        }
    }

    private int MonitorDpi(Screen screen)
    {
        var centre = new NativeMethods.POINT { X = screen.Bounds.Left + (screen.Bounds.Width / 2), Y = screen.Bounds.Top + (screen.Bounds.Height / 2) };
        var monitor = NativeMethods.MonitorFromPoint(centre, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0 ? (int)dx : DeviceDpi;
    }

    private void DrawCancel(Graphics g, Rectangle r)
    {
        if (r == _hoverRect)
        {
            using var hb = new SolidBrush(Hover);
            g.FillEllipse(hb, r);
        }

        var c = new PointF(r.Left + (r.Width / 2f), r.Top + (r.Height / 2f));
        var d = S(5);
        using var pen = new Pen(Foreground, Math.Max(1.5f, S(2) * 0.8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, c.X - d, c.Y - d, c.X + d, c.Y + d);
        g.DrawLine(pen, c.X - d, c.Y + d, c.X + d, c.Y - d);
    }

    /// <summary>Green circle with a white check mark: "done — insert the text".</summary>
    private void DrawTick(Graphics g, Rectangle r)
    {
        var inset = S(2);
        var circle = Rectangle.Inflate(r, -inset, -inset);
        using (var b = new SolidBrush(r == _hoverRect ? TickGreenHover : TickGreen))
        {
            g.FillEllipse(b, circle);
        }

        var c = new PointF(r.Left + (r.Width / 2f), r.Top + (r.Height / 2f));
        var u = S(10) / 2f;
        using var pen = new Pen(Color.White, Math.Max(2f, S(2) * 1.1f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen,
        [
            new PointF(c.X - (u * 0.9f), c.Y + (u * 0.05f)),
            new PointF(c.X - (u * 0.25f), c.Y + (u * 0.65f)),
            new PointF(c.X + (u * 0.95f), c.Y - (u * 0.6f)),
        ]);
    }

    /// <summary>Small white three-bar sound-wave glyph (the app's logo motif) centred at (cx, cy); size is its height.</summary>
    private static void DrawWave(Graphics g, float cx, float cy, float size)
    {
        var barW = Math.Max(2f, size * 0.22f);
        var gap = barW * 0.7f;
        using var white = new SolidBrush(Color.White);
        (float Offset, float Height)[] bars = [(-(barW + gap), 0.55f), (0, 1f), (barW + gap, 0.7f)];
        foreach (var (offset, height) in bars)
        {
            var h = size * height;
            using var bar = RoundedRect(new RectangleF(cx + offset - (barW / 2f), cy - (h / 2f), barW, h), barW / 2f, vertical: true);
            g.FillPath(white, bar);
        }
    }

    private void DrawMeter(Graphics g, Rectangle area)
    {
        // Map peak level to 0..5 bars on a dB scale (−50 dB … −10 dB).
        var db = _meter <= 0.0001f ? -100 : 20 * Math.Log10(_meter);
        var lit = (int)Math.Clamp(Math.Round((db + 50) / 8.0), 0, 5);
        var barW = S(4);
        var gap = S(3);
        for (var i = 0; i < 5; i++)
        {
            var bh = S(6 + (i * 3));
            var x = area.Left + (i * (barW + gap));
            var y = area.Top + ((area.Height - bh) / 2);
            using var b = new SolidBrush(i < lit ? Green : BarOff);
            g.FillRectangle(b, x, y, barW, bh);
        }
    }

    private int S(int logical) => (int)Math.Round(logical * _dpi / 96.0);

    private static Font MakeFont(int dpi, float px) => new("Segoe UI", px * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel);

    /// <summary>Stadium shape: round ends left/right (or top/bottom when <paramref name="vertical"/>).</summary>
    private static GraphicsPath RoundedRect(RectangleF r, float radius, bool vertical = false)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (vertical)
        {
            path.AddArc(r.Left, r.Top, d, d, 180, 180);
            path.AddArc(r.Left, r.Bottom - d, d, d, 0, 180);
        }
        else
        {
            path.AddArc(r.Left, r.Top, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 180);
        }

        path.CloseFigure();
        return path;
    }
}
