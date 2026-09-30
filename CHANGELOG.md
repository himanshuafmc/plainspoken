# Changelog

All notable changes to Plainspoken. Versions follow [Semantic Versioning](https://semver.org/).

## [0.2.1] - 2026-09-30

A maintenance update for both apps. Nothing changes in how Plainspoken works; it's built with newer, up-to-date components.

### Android
- Built with the latest Android build tools and a newer version of OkHttp, the library that talks to Google.
- Installs over 0.2.0 as a normal update; your key, settings and history stay.

### Windows
- Uses a newer version of NAudio, the library that records from your microphone.
- Replace your old `Plainspoken.exe` with the new one; your settings and history stay.

## [0.2.0] - 2026-09-30

Plainspoken for **Android** is here — the same Plainspoken, now on your phone. 📱

### New: Android app
- **A voice keyboard.** In any app, switch to the Plainspoken keyboard, tap the big mic, speak, and tap ✓ — clean, punctuated text goes straight into the text box. It also has ✕ to cancel, Retry, and keys for switching keyboard, comma, space, full stop, delete and enter.
- **Everything from Windows:** English, Hindi and Hinglish; live streaming with automatic fallback; the backup model when Google's free limits are hit; saved recordings you can retry; your last 20 transcripts; vocabulary; Smart or Verbatim.
- **Easy setup:** the app walks you through the key, the microphone and turning the keyboard on, with a box to try it.
- **Private:** your key is encrypted with the Android Keystore, the app is excluded from cloud backup, voice typing is off in password boxes, and nothing is kept in history for incognito text boxes.
- **One settings file for both apps:** export on Windows, import on Android, or the other way round.
- Android 8.0 or newer. Download the APK from this release (not on the Play Store yet).

### Windows
- No changes to the app itself; it moves to version 0.2.0 so both apps share one version number.

## [0.1.1] - 2026-09-30

### Fixed
- **Missing spaces.** Words were sometimes stuck together ("tomorrowmorning"), and there was often no space after a full stop ("done.Next"). This happened because Google sends live text in pieces that don't always include their spaces. Plainspoken now puts the spaces back — between words, after full stops, question marks, commas and colons — without splitting numbers like 2.5 or 10:30, abbreviations like a.m., or web addresses.

## [0.1.0] - 2026-09-30

The first public release. 🎉

### What you can do
- **Dictate anywhere on Windows.** Press Ctrl + Alt + Space (or click the small on-screen button), speak, and press it again. Clean, punctuated text appears where your cursor is — Word, Gmail, WhatsApp, Outlook, Notepad and more.
- **Speak English, Hindi or Hinglish.** Fillers like "um" and "uh", repeats and false starts are removed; lists, dates and amounts are tidied up.
- **Fast results.** Your voice is streamed to Google while you talk, so the text is usually ready moments after you stop. If streaming ever fails, Plainspoken quietly uses the standard method instead.
- **Keeps working when Google's free limits are hit.** It switches to a backup speech model automatically (a little less accurate), so you rarely have to wait.
- **Never lose a dictation.** If the internet drops or Google is busy, the recording is saved and one click on "Retry last dictation" gets your text.
- **Your last 20 transcripts** are one right-click away in the tray menu.
- **Your own words.** Add names and special terms to the vocabulary list so they're spelled right.
- **Share settings** with family or colleagues by exporting a file — your API key is never included.

### Good to know
- Free with your own Google Gemini API key from aistudio.google.com/apikey. On Google's free tier, Google may use recordings to improve its products — don't dictate confidential information.
- Windows may show "Windows protected your PC" the first time: click More info → Run anyway. The app isn't code-signed yet.
- Windows 10/11, 64-bit. No installer; unzip and run.

[0.2.1]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.2.1
[0.2.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.2.0
[0.1.1]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.1.1
[0.1.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.1.0
