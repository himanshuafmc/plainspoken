<p align="center"><img src="assets/plainspoken-256.png" width="96" alt="Plainspoken icon"></p>

<h1 align="center">Plainspoken</h1>

<p align="center"><b>Speak naturally. Get clean text anywhere.</b><br>
Free, open-source voice typing for <b>Windows</b> and <b>Android</b> — English, Hindi and Hinglish.</p>

<p align="center">
  <a href="https://github.com/himanshuafmc/plainspoken/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/himanshuafmc/plainspoken?label=download"></a>
  <a href="LICENSE"><img alt="Licence: GPL-3.0" src="https://img.shields.io/badge/licence-GPL--3.0-blue"></a>
  <a href="https://github.com/himanshuafmc/plainspoken/actions/workflows/build.yml"><img alt="Build status" src="https://github.com/himanshuafmc/plainspoken/actions/workflows/build.yml/badge.svg"></a>
</p>

<!-- TODO (maintainer): record assets/demo.gif — one dictation from hotkey to inserted text — and show it here:
<p align="center"><img src="assets/demo.gif" width="640" alt="Plainspoken demo"></p>
-->

Talk, and Plainspoken turns what you said into tidy, punctuated text in whatever you're using — Word, Gmail,
WhatsApp, Outlook, Notepad, anything with a text box. "Um"s, repeats and false starts are removed, and lists,
dates and amounts come out properly formatted.

- **On Windows** press **Ctrl + Alt + Space**, speak, and press it again.
- **On Android** Plainspoken is a keyboard: tap the mic, speak, and tap **✓**.

Both apps are the same Plainspoken: the same speech engine and clean-up, the same settings file, one release.

## Why Plainspoken

