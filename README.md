<p align="center"><img src="assets/plainspoken-256.png" width="96" alt="Plainspoken icon"></p>

<h1 align="center">Plainspoken</h1>

<p align="center"><b>Speak naturally. Get clean text anywhere.</b><br>
Free, open-source voice typing for Windows (Android coming) — English, Hindi and Hinglish.</p>

<p align="center">
  <a href="https://github.com/himanshuafmc/plainspoken/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/himanshuafmc/plainspoken?label=download"></a>
  <a href="LICENSE"><img alt="Licence: GPL-3.0" src="https://img.shields.io/badge/licence-GPL--3.0-blue"></a>
  <a href="https://github.com/himanshuafmc/plainspoken/actions/workflows/windows.yml"><img alt="Build status" src="https://github.com/himanshuafmc/plainspoken/actions/workflows/windows.yml/badge.svg"></a>
</p>

<!-- TODO (maintainer): record assets/demo.gif — one dictation from hotkey to inserted text — and show it here:
<p align="center"><img src="assets/demo.gif" width="640" alt="Plainspoken demo"></p>
-->

Press a key, talk, press it again. Plainspoken turns what you said into tidy, punctuated text and types it into
whatever you're using — Word, Gmail, WhatsApp, Outlook, Notepad, anything with a text box. "Um"s, repeats and
false starts are removed, and lists, dates and amounts come out properly formatted.

## Why Plainspoken

