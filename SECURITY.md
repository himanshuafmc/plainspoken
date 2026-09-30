# Security policy

## Supported versions

Only the [latest release](https://github.com/himanshuafmc/plainspoken/releases/latest) gets fixes. Please update before reporting.

## Reporting a vulnerability

Please report security problems **privately**: open the repository's **Security** tab and click **Report a vulnerability**. Don't open a public issue for them.

Useful things to include: the Plainspoken version, Windows or Android version, what an attacker could do, and steps to reproduce. Never include your real API key or real dictated text; a made-up example is enough.

This is a volunteer project, so there is no fixed response time, but reports are taken seriously and you'll be credited in the release notes if you wish.

## What counts

For example:

- the Gemini API key being readable by other Windows users or other Android apps, written in plain text, logged or exported;
- the Android keyboard recording or sending audio when the mic wasn't tapped, or in a password box;
- dictated text ending up anywhere other than the target app, the clipboard (restored afterwards), local history and Google's API;
- audio or text being sent anywhere other than the configured Gemini API address;
- a way to make Plainspoken type or paste text the user didn't dictate.

Out of scope: Google's own services and data policies, other apps reading the clipboard while text is being pasted, the unsigned-exe SmartScreen warning, and Android's standard warning when any keyboard is turned on (both known limitations).

## Checking a download

Every release is built by GitHub Actions from the tagged source, and the release notes list the SHA-256 of both files. To check the Windows zip in PowerShell:

```
Get-FileHash .\Plainspoken-win-x64-<version>.zip -Algorithm SHA256
```

The Android APK is signed with the same release key every time; Android refuses to install an update signed with
a different key.
