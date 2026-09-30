using System.Net;
using Plainspoken.App.Audio;
using Plainspoken.App.Platform;
using Plainspoken.App.UI;
using Plainspoken.Core;
using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Transcription;

namespace Plainspoken.App;

/// <summary>What the settings and first-run windows need from the running app.</summary>
internal interface ISettingsHost
{
    PlainspokenSettings CurrentSettings { get; }

    string? ApiKey { get; }

    HttpClient Http { get; }

    ILog Log { get; }

    /// <summary>Saves settings (and the key, if given). Returns false with a message if the hotkey can't be registered.</summary>
    bool TryApply(PlainspokenSettings updated, string? newApiKey, out string? error);

    void SuspendHotkey();

    void ResumeHotkey();

    void ClearHistory();
}

/// <summary>Owns the tray icon, hotkeys, overlay and the dictation controller; the app's lifetime.</summary>
internal sealed class TrayContext : ApplicationContext, IDictationView, ISettingsHost
{
    private readonly ILog _log;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryStore _history;
    private readonly PendingStore _pending;
    private readonly HttpClient _http;
    private readonly WasapiRecorder _recorder;
    private readonly ForegroundTracker _foreground;
    private readonly TextInserter _inserter;
    private readonly Sounds _sounds;
    private readonly OverlayForm _overlay;
    private readonly HotkeyWindow _hotkeys;
    private readonly TrayIcons _icons;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly DictationController _controller;
    private PlainspokenSettings _settings;
    private SettingsForm? _settingsForm;
    private FirstRunForm? _firstRunForm;
    private UserAction _balloonAction;
    private bool _hotkeyOk;
    private bool _exiting;

    public TrayContext(ILog log)
    {
        _log = log;
        _settingsStore = new SettingsStore(AppPaths.SettingsFile, log);
        _settings = _settingsStore.Load(CreateDefaultSettings);
        if (!_settingsStore.Exists)
        {
            _settingsStore.Save(_settings);
        }

        _history = new HistoryStore(AppPaths.HistoryFile, log);
        _pending = new PendingStore(AppPaths.PendingDir, log);
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            // Keep the connection open between dictations so the next one skips DNS/TCP/TLS setup.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeouts are set by GeminiClient
        };

        _recorder = new WasapiRecorder(log);
        _foreground = new ForegroundTracker();
        _inserter = new TextInserter(() => _settings, _foreground, log);
        _sounds = new Sounds();
        _icons = new TrayIcons();

        var transcriber = new TranscriptionService(TranscriptionService.GeminiEngines(_http, () => ApiKey, log), log,
            warmUp: TranscriptionService.GeminiWarmUp(_http, () => ApiKey, log));
        var live = new GeminiLiveTranscriber(() => new WebSocketLiveSocket(), () => ApiKey, log);
        _controller = new DictationController(() => _settings, () => !string.IsNullOrEmpty(ApiKey), _recorder, transcriber,
            _inserter, this, _sounds, _history, _pending, log, liveTranscriber: live);

        _overlay = new OverlayForm(() => _controller.Elapsed, () => _controller.Level, () => HotkeyKeys.Display(_settings.Hotkey))
        {
            MiniEnabled = _settings.ShowMiniButton,
            CustomAnchor = ParseAnchor(_settings.Local.MiniButtonPosition),
        };
        _overlay.DoneClicked += (_, _) => Fire(_controller.ToggleAsync);
        _overlay.MiniClicked += (_, _) => OnMiniClicked();
        _overlay.MiniMenuRequested += (_, point) => ShowMiniMenu(point);
        _overlay.AnchorChanged += (_, point) => SaveAnchor(point);
        _overlay.CancelClicked += (_, _) => Fire(_controller.CancelAsync);
        _overlay.Tick += (_, _) => Fire(_controller.TickAsync);
        _overlay.ErrorClicked += (_, _) => RunAction(_balloonAction);
        _ = _overlay.Handle; // create now so BeginInvoke works from other threads

        _recorder.CaptureFaulted += (_, _) => _overlay.BeginInvoke(() =>
        {
            if (_controller.State == DictationState.Recording)
            {
                Fire(_controller.ToggleAsync); // transcribe what we have
            }
        });

        _hotkeys = new HotkeyWindow();
        _hotkeys.Pressed += OnHotkey;

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = _icons.For(DictationState.Idle),
            Text = AppInfo.Name,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += OnTrayClick;
        _tray.BalloonTipClicked += (_, _) => RunAction(_balloonAction);

        StartupRegistration.Apply(_settings.Local.StartWithWindows, log);
        RegisterMainHotkey(showErrors: true);
        UpdateTray(DictationState.Idle);

