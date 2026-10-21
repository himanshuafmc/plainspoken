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
    private const string NoBackup = "(none)";

    private static readonly HoldKey[] HoldKeys = [HoldKey.RightCtrl, HoldKey.RightAlt, HoldKey.RightShift, HoldKey.Off];

    private readonly ISettingsHost _host;
    private readonly HistoryStore _history;
    private PlainspokenSettings _working;

    /// <summary>Keys typed in this window, by key slot, saved with Save (empty = remove).</summary>
    private readonly Dictionary<string, string> _newKeys = new(StringComparer.Ordinal);

    private readonly ComboBox[] _services = [Ui.Combo(), Ui.Combo(), Ui.Combo()];
    private readonly Label _servicesStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 2, 3, 6) };
    private readonly HotkeyBox _hotkey = new();
    private readonly ComboBox _holdKey = Ui.Combo("Right Ctrl (recommended)", "Right Alt", "Right Shift", "Off");
    private readonly ComboBox _languages = Ui.Combo(
        LanguagePresets.DisplayName(LanguagePreset.EnglishHindi), LanguagePresets.DisplayName(LanguagePreset.Auto),
        LanguagePresets.DisplayName(LanguagePreset.English));
    private readonly ComboBox _mode = Ui.Combo("Smart — clean up fillers, punctuation, lists (recommended)", "Verbatim — every word as spoken");
    private readonly ComboBox _hindiScript = Ui.Combo("Roman letters (kal meeting hai)", "Devanagari (कल meeting है)", "As the service writes them");
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
    private readonly CheckBox _learn = Ui.Check("Offer to add words I correct by hand");
    private readonly ComboBox _cleanup = Ui.Combo("Gemini Flash (recommended)", "OpenAI-compatible chat model", "Off — only remove um, uh and hmm");
    private readonly TextBox _cleanupGeminiModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly CheckBox _cleanupAlsoGemini = Ui.Check("Also tidy the Gemini services' text (they already tidy it themselves)");
    private readonly TextBox _cleanupBaseUrl = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _cleanupModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _cleanupKey = new() { UseSystemPasswordChar = true, Width = 240, Anchor = AnchorStyles.Left };
    private readonly Label _cleanupStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 2, 3, 6) };
    private readonly Button _cleanupTest;
    private readonly DataGridView _snippets = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = true,
        AllowUserToDeleteRows = true,
        RowHeadersWidth = 28,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
    };
    private readonly ComboBox _insertion = Ui.Combo("Paste (recommended)", "Type it out (for apps that block paste)");
    private readonly CheckBox _trailingSpace = Ui.Check("Add a space after the text");
    private readonly CheckBox _sounds = Ui.Check("Play start and stop sounds");
    private readonly CheckBox _startWithWindows = Ui.Check("Start Plainspoken when Windows starts");
    private readonly CheckBox _keepHistory = Ui.Check("Keep the last 20 transcripts (tray menu → Recent)");
    private readonly CheckBox _miniButton = Ui.Check("Show a small dictation button on screen when idle");
    private readonly NumericUpDown _hedge = new() { Minimum = 0, Maximum = 30, Width = 60, Anchor = AnchorStyles.Left };
    private readonly CheckBox _compress = Ui.Check("Compress the audio before sending (smaller, faster uploads)");
    private readonly TextBox _currency = new() { Width = 50, Anchor = AnchorStyles.Left, MaxLength = 4 };
    private bool _resetMiniPosition;
    private readonly NumericUpDown _maxMinutes = new() { Minimum = 1, Maximum = 30, Width = 70, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _timeout = new() { Minimum = 10, Maximum = 600, Width = 70, Anchor = AnchorStyles.Left };

    public SettingsForm(ISettingsHost host, HistoryStore history)
    {
        _host = host;
        _history = history;
        _working = host.CurrentSettings;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{AppInfo.Name} settings";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 540);
        MinimumSize = new Size(460, 360);
        ShowInTaskbar = true;
        MaximizeBox = true;

        for (var i = 0; i < _services.Length; i++)
        {
            if (i > 0)
            {
                _services[i].Items.Add(NoBackup);
            }

            _services[i].Items.AddRange(SpeechServices.All.Select(s => (object)s.DisplayName()).ToArray());
            _services[i].SelectedIndexChanged += (_, _) => UpdateServicesStatus();
        }

        _cleanupTest = Ui.Button("Test key", async (_, _) => await TestCleanupKeyAsync().ConfigureAwait(true));
        _cleanup.SelectedIndexChanged += (_, _) => UpdateCleanupFields();
        _vocabulary.TextChanged += (_, _) => UpdateVocabularyCount();
        _hotkey.CaptureEnded += (_, _) => _host.ResumeHotkey();
        _snippets.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "When I say", FillWeight = 35, MaxInputLength = SnippetCleaner.MaxTriggerLength });
        _snippets.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Type this",
            FillWeight = 65,
            MaxInputLength = SnippetCleaner.MaxTextLength,
            DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
        });
        _snippets.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Ui.Page("General", BuildGeneral()));
        tabs.TabPages.Add(Ui.Page("Clean-up", BuildCleanup()));
        tabs.TabPages.Add(Ui.Page("Vocabulary", BuildVocabulary()));
        tabs.TabPages.Add(Ui.Page("Snippets", BuildSnippets()));
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
        LoadFrom(_working);
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

    private string? KeyFor(string slot) => _newKeys.TryGetValue(slot, out var k) ? (k.Length == 0 ? null : k) : _host.KeyFor(slot);

    private TableLayoutPanel BuildGeneral()
    {
        var g = Ui.Grid();
        string[] labels = ["Speech service", "First backup", "Second backup"];
        for (var i = 0; i < _services.Length; i++)
        {
            var index = i;
            Ui.Row(g, labels[i], Ui.Flow(_services[i], Ui.Button("Set up…", (_, _) => SetUp(index))));
        }

        Ui.Row(g, null, _servicesStatus);
        Ui.Row(g, null, Ui.Note("If the main service fails, the first backup is used at once; if it is slow, the backup is asked too " +
                                "(Options → Speed). Each service needs its own key, set with Set up…"));

        var change = Ui.Button("Change…", (_, _) =>
        {
            _host.SuspendHotkey(); // so pressing the current hotkey is captured, not triggered
            _hotkey.BeginCapture();
        });
        var reset = Ui.Button("Default", (_, _) => _hotkey.Value = PlainspokenSettings.DefaultHotkey);
        Ui.Row(g, "Hotkey", Ui.Flow(_hotkey, change, reset));
        Ui.Row(g, null, Ui.Note("Press once to start, again to stop. Esc cancels while recording."));
        Ui.Row(g, "Hold to talk", _holdKey);
        Ui.Row(g, null, Ui.Note("Hold the key, speak, and let go to type. A quick tap, or a shortcut such as Ctrl+C, works as usual."));
        Ui.Row(g, "Languages", _languages);
        Ui.Row(g, "Mode", _mode);
        Ui.Row(g, "Hindi words in", _hindiScript);
        var refresh = Ui.Button("Refresh", (_, _) => LoadMicrophones(SelectedMicrophoneId()));
        Ui.Row(g, "Microphone", Ui.Flow(_microphone, refresh));
        return g;
    }

    private TableLayoutPanel BuildCleanup()
    {
        var g = Ui.Grid();
        Ui.Row(g, null, Ui.Note("In Smart mode, the text from Sarvam, OpenAI-compatible, Deepgram and ElevenLabs is tidied by a language " +
                                "model: fillers (um, aaa, matlab…), repeats and false starts go, punctuation and grammar are fixed, and your " +
                                "own words, language and tone stay. It never translates or answers what you said."));
        Ui.Row(g, "Clean-up by", _cleanup);
        Ui.Row(g, "Gemini model", _cleanupGeminiModel);
        Ui.Row(g, null, Ui.Note("Uses your Gemini key and address (Settings → General → Set up… for a Gemini service)."));
        Ui.Row(g, null, _cleanupAlsoGemini);
        Ui.Row(g, "Chat address", _cleanupBaseUrl);
        Ui.Row(g, "Chat model", _cleanupModel);
        var show = Ui.Check("Show");
        show.CheckedChanged += (_, _) => _cleanupKey.UseSystemPasswordChar = !show.Checked;
        Ui.Row(g, "Chat key", Ui.Flow(_cleanupKey, show, _cleanupTest));
        Ui.Row(g, null, _cleanupStatus);
        Ui.Row(g, null, Ui.Note("For example OpenAI (https://api.openai.com/v1, gpt-4o-mini) or Groq (https://api.groq.com/openai/v1, " +
                                "llama-3.3-70b-versatile). Leave the key empty to keep the saved one."));
        return g;
    }

    private TableLayoutPanel BuildVocabulary()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(Ui.Note("Names and special words Plainspoken should spell correctly — one per line (up to 1,000). " +
                               "For example: people's names, hospitals, medical terms, abbreviations."), 0, 0);
        t.Controls.Add(_vocabulary, 0, 1);
        t.Controls.Add(_vocabularyCount, 0, 2);
        t.Controls.Add(_learn, 0, 3);
        t.Controls.Add(Ui.Note("When you fix a name Plainspoken typed, it offers to add it the next time you dictate in that app. " +
                               "To spot the change it reads that text box once, on your computer; the text is not saved or sent anywhere."), 0, 4);
        return t;
    }

    private TableLayoutPanel BuildSnippets()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(Ui.Note("Say a phrase to type a longer text, such as your address or a sign-off. Saying only the phrase types the " +
                               "text; inside a sentence the phrase is replaced. Up to 100."), 0, 0);
        t.Controls.Add(_snippets, 0, 1);
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
        Ui.Row(g, "Speed", Ui.Flow(new Label { Text = "Ask the backup too after", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }, _hedge,
            new Label { Text = "seconds", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));
        Ui.Row(g, null, Ui.Note("0 = only when the main service fails. Long recordings wait a little longer."));
        Ui.Row(g, null, _compress);
        Ui.Row(g, "Currency", _currency);
        Ui.Row(g, null, Ui.Button("Speed and cost…", (_, _) => _host.ShowStats()));
        return g;
    }

    private TableLayoutPanel BuildAdvanced()
    {
        var g = Ui.Grid();
        Ui.Row(g, "Request timeout", Ui.Flow(_timeout, new Label { Text = "seconds", AutoSize = true, Margin = new Padding(3, 6, 3, 3) }));
        Ui.Row(g, null, Ui.Note("Models, addresses and streaming are set per service: Settings → General → Set up…"));
        Ui.Row(g, "Share settings", Ui.Flow(Ui.Button("Export…", (_, _) => Export()), Ui.Button("Import…", (_, _) => Import())));
        Ui.Row(g, null, Ui.Note("Exported files never include your keys. Share them to give others your vocabulary, snippets and preferences."));
        Ui.Row(g, "Folders", Ui.Flow(
            Ui.Button("Data folder", (_, _) => AppPaths.OpenFolder(AppPaths.LocalDir)),
            Ui.Button("Logs", (_, _) => AppPaths.OpenFolder(AppPaths.LogsDir))));
        Ui.Row(g, null, Ui.Button("Restore service defaults", (_, _) =>
        {
            var d = new PlainspokenSettings().Normalize().Transcription;
            _timeout.Value = d.RequestTimeoutSeconds;
            var t = _working.Transcription;
            t.Services = d.Services;
            t.TranscribeModel = d.TranscribeModel;
            t.GenerateModel = d.GenerateModel;
            t.ApiBaseUrl = d.ApiBaseUrl;
            t.LiveStreaming = d.LiveStreaming;
            t.LiveModel = d.LiveModel;
            MessageBox.Show(this, "Every service's model, address, streaming and price is back to its default. Click Save to keep this.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));
        return g;
    }

    private void LoadFrom(PlainspokenSettings s)
    {
        var t = s.Transcription;
        var order = t.OrderedServices();
        for (var i = 0; i < _services.Length; i++)
        {
            _services[i].SelectedIndex = i < order.Count ? (int)order[i] + (i > 0 ? 1 : 0) : 0;
        }

        _hotkey.Value = s.Hotkey;
        _holdKey.SelectedIndex = Math.Max(0, Array.IndexOf(HoldKeys, s.HoldKey));
        _languages.SelectedIndex = (int)t.Languages;
        _mode.SelectedIndex = (int)t.Mode;
        _hindiScript.SelectedIndex = (int)t.HindiScript;
        LoadMicrophones(s.Local.MicrophoneId);
        _vocabulary.Text = VocabularyCleaner.ToText(t.CustomVocabulary);
        _learn.Checked = s.Local.LearnFromCorrections;
        _cleanup.SelectedIndex = (int)t.Cleanup.Provider;
        _cleanupGeminiModel.Text = t.Cleanup.GeminiModel;
        _cleanupAlsoGemini.Checked = t.Cleanup.AlsoGemini;
        _cleanupBaseUrl.Text = t.Cleanup.OpenaiBaseUrl;
        _cleanupModel.Text = t.Cleanup.OpenaiModel;
        _cleanupKey.Text = string.Empty;
        _cleanupStatus.ForeColor = SystemColors.GrayText;
        _cleanupStatus.Text = KeyFor(SpeechServices.CleanupKey) is null ? "No chat key saved." : "A chat key is saved.";
        _snippets.Rows.Clear();
        foreach (var snippet in s.Snippets)
        {
            _snippets.Rows.Add(snippet.Trigger, snippet.Text);
        }

        _insertion.SelectedIndex = (int)s.Insertion.Method;
        _trailingSpace.Checked = s.Insertion.TrailingSpace;
        _sounds.Checked = s.Recording.Sounds;
        _startWithWindows.Checked = s.Local.StartWithWindows;
        _keepHistory.Checked = s.History.Enabled;
        _miniButton.Checked = s.ShowMiniButton;
        _maxMinutes.Value = Math.Clamp(Math.Max(1, s.Recording.MaxSeconds / 60), 1, 30);
        _hedge.Value = Math.Clamp(t.HedgeSeconds, 0, 30);
        _compress.Checked = t.CompressAudio;
        _currency.Text = t.Currency;
        _timeout.Value = Math.Clamp(t.RequestTimeoutSeconds, 10, 600);
        UpdateVocabularyCount();
        UpdateCleanupFields();
        UpdateServicesStatus();
    }

    private List<string> SelectedOrder()
    {
        var order = new List<string>();
        for (var i = 0; i < _services.Length; i++)
        {
            var index = _services[i].SelectedIndex - (i > 0 ? 1 : 0);
            if (index >= 0 && index < SpeechServices.All.Count)
            {
                var id = SpeechServices.All[index].Id();
                if (!order.Contains(id))
                {
                    order.Add(id);
                }
            }
        }

        return order;
    }

    private PlainspokenSettings BuildFromUi()
    {
        var s = SettingsSerializer.Clone(_working);
        var t = s.Transcription;
        t.ServiceOrder = SelectedOrder();
        s.Hotkey = _hotkey.Value;
        s.HoldKey = HoldKeys[Math.Max(0, _holdKey.SelectedIndex)];
        t.Languages = (LanguagePreset)Math.Max(0, _languages.SelectedIndex);
        t.Mode = (TranscriptionMode)Math.Max(0, _mode.SelectedIndex);
        t.HindiScript = (HindiScript)Math.Max(0, _hindiScript.SelectedIndex);
        s.Local.MicrophoneId = SelectedMicrophoneId();
        t.CustomVocabulary = VocabularyCleaner.FromText(_vocabulary.Text);
        s.Local.LearnFromCorrections = _learn.Checked;
        t.Cleanup.Provider = (CleanupProvider)Math.Max(0, _cleanup.SelectedIndex);
        t.Cleanup.GeminiModel = _cleanupGeminiModel.Text;
        t.Cleanup.AlsoGemini = _cleanupAlsoGemini.Checked;
        t.Cleanup.OpenaiBaseUrl = _cleanupBaseUrl.Text;
        t.Cleanup.OpenaiModel = _cleanupModel.Text;
        s.Snippets = _snippets.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => new Snippet { Trigger = r.Cells[0].Value as string ?? string.Empty, Text = r.Cells[1].Value as string ?? string.Empty })
            .ToList();
        s.Insertion.Method = (InsertionMethod)Math.Max(0, _insertion.SelectedIndex);
        s.Insertion.TrailingSpace = _trailingSpace.Checked;
        s.Recording.Sounds = _sounds.Checked;
        s.Local.StartWithWindows = _startWithWindows.Checked;
        s.History.Enabled = _keepHistory.Checked;
        s.ShowMiniButton = _miniButton.Checked;
        if (_resetMiniPosition)
        {
            s.Local.MiniButtonPosition = null;
        }

        s.Recording.MaxSeconds = (int)_maxMinutes.Value * 60;
        t.HedgeSeconds = (int)_hedge.Value;
        t.CompressAudio = _compress.Checked;
        t.Currency = _currency.Text;
        t.RequestTimeoutSeconds = (int)_timeout.Value;
        return s.Normalize();
    }

    private void SetUp(int index)
    {
        var selected = _services[index].SelectedIndex - (index > 0 ? 1 : 0);
        if (selected < 0)
        {
            MessageBox.Show(this, "Choose a service first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var service = SpeechServices.All[selected];
        _working = BuildFromUi(); // keep what was changed on the other tabs
        using var form = new ServiceForm(_host, _working, service, KeyFor(service.KeySlot()));
        if (form.ShowDialog(this) == DialogResult.OK && form.NewKey is { } key)
        {
            _newKeys[service.KeySlot()] = key;
        }

        UpdateServicesStatus();
    }

    private void UpdateServicesStatus()
    {
        var missing = SelectedOrder().Select(id => SpeechServices.ById(id)!.Value).Where(s => KeyFor(s.KeySlot()) is null)
            .Select(s => s.DisplayName()).ToList();
        _servicesStatus.ForeColor = missing.Count == 0 ? Ui.Good : Ui.Bad;
        _servicesStatus.Text = missing.Count == 0
            ? "Every chosen service has a key."
            : $"No key yet for: {string.Join(", ", missing)}. Click Set up… next to it.";
    }

    private void UpdateCleanupFields()
    {
        var provider = (CleanupProvider)Math.Max(0, _cleanup.SelectedIndex);
        _cleanupGeminiModel.Enabled = provider == CleanupProvider.Gemini;
        _cleanupAlsoGemini.Enabled = provider != CleanupProvider.Off;
        var chat = provider == CleanupProvider.OpenAI;
        _cleanupBaseUrl.Enabled = chat;
        _cleanupModel.Enabled = chat;
        _cleanupKey.Enabled = chat;
        _cleanupTest.Enabled = chat;
    }

    private void Save()
    {
        if (_hotkey.Capturing)
        {
            return;
        }

        var updated = BuildFromUi();
        var keys = new Dictionary<string, string>(_newKeys, StringComparer.Ordinal);
        if (_cleanupKey.Text.Trim().Length > 0)
        {
            keys[SpeechServices.CleanupKey] = _cleanupKey.Text.Trim();
        }

        string? Final(SpeechService s) => keys.TryGetValue(s.KeySlot(), out var k) ? (k.Length == 0 ? null : k) : _host.KeyFor(s.KeySlot());
        if (updated.Transcription.OrderedServices().All(s => Final(s) is null) &&
            MessageBox.Show(this, "None of the chosen speech services has a key, so Plainspoken can't transcribe yet. Save anyway?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        if (!_host.TryApply(updated, keys, out var error))
        {
            MessageBox.Show(this, error ?? "Couldn't save settings.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Close();
    }

    private async Task TestCleanupKeyAsync()
    {
        var key = _cleanupKey.Text.Trim().Length > 0 ? _cleanupKey.Text.Trim() : KeyFor(SpeechServices.CleanupKey) ?? string.Empty;
        _cleanupTest.Enabled = false;
        _cleanupStatus.ForeColor = SystemColors.GrayText;
        _cleanupStatus.Text = "Checking…";
        try
        {
            var result = await KeyTester.TestCleanupAsync(_host.Http, BuildFromUi(), key, _host.Log, CancellationToken.None).ConfigureAwait(true);
            if (IsDisposed)
            {
                return;
            }

            _cleanupStatus.ForeColor = result.Ok ? Ui.Good : Ui.Bad;
            _cleanupStatus.Text = result.Message;
        }
        finally
        {
            if (!IsDisposed)
            {
                _cleanupTest.Enabled = true;
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
            Title = "Export settings (your keys are not included)",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, SettingsSerializer.Export(BuildFromUi()));
            MessageBox.Show(this, "Settings exported. Your keys were not included.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            LoadFrom(_working);
            MessageBox.Show(this, "Imported. Check the tabs, then click Save to keep these settings.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(this, "That file isn't a Plainspoken settings file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
