# Contributing to Plainspoken

Thanks for helping! Bug reports, test results on different PCs and small, focused pull requests are all welcome.

## Reporting a problem

- Use the **[bug report form](https://github.com/himanshuafmc/plainspoken/issues/new?template=bug_report.yml)**.
- Attach the newest log from `%LOCALAPPDATA%\Plainspoken\logs\` if you can. Logs hold timings and error codes, never your words or your key.
- **Never post your Gemini API key or anything private you dictated.** Describe the kind of text instead ("a two-line Hinglish WhatsApp message").
- Security problems (for example, a way the key or dictated text could leak) go through [SECURITY.md](SECURITY.md), not a public issue.

## Building and testing

**Windows app** — needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app runs only on Windows, but the core logic and its tests also build on Linux and macOS.

```
dotnet build windows/Plainspoken.sln -p:EnableWindowsTargeting=true
dotnet test windows/tests/Plainspoken.Core.Tests
```

**Android app** — needs JDK 17; the app also needs the Android SDK (API 35). The core tests run anywhere.

```
cd android
./gradlew -p core test
./gradlew :app:assembleRelease :app:lintRelease
```

The optional live smoke test calls the real Gemini API with your own key (it never runs in CI):

```
windows/tools/Plainspoken.Smoke/make-clips.sh clips          # synthetic clips, needs espeak-ng
GEMINI_API_KEY=... dotnet run --project windows/tools/Plainspoken.Smoke -- --engine both clips/*.wav
```

## How the code is organised

- `windows/src/Plainspoken.Core` — everything that isn't Windows-specific: the Gemini engines, audio conversion, settings, storage, error handling and the dictation state machine.
- `windows/src/Plainspoken.App` — the WinForms tray app: hotkey, microphone, overlay, text insertion and the settings windows.
- `android/core` — the same logic in Kotlin (no Android APIs); `android/app` — the voice keyboard and the app screens. See [`android/README.md`](android/README.md).
- `shared/` — the settings schema, the starter vocabulary and `test-fixtures/`, which **both** test suites read. When behaviour changes, change both apps and add the case to the shared fixtures (for example `text-cases.json`).
- `docs/SPEC.md` — how the app must behave on every platform. Update it when behaviour changes.
- `docs/DECISIONS.md` — why things are the way they are. Add a line for any choice a future contributor might question.

## Ground rules for pull requests

- Keep each pull request to one change, and explain the *why* in its description.
- The build treats warnings as errors; CI must be green.
- Changes to either core come with unit tests; behaviour shared by both apps is changed in both.
- UI changes: say what you tried by hand, on which device, using the relevant part of [`docs/TEST-CHECKLIST.md`](docs/TEST-CHECKLIST.md).
- **No secrets or personal data, ever:** no API keys (not even fake ones that look real; build them at run time as `FakeKey` does), no real recordings (`*.wav` is ignored; use `make-clips.sh`), no real names, places or personal vocabulary in tests, fixtures or examples.
- Only your own work, or code under a GPL-3.0-compatible licence with its notice added to `THIRD-PARTY-NOTICES.md`. Don't copy code, text, icons or sounds from other dictation apps.
- New dependencies need a good reason; Windows uses only NAudio, Android only OkHttp (with the few AndroidX libraries it needs) and the kotlinx libraries.

By contributing you agree that your contribution is licensed under the [GNU GPL v3.0](LICENSE), like the rest of the project.

## Weekly maintenance (maintainers)

A weekly Claude Code run checks both apps, the Gemini API and bug reports labelled `claude-fix`, and proposes fixes
and releases as pull requests. See [docs/MAINTENANCE.md](docs/MAINTENANCE.md).

## Releases (maintainers)

Both apps always share one version number and one release.

1. Change the `VERSION` file at the repository root and add a section for that version to `CHANGELOG.md`.
2. Push a tag `v<version>`, or push a commit whose message contains `[release]`.
3. GitHub Actions tests and builds both apps, creates the tag if needed, and publishes the Windows zip and the Android APK with the CHANGELOG section and both SHA-256 checksums as release notes.

The APK must always be signed with the same key, or phones can't install updates. The key lives only in the
repository's **Settings → Secrets and variables → Actions**, as four secrets:

| Secret | Value |
|---|---|
| `PLAINSPOKEN_KEYSTORE_BASE64` | the keystore file, base64-encoded |
| `PLAINSPOKEN_KEYSTORE_PASSWORD` | the keystore password |
| `PLAINSPOKEN_KEY_ALIAS` | the key alias |
| `PLAINSPOKEN_KEY_PASSWORD` | the key password |

Keep a private backup of the keystore and passwords. The release job refuses to publish an APK that isn't signed
with this key.
