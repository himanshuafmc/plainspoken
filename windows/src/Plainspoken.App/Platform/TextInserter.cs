using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.App.Platform;

/// <summary>
/// Puts the transcript where the cursor is. Default: clipboard paste (save clipboard → set text →
/// Ctrl+V → wait 250 ms → restore). Alternative: type it out with Unicode key events.
/// Must be called on the UI (STA) thread because it uses the clipboard.
/// </summary>
internal sealed class TextInserter : ITextInserter
{
    private const int ClipboardRetries = 10;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(250);

    private readonly Func<PlainspokenSettings> _settings;
    private readonly ForegroundTracker _foreground;
    private readonly ILog _log;

    public TextInserter(Func<PlainspokenSettings> settings, ForegroundTracker foreground, ILog log)
    {
        _settings = settings;
        _foreground = foreground;
        _log = log;
    }

    public async Task<InsertOutcome> InsertAsync(string text, CancellationToken ct)
    {
        await WaitForModifiersReleasedAsync().ConfigureAwait(true);
        var target = await EnsureTargetFocusedAsync().ConfigureAwait(true);
        if (target == IntPtr.Zero)
        {
            _log.Warn("insert: no target window; copying instead");
            return await CopyAsync(text).ConfigureAwait(true) ? InsertOutcome.CopiedToClipboard : InsertOutcome.Failed;
        }

        if (!NativeMethods.IsCurrentProcessElevated() && NativeMethods.IsElevated(target))
        {
            // UIPI: Windows silently drops input sent to apps running as Administrator.
            _log.Warn("insert: target app is elevated (UIPI); copying instead");
            return await CopyAsync(text).ConfigureAwait(true) ? InsertOutcome.CopiedToClipboard : InsertOutcome.Failed;
        }

        return _settings().Insertion.Method == InsertionMethod.Type
            ? await TypeAsync(text).ConfigureAwait(true)
            : await PasteAsync(text).ConfigureAwait(true);
    }

    public async Task<bool> CopyAsync(string text)
    {
        return await TrySetClipboardAsync(TranscriptData(text)).ConfigureAwait(true);
    }

    private async Task<InsertOutcome> PasteAsync(string text)
    {
        var (readOk, saved) = await SnapshotClipboardAsync().ConfigureAwait(true);
        if (!await TrySetClipboardAsync(TranscriptData(text)).ConfigureAwait(true))
        {
            _log.Warn("insert: clipboard is locked by another app");
            return InsertOutcome.Failed;
        }

        var ourSequence = NativeMethods.GetClipboardSequenceNumber();
        var sent = SendKeys(
            Key(NativeMethods.VK_CONTROL, false), Key(NativeMethods.VK_V, false),
            Key(NativeMethods.VK_V, true), Key(NativeMethods.VK_CONTROL, true));
        if (sent != 4)
        {
            _log.Warn($"insert: SendInput sent {sent}/4 (error {Marshal.GetLastWin32Error()}); text left on clipboard");
            return InsertOutcome.CopiedToClipboard;
        }

        // Give the target app time to read the clipboard before we put the old contents back.
        await Task.Delay(RestoreDelay).ConfigureAwait(true);
        if (saved is not null && NativeMethods.GetClipboardSequenceNumber() == ourSequence)
        {
            if (!await TrySetClipboardAsync(saved).ConfigureAwait(true))
            {
                _log.Warn("insert: could not restore the previous clipboard");
            }
        }
        else if (saved is null && readOk && NativeMethods.GetClipboardSequenceNumber() == ourSequence)
        {
            // The clipboard was empty before: leave it empty again.
            await TryClearClipboardAsync().ConfigureAwait(true);
        }
        else if (NativeMethods.GetClipboardSequenceNumber() != ourSequence)
        {
            _log.Info("insert: clipboard changed by someone else; not restoring");
        }

        return InsertOutcome.Inserted;
    }

