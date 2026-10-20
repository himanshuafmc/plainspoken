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

- **On Windows** hold **Right Ctrl** while you speak and let go — or press **Ctrl + Alt + Space**, speak, and press it again.
- **On Android** a small **floating mic** appears next to your usual keyboard: tap it, speak, and tap it again — or hold it while you speak and let go. (Prefer a voice keyboard? Plainspoken is one too.)

Both apps are the same Plainspoken: the same speech engine and clean-up, the same settings file, one release.

## Why Plainspoken

- **Free, or pay only for what you use.** It works with Google's Gemini and your own free API key, or with Sarvam, OpenAI (or any OpenAI-compatible service such as Groq), Deepgram or ElevenLabs and your own key, paying them per minute of audio. No subscription, no account with us.
- **Private by design.** There are no Plainspoken servers and no tracking. Your audio goes only to the speech service you choose (see [Privacy](#privacy)).
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
2. Paste the key into the welcome window, click **Test key**, tick the notice and click **Finish**. (Using Sarvam, OpenAI, Deepgram or ElevenLabs instead? Tick the notice, click **Use another service…** and set it up in Settings.)
3. Click where you want the text, hold **Right Ctrl**, speak, and let go — or press **Ctrl + Alt + Space**, speak, and press it again. The text appears a moment later.

**"Windows protected your PC"?** Click **More info**, then **Run anyway**. It appears because Plainspoken isn't code-signed (certificates cost money every year), not because anything is wrong.

### Android (3 minutes)

1. Download the APK on your phone and open it. Android asks you to allow installing apps from your browser or file manager — allow it, then tap **Install**. (Play Protect may say it doesn't know the app; tap **Install anyway**.)
2. Open **Plainspoken** and follow the checklist: paste your key, read the notice, allow the microphone, and turn on the **floating mic** in Accessibility settings (Android 13 and later first ask you to allow "restricted settings" for apps installed from outside the Play Store — the checklist shows how). Android shows its standard warning for any accessibility service — Plainspoken looks only at the text box you're typing in, never at passwords, and only sends your voice to your speech service when you tap the mic.
3. In any app, tap a text box: the Plainspoken mic appears next to your keyboard. Tap it, speak, and tap it again.

Both builds are made by GitHub Actions straight from this source code, and the release notes list each file's SHA-256.

## Everyday use

**Windows**

- **Hold to talk:** hold **Right Ctrl**, speak, let go. A quick tap, or Right Ctrl in a shortcut, works as usual. (Settings → General can switch it to Right Alt or Right Shift, or off.)
- **Start / stop:** **Ctrl + Alt + Space**, or click the small button at the bottom of the screen. While listening, a bar shows a timer and a sound-level meter; click the green **✓** (or press the hotkey again) to finish. **Esc** or **✕** cancels.
- **The small button** stays near the bottom of the screen while idle. Click it to dictate, drag it anywhere, right-click for options. It hides during full-screen videos and presentations.
- **Tray icon** (near the clock): left-click to start/stop; right-click for **Recent transcripts**, **Retry last dictation**, **Settings**, **Speed and cost** and **About**.
- **It learns your words:** fix a misheard name in the text box yourself; the next time you dictate in that window, Plainspoken offers to add the name to your vocabulary (click the green suggestion).
- **Snippets:** save phrases like "insert my address" in Settings → Snippets, and saying them types the saved text.

**Android**

- **The floating mic:** appears next to your keyboard whenever you type, in any app (never in password boxes). Tap to start and tap again to finish, or hold while you speak and let go. Drag it to move it; it remembers the spot. Messages and the "add to vocabulary" offer show beside it. Turn it off in Settings → Floating mic.
- **The keyboard (optional):** a big **mic** button (turns into a green **✓** while listening; or hold it while you speak and let go), **✕** to cancel, **Retry** when a recording is waiting, and keys for 🌐 switch keyboard, comma, space, full stop, delete and enter. Long-press 🌐 to pick any keyboard.
- **Switching back and forth:** tap 🌐 to return to your usual keyboard, and use the keyboard button in the navigation bar (or your keyboard's 🌐 key) to come back to Plainspoken.
- **The app:** recent transcripts (tap to copy), Retry for saved recordings, and Settings. The ⚙ key on the keyboard opens it.
- Voice typing is switched off in password boxes, and nothing is kept in history for incognito text boxes.
- **It learns your words:** fix a misheard name with your keyboard; the next time you dictate in that app (floating mic) or switch back to the Plainspoken keyboard, it offers to add the name to your vocabulary.
- **Snippets:** save phrases like "insert my address" in Settings, and saying them types the saved text.

**Tips for both:** speak normally and don't worry about "um"s. Say lists naturally ("first milk, second eggs…"). Add names and special words to the **Vocabulary** so they're spelled right. Choose Roman letters or Devanagari for Hindi words in Settings (Gemini's own models may still mix them).

## Settings

Windows: right-click the tray icon → **Settings…** · Android: open the Plainspoken app → **Settings**.

| Setting | What it does |
|---|---|
| Speech services | Choose the main service and up to two backups: Gemini, Sarvam, OpenAI-compatible, Deepgram, ElevenLabs — each with its own key (with **Test key**, stored encrypted on that device only), model, streaming switch and price. If one fails or is slow, the next takes over. Windows: Settings → General → **Set up…** |
| Clean-up | Tidies raw text from Sarvam, OpenAI-compatible, Deepgram and ElevenLabs: "um"s and repeats removed, punctuation and grammar fixed, your corrections applied. Uses Gemini (default) or an OpenAI-compatible service. |
| Hindi words | Roman letters ("kal meeting hai"), Devanagari, or as the service gives |
| Snippets | Spoken shortcuts that type saved text |
| Languages and style | English + Hindi, auto-detect or English only; **Smart** (clean) or **Verbatim** (every word) |
| Vocabulary | Words and names to spell correctly, one per line (up to 1,000) |
| Options | Trailing space, sounds, history, longest recording, when to ask the backup too, compressed uploads, currency. Windows also: hotkey, hold-to-talk key, paste or "type it out", start with Windows, the small on-screen button |
| Advanced | Request timeout, export/import, folders |
| Export / import | Saves settings, vocabulary and snippets to a file — never your keys. A file from Windows imports on Android and the other way round. |

## Privacy

- Audio goes **only** to the speech service you use — Google's Gemini API by default, or Sarvam, an OpenAI-compatible service, Deepgram or ElevenLabs if you set them up — using your own key. The raw text from those services goes to the clean-up service you chose (Gemini by default). Plainspoken has no servers, no analytics and no telemetry.
- With **live streaming** (the default), audio is streamed to the service while you speak (Gemini, Sarvam and Deepgram can). If that fails, the recording is sent once more the normal way: short recordings travel inside the request and are never stored as files; long ones (over ~5 minutes) are uploaded and then **deleted straight after** transcription.
- **On Google's free tier, Google may use your audio and text to improve its products.** Don't dictate official, confidential or classified information. (A paid Google key changes this — see Google's terms.)
- Your keys are stored encrypted — Windows DPAPI on the PC, the Android Keystore on the phone — and are never written to logs or exported settings. The Android app is excluded from cloud backup.
- Everything else stays on the device: settings, the last 20 transcripts, unsent recordings, speed statistics and logs. Logs and statistics contain timings and error codes, never your words.
- On Android the microphone is only used while you dictate, and voice typing is off in password boxes.
- To learn your corrections, the Windows app reads the text box you dictated into once, at your next dictation there, on your computer; nothing is saved or sent. Password boxes are never read. Turn it off in Settings → Vocabulary.

## Troubleshooting

| What you see | What to do |
|---|---|
| "Add your free Gemini API key…" / "key was not accepted" | Paste the key again from AI Studio and tap **Test key** (Windows: Settings opens by itself; Android: tap **Open app**). |
| "Free daily limit reached. Resets at …" | Google's free limit for today is used up; it resets at midnight US Pacific time (shown in your local time). Your recording is saved — **Retry** later. |
| "Too many requests right now" | The free plan allows only a few requests per minute. Wait a minute, then **Retry**. Add a backup service in Settings and Plainspoken switches by itself. |
| "Your … account is out of credits" | Top up that service, or make another service the main one in Settings. |
| Windows: holding Right Ctrl does nothing | Hold it on its own for a moment before speaking; check Settings → General → Hold to talk. |
| "No internet connection" | Reconnect, then **Retry** (Windows: tray menu → Retry last dictation → Ctrl + V; Android: the **Retry** key). |
| "Didn't catch that" | The recording was very short or silent. Speak a bit louder or closer to the microphone. |
| Windows: "Microphone access is turned off for desktop apps" | Click the message and turn on **Let desktop apps access your microphone** in Windows Settings. |
| Android: "Plainspoken needs permission to use the microphone" | Tap **Allow**. If Android no longer asks, open the app's settings → Permissions → Microphone → Allow. |
| Android: the floating mic doesn't appear | Open the Plainspoken app — its checklist shows whether the floating mic is on. It appears only while a keyboard is showing, and not over the Plainspoken keyboard. |
| Android: "Restricted setting" when turning on the floating mic | Open Plainspoken's App info → ⋮ (top right) → **Allow restricted settings**, then switch it on again. |
| Android: the floating mic says Android didn't let it use the microphone | Please report it with the log (Settings → Export the log) and use the Plainspoken keyboard meanwhile. |
| Android: the Plainspoken keyboard doesn't appear | Open the Plainspoken app — its checklist shows which step is missing (keyboard turned on, keyboard chosen). |
| "Copied — …" | That text box couldn't take the text directly. Paste it (Windows: Ctrl + V; Android: long-press the box → Paste). |
| Windows: the hotkey does nothing / "already used by another app" | Choose another hotkey in **Settings → General**. |

Nothing you dictate is lost: if anything fails, the recording is kept until it's transcribed.

## Known limitations

- Google's free tier is small: a few requests a minute per model, and a daily cap (in September 2026 Google's own error message said 25 a day for the main speech model). When a limit is hit, or the service is slow, Plainspoken switches to your backup service by itself (by default the general Gemini model, gemini-3.5-flash).
- Hindi output script (Roman or Devanagari) can vary.
- Windows: text can't be typed into apps running **as Administrator** (a Windows security rule); Plainspoken copies it instead. The app isn't code-signed, so SmartScreen warns on first run.
- Android: not on the Play Store yet, so updates are a new download from the Releases page.
- Android: the floating mic types through Android's accessibility interface. A few apps (some games, banking apps and custom editors) don't accept text that way; then the text is copied and you paste it, or use the Plainspoken keyboard.
- The Sarvam, OpenAI-compatible, Deepgram and ElevenLabs connections follow those services' published APIs; if one misbehaves, please report it with the log.
- Windows: learning corrections works in apps that let Windows read their text boxes (most do; some, such as a few terminals and games, don't).

## Roadmap

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
