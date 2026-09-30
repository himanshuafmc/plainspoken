# Contributing to Plainspoken

Thanks for helping! Bug reports, test results on different PCs and small, focused pull requests are all welcome.

## Reporting a problem

- Use the **[bug report form](https://github.com/himanshuafmc/plainspoken/issues/new?template=bug_report.yml)**.
- Attach the newest log from `%LOCALAPPDATA%\Plainspoken\logs\` if you can. Logs hold timings and error codes, never your words or your key.
- **Never post your Gemini API key or anything private you dictated.** Describe the kind of text instead ("a two-line Hinglish WhatsApp message").
- Security problems (for example, a way the key or dictated text could leak) go through [SECURITY.md](SECURITY.md), not a public issue.

## Building and testing

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). The app itself runs only on Windows, but the core logic and its tests also build on Linux and macOS.

```
dotnet build windows/Plainspoken.sln -p:EnableWindowsTargeting=true
dotnet test windows/tests/Plainspoken.Core.Tests
```

The optional live smoke test calls the real Gemini API with your own key (it never runs in CI):

```
windows/tools/Plainspoken.Smoke/make-clips.sh clips          # synthetic clips, needs espeak-ng
GEMINI_API_KEY=... dotnet run --project windows/tools/Plainspoken.Smoke -- --engine both clips/*.wav
```

## How the code is organised

- `windows/src/Plainspoken.Core` — everything that isn't Windows-specific: the Gemini engines, audio conversion, settings, storage, error handling and the dictation state machine. It must not use Windows APIs, so the future Android app can follow it closely.
- `windows/src/Plainspoken.App` — the WinForms tray app: hotkey, microphone, overlay, text insertion and the settings windows.
- `docs/SPEC.md` — how the app must behave on every platform. Update it when behaviour changes.
- `docs/DECISIONS.md` — why things are the way they are. Add a line for any choice a future contributor might question.

## Ground rules for pull requests

- Keep each pull request to one change, and explain the *why* in its description.
- The build treats warnings as errors; CI must be green.
- Changes to `Plainspoken.Core` come with unit tests.
- Windows UI changes: say what you tried by hand, using the relevant part of [`docs/TEST-CHECKLIST.md`](docs/TEST-CHECKLIST.md).
- **No secrets or personal data, ever:** no API keys (not even fake ones that look real; build them at run time as `FakeKey` does), no real recordings (`*.wav` is ignored; use `make-clips.sh`), no real names, places or personal vocabulary in tests, fixtures or examples.
- Only your own work, or code under a GPL-3.0-compatible licence with its notice added to `THIRD-PARTY-NOTICES.md`. Don't copy code, text, icons or sounds from other dictation apps.
- New dependencies need a good reason; the app currently uses only NAudio.

By contributing you agree that your contribution is licensed under the [GNU GPL v3.0](LICENSE), like the rest of the project.

## Releases (maintainers)

1. Bump `VersionPrefix` in `windows/Directory.Build.props` and add a section for that version to `CHANGELOG.md`.
2. Push a tag `v<version>`, or push a commit whose message contains `[release]`.
3. GitHub Actions builds and tests the app, creates the tag if needed, and publishes the zip with the CHANGELOG section and the zip's SHA-256 as release notes.
