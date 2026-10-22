# Plan: offline fallback (0.6.0)

Working notes for the 0.6.0 change. When the work is done, the decisions move into `docs/SPEC.md` and
`docs/DECISIONS.md` and this file is deleted.

## Decided with the maintainer

- Online services stay the main path (main service, then backups, with hedging as today).
- Offline is the last resort, on both apps:
  - no internet when recording stops → offline at once;
  - no usable service (no keys, or all paused) → offline at once;
  - every online service failed → offline at once;
  - nothing back after **6 s** (from the end of recording) → offline starts as well; the first text wins.
- Engine: **whisper.cpp** (MIT) on both apps, transcribing the audio already recorded.
  - Android: built from source as a native library (arm64-v8a first), called through JNI. Default model **small**.
  - Windows: **Whisper.net** (MIT, wraps whisper.cpp) in the Core. Default model **medium**; also offer
    **large-v3-turbo** (about the same size, faster and more accurate) so the maintainer can compare.
- Model delivery: **download once** from Settings while online (no model in the APK or zip). Show size and progress;
  check a pinned SHA-256 before use; store under app data (Android `filesDir/models`, Windows `%LOCALAPPDATA%`).
- Offline text: no LLM clean-up (local filler filter only), a short "Typed offline" note, and History marks it.
- Settings: "Offline fallback" section with an on/off switch, model choice, Download / Delete, and the space used.
- Released as **0.6.0** for both apps in one new PR.

## Model files

From `https://huggingface.co/ggerganov/whisper.cpp/resolve/main/` (blocked in the cloud sandbox, so the checksums are
pinned from a CI job or the maintainer's machine, not guessed):

| Use | File | Approx. size |
| :-- | :-- | --: |
| Android default | `ggml-small-q5_1.bin` | 190 MB |
| Android faster option | `ggml-base-q5_1.bin` | 60 MB |
| Windows default | `ggml-medium-q5_0.bin` | 515 MB |
| Windows option | `ggml-large-v3-turbo-q5_0.bin` | 550 MB |

## Hindi and Hinglish

Whisper tends to write Hindi in Devanagari, and with language `en` it may translate. Use language auto-detection (or
`hi` for the Hindi presets), never forced `en` for mixed speech; nudge Roman script with an initial prompt when the
Hindi-script setting is Roman. Document that offline Hinglish is weaker.

## Steps

1. **Core, both platforms (testable here):** an `OfflineTranscriber` interface (`isReady`, `transcribe(wav, request)`);
   the router gains the offline rules above with a fake in tests (no network → offline; no keys → offline; all failed →
   offline; 6 s hedge; offline skips clean-up; stats record service "offline"). A network check is injected.
2. **Windows engine:** Whisper.net + CPU runtime in Core; model download with SHA-256 check; Settings section; tray
   note. Check the single-file publish still works and the zip growth.
3. **Android engine:** `android/whisper` module with CMake (whisper.cpp pinned by tag and hash), a small JNI bridge, a
   Kotlin wrapper on a background thread; model download (foreground progress in Settings); Settings section.
   CI: NDK build for arm64-v8a, lint both editions.
4. Docs: SPEC, DECISIONS, CHANGELOG 0.6.0, TEST-CHECKLIST (offline on both: airplane mode, wrong key, slow service).
5. Version 0.6.0, PR, CI green, maintainer tests on devices, merge with `[release]`.

## Progress

- [x] Plan agreed; branch restarted from `main` after 0.5.0.
- [ ] Step 1 · [ ] Step 2 · [ ] Step 3 · [ ] Step 4 · [ ] Step 5