- **Free.** It uses Google's Gemini speech model with your own free API key. No subscription, no account with us.
- **Private by design.** There are no Plainspoken servers and no tracking. Your audio goes only to Google (see [Privacy](#privacy)).
- **Good at Indian English, Hindi and Hinglish**, including switching languages mid-sentence.
- **Open source** under the GPL-3.0. Anyone can read, check and improve the code.
- **Android is coming** — most dictation happens on phones, and that's the next step.

It's a free alternative to paid dictation apps, built for everyday typing rather than meetings or transcripts.

## Download

➡️ **[Download the latest release](https://github.com/himanshuafmc/plainspoken/releases/latest)** — the file is called `Plainspoken-win-x64-<version>.zip`.

Windows 10 or 11 (64-bit). No installer: unzip it and run it.

## Quick start (3 minutes)

1. **Get a free key.** Open [aistudio.google.com/apikey](https://aistudio.google.com/apikey), sign in with a Google account, click **Create API key** and copy it.
2. **Run Plainspoken and paste the key.** Unzip the download somewhere permanent (for example `Documents\Plainspoken`), double-click `Plainspoken.exe`, paste the key into the welcome window, click **Test key**, tick the notice and click **Finish**.
3. **Talk.** Click where you want the text, press **Ctrl + Alt + Space**, speak, and press **Ctrl + Alt + Space** again. The text appears a moment later.

### "Windows protected your PC"?

The first time you run it, Windows SmartScreen may show a blue warning. Click **More info**, then **Run anyway**.
This appears because Plainspoken isn't code-signed (signing certificates cost money every year), not because anything
is wrong. The full source code is here, and every release is built by GitHub Actions straight from it.

## Everyday use

- **Start / stop:** press **Ctrl + Alt + Space**, or click the small button at the bottom of the screen. While listening, a bar shows a timer and a sound-level meter. Click the green **✓** (or press the hotkey again) to finish.
- **Cancel:** press **Esc** or click **✕**. Nothing is typed.
- **The small button:** stays near the bottom of the screen while idle. Point at it for a hint, click it to dictate, drag it anywhere (it remembers the spot), right-click it for options. It hides during full-screen videos and presentations.
- **Tray icon** (near the clock; click **^** if you can't see it): teal when ready, red while listening, amber while transcribing. Left-click to start/stop; right-click for **Recent transcripts** (the last 20 — click one to copy it), **Retry last dictation**, **Settings** and **About**.
- **Tips:** speak normally and don't worry about "um"s. Say lists naturally ("first milk, second eggs…"). Add names and special words in **Settings → Vocabulary** so they're spelled right. Hindi may come out in Roman letters (Hinglish style) or Devanagari — Google decides and doesn't offer a switch yet.

## Settings

Right-click the tray icon → **Settings…**

| Tab | What's there |
|---|---|
| General | Your Gemini key (with **Test key**), the hotkey, languages (English + Hindi, auto-detect, or English only), Smart or Verbatim mode, microphone |
| Vocabulary | Words and names to spell correctly, one per line (up to 1,000) |
| Options | Paste or "type it out" (for apps that block pasting), trailing space, sounds, start with Windows, history, longest recording, the small on-screen button |
| Advanced | Live streaming (on by default), speech engine and model names, API address, timeout, export/import settings (never includes your key), data and log folders |

## Privacy

- Audio goes **only** to Google's Gemini API, using your key. Plainspoken has no servers, no analytics and no telemetry.
- With **live streaming** (the default), audio is streamed to Google while you speak. If that fails, the recording is sent once more the normal way: short recordings travel inside the request and are never stored as files; long ones (over ~5 minutes) are uploaded and then **deleted straight after** transcription.
- **On Google's free tier, Google may use your audio and text to improve its products.** Don't dictate official, confidential or classified information. (A paid Google key changes this — see Google's terms.)
- Your key is stored encrypted for your Windows account only (Windows DPAPI) and is never written to logs or exported settings.
- Everything else stays on your PC: settings in `%APPDATA%\Plainspoken`, history, unsent recordings and logs in `%LOCALAPPDATA%\Plainspoken`. Logs contain timings and error codes, never your words.

## Troubleshooting

| What you see | What to do |
|---|---|
| "Add your free Gemini API key…" / "key was not accepted" | Settings opens by itself. Paste the key again from AI Studio and click **Test key**. |
| "Free daily limit reached. Resets at …" | Google's free limit for today is used up; it resets at midnight US Pacific time (shown in your local time). Your recording is saved — use **Retry last dictation** later. |
| "Too many requests right now" | The free plan allows only a few requests per minute. Wait a minute, then **Retry last dictation**. |
| "No internet connection" | Reconnect, then tray menu → **Retry last dictation** → press **Ctrl + V**. |
| "Didn't catch that" | The recording was very short or silent. Speak a bit louder or closer to the microphone. |
| "Microphone access is turned off for desktop apps" | Click the message and turn on **Let desktop apps access your microphone** in Windows Settings. |
| "Copied — press Ctrl+V" | That app didn't accept typed text. Press **Ctrl + V** to paste it. |
| The hotkey does nothing / "already used by another app" | Choose another hotkey in **Settings → General**. |

Nothing you dictate is lost: if anything fails, the recording is kept until it's transcribed.

## Known limitations

- Windows only for now (10 and 11, 64-bit).
- Text can't be typed into apps running **as Administrator** (a Windows security rule). Plainspoken copies the text instead so you can press Ctrl + V.
- Google's free tier is small: a few requests a minute per model, and a daily cap (in September 2026 Google's own error message said 25 a day for the main speech model). When a limit is hit, Plainspoken automatically tries a backup model. The backup is fast but less accurate — it can drop or invent words, especially in Hindi.
- The app isn't code-signed, so SmartScreen warns on first run.
- Hindi output script (Roman or Devanagari) can vary.

## Roadmap

- 📱 Android app (floating mic bubble or keyboard) that shares the same settings file
- ✨ Live text appearing while you're still speaking
- 🌐 More Indian languages
- 🎬 A demo GIF and a code-signed build

## Contributing

Bug reports are very welcome — please use the **[bug report form](https://github.com/himanshuafmc/plainspoken/issues/new?template=bug_report.yml)**, and never paste your API key or anything private you dictated. Ideas go in the [feature request form](https://github.com/himanshuafmc/plainspoken/issues/new?template=feature_request.yml). Security problems: see [SECURITY.md](SECURITY.md). Pull requests are reviewed when time allows — [CONTRIBUTING.md](CONTRIBUTING.md) explains how to build, test and what a good pull request looks like.

For developers: `windows/` holds the .NET 10 solution (`Plainspoken.Core` is portable, tested logic; `Plainspoken.App` is the WinForms tray app). Build with `dotnet build windows/Plainspoken.sln -p:EnableWindowsTargeting=true` and test with `dotnet test windows/tests/Plainspoken.Core.Tests`. The behaviour spec that the Android app will share is in [`docs/SPEC.md`](docs/SPEC.md); design decisions are in [`docs/DECISIONS.md`](docs/DECISIONS.md).

## Licence

Plainspoken is free software under the [GNU General Public License v3.0](LICENSE). It comes with no warranty.
Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
