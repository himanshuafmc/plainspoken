using Plainspoken.App.Platform;
using Plainspoken.Core;
using Plainspoken.Core.Transcription;

namespace Plainspoken.App.UI;

/// <summary>Welcome dialog: get a free key → paste it → try the hotkey, plus the free-tier privacy notice.</summary>
internal sealed class FirstRunForm : Form
{
    public const string PrivacyNotice =
        "This app uses Google's free tier. Google may use your recordings to improve its products. " +
        "Do not dictate official, confidential or classified information.";

    private const string KeyUrl = "https://aistudio.google.com/apikey";

    private readonly ISettingsHost _host;
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 320, Anchor = AnchorStyles.Left };
    private readonly CheckBox _show = Ui.Check("Show");
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(470, 0), Margin = new Padding(3, 2, 3, 6) };
    private readonly CheckBox _accept = new()
    {
        Text = "I understand",
        AutoSize = true,
        Margin = new Padding(3, 6, 3, 3),
    };
    private readonly Button _finish;
    private readonly Button _test;

    public FirstRunForm(ISettingsHost host)
    {
        _host = host;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"Welcome to {AppInfo.Name}";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(540, 520);
        MinimumSize = new Size(440, 360);
        MaximizeBox = false;

        _key.Text = host.ApiKey ?? string.Empty;
        _show.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_show.Checked;
        _key.TextChanged += (_, _) => UpdateFinish();
        _accept.CheckedChanged += (_, _) => UpdateFinish();
        _test = Ui.Button("Test key", async (_, _) => await TestAsync().ConfigureAwait(true));
        _finish = Ui.Button("Finish", (_, _) => Finish());
        var later = Ui.Button("Later", (_, _) => Close());

        var hotkey = HotkeyKeys.Display(host.CurrentSettings.Hotkey);
        var body = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 12, 16, 8),
        };
        void Add(Control c)
        {
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.Controls.Add(c, 0, body.RowCount++);
        }

        Add(Heading($"{AppInfo.Name} types what you say — in English, Hindi or Hinglish."));
        Add(Step("1. Get a free Gemini API key"));
        Add(Para("Open Google AI Studio, sign in with your Google account, click \"Create API key\" and copy it."));
        Add(Ui.Button("Open Google AI Studio", (_, _) => AppPaths.OpenUrl(KeyUrl)));
        Add(Step("2. Paste your key here"));
        Add(Ui.Flow(_key, _show, _test));
        Add(_status);
        Add(Step("3. Try it"));
        Add(Para($"Click where you want to type (for example in Notepad), press {hotkey}, speak, then press {hotkey} again. " +
                 $"{AppInfo.Name} lives in the system tray, near the clock."));

        var notice = new Panel
        {
            BackColor = Color.FromArgb(255, 248, 225),
            Padding = new Padding(10),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 12, 3, 3),
        };
        var noticeText = new Label
        {
            Text = PrivacyNotice,
            AutoSize = true,
            MaximumSize = new Size(470, 0),
            ForeColor = Color.FromArgb(120, 53, 15),
            Dock = DockStyle.Top,
        };
        var noticeLayout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Color.Transparent };
        noticeLayout.Controls.Add(noticeText, 0, 0);
        noticeLayout.Controls.Add(_accept, 0, 1);
        notice.Controls.Add(noticeLayout);
        Add(notice);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(body);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(later);
        buttons.Controls.Add(_finish);
        AcceptButton = _finish;

        Controls.Add(scroll);
        Controls.Add(buttons);
        UpdateFinish();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.FitToScreen(this);
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(480, 0),
        Font = new Font("Segoe UI", 11f, FontStyle.Regular),
        Margin = new Padding(3, 0, 3, 8),
    };

    private static Label Step(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
        Margin = new Padding(3, 12, 3, 3),
    };

    private static Label Para(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(480, 0),
        Margin = new Padding(3, 0, 3, 6),
    };

    private void UpdateFinish() => _finish.Enabled = _accept.Checked && _key.Text.Trim().Length > 0;

    private async Task TestAsync()
    {
        _test.Enabled = false;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Checking…";
        try
        {
            var s = _host.CurrentSettings;
            var result = await KeyTester.TestAsync(_host.Http, s.Transcription.ApiBaseUrl, s.Transcription.ModelFor(s.Transcription.Engine),
                _key.Text.Trim(), _host.Log, CancellationToken.None).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            _status.ForeColor = result.Ok ? Ui.Good : Ui.Bad;
            _status.Text = result.Message;
        }
        finally
        {
            if (!IsDisposed)
            {
                _test.Enabled = true;
            }
        }
    }

    private void Finish()
    {
        var settings = _host.CurrentSettings;
        settings.Local.FirstRunCompleted = true;
        if (!_host.TryApply(settings, _key.Text.Trim(), out var error))
        {
            MessageBox.Show(this, error ?? "Couldn't save.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _host.Log.Info("first run completed");
        Close();
    }
}
