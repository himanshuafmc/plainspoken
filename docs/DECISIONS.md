# Decisions

A short log of the choices made while building Plainspoken, and why. Newest entries at the bottom of each section.

## Settled up front
Windows 10/11 x64 · C# / .NET 10 · WinForms · NAudio for capture · P/Invoke for the hotkey and input · Google Gemini API on the free tier with each user's own key · toggle (not push-to-talk) · portable single-file exe built by GitHub Actions, unsigned, no installer or auto-update · PerMonitorV2 DPI · no telemetry, no servers of our own · GPL-3.0.

## Speech engines
- **Default: live streaming with `gemini-3.5-transcribe-live`, falling back to `gemini-3.5-transcribe`.** The batch model was verified end to end on 2026-09-29/30 (see `SPEC.md` §4). The live model was confirmed working by the maintainer on a Windows PC; it could not be tested from the cloud dev VM because its proxy blocks WebSockets. Because the live protocol for this model isn't documented yet, several setup shapes are tried in order and the accepted one is remembered, every server message *shape* is logged (never text), and any failure, timeout or empty result falls back to the batch model using the saved recording.
- **Live mode pauses itself after 2 failures in a row (15 minutes).** Many office and campus networks block WebSockets; without the pause every dictation there would wait for the live timeout (~8 s) before falling back. A success resets the count, and changing the key, live model or API address ends the pause.
- **Transcript pieces are joined with rules, not glued (v0.1.1).** v0.1.0 glued live chunks together as they came, and users saw "tomorrowmorning" and "done.Next". A space can't simply be added at every boundary, because some streams split words into tokens that carry their own spaces. The joiner (SPEC §4.1b) adds a space where a boundary is clearly between words or sentences and leaves token streams alone; a final pass adds a missing space after punctuation, whatever the source. The log counts how pieces arrive so the rule can be tuned from real logs.
- **Batch audio is sent inline for clips up to 10 MB (~5 minutes).** The brief expected Files-API upload only ("inline not supported"), but inline requests were accepted in testing up to 9.6 MB and remove two round trips: an 11 s clip took ~2.0 s instead of 3.2–4.2 s. If Google ever rejects an inline request with a 4xx, the same clip is retried through the Files API (upload → transcribe → delete). Longer clips always use the Files API.
- **Fallback engine `generate` (`gemini-3.5-flash-lite` via generateContent)** is implemented and selectable in Settings → Advanced. It is noticeably weaker (in testing it sometimes kept repeated words), so it is a backup, not an equal alternative.
- **Backup engine on rate limit, default on.** The free tier for `gemini-3.5-transcribe` allows about 3 requests per minute (and Google's message has also named 25 per day). On a 429 the same audio is sent once to the other engine.
- **A short retry delay beats "per day" wording.** On 2026-09-30 a 429 said "limit: 25 requests per day … retry in 17s", and a retry 30 s later worked. Treating it as the daily quota would have told the user to wait until tomorrow, so only a structured `PerDay` quota id, or a long/absent retry delay, counts as daily.
- The REST response has no `output_text`; the parser reads `steps[].content[].text` and also tolerates `output_text` and `outputs[]`.
- The HTTPS connection is warmed up (a metadata GET) when recording starts and kept alive for 5 minutes, so the real request skips DNS/TCP/TLS setup.
- Hindi output script varies (Roman or Devanagari) and there is no documented control; documented as a limitation.

## Architecture
- `Plainspoken.Core` (net10.0, no Windows APIs) holds everything portable: settings, engines, WAV/resampling, clip checks, retry, error mapping, quota-reset time, history/pending stores, logger and the dictation state machine behind small interfaces. The Windows app only implements those interfaces, so the state machine is unit-tested on Linux and can be ported to Kotlin.
- Resampling to 16 kHz uses a small windowed-sinc resampler in Core (testable on Linux) instead of NAudio's resamplers.
- Plain constructor injection, no DI container. `HttpClient` takes an injectable `HttpMessageHandler`; the live WebSocket sits behind `ILiveSocket` for the same reason.
- An opt-in console tool, `windows/tools/Plainspoken.Smoke`, runs the live smoke test. It never runs in CI (CI has no key and must never have one).

## Dependencies
- **NAudio.Wasapi 2.4** (MIT, GPL-compatible), pinned to the mature 2.x line.
- DPAPI comes from the .NET 10 Windows Desktop framework; no package needed.
- Test-only: xUnit 2.9, Microsoft.NET.Test.Sdk. Nothing else.

## Windows behaviour
- **Overlay never takes focus:** `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, `ShowWithoutActivation`, `WM_MOUSEACTIVATE → MA_NOACTIVATE`, no focusable child controls, topmost re-asserted with `SWP_NOACTIVATE` (the WinForms `TopMost` setter can activate).
- **Green ✓ instead of ■** for "stop and insert": a tick reads as "done, put the text in"; ✕ cancels.
- **Idle mini button** (on by default): the same non-activating window shrinks to a small button, so a click starts dictation without stealing focus. Draggable; its position is stored per device (`local.miniButtonPosition`), not in exported settings. Hidden while a full-screen window is in front.
- Microphone capture objects are created on a thread-pool thread, so NAudio raises its events on its own thread; the UI thread never blocks waiting for audio.
- Before pasting, wait (≤ 1 s) for Ctrl/Alt/Shift/Win to be released. If the taskbar has focus (dictation toggled from the tray), focus returns to the last app first.
- Dictated text on the clipboard is excluded from Win+V history and cloud clipboard.
- "Type it out" sends line breaks as Shift+Enter so chat apps don't send early.
- Apps running as Administrator are detected (their process token can't be queried from a normal process) and get a copy-to-clipboard instead of a paste that UIPI would silently drop.
- Single instance via a per-user named mutex; a second launch asks the running copy to open Settings.

## Public repository
- GPL-3.0 (`LICENSE` is the unmodified text). No per-file licence headers; `THIRD-PARTY-NOTICES.md` credits NAudio and .NET and ships in the release zip.
- No personal data anywhere: seed vocabulary is generic product names, smoke-test clips are generated with espeak-ng (`make-clips.sh`) and never committed, and unit-test fixtures contain only synthetic sentences. Commits use the maintainer's GitHub no-reply address.
- The app icon (speech bubble + sound wave) is original; `assets/plainspoken.svg` is the source and `assets/make_icon.py` renders the `.ico` from it with headless Chromium.

## Android (v0.2.0)
- **Same repository, one brand, one release.** `windows/` and `android/` live side by side with one README, one issue tracker and one Releases page. Both apps share the `VERSION` file, so every release carries the Windows zip and the Android APK with the same number; the Windows app moved from 0.1.1 to 0.2.0 without changes.
- **A voice keyboard first, a floating bubble later.** Android has no global hotkey and apps can't type into other apps. An input method is Android's official way to put text into any text box, needs only the microphone permission and is allowed on the Play Store. A floating bubble (closest to the Windows mini button) needs "display over other apps" plus an Accessibility service, which triggers strong warnings and Play Store restrictions; it is planned as an option.
- **The core is plain Kotlin in its own Gradle build** (`android/core`), a port of the Windows core, so it builds and tests on any machine without the Android SDK. Both test suites read the same `shared/test-fixtures/` (API responses, errors, an exported settings file, and `text-cases.json` for joining and spacing), which keeps the two apps' behaviour identical.
- **Framework views, no AndroidX UI libraries.** The app's only libraries are OkHttp and the kotlinx libraries. That keeps the APK small, the dependency list short for an open-source app, and let the app code be compile-checked against the Android framework on a machine that cannot download the Android SDK. Since OkHttp 5, OkHttp's Android build brings in AndroidX Startup, Annotation and Tracing, so `android.useAndroidX` is on; the app code itself still uses no AndroidX.
- **Android Gradle Plugin 9 with built-in Kotlin** (Gradle 9). AGP 9 compiles Kotlin itself, so the app no longer applies `org.jetbrains.kotlin.android` and its Kotlin version comes with AGP. The `core` build is plain Kotlin/JVM and keeps its own Kotlin version.
- **Package name `app.plainspoken`** (permanent: it identifies the app for updates and on the Play Store). **minSdk 26** (Android 8.0): `java.time` and `java.util.Base64` are available without desugaring; **targetSdk 35**, with edge-to-edge insets handled. **compileSdk 37** because OkHttp 5 requires it; raising compileSdk only makes newer APIs visible to the compiler, while targetSdk decides behaviour on the phone.
- **Minification is off** for now, so the release APK runs exactly the tested classes; it can be turned on once real-device testing covers it.
- **Privacy on the phone:** the key is encrypted with an Android Keystore key that never leaves the device; backup is off; the mic is disabled in password boxes; incognito text boxes don't add to history.
- **Start feedback is a vibration, not a sound**, because the microphone opens at the same moment and would record the chirp (and a clip with a chirp would pass the silence check and waste a free request).
- **Signing:** the release APK is signed in GitHub Actions with a key stored only in repository secrets; the release job refuses to publish an APK signed with anything else. Builds without the secrets (forks, test builds) use a throwaway debug key and are named `…-test.apk`.
- **Not on the Play Store yet.** It needs a developer account ($25, identity verification, a public contact email) and, for new personal accounts, a 14-day closed test with 12 testers. The APK on the Releases page is the official download until then.

## CI and releases
- Push runs on every branch and `v*` tags; same-repo `pull_request` runs are skipped because the push run on the same commit already reports to the PR.
- One workflow (`build.yml`) runs the Windows core tests, the Windows build and the Android build (core tests, APK, lint) on every push.
- A release is created for a pushed `v*` tag (which must equal `v` + the `VERSION` file), or for a branch push whose commit message contains `[release]` (then CI creates the tag). The second path exists because cloud dev sessions can push branches but not tags. The release carries both the Windows zip and the signed Android APK.
- Release notes come from the matching `CHANGELOG.md` section.
