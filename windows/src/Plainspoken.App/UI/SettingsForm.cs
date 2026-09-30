using System.Text.Json;
using Plainspoken.App.Audio;
using Plainspoken.App.Platform;
using Plainspoken.Core;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Transcription;

namespace Plainspoken.App.UI;

/// <summary>
/// Settings window. Sized for a 1366×768 laptop at 125 % scaling (≈ 540 logical px tall),
/// resizable, and every tab scrolls if the window is made smaller.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string KeyUrl = "https://aistudio.google.com/apikey";

    private readonly ISettingsHost _host;
    private readonly HistoryStore _history;
    private readonly string _originalKey;
    private PlainspokenSettings _working;

    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 300, Anchor = AnchorStyles.Left };
    private readonly CheckBox _showKey = Ui.Check("Show");
    private readonly Label _keyStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 2, 3, 6) };
    private readonly Button _testKey;
    private readonly HotkeyBox _hotkey = new();
    private readonly ComboBox _languages = Ui.Combo(
        LanguagePresets.DisplayName(LanguagePreset.EnglishHindi), LanguagePresets.DisplayName(LanguagePreset.Auto),
        LanguagePresets.DisplayName(LanguagePreset.English));
    private readonly ComboBox _mode = Ui.Combo("Smart — clean up fillers, punctuation, lists (recommended)", "Verbatim — every word as spoken");
    private readonly ComboBox _microphone = Ui.Combo();
    private readonly List<string?> _microphoneIds = [];
    private readonly TextBox _vocabulary = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        Dock = DockStyle.Fill,
        WordWrap = false,
    };
    private readonly Label _vocabularyCount = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText };
    private readonly ComboBox _insertion = Ui.Combo("Paste (recommended)", "Type it out (for apps that block paste)");
    private readonly CheckBox _trailingSpace = Ui.Check("Add a space after the text");
    private readonly CheckBox _sounds = Ui.Check("Play start and stop sounds");
    private readonly CheckBox _startWithWindows = Ui.Check("Start Plainspoken when Windows starts");
    private readonly CheckBox _keepHistory = Ui.Check("Keep the last 20 transcripts (tray menu → Recent)");
    private readonly CheckBox _miniButton = Ui.Check("Show a small dictation button on screen when idle");
    private readonly CheckBox _liveStreaming = Ui.Check("Live streaming: send audio while you speak (faster)");
    private readonly TextBox _liveModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private bool _resetMiniPosition;
    private readonly NumericUpDown _maxMinutes = new() { Minimum = 1, Maximum = 30, Width = 70, Anchor = AnchorStyles.Left };
    private readonly ComboBox _engine = Ui.Combo("Gemini Transcribe (recommended)", "Gemini Flash-Lite (backup)");
    private readonly CheckBox _useBackup = Ui.Check("If the free limit is reached, try the other engine once");
    private readonly TextBox _transcribeModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _generateModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _baseUrl = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 600, Width = 70, Anchor = AnchorStyles.Left };

    public SettingsForm(ISettingsHost host, HistoryStore history)
    {
        _host = host;
        _history = history;
        _working = host.CurrentSettings;
        _originalKey = host.ApiKey ?? string.Empty;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{AppInfo.Name} settings";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(580, 520);
        MinimumSize = new Size(460, 360);
        ShowInTaskbar = true;
        MaximizeBox = true;

        _testKey = Ui.Button("Test key", async (_, _) => await TestKeyAsync().ConfigureAwait(true));
        _showKey.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_showKey.Checked;
        _vocabulary.TextChanged += (_, _) => UpdateVocabularyCount();
        _hotkey.CaptureEnded += (_, _) => _host.ResumeHotkey();

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Ui.Page("General", BuildGeneral()));
        tabs.TabPages.Add(Ui.Page("Vocabulary", BuildVocabulary()));
        tabs.TabPages.Add(Ui.Page("Options", BuildOptions()));
        tabs.TabPages.Add(Ui.Page("Advanced", BuildAdvanced()));

        var save = Ui.Button("Save", (_, _) => Save());
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        LoadFrom(_working, _originalKey);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.FitToScreen(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hotkey.Capturing)
        {
            _host.ResumeHotkey();
        }

        base.OnFormClosed(e);
    }

    private TableLayoutPanel BuildGeneral()
    {
        var g = Ui.Grid();
        Ui.Row(g, "Gemini API key", Ui.Flow(_key, _showKey, _testKey));
        Ui.Row(g, null, _keyStatus);
        Ui.Row(g, null, Ui.Link("Get a free key at aistudio.google.com/apikey", KeyUrl));

        var change = Ui.Button("Change…", (_, _) =>
        {
            _host.SuspendHotkey(); // so pressing the current hotkey is captured, not triggered
            _hotkey.BeginCapture();
        });
        var reset = Ui.Button("Default", (_, _) => _hotkey.Value = PlainspokenSettings.DefaultHotkey);
        Ui.Row(g, "Hotkey", Ui.Flow(_hotkey, change, reset));
        Ui.Row(g, null, Ui.Note("Press once to start, again to stop. Esc cancels while recording."));
        Ui.Row(g, "Languages", _languages);
        Ui.Row(g, "Mode", _mode);
        var refresh = Ui.Button("Refresh", (_, _) => LoadMicrophones(SelectedMicrophoneId()));
        Ui.Row(g, "Microphone", Ui.Flow(_microphone, refresh));
        return g;
    }

    private TableLayoutPanel BuildVocabulary()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Ui.Note("Names and special words Plainspoken should spell correctly — one per line (up to 1,000). " +
                               "For example: people's names, hospitals, medical terms, abbreviations."), 0, 0);
        t.Controls.Add(_vocabulary, 0, 1);
        t.Controls.Add(_vocabularyCount, 0, 2);
        return t;
    }

    private TableLayoutPanel BuildOptions()
    {
        var g = Ui.Grid();
        Ui.Row(g, "Insert text by", _insertion);
        Ui.Row(g, null, _trailingSpace);
        Ui.Row(g, null, _sounds);
        Ui.Row(g, null, _startWithWindows);
        Ui.Row(g, null, Ui.Flow(_keepHistory, Ui.Button("Clear history", (_, _) => ClearHistory())));
        Ui.Row(g, null, Ui.Flow(_miniButton, Ui.Button("Reset its position", (_, _) =>
        {
            _resetMiniPosition = true;
            MessageBox.Show(this, "The button will move back to the bottom centre when you click Save.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        })));
        Ui.Row(g, null, Ui.Note("Drag the button to move it. Right-click it for more options."));
        Ui.Row(g, "Longest recording", Ui.Flow(_maxMinutes, new Label { Text = "minutes", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));
        Ui.Row(g, null, Ui.Note("You'll see a warning 30 seconds before the limit; then Plainspoken stops and transcribes."));
        return g;
    }

    private TableLayoutPanel BuildAdvanced()
    {
        var g = Ui.Grid();
        Ui.Row(g, "Speech engine", _engine);
        Ui.Row(g, null, _useBackup);
        Ui.Row(g, "Transcribe model", _transcribeModel);
        Ui.Row(g, "Backup model", _generateModel);
        Ui.Row(g, "API base URL", _baseUrl);
        Ui.Row(g, "Request timeout", Ui.Flow(_timeout, new Label { Text = "seconds", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));
        Ui.Row(g, null, _liveStreaming);
        Ui.Row(g, "Live model", _liveModel);
        Ui.Row(g, null, Ui.Note("Live streaming sends your voice to Google while you speak, so the text is ready sooner. " +
                                "If it fails for any reason, Plainspoken automatically uses the normal engine; nothing is lost."));
        Ui.Row(g, "Share settings", Ui.Flow(Ui.Button("Export…", (_, _) => Export()), Ui.Button("Import…", (_, _) => Import())));
        Ui.Row(g, null, Ui.Note("Exported files never include your API key. Share them to give others your vocabulary and preferences."));
        Ui.Row(g, "Folders", Ui.Flow(
            Ui.Button("Data folder", (_, _) => AppPaths.OpenFolder(AppPaths.LocalDir)),
            Ui.Button("Logs", (_, _) => AppPaths.OpenFolder(AppPaths.LogsDir))));
        Ui.Row(g, null, Ui.Button("Restore advanced defaults", (_, _) =>
        {
            var d = new TranscriptionSettings();
            _engine.SelectedIndex = 0;
            _useBackup.Checked = d.UseBackupEngineWhenLimited;
            _transcribeModel.Text = d.TranscribeModel;
            _generateModel.Text = d.GenerateModel;
            _baseUrl.Text = d.ApiBaseUrl;
            _timeout.Value = d.RequestTimeoutSeconds;
            _liveStreaming.Checked = d.LiveStreaming;
            _liveModel.Text = d.LiveModel;
        }));
        return g;
    }

    private void LoadFrom(PlainspokenSettings s, string key)
    {
        _key.Text = key;
        _hotkey.Value = s.Hotkey;
        _languages.SelectedIndex = (int)s.Transcription.Languages;
        _mode.SelectedIndex = (int)s.Transcription.Mode;
        LoadMicrophones(s.Local.MicrophoneId);
        _vocabulary.Text = VocabularyCleaner.ToText(s.Transcription.CustomVocabulary);
        _insertion.SelectedIndex = (int)s.Insertion.Method;
        _trailingSpace.Checked = s.Insertion.TrailingSpace;
        _sounds.Checked = s.Recording.Sounds;
        _startWithWindows.Checked = s.Local.StartWithWindows;
        _keepHistory.Checked = s.History.Enabled;
        _miniButton.Checked = s.ShowMiniButton;
        _liveStreaming.Checked = s.Transcription.LiveStreaming;
        _liveModel.Text = s.Transcription.LiveModel;
        _maxMinutes.Value = Math.Clamp(Math.Max(1, s.Recording.MaxSeconds / 60), 1, 30);
        _engine.SelectedIndex = (int)s.Transcription.Engine;
        _useBackup.Checked = s.Transcription.UseBackupEngineWhenLimited;
        _transcribeModel.Text = s.Transcription.TranscribeModel;
        _generateModel.Text = s.Transcription.GenerateModel;
        _baseUrl.Text = s.Transcription.ApiBaseUrl;
        _timeout.Value = Math.Clamp(s.Transcription.RequestTimeoutSeconds, 10, 600);
        UpdateVocabularyCount();
        _keyStatus.Text = string.IsNullOrEmpty(key) ? "No key yet — Plainspoken needs one to transcribe." : string.Empty;
        _keyStatus.ForeColor = Ui.Bad;
    }

    private PlainspokenSettings BuildFromUi()
    {
        var s = SettingsSerializer.Clone(_working);
        s.Hotkey = _hotkey.Value;
        s.Transcription.Languages = (LanguagePreset)Math.Max(0, _languages.SelectedIndex);
        s.Transcription.Mode = (TranscriptionMode)Math.Max(0, _mode.SelectedIndex);
        s.Local.MicrophoneId = SelectedMicrophoneId();
        s.Transcription.CustomVocabulary = VocabularyCleaner.FromText(_vocabulary.Text);
        s.Insertion.Method = (InsertionMethod)Math.Max(0, _insertion.SelectedIndex);
        s.Insertion.TrailingSpace = _trailingSpace.Checked;
        s.Recording.Sounds = _sounds.Checked;
        s.Local.StartWithWindows = _startWithWindows.Checked;
        s.History.Enabled = _keepHistory.Checked;
        s.ShowMiniButton = _miniButton.Checked;
        s.Transcription.LiveStreaming = _liveStreaming.Checked;
        s.Transcription.LiveModel = _liveModel.Text;
        if (_resetMiniPosition)
        {
            s.Local.MiniButtonPosition = null;
        }
        s.Recording.MaxSeconds = (int)_maxMinutes.Value * 60;
        s.Transcription.Engine = (EngineKind)Math.Max(0, _engine.SelectedIndex);
        s.Transcription.UseBackupEngineWhenLimited = _useBackup.Checked;
        s.Transcription.TranscribeModel = _transcribeModel.Text;
        s.Transcription.GenerateModel = _generateModel.Text;
        s.Transcription.ApiBaseUrl = _baseUrl.Text;
        s.Transcription.RequestTimeoutSeconds = (int)_timeout.Value;
        return s.Normalize();
    }

    private void Save()
    {
        if (_hotkey.Capturing)
        {
            return;
        }

        var updated = BuildFromUi();
        var key = _key.Text.Trim();
        string? newKey = key == _originalKey ? null : key;
        if (key.Length == 0 && _originalKey.Length > 0 &&
            MessageBox.Show(this, "Remove your API key? Plainspoken can't transcribe without one.", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        if (!_host.TryApply(updated, newKey, out var error))
        {
            MessageBox.Show(this, error ?? "Couldn't save settings.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Close();
    }

    private async Task TestKeyAsync()
    {
        _testKey.Enabled = false;
        _keyStatus.ForeColor = SystemColors.GrayText;
        _keyStatus.Text = "Checking…";
        try
        {
            var engine = (EngineKind)Math.Max(0, _engine.SelectedIndex);
            var model = engine == EngineKind.Transcribe ? _transcribeModel.Text : _generateModel.Text;
            var result = await KeyTester.TestAsync(_host.Http, _baseUrl.Text, model, _key.Text.Trim(), _host.Log, CancellationToken.None)
                .ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            _keyStatus.ForeColor = result.Ok ? Ui.Good : Ui.Bad;
            _keyStatus.Text = result.Message;
        }
        finally
        {
            if (!IsDisposed)
            {
                _testKey.Enabled = true;
            }
        }
    }

    private void LoadMicrophones(string? selectedId)
    {
        _microphone.Items.Clear();
        _microphoneIds.Clear();
        _microphone.Items.Add("System default");
        _microphoneIds.Add(null);
        foreach (var (id, name) in WasapiRecorder.ListDevices())
        {
            _microphone.Items.Add(name);
            _microphoneIds.Add(id);
        }

        var index = _microphoneIds.IndexOf(selectedId);
        if (index < 0 && selectedId is not null)
        {
            _microphone.Items.Add("(saved microphone — not connected)");
            _microphoneIds.Add(selectedId);
            index = _microphoneIds.Count - 1;
        }

        _microphone.SelectedIndex = Math.Max(0, index);
    }

    private string? SelectedMicrophoneId() =>
        _microphone.SelectedIndex >= 0 && _microphone.SelectedIndex < _microphoneIds.Count ? _microphoneIds[_microphone.SelectedIndex] : null;

    private void UpdateVocabularyCount()
    {
        var count = VocabularyCleaner.FromText(_vocabulary.Text).Count;
        _vocabularyCount.Text = count >= VocabularyCleaner.MaxTerms
            ? $"{count} terms (maximum reached; extra lines are ignored)"
            : $"{count} term{(count == 1 ? "" : "s")}";
    }

    private void ClearHistory()
    {
        if (_history.Entries.Count == 0)
        {
            MessageBox.Show(this, "History is already empty.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(this, "Delete all saved transcripts?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _host.ClearHistory();
        }
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog
        {
            FileName = "plainspoken-settings.json",
            Filter = "Plainspoken settings (*.json)|*.json",
            Title = "Export settings (your API key is not included)",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, SettingsSerializer.Export(BuildFromUi()));
            MessageBox.Show(this, "Settings exported. Your API key was not included.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Couldn't write that file. Try another folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Plainspoken settings (*.json)|*.json|All files (*.*)|*.*", Title = "Import settings" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _working = SettingsSerializer.Import(File.ReadAllText(dialog.FileName), BuildFromUi());
            LoadFrom(_working, _key.Text);
            MessageBox.Show(this, "Imported. Check the tabs, then click Save to keep these settings.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, "That file isn't a Plainspoken settings file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