    private async Task<InsertOutcome> TypeAsync(string text)
    {
        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (var ch in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (ch == '\n')
            {
                // Shift+Enter: a new line in chat apps instead of "send".
                inputs.Add(Key(NativeMethods.VK_SHIFT, false));
                inputs.Add(Key(NativeMethods.VK_RETURN, false));
                inputs.Add(Key(NativeMethods.VK_RETURN, true));
                inputs.Add(Key(NativeMethods.VK_SHIFT, true));
            }
            else
            {
                inputs.Add(Unicode(ch, false));
                inputs.Add(Unicode(ch, true));
            }
        }

        const int batch = 64; // small batches with pauses so slow apps keep up
        for (var i = 0; i < inputs.Count; i += batch)
        {
            var chunk = inputs.GetRange(i, Math.Min(batch, inputs.Count - i)).ToArray();
            if (SendKeys(chunk) != chunk.Length)
            {
                _log.Warn("insert: typing was interrupted; copying the text instead");
                return await CopyAsync(text).ConfigureAwait(true) ? InsertOutcome.CopiedToClipboard : InsertOutcome.Failed;
            }

            await Task.Delay(8).ConfigureAwait(true);
        }

        return InsertOutcome.Inserted;
    }

    /// <summary>If the taskbar/tray has focus (dictation toggled from the tray icon), refocus the last app.</summary>
    private async Task<IntPtr> EnsureTargetFocusedAsync()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (!_foreground.IsTrayOrHidden(fg))
        {
            return fg;
        }

        var last = _foreground.LastExternalWindow;
        if (last == IntPtr.Zero || !NativeMethods.IsWindow(last))
        {
            return IntPtr.Zero;
        }

        NativeMethods.SetForegroundWindow(last);
        await Task.Delay(120).ConfigureAwait(true);
        return NativeMethods.GetForegroundWindow() == last ? last : IntPtr.Zero;
    }

    private static async Task WaitForModifiersReleasedAsync()
    {
        // The stop hotkey's Ctrl/Alt may still be held; Ctrl+Alt+V would not paste.
        for (var i = 0; i < 50; i++)
        {
            if (!IsDown(NativeMethods.VK_CONTROL) && !IsDown(NativeMethods.VK_MENU) && !IsDown(NativeMethods.VK_SHIFT) &&
                !IsDown(NativeMethods.VK_LWIN) && !IsDown(NativeMethods.VK_RWIN))
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(true);
        }
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static DataObject TranscriptData(string text)
    {
        var data = new DataObject();
        data.SetText(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), TextDataFormat.UnicodeText);
        ExcludeFromHistory(data);
        return data;
    }

    /// <summary>Keeps dictated text out of Win+V clipboard history and cloud clipboard.</summary>
    private static void ExcludeFromHistory(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
    }

    /// <summary>Copies the common clipboard formats (text, RTF, HTML, files, image) so they can be restored.</summary>
    private async Task<(bool ReadOk, DataObject? Data)> SnapshotClipboardAsync()
    {
        for (var attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                var copy = new DataObject();
                var any = false;
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    copy.SetText(Clipboard.GetText(TextDataFormat.UnicodeText), TextDataFormat.UnicodeText);
                    any = true;
                }

                if (Clipboard.ContainsText(TextDataFormat.Rtf))
                {
                    copy.SetText(Clipboard.GetText(TextDataFormat.Rtf), TextDataFormat.Rtf);
                    any = true;
                }

                if (Clipboard.ContainsText(TextDataFormat.Html))
                {
                    copy.SetText(Clipboard.GetText(TextDataFormat.Html), TextDataFormat.Html);
                    any = true;
                }

                if (Clipboard.ContainsFileDropList())
                {
                    var files = new StringCollection();
                    files.AddRange(Clipboard.GetFileDropList().Cast<string>().ToArray());
                    copy.SetFileDropList(files);
                    any = true;
                }

                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    copy.SetImage(image);
                    any = true;
                }

                if (!any)
                {
                    return (true, null);
                }

                ExcludeFromHistory(copy);
                return (true, copy);
            }
            catch (ExternalException)
            {
                await Task.Delay(ClipboardRetryDelay).ConfigureAwait(true);
            }
        }

        _log.Warn("insert: could not read the clipboard to save it");
        return (false, null);
    }

    private static async Task TryClearClipboardAsync()
    {
        for (var attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.Clear();
                return;
            }
            catch (ExternalException)
            {
                await Task.Delay(ClipboardRetryDelay).ConfigureAwait(true);
            }
        }
    }

    private static async Task<bool> TrySetClipboardAsync(DataObject data)
    {
        for (var attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (ExternalException)
            {
                await Task.Delay(ClipboardRetryDelay).ConfigureAwait(true);
            }
        }

        return false;
    }

    private static uint SendKeys(params NativeMethods.INPUT[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());

    private static NativeMethods.INPUT Key(ushort vk, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)NativeMethods.MapVirtualKey(vk, 0),
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };

    private static NativeMethods.INPUT Unicode(char ch, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
            },
        },
    };
}
