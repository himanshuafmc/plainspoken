# Plainspoken for Android — Phase 2 (not started)

This folder is a placeholder. The Android app will be a separate Kotlin app that
implements [`docs/SPEC.md`](../docs/SPEC.md) and imports the same settings JSON
(`shared/settings.schema.json`) as the Windows app.

The UI is still undecided: a floating mic bubble (accessibility service + overlay)
or a custom keyboard (IME) with a mic button. APKs will be built in GitHub Actions,
because the Android SDK download host is not reachable from the cloud dev VM.