        _overlay.GoIdle(); // shows the mini button if enabled

        if (!_settings.Local.FirstRunCompleted)
        {
            _overlay.BeginInvoke(ShowFirstRun);
        }
    }

    public PlainspokenSettings CurrentSettings => SettingsSerializer.Clone(_settings);

    public string? ApiKey => ApiKeyProtector.Unprotect(_settings.Local.ApiKeyProtected);

    public HttpClient Http => _http;

    public ILog Log => _log;

    /// <summary>Called (on the UI thread) when a second copy of the app is launched.</summary>
    public void ShowSettings()
    {
        if (_exiting)
        {
            return;
        }

        if (!_settings.Local.FirstRunCompleted)
        {
            ShowFirstRun();
            return;
        }

        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(this, _history);
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    public void ShowUnexpectedError()
    {
        if (!_exiting)
        {
            ShowError(new UserMessage("Something went wrong, but Plainspoken is still running. Please try again."));
        }
    }

    // ---- ISettingsHost ------------------------------------------------------------------

    public bool TryApply(PlainspokenSettings updated, string? newApiKey, out string? error)
    {
        ArgumentNullException.ThrowIfNull(updated);
        error = null;
        updated.Normalize();
        var previous = _settings;
        if (newApiKey is not null)
        {
            updated.Local.ApiKeyProtected = ApiKeyProtector.Protect(newApiKey);
        }

        if (updated.Local.MiniButtonPosition is not null)
        {
            // Keep a position dragged while the Settings window was open (null means "reset" was pressed).
            updated.Local.MiniButtonPosition = previous.Local.MiniButtonPosition;
        }

        var hotkeyChanged = !string.Equals(previous.Hotkey, updated.Hotkey, StringComparison.Ordinal);
        _settings = updated;
        if (!RegisterMainHotkey(showErrors: false, out var hotkeyError) && hotkeyChanged)
        {
            // Keep the old, working hotkey and let the user pick another one.
            _settings = previous;
            RegisterMainHotkey(showErrors: false);
            error = hotkeyError;
            return false;
        }

        _settingsStore.Save(_settings);
        StartupRegistration.Apply(_settings.Local.StartWithWindows, _log);
        if (!_settings.History.Enabled && _history.Entries.Count > 0)
        {
            _history.Clear();
        }

        _overlay.MiniEnabled = _settings.ShowMiniButton;
        _overlay.CustomAnchor = ParseAnchor(_settings.Local.MiniButtonPosition);
        if (_controller.State == DictationState.Idle && _overlay.Mode is PillMode.Mini or PillMode.Hidden)
        {
            _overlay.GoIdle();
        }

        UpdateTray(_controller.State);
        _log.Info("settings saved");
        return true;
    }

    public void SuspendHotkey() => _hotkeys.Unregister(HotkeyWindow.ToggleId);

    public void ResumeHotkey() => RegisterMainHotkey(showErrors: false);

    public void ClearHistory() => _history.Clear();

    // ---- IDictationView (called on the UI thread by the controller) --------------------

    public void OnStateChanged(DictationState state)
    {
        if (state == DictationState.Recording)
        {
            // Esc cancels only while recording; it is released immediately afterwards.
            if (!_hotkeys.Register(HotkeyWindow.CancelId, HotkeyModifiers.None, Keys.Escape, out var err))
            {
                _log.Warn($"Esc hotkey: {err}");
            }
        }
        else
        {
            _hotkeys.Unregister(HotkeyWindow.CancelId);
        }

        if (state == DictationState.Idle)
        {
            _overlay.EndBusy();
        }

        UpdateTray(state);
    }

    public void ShowRecording() => _overlay.ShowRecording();

    public void ShowTranscribing(string text) => _overlay.ShowTranscribing(text);

    public void ShowWarning(string text) => _overlay.ShowWarning(text);

    public void ShowHint(string text) => _overlay.ShowNote(text);

    public void ShowError(UserMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _balloonAction = message.Action;
        _overlay.ShowHint(message.Text, error: true);
        var suffix = message.Action switch
        {
            UserAction.OpenSettings => " Click to open Settings.",
            UserAction.Retry => " Click to retry.",
            UserAction.OpenMicrophonePrivacySettings => " Click to open Windows Settings.",
            _ => string.Empty,
        };
        if (message.Action == UserAction.OpenMicrophonePrivacySettings)
        {
            // Recording couldn't start anyway, so a dialog with a real button is fine here.
            _overlay.BeginInvoke(() => ShowMicrophoneBlockedDialog(message.Text));
            return;
        }

        _tray.ShowBalloonTip(8000, AppInfo.Name, message.Text + suffix, ToolTipIcon.Warning);
        if (message.RunActionNow)
        {
            RunAction(message.Action);
        }
    }

    public void HideOverlay() => _overlay.GoIdle();

    // ---- lifetime ------------------------------------------------------------------------

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _log.Info("exiting");
        _tray.Visible = false;
        _hotkeys.Dispose(); // unregisters every hotkey
        if (_recorder.IsRecording)
        {
            _ = _recorder.CancelAsync();
        }

        _settingsForm?.Close();
        _firstRunForm?.Close();
        _overlay.HideCompletely();
        _overlay.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeys.Dispose();
            _recorder.Dispose();
            _foreground.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _icons.Dispose();
            _sounds.Dispose();
            _overlay.Dispose();
            _http.Dispose();
        }

        base.Dispose(disposing);
    }

    private static PlainspokenSettings CreateDefaultSettings()
    {
        var settings = new PlainspokenSettings();
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("Plainspoken.vocabulary.default.txt");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            settings.Transcription.CustomVocabulary = VocabularyCleaner.FromText(reader.ReadToEnd());
        }

        return settings;
    }

    private void Fire(Func<Task> action)
    {
        // Controller methods never throw; this also observes any unexpected fault.
        action().ContinueWith(t => _log.Error("background task failed", t.Exception), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void OnHotkey(object? sender, int id)
    {
        if (id == HotkeyWindow.ToggleId)
        {
            Fire(_controller.ToggleAsync);
        }
        else if (id == HotkeyWindow.CancelId)
        {
            Fire(_controller.CancelAsync);
        }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (!_settings.Local.FirstRunCompleted)
            {
                ShowFirstRun();
                return;
            }

            Fire(_controller.ToggleAsync);
        }
    }

    private bool RegisterMainHotkey(bool showErrors) => RegisterMainHotkey(showErrors, out _);

    private bool RegisterMainHotkey(bool showErrors, out string? error)
    {
        error = null;
        if (!HotkeyKeys.TryResolve(_settings.Hotkey, out var mods, out var key, out var parseError))
        {
            error = parseError;
        }
        else if (!_hotkeys.Register(HotkeyWindow.ToggleId, mods, key, out var regError))
        {
            error = $"{HotkeyKeys.Display(_settings.Hotkey)} {regError}. Choose another hotkey in Settings.";
        }

        _hotkeyOk = error is null;
        if (!_hotkeyOk)
        {
            _log.Warn($"hotkey registration failed: {error}");
            if (showErrors)
            {
                ShowError(new UserMessage(error!, UserAction.OpenSettings));
            }
        }

        return _hotkeyOk;
    }

    private void UpdateTray(DictationState state)
    {
        if (_exiting)
        {
            return;
        }

        _tray.Icon = _icons.For(state);
        var status = state switch
        {
            DictationState.Recording => "listening…",
            DictationState.Transcribing => "transcribing…",
            _ => _hotkeyOk ? $"press {HotkeyKeys.Display(_settings.Hotkey)} to dictate" : "hotkey not set",
        };
        var tip = $"{AppInfo.Name} — {status}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToArray())
        {
            item.Dispose();
        }

        _menu.Items.Clear();
        var hotkey = HotkeyKeys.Display(_settings.Hotkey);
        var toggleText = _controller.State == DictationState.Recording ? "Stop dictation" : "Start dictation";
        var toggle = new ToolStripMenuItem(toggleText, null, (_, _) => Fire(_controller.ToggleAsync))
        {
            Enabled = _controller.State != DictationState.Transcribing,
            ShortcutKeyDisplayString = hotkey,
        };
        _menu.Items.Add(toggle);

        var pendingCount = _pending.Count;
        var retry = new ToolStripMenuItem(pendingCount > 1 ? $"Retry last dictation ({pendingCount} saved)" : "Retry last dictation",
            null, (_, _) => Fire(_controller.RetryLastAsync))
        {
            Enabled = pendingCount > 0 && _controller.State == DictationState.Idle,
        };
        _menu.Items.Add(retry);

        var recent = new ToolStripMenuItem("Recent transcripts");
        var entries = _history.Entries;
        if (entries.Count == 0)
        {
            recent.DropDownItems.Add(new ToolStripMenuItem(_settings.History.Enabled ? "(none yet)" : "(history is off)") { Enabled = false });
        }
        else
        {
            foreach (var entry in entries)
            {
                var label = entry.Text.ReplaceLineEndings(" ");
                label = label.Length > 60 ? label[..57] + "…" : label;
                var text = entry.Text;
                recent.DropDownItems.Add(new ToolStripMenuItem(label.Replace("&", "&&", StringComparison.Ordinal), null,
                    (_, _) => Fire(async () =>
                    {
                        var ok = await _inserter.CopyAsync(text).ConfigureAwait(true);
                        _overlay.ShowHint(ok ? UserMessages.Copied : "Couldn't copy (clipboard busy)", error: !ok);
                    }))
                {
                    ToolTipText = entry.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture),
                });
            }
        }

        _menu.Items.Add(recent);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Show mini button", null, (_, _) => SetMiniButton(!_settings.ShowMiniButton))
        {
            Checked = _settings.ShowMiniButton,
        });
        _menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        _menu.Items.Add($"About {AppInfo.Name} {AppInfo.Version}", null, (_, _) => ShowAbout());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _menu.ResumeLayout();
    }

    private static void ShowAbout()
    {
        MessageBox.Show(
            $"{AppInfo.Name} {AppInfo.Version}\n\nFree, open-source voice typing for English, Hindi and Hinglish, using Google Gemini.\n\n" +
            $"Your data folder: {AppPaths.LocalDir}\n\n" +
            "This program is free software, licensed under the GNU General Public License v3.0. It comes with ABSOLUTELY NO WARRANTY.\n" +
            "Source code and help: https://github.com/himanshuafmc/plainspoken",
            $"About {AppInfo.Name}", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowFirstRun()
    {
        if (_firstRunForm is { IsDisposed: false })
        {
            _firstRunForm.Activate();
            return;
        }

        _firstRunForm = new FirstRunForm(this);
        _firstRunForm.FormClosed += (_, _) =>
        {
            _firstRunForm = null;
            UpdateTray(_controller.State);
        };
        _firstRunForm.Show();
        _firstRunForm.Activate();
    }

    private static void ShowMicrophoneBlockedDialog(string text)
    {
        var open = new TaskDialogButton("Open microphone settings");
        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = "Plainspoken can't use the microphone",
            Text = text,
            Icon = TaskDialogIcon.Warning,
            Buttons = { open, TaskDialogButton.Close },
            DefaultButton = open,
        };
        if (TaskDialog.ShowDialog(page) == open)
        {
            MicrophonePrivacy.OpenSettings();
        }
    }

    private void OnMiniClicked()
    {
        if (!_settings.Local.FirstRunCompleted)
        {
            ShowFirstRun();
            return;
        }

        Fire(_controller.ToggleAsync);
    }

    private void ShowMiniMenu(Point screenPoint)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add($"Start dictation ({HotkeyKeys.Display(_settings.Hotkey)})", null, (_, _) => OnMiniClicked());
        menu.Items.Add("Move back to the bottom centre", null, (_, _) => SaveAnchor(null))
            .Enabled = _settings.Local.MiniButtonPosition is not null;
        menu.Items.Add("Hide mini button", null, (_, _) => SetMiniButton(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Closed += (_, _) => _overlay.BeginInvoke(menu.Dispose);
        menu.Show(screenPoint);
    }

    private void SetMiniButton(bool show)
    {
        _settings.ShowMiniButton = show;
        _settingsStore.Save(_settings);
        _overlay.MiniEnabled = show;
        if (_controller.State == DictationState.Idle && _overlay.Mode is PillMode.Mini or PillMode.Hidden)
        {
            _overlay.GoIdle();
        }

        if (!show)
        {
            _tray.ShowBalloonTip(5000, AppInfo.Name, "Mini button hidden. Turn it back on from the tray menu or Settings.", ToolTipIcon.Info);
        }
    }

    private void SaveAnchor(Point? point)
    {
        _settings.Local.MiniButtonPosition = point is { } p
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X},{p.Y}")
            : null;
        _settingsStore.Save(_settings);
        _overlay.CustomAnchor = point;
        if (point is null && _controller.State == DictationState.Idle && _overlay.Mode == PillMode.Mini)
        {
            _overlay.GoIdle();
        }
    }

    private static Point? ParseAnchor(string? text)
    {
        var parts = text?.Split(',');
        return parts is { Length: 2 } &&
               int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
               int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var y)
            ? new Point(x, y)
            : null;
    }

    private void RunAction(UserAction action)
    {
        switch (action)
        {
            case UserAction.OpenSettings:
                ShowSettings();
                break;
            case UserAction.Retry:
                Fire(_controller.RetryLastAsync);
                break;
            case UserAction.OpenMicrophonePrivacySettings:
                MicrophonePrivacy.OpenSettings();
                break;
        }
    }
}
