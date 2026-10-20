using Plainspoken.App.Platform;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.App.UI;

/// <summary>
/// Set-up dialog for one speech service: key (with a free test request), model, address, streaming, vocabulary hints and
/// price. It edits the settings window's working copy; nothing is saved until the settings window's Save.
/// </summary>
internal sealed class ServiceForm : Form
{
    private static readonly (string Value, string Label)[] SarvamModes =
    [
        ("", "Automatic (follows the Hindi script setting)"),
        ("translit", "translit — everything in Roman letters"),
        ("codemix", "codemix — English in English, Hindi in Devanagari"),
        ("transcribe", "transcribe — each language in its own script"),
    ];

    private readonly ISettingsHost _host;
    private readonly PlainspokenSettings _working;
    private readonly SpeechService _service;
    private readonly string? _savedKey;
    private readonly TextBox _key = new() { UseSystemPasswordChar = true, Width = 300, Anchor = AnchorStyles.Left };
    private readonly CheckBox _showKey = Ui.Check("Show");
    private readonly Label _keyStatus = new() { AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 2, 3, 6) };
    private readonly Button _test;
    private readonly TextBox _model = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _liveModel = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly TextBox _baseUrl = new() { Width = 300, Anchor = AnchorStyles.Left };
    private readonly CheckBox _streaming = Ui.Check("Stream while you speak");
    private readonly CheckBox _hints = Ui.Check("Send my vocabulary as hints");
    private readonly ComboBox _sarvamMode = Ui.Combo(SarvamModes.Select(m => (object)m.Label).ToArray());
    private readonly NumericUpDown _price = new() { Minimum = 0, Maximum = 100_000, DecimalPlaces = 2, Width = 100, Anchor = AnchorStyles.Left };
    private bool _removeKey;

    public ServiceForm(ISettingsHost host, PlainspokenSettings working, SpeechService service, string? savedKey)
    {
        _host = host;
        _working = working;
        _service = service;
        _savedKey = savedKey;

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = service.DisplayName();
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(440, 340);
        ShowInTaskbar = false;
        MinimizeBox = false;

        _test = Ui.Button("Test key", async (_, _) => await TestAsync().ConfigureAwait(true));
        _showKey.CheckedChanged += (_, _) => _key.UseSystemPasswordChar = !_showKey.Checked;

        var g = Ui.Grid();
        Ui.Row(g, null, Ui.Note(Describe(service)));
        Ui.Row(g, "API key", Ui.Flow(_key, _showKey, _test));
        Ui.Row(g, null, _keyStatus);
        var shared = service.IsGemini() ? " Both Gemini choices use the same key." : string.Empty;
        var keyNote = (string.IsNullOrEmpty(savedKey)
            ? "No key saved yet."
            : "A key is saved (encrypted for your Windows account). Paste a new one to replace it.") + shared;
        Ui.Row(g, null, Ui.Flow(Ui.Note(keyNote), Ui.Button("Remove key", (_, _) => RemoveKey())));
        Ui.Row(g, null, Ui.Link($"Get a key at {service.Info().KeyUrl}", "https://" + service.Info().KeyUrl));
        Ui.Row(g, "Model", _model);
        Ui.Row(g, null, Ui.Note(ModelNote(service)));
        if (service == SpeechService.Gemini)
        {
            Ui.Row(g, null, _streaming);
            Ui.Row(g, "Live model", _liveModel);
        }

        Ui.Row(g, "Address", _baseUrl);
        Ui.Row(g, null, Ui.Note(service.IsGemini()
            ? "Change only for a different server or region. Both Gemini choices and the Gemini clean-up use this address."
            : "Change only for a different server or region."));
        if (service == SpeechService.Sarvam)
        {
            Ui.Row(g, "Output", _sarvamMode);
        }

        if (service.Info().CanStream && service != SpeechService.Gemini)
        {
            Ui.Row(g, null, _streaming);
        }

        if (service.Info().CanStream)
        {
            Ui.Row(g, null, Ui.Note("Sends your voice as you speak, so the text is ready soon after you stop; it helps most with long " +
                                    "dictations. Used when this is the main service; if streaming fails the normal way is used."));
        }

        if (!service.IsGemini())
        {
            Ui.Row(g, null, _hints);
            Ui.Row(g, null, Ui.Note(HintNote(service)));
        }

        var currency = working.Transcription.Currency;
        Ui.Row(g, $"Price ({currency}/hour)", _price);
        Ui.Row(g, null, Ui.Note("What one hour of audio costs you on this service. Used only for the estimate in Speed and cost. 0 = not set."));

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(g);
        var ok = Ui.Button("OK", (_, _) => Accept());
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        var reset = Ui.Button("Defaults", (_, _) => LoadDefaults());
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(reset);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(scroll);
        Controls.Add(buttons);
        LoadFrom(working);
    }

    /// <summary>After OK: the key typed in (null = keep the saved one; empty = remove it).</summary>
    public string? NewKey { get; private set; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Ui.FitToScreen(this);
    }

    private static string Describe(SpeechService service) => service switch
    {
        SpeechService.Gemini => "Google's speech model: accurate, tidies the text itself, can stream. The free tier is small and sometimes busy.",
        SpeechService.GeminiGeneral => "A general Gemini model listening to the audio: fast and usually available; tidies the text itself.",
        SpeechService.Sarvam => "Built for Indian languages and Hinglish; can stream. Paid per second of audio.",
        SpeechService.OpenAI => "Any service with an OpenAI-style transcription API: OpenAI, Groq (address https://api.groq.com/openai/v1), " +
                                "or your own Whisper server.",
        SpeechService.Deepgram => "Very fast, can stream; follows English and Hindi in the same sentence. Paid per minute.",
        _ => "Accurate transcription with Hindi support. Paid per hour of audio.",
    };

    private static string ModelNote(SpeechService service) => service switch
    {
        SpeechService.Gemini => "Default gemini-3.5-transcribe. Change only if Google renames it.",
        SpeechService.GeminiGeneral => "Default gemini-3.5-flash (more accurate); gemini-3.5-flash-lite is faster.",
        SpeechService.Sarvam => "saaras:v3 (default) or saaras:v4 (newer; also uses your vocabulary).",
        SpeechService.OpenAI => "For example gpt-4o-mini-transcribe, gpt-4o-transcribe or whisper-1 (OpenAI), whisper-large-v3-turbo (Groq).",
        SpeechService.Deepgram => "nova-3 (default; uses your vocabulary).",
        _ => "scribe_v2 (default; leaves out fillers in Smart mode) or scribe_v1.",
    };

    private static string HintNote(SpeechService service) => service switch
    {
        SpeechService.Sarvam => "Works with saaras:v4 only (up to 50 words).",
        SpeechService.ElevenLabs => "ElevenLabs charges 20 % more when hints are sent.",
        SpeechService.Deepgram => "Works with nova-3 (up to 100 words).",
        _ => "Names are added to the request so they're spelled right.",
    };

    private void LoadFrom(PlainspokenSettings s)
    {
        var t = s.Transcription;
        var config = t.Services[_service];
        _model.Text = t.ModelOf(_service);
        _liveModel.Text = t.LiveModel;
        _baseUrl.Text = t.BaseUrlOf(_service);
        _streaming.Checked = _service == SpeechService.Gemini ? t.LiveStreaming : config.Streaming;
        _hints.Checked = config.VocabularyHints;
        _price.Value = (decimal)Math.Clamp(config.PricePerHour, 0, 100_000);
        var mode = Array.FindIndex(SarvamModes, m => m.Value == config.Option);
        _sarvamMode.SelectedIndex = Math.Max(0, mode);
        _keyStatus.Text = string.Empty;
    }

    private void LoadDefaults()
    {
        var defaults = new PlainspokenSettings().Normalize();
        defaults.Transcription.Currency = _working.Transcription.Currency;
        LoadFrom(defaults);
    }

    /// <summary>Copies the fields into <paramref name="s"/>.</summary>
    private void ApplyTo(PlainspokenSettings s)
    {
        var t = s.Transcription;
        var config = t.Services[_service];
        switch (_service)
        {
            case SpeechService.Gemini:
                t.TranscribeModel = _model.Text;
                t.LiveModel = _liveModel.Text;
                t.LiveStreaming = _streaming.Checked;
                t.ApiBaseUrl = _baseUrl.Text;
                break;
            case SpeechService.GeminiGeneral:
                t.GenerateModel = _model.Text;
                t.ApiBaseUrl = _baseUrl.Text;
                break;
            default:
                config.Model = _model.Text;
                config.ApiBaseUrl = _baseUrl.Text;
                config.Streaming = _streaming.Checked;
                config.VocabularyHints = _hints.Checked;
                break;
        }

        config.PricePerHour = (double)_price.Value;
        if (_service == SpeechService.Sarvam)
        {
            config.Option = SarvamModes[Math.Max(0, _sarvamMode.SelectedIndex)].Value;
        }
    }

    private void RemoveKey()
    {
        _key.Text = string.Empty;
        _removeKey = true;
        _keyStatus.ForeColor = SystemColors.GrayText;
        _keyStatus.Text = "The key will be removed when you click OK and then Save.";
    }

    private void Accept()
    {
        ApplyTo(_working);
        _working.Normalize();
        var typed = _key.Text.Trim();
        NewKey = typed.Length > 0 ? typed : _removeKey ? string.Empty : null;
        DialogResult = DialogResult.OK;
        Close();
    }

    private async Task TestAsync()
    {
        var key = _key.Text.Trim().Length > 0 ? _key.Text.Trim() : _savedKey ?? string.Empty;
        var trial = SettingsSerializer.Clone(_working);
        ApplyTo(trial);
        trial.Normalize();
        _test.Enabled = false;
        _keyStatus.ForeColor = SystemColors.GrayText;
        _keyStatus.Text = "Checking…";
        try
        {
            var result = await KeyTester.TestAsync(_host.Http, _service, trial, key, _host.Log, CancellationToken.None).ConfigureAwait(true);
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
                _test.Enabled = true;
            }
        }
    }
}
