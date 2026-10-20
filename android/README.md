# Plainspoken for Android

The Android app: a **voice keyboard** with a big mic button, a **floating mic** that works next to any keyboard (an
accessibility service, in the floating-mic edition only), and a small app for setup, settings and recent transcripts. It behaves like the Windows app because both follow [`docs/SPEC.md`](../docs/SPEC.md) and are tested
against the same files in [`shared/test-fixtures/`](../shared/test-fixtures).

## Layout

| Folder | What it is |
|---|---|
| `core/` | Plain Kotlin (no Android APIs), a separate Gradle build: settings in the shared JSON format; the speech services (Gemini — inline audio, Files API, live streaming — Sarvam, OpenAI-compatible, Deepgram and ElevenLabs, with Sarvam and Deepgram streaming); the router (service order, switching on failure or slowness, pausing failing services); the clean-up step and its safety check; snippets; the correction learner; text joining; WAV and splitting; saved recordings, history and stats; the redacting logger and the dictation state machine. Started as a port of `windows/src/Plainspoken.Core`. |
| `app/` | The Android app. Framework views only (no AndroidX UI libraries): `dot/` (the floating mic: an `AccessibilityService` that shows a draggable mic overlay next to the keyboard and types into the focused text box), `keyboard/` (the `InputMethodService`, its custom-drawn mic button with hold-to-talk, keys and the "add to vocabulary" offer), `platform/` (microphone via `AudioRecord`, AAC compression via `MediaCodec`, sounds, Android Keystore key storage), the setup/home, settings, per-service, speed-and-cost and history screens. |

The app targets Android 8.0+ (API 26) and Android 15 (API 35). Its only libraries are OkHttp, kotlinx.coroutines
and kotlinx.serialization, plus three small AndroidX libraries that OkHttp itself needs (see `THIRD-PARTY-NOTICES.md`).
It is built with the Android Gradle Plugin 9, which compiles the Kotlin code itself.

There are two editions (product flavors) with the same application id and signing key: `standard` (the keyboard) and
`floatingMic`, which adds the floating mic's accessibility service in `app/src/floatingMic/AndroidManifest.xml`. They
share all code. The floating mic is a separate APK because Play Protect blocks installing apps with an accessibility
service from the phone in some countries; it is installed with Play Protect's scanning briefly off, or over USB (see the
README and `docs/DECISIONS.md`).

## Build and test

Needs JDK 17 and, for the app, the Android SDK (API 37).

```
cd android
./gradlew -p core test               # core tests: any machine, no Android SDK needed
./gradlew :app:assembleRelease       # both APKs (app/build/outputs/apk/standard/release/ and …/floatingMic/release/)
./gradlew :app:lintStandardRelease :app:lintFloatingMicRelease
./gradlew -p core cleanupEval        # optional: score the clean-up step on shared/test-fixtures/cleanup-cases.json
```

`cleanupEval` calls the real Gemini API (set `GEMINI_API_KEY`; `-Pmodel=…` tries another model, `-Pcases=hi-` runs only
some cases). It is a tuning tool, never part of CI.

Without signing secrets the release APK is signed with the debug key, which is fine for testing on your own phone.
GitHub Actions builds every push; a `[release]` commit or a `v*` tag publishes the APK next to the Windows zip,
signed with the release key from the repository secrets (see `CONTRIBUTING.md`).
