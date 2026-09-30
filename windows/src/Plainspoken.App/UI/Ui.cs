using Plainspoken.App.Platform;

namespace Plainspoken.App.UI;

/// <summary>Small helpers for code-built, DPI-friendly WinForms layouts (TableLayoutPanel + AutoSize).</summary>
internal static class Ui
{
    public static readonly Color Good = Color.FromArgb(21, 128, 61);
    public static readonly Color Bad = Color.FromArgb(185, 28, 28);

    /// <summary>The exe's own icon, loaded once and shared by all windows.</summary>
    public static Icon? AppIcon { get; } = LoadAppIcon();

    /// <summary>A two-column grid (label | control) that grows vertically; put it in a scrollable page.</summary>
    public static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    public static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(4), UseVisualStyleBackColor = true };
        page.Controls.Add(content);
        return page;
    }

    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 10, 3),
        Font = bold ? new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Bold) : null,
    };

    public static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 0, 3, 8),
    };

    public static void Row(TableLayoutPanel grid, string? label, Control control)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(control);
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label is null)
        {
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }
        else
        {
            grid.Controls.Add(Label(label), 0, row);
            grid.Controls.Add(control, 1, row);
        }
    }

    public static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        f.Controls.AddRange(controls);
        return f;
    }

    public static ComboBox Combo(params object[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, Anchor = AnchorStyles.Left };
        c.Items.AddRange(items);
        return c;
    }

    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };

    public static Button Button(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(88, 0) };
        b.Click += onClick;
        return b;
    }

    public static LinkLabel Link(string text, string url)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
        l.LinkClicked += (_, _) => AppPaths.OpenUrl(url);
        return l;
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Keeps a window inside the working area of its screen (small laptop screens, 125 % scaling).</summary>
    public static void FitToScreen(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        var wa = Screen.FromControl(form).WorkingArea;
        var w = Math.Min(form.Width, wa.Width - 20);
        var h = Math.Min(form.Height, wa.Height - 20);
        form.Bounds = new Rectangle(wa.Left + ((wa.Width - w) / 2), wa.Top + ((wa.Height - h) / 2), w, h);
    }
}

/// <summary>Text box that records a key combination when "Change…" is pressed.</summary>
internal sealed class HotkeyBox : TextBox
{
    private string _value = string.Empty;

    public HotkeyBox()
    {
        ReadOnly = true;
        Width = 200;
        Anchor = AnchorStyles.Left;
        BackColor = SystemColors.Window;
    }

    public event EventHandler? CaptureEnded;

    public bool Capturing { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = HotkeyKeys.Display(value);
        }
    }

    public void BeginCapture()
    {
        Capturing = true;
        Text = "Press the new keys… (Esc to cancel)";
        Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!Capturing)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape && (keyData & Keys.Modifiers) == 0)
        {
            EndCapture();
            return true;
        }

        var win = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
                  (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;
        var gesture = HotkeyKeys.FromKeyEvent(new KeyEventArgs(keyData), win);
        if (gesture is not null)
        {
            if (HotkeyKeys.TryResolve(gesture, out _, out _, out var error))
            {
                _value = gesture;
                EndCapture();
            }
            else
            {
                Text = error + " Try again…";
            }
        }

        return true; // swallow everything while capturing (also stops Alt+Space opening the system menu)
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        if (Capturing)
        {
            EndCapture();
        }
    }

    private void EndCapture()
    {
        Capturing = false;
        Text = HotkeyKeys.Display(_value);
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }
}
