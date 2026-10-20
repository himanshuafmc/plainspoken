# Changelog

All notable changes to Plainspoken. Versions follow [Semantic Versioning](https://semver.org/).

## [0.5.0] - 2026-10-20

The **Windows** app catches up with Android — any speech service, cleaner text, hold-to-talk, snippets and more — and **Android** gets a floating mic that works with your usual keyboard.

### Windows
- **Choose your speech service.** Besides Gemini, Plainspoken now works with **Sarvam** (Saaras, built for Indian languages and Hinglish), **OpenAI** or any OpenAI-compatible service (such as Groq or your own Whisper server), **Deepgram** and **ElevenLabs** — each with your own key, stored encrypted for your Windows account. Pick a main service and up to two backups in Settings → General, and click **Set up…** next to each.
- **Never stuck waiting.** If the service in use fails, runs out of credit or is slow, Plainspoken switches to the next one by itself — after a few seconds it asks the backup at the same time and uses whichever answers first. A service that keeps failing is skipped for 15 minutes.
- **Cleaner text.** Raw text from Sarvam, OpenAI-compatible services, Deepgram and ElevenLabs is tidied up: "um"s, "aaa"s and repeats removed, punctuation and grammar fixed, your self-corrections applied, lists formatted — never translated. Gemini Flash by default, or any OpenAI-compatible chat model (Settings → Clean-up).
- **Hold to talk:** hold **Right Ctrl**, speak, let go. A quick tap or a shortcut like Ctrl+C works as before. Ctrl+Alt+Space still toggles. (Right Alt, Right Shift or off in Settings.)
- **Hindi words in Roman letters or Devanagari** — your choice.
- **Faster:** Sarvam and Deepgram can stream while you speak, and uploads are compressed (about a third of the size) for every service except Gemini.
- **Snippets:** say a phrase like "insert my address" and Plainspoken types the text you saved for it (Settings → Snippets).
- **Learns your words:** fix a misheard name in the text box yourself, and the next time you dictate in that window Plainspoken offers to add it to your vocabulary. It reads that text box once, on your computer, and never keeps or sends what it read; you can turn it off.
- **Speed and cost** (tray menu): how fast each service has been for you, how often it failed and, once you enter its price, what it cost this month.
- Error messages name the service that had the problem ("Sarvam took too long to answer", "Your Deepgram account is out of credits").
- The Gemini "general model" now defaults to the more accurate gemini-3.5-flash.
- Your settings move to version 2 automatically; your Gemini key, vocabulary and history stay. Settings files now carry the same services, clean-up and snippets on both apps.

### Android
- **Floating mic:** a small mic now appears next to your usual keyboard (Gboard, Samsung Keyboard, …) in any app. Tap it to dictate and tap again to finish, or hold it while you speak — no more switching keyboards. Drag it anywhere; it remembers the spot. It never appears in password boxes. Turn it on from the checklist in the app (Accessibility settings); the Plainspoken keyboard is still there if you prefer it.
- The floating mic also learns your corrections: fix a name by hand, and the next time you dictate in that app it offers to add it to your vocabulary.
- Settings files keep the Windows hold-to-talk setting when they go from Windows to Android and back.

## [0.4.0] - 2026-10-18

A big update for **Android**: use any speech service you like, and get cleaner text, faster.

### Android
- **Choose your speech service.** Besides Gemini, Plainspoken now works with **Sarvam** (Saaras, built for Indian languages and Hinglish), **OpenAI** or any OpenAI-compatible service (such as Groq or your own Whisper server), **Deepgram** and **ElevenLabs** — each with your own key. Pick a main service and up to two backups in Settings → Speech services.
- **Never stuck waiting.** If the service in use fails, runs out of credit or is slow, Plainspoken switches to the next one by itself — after a few seconds it even asks the backup at the same time and uses whichever answers first. A service that keeps failing is skipped for 15 minutes.
- **Cleaner text.** Raw text from Sarvam, OpenAI-compatible services, Deepgram and ElevenLabs is tidied up: "um"s, "aaa"s and repeats removed, punctuation and grammar fixed, your self-corrections applied ("5 baje, nahi, 6 baje" becomes "6 baje"), lists formatted. Your words and your language stay as you spoke them — Hinglish is never translated. It uses Gemini by default, or any OpenAI-compatible service.
- **Hindi words in Roman letters or Devanagari** — your choice.
- **Faster:** Sarvam and Deepgram can stream while you speak, and uploads are compressed (about eight times smaller) for every service except Gemini.
- **Hold to talk:** hold the mic while you speak and let go. Tapping works as before.
- **Snippets:** say a phrase like "insert my address" and Plainspoken types the text you saved for it.
- **Learns your words:** fix a misheard name with your usual keyboard, come back to Plainspoken, and it offers to add the name to your vocabulary.
- **Speed and cost:** a new screen shows how fast each service has been for you, how often it failed and, once you enter its price, what it cost this month.
- The Gemini "general model" now defaults to the more accurate gemini-3.5-flash.
- Installs over 0.2.2 as a normal update; your Gemini key, settings and history stay.

### Windows
- No changes to the app itself; it moves to version 0.4.0 so both apps share one version number. Settings files exported from the Android app still import on Windows (the new Android-only settings are ignored there).

## [0.2.2] - 2026-10-10

A maintenance update for both apps. Nothing changes in how Plainspoken works; it's built with newer, up-to-date components.

### Android
- Built with a newer version of Kotlin, the language the app is written in, plus newer helper libraries for background work and for reading Google's replies.
- Installs over 0.2.1 as a normal update; your key, settings and history stay.

### Windows
- No changes to the app itself; it moves to version 0.2.2 so both apps share one version number.
- Replace your old `Plainspoken.exe` with the new one if you like; your settings and history stay.

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

[0.5.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.5.0
[0.4.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.4.0
[0.2.2]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.2.2
[0.2.1]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.2.1
[0.2.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.2.0
[0.1.1]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.1.1
[0.1.0]: https://github.com/himanshuafmc/plainspoken/releases/tag/v0.1.0
