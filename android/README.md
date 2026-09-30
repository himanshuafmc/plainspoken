# Plainspoken for Android

The Android app: a **voice keyboard** with a big mic button, plus a small app for setup, settings and recent
transcripts. It behaves like the Windows app because both follow [`docs/SPEC.md`](../docs/SPEC.md) and are tested
against the same files in [`shared/test-fixtures/`](../shared/test-fixtures).

## Layout

| Folder | What it is |
|---|---|
| `core/` | Plain Kotlin (no Android APIs), a separate Gradle build: settings in the shared JSON format, the Gemini engines (inline audio, Files API, backup engine, live WebSocket streaming with fallback and pause), text joining and clean-up, WAV, saved recordings, history, the redacting logger and the dictation state machine. A port of `windows/src/Plainspoken.Core`. |
| `app/` | The Android app. Framework views only (no AndroidX): `keyboard/` (the `InputMethodService` and its custom-drawn mic button and keys), `platform/` (microphone via `AudioRecord`, sounds, Android Keystore key storage), the setup/home, settings and history screens. |

The app targets Android 8.0+ (API 26) and Android 15 (API 35). Its only libraries are OkHttp, kotlinx.coroutines
and kotlinx.serialization (see `THIRD-PARTY-NOTICES.md`).

## Build and test

Needs JDK 17 and, for the app, the Android SDK (API 35).

```
cd android
./gradlew -p core test               # core tests: any machine, no Android SDK needed
./gradlew :app:assembleRelease       # the APK (app/build/outputs/apk/release/)
./gradlew :app:lintRelease
```

Without signing secrets the release APK is signed with the debug key, which is fine for testing on your own phone.
GitHub Actions builds every push; a `[release]` commit or a `v*` tag publishes the APK next to the Windows zip,
signed with the release key from the repository secrets (see `CONTRIBUTING.md`).