- **Free.** It uses Google's Gemini speech model with your own free API key. No subscription, no account with us.
- **Private by design.** There are no Plainspoken servers and no tracking. Your audio goes only to Google (see [Privacy](#privacy)).
- **Good at Indian English, Hindi and Hinglish**, including switching languages mid-sentence.
- **Your PC and your phone.** Export your settings and vocabulary on one and import them on the other.
- **Open source** under the GPL-3.0. Anyone can read, check and improve the code.

It's a free alternative to paid dictation apps, built for everyday typing rather than meetings or transcripts.

## Download

➡️ **[Download the latest release](https://github.com/himanshuafmc/plainspoken/releases/latest)**

| | Windows | Android |
|---|---|---|
| File | `Plainspoken-win-x64-<version>.zip` | `Plainspoken-android-<version>.apk` |
| Needs | Windows 10 or 11, 64-bit | Android 8.0 or newer |
| Install | Unzip it and run `Plainspoken.exe` — no installer | Open the file on your phone and allow the install |

Not on the Play Store yet — the APK on the Releases page is the official Android download.

## Quick start

First, **get a free key** (both apps use it): open [aistudio.google.com/apikey](https://aistudio.google.com/apikey), sign in with a Google account, click **Create API key** and copy it.

### Windows (3 minutes)

1. Unzip the download somewhere permanent (for example `Documents\Plainspoken`) and double-click `Plainspoken.exe`.
2. Paste the key into the welcome window, click **Test key**, tick the notice and click **Finish**.
3. Click where you want the text, press **Ctrl + Alt + Space**, speak, and press **Ctrl + Alt + Space** again. The text appears a moment later.

**"Windows protected your PC"?** Click **More info**, then **Run anyway**. It appears because Plainspoken isn't code-signed (certificates cost money every year), not because anything is wrong.

### Android (3 minutes)

1. Download the APK on your phone and open it. Android asks you to allow installing apps from your browser or file manager — allow it, then tap **Install**. (Play Protect may say it doesn't know the app; tap **Install anyway**.)
2. Open **Plainspoken** and follow the checklist: paste your key, read the notice, allow the microphone, turn on the Plainspoken keyboard and choose it. Android shows its standard warning for any new keyboard — Plainspoken only sends your voice to Google when you tap the mic.
3. In any app, tap a text box, tap the **mic** on the Plainspoken keyboard, speak, and tap **✓**.

Both builds are made by GitHub Actions straight from this source code, and the release notes list each file's SHA-256.

## Everyday use

**Windows**

- **Start / stop:** **Ctrl + Alt + Space**, or click the small button at the bottom of the screen. While listening, a bar shows a timer and a sound-level meter; click the green **✓** (or press the hotkey again) to finish. **Esc** or **✕** cancels.
- **The small button** stays near the bottom of the screen while idle. Click it to dictate, drag it anywhere, right-click for options. It hides during full-screen videos and presentations.
- **Tray icon** (near the clock): left-click to start/stop; right-click for **Recent transcripts**, **Retry last dictation**, **Settings** and **About**.

**Android**

- **The keyboard:** a big **mic** button (turns into a green **✓** while listening), **✕** to cancel, **Retry** when a recording is waiting, and keys for 🌐 switch keyboard, comma, space, full stop, delete and enter. Long-press 🌐 to pick any keyboard.
- **Switching back and forth:** tap 🌐 to return to your usual keyboard, and use the keyboard button in the navigation bar (or your keyboard's 🌐 key) to come back to Plainspoken.
- **The app:** recent transcripts (tap to copy), Retry for saved recordings, and Settings. The ⚙ key on the keyboard opens it.
- Voice typing is switched off in password boxes, and nothing is kept in history for incognito text boxes.

**Tips for both:** speak normally and don't worry about "um"s. Say lists naturally ("first milk, second eggs…"). Add names and special words to the **Vocabulary** so they're spelled right. Hindi may come out in Roman letters (Hinglish style) or Devanagari — Google decides and doesn't offer a switch yet.

## Settings

Windows: right-click the tray icon → **Settings…** · Android: open the Plainspoken app → **Settings**.

| Setting | What it does |
|---|---|
| Key | Your Gemini key, with **Test key**. Stored encrypted on that device only. |
| Languages and style | English + Hindi, auto-detect or English only; **Smart** (clean) or **Verbatim** (every word) |
| Vocabulary | Words and names to spell correctly, one per line (up to 1,000) |
| Options | Trailing space, sounds, history, longest recording. Windows also: hotkey, paste or "type it out", start with Windows, the small on-screen button |
| Advanced | Live streaming (on by default), backup engine, model names, API address, timeout |
| Export / import | Saves settings and vocabulary to a file — never your key. A file from Windows imports on Android and the other way round. |

## Privacy

- Audio goes **only** to Google's Gemini API, using your key. Plainspoken has no servers, no analytics and no telemetry.
- With **live streaming** (the default), audio is streamed to Google while you speak. If that fails, the recording is sent once more the normal way: short recordings travel inside the request and are never stored as files; long ones (over ~5 minutes) are uploaded and then **deleted straight after** transcription.
- **On Google's free tier, Google may use your audio and text to improve its products.** Don't dictate official, confidential or classified information. (A paid Google key changes this — see Google's terms.)
- Your key is stored encrypted — Windows DPAPI on the PC, the Android Keystore on the phone — and is never written to logs or exported settings. The Android app is excluded from cloud backup.
- Everything else stays on the device: settings, the last 20 transcripts, unsent recordings and logs. Logs contain timings and error codes, never your words.
- On Android the microphone is only used while you dictate, and voice typing is off in password boxes.

## Troubleshooting

| What you see | What to do |
|---|---|
| "Add your free Gemini API key…" / "key was not accepted" | Paste the key again from AI Studio and tap **Test key** (Windows: Settings opens by itself; Android: tap **Open app**). |
| "Free daily limit reached. Resets at …" | Google's free limit for today is used up; it resets at midnight US Pacific time (shown in your local time). Your recording is saved — **Retry** later. |
| "Too many requests right now" | The free plan allows only a few requests per minute. Wait a minute, then **Retry**. |
| "No internet connection" | Reconnect, then **Retry** (Windows: tray menu → Retry last dictation → Ctrl + V; Android: the **Retry** key). |
| "Didn't catch that" | The recording was very short or silent. Speak a bit louder or closer to the microphone. |
| Windows: "Microphone access is turned off for desktop apps" | Click the message and turn on **Let desktop apps access your microphone** in Windows Settings. |
| Android: "Plainspoken needs permission to use the microphone" | Tap **Allow**. If Android no longer asks, open the app's settings → Permissions → Microphone → Allow. |
| Android: the Plainspoken keyboard doesn't appear | Open the Plainspoken app — its checklist shows which step is missing (keyboard turned on, keyboard chosen). |
| "Copied — …" | That text box couldn't take the text directly. Paste it (Windows: Ctrl + V; Android: long-press the box → Paste). |
| Windows: the hotkey does nothing / "already used by another app" | Choose another hotkey in **Settings → General**. |

Nothing you dictate is lost: if anything fails, the recording is kept until it's transcribed.

## Known limitations

- Google's free tier is small: a few requests a minute per model, and a daily cap (in September 2026 Google's own error message said 25 a day for the main speech model). When a limit is hit, Plainspoken automatically tries a backup model. The backup is fast but less accurate — it can drop or invent words, especially in Hindi.
- Hindi output script (Roman or Devanagari) can vary.
- Windows: text can't be typed into apps running **as Administrator** (a Windows security rule); Plainspoken copies it instead. The app isn't code-signed, so SmartScreen warns on first run.
- Android: not on the Play Store yet, so updates are a new download from the Releases page. It's a voice keyboard: you switch to it to dictate and back for typing letters.

## Roadmap

- 🫧 Android: an optional floating mic bubble, as well as the keyboard
- 🛒 Android: Play Store listing
- ✨ Live text appearing while you're still speaking
- 🌐 More Indian languages
- 🎬 A demo GIF and a code-signed Windows build

## Contributing

Bug reports are very welcome — please use the **[bug report form](https://github.com/himanshuafmc/plainspoken/issues/new?template=bug_report.yml)**, and never paste your API key or anything private you dictated. Ideas go in the [feature request form](https://github.com/himanshuafmc/plainspoken/issues/new?template=feature_request.yml). Security problems: see [SECURITY.md](SECURITY.md). Pull requests are reviewed when time allows — [CONTRIBUTING.md](CONTRIBUTING.md) explains how to build and test both apps.

For developers: `windows/` holds the .NET 10 solution and `android/` the Kotlin app. Both follow the same behaviour spec, [`docs/SPEC.md`](docs/SPEC.md), and are tested against the same files in `shared/test-fixtures/`. Design decisions are in [`docs/DECISIONS.md`](docs/DECISIONS.md).

## Licence

Plainspoken is free software under the [GNU General Public License v3.0](LICENSE). It comes with no warranty.
Third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
