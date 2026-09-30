# Plainspoken behaviour spec (platform-neutral)

Version 1 · applies to Windows (Phase 1) and Android (Phase 2).
Anything marked **[Windows]** or **[Android]** is platform-specific; everything else must behave the same on both.
The product name comes from one constant in code (`AppInfo.Name`).

## 1. What the app does

1. The user starts dictation (Windows: global hotkey, default `Ctrl+Alt+Space`, or left-click on the tray icon).
2. A small "Listening" pill appears and a soft start sound plays. The user speaks English, Hindi or Hinglish.
3. The user stops dictation (same hotkey again, or the green ✓ on the pill). The pill shows "Transcribing…".
4. The audio is sent to Google Gemini, which returns cleaned-up text (no "um/uh", repeats or false starts; punctuation; lists, dates and amounts formatted).
5. The text is inserted where the cursor is. It is always added to history.
6. Cancel (`Esc` while recording, or ✕) discards the recording and inserts nothing.

Nothing is ever lost: the recording is saved to disk *before* any network call and deleted only after a transcript has been obtained. Any failure leaves it available to "Retry last dictation".

## 2. State machine

```
            toggle                     toggle / auto-stop
   Idle ───────────────▶ Recording ─────────────────────▶ Transcribing ──▶ Idle
    ▲                      │  cancel (Esc/✕)                 │  cancel (✕): abort request,
    └──────────────────────┘                                 │  keep audio for retry
    ▲                                                        │
    └────────────────────────────────────────────────────────┘ success / error
```

| State | Toggle (hotkey / tray click) | Cancel | Notes |
|---|---|---|---|
| Idle | start recording | — | Retry is only allowed from Idle. |
| Recording | stop → clip checks → Transcribing | discard audio → Idle | Esc is a temporary global hotkey only in this state [Windows]. |
| Transcribing | ignored, show "Busy — still transcribing" hint | abort, keep audio in pending → Idle | |

Recording limits:
- Maximum length is `recording.maxSeconds` (default 600). At `max − 30 s` the pill shows "30 s left"; at `max` recording auto-stops and transcribes.
- Clip checks after stop (no API call if either fails, show "Didn't catch that"):
  - duration < 0.6 s → too short;
  - silence: the audio is split into 30 ms frames; if fewer than 3 frames have RMS ≥ 0.0056 (≈ −45 dBFS, full scale = 1.0) the clip is treated as silent.

## 3. Audio format

Sent audio is WAV, 16 000 Hz, mono, 16-bit signed little-endian PCM (≈ 1.9 MB per minute), with a standard 44-byte RIFF header.
[Windows] capture is WASAPI shared mode in the device mix format (usually 48 kHz float stereo) and is converted on the fly: downmix to mono → windowed-sinc low-pass resample to 16 kHz → PCM16.

## 4. Gemini API contract (verified 2026-09-29 against the live API)

Base URL (configurable): `https://generativelanguage.googleapis.com`. Auth header: `x-goog-api-key: <key>` on every request.

### 4.1 Engine "transcribe" (default): `gemini-3.5-transcribe` via the Interactions API

**Short and medium clips (≤ 10 MB WAV, about 5 minutes) are sent inline in a single request** (verified 2026-09-30 up to 9.6 MB; an 11 s clip took 2.0 s instead of 3.2–4.2 s via upload):

```
POST {base}/v1beta/interactions
{ "model": "gemini-3.5-transcribe",
  "input": [{ "type": "audio", "data": "<base64 WAV>", "mime_type": "audio/wav" }],
  "generation_config": { "transcription_config": { … as below … } } }
```

If Google answers an inline request with a 4xx "bad request", the same clip is retried once through the Files API. Longer clips always use the Files API (steps 1–3 below).

Connection warm-up: when recording starts, the app sends `GET {base}/v1beta/models/{model}` in the background (at most once per 20 s) so the DNS/TCP/TLS setup is done before the user stops speaking. Idle HTTPS connections are kept for 5 minutes.

Step 1 — resumable upload (Files API, clips over 10 MB):

```
POST {base}/upload/v1beta/files
  X-Goog-Upload-Protocol: resumable
  X-Goog-Upload-Command: start
  X-Goog-Upload-Header-Content-Length: <bytes>
  X-Goog-Upload-Header-Content-Type: audio/wav
  Content-Type: application/json
  {"file":{"display_name":"plainspoken-dictation"}}
→ 200, response header X-Goog-Upload-URL: <upload url>

POST <upload url>
  X-Goog-Upload-Offset: 0
  X-Goog-Upload-Command: upload, finalize
  <raw bytes>
→ 200 {"file":{"name":"files/abc123","uri":"{base}/v1beta/files/abc123","state":"ACTIVE", "mimeType":"audio/wav","sizeBytes":"328644",...}}
```

If `state` is `PROCESSING`, poll `GET {base}/v1beta/{file.name}` every 500 ms (max 30 s) until `ACTIVE`; `FAILED` is an error. In testing, WAV files were `ACTIVE` immediately.

Step 2 — transcribe:

```
POST {base}/v1beta/interactions
{
  "model": "gemini-3.5-transcribe",
  "input": [{ "type": "audio", "uri": "<file.uri>", "mime_type": "audio/wav" }],
  "generation_config": {
    "transcription_config": {
      "mode": "smart",                         // or "verbatim"
      "custom_vocabulary": ["term1", "term2"], // omitted when empty
      "language_codes": ["en-IN", "hi-IN"]     // omitted for auto-detect
    }
  }
}
```

Verified response (HTTP 200):

```json
{
  "id": "v1_…",
  "status": "completed",
  "object": "interaction",
  "model": "gemini-3.5-transcribe",
  "steps": [
    { "type": "model_output",
      "content": [ { "type": "text", "text": "Hello, this is a test of the Plainspoken dictation app. …" } ] }
  ],
  "usage": { "total_tokens": 188, … }
}
```

- There is **no** top-level `output_text` in the REST JSON (that is an SDK convenience). The transcript is the concatenation of every `content[].text` where `content[].type == "text"` inside `steps[]` with `type == "model_output"`.
- Parser tolerance (in order): top-level `output_text`/`outputText` string → `outputs[]` items of type `text` → `steps[]` as above (a step without `type` is accepted). Unknown fields are ignored.
- If `status` is `in_progress`/`queued` and an `id` is present, poll `GET {base}/v1beta/interactions/{id}` every 1 s until the request timeout. `failed`/`cancelled` → error.
- `status == "completed"` with empty text → treated as "Didn't catch that" (no error, audio deleted).
- Observed behaviour: smart mode removed "Um" and used the custom vocabulary ("Plainspoken"); verbatim kept "Um". Hindi speech was returned in **Roman script** ("Namaste, main kal office aaunga."). There is no documented script control.
- Hindi script is not stable: the same kind of clip came back in Roman script on 2026-09-29 and in Devanagari on 2026-09-30.

Step 3 — delete: `DELETE {base}/v1beta/{file.name}` → `200 {}`. Done after the transcript is obtained or after the final failure; best-effort (a 403/404 means already gone). Deleting never delays text insertion.

Live smoke test through the Core code (`windows/tools/Plainspoken.Smoke`, 2026-09-30, synthetic espeak-ng clips, seed vocabulary, smart mode, English + Hindi, inline audio):

| Clip | Engine | Result | Time |
|---|---|---|---|
| "Um, so, please add milk, eggs and, uh, bread to the shopping list. The total is two thousand five hundred rupees." | transcribe | "Please add milk, eggs, and bread to the shopping list. The total is 2,500 rupees." | 2.3 s |
| same | generate | "So, please add milk, eggs, and bread to the shopping list. The total is ₹2,500." | 1.2 s |
| Hinglish ("कल सुबह दस बजे meeting है, please send the WhatsApp message to everyone.") | transcribe | "Kal subah 10:00 baje meeting hai. Please send the WhatsApp message to everyone." | 2.1 s |
| same | generate | "Also, remember that the Google Meet meeting is at 5:00 PM. Please send the WhatsApp message to everyone." (Hindi part invented, vocabulary term inserted) | 1.0 s |

The transcribe engine is clearly better and is the default; the generate engine is only a backup.

### 4.1a Live streaming (on by default, with fallback): `gemini-3.5-transcribe-live`

Setting `transcription.liveStreaming` (default `true`). Audio is streamed while the user speaks so the transcript is ready right after they stop. The protocol below follows the general Gemini Live API; it has been confirmed working on a Windows PC, but not from the cloud build machine (its proxy blocks WebSockets), so it has no automated test against the real service. It never replaces the safety net: the recording is still saved, and if the live session fails, times out or returns no text, the normal engine (4.1) transcribes the saved audio.

- WebSocket `wss://{host}/ws/google.ai.generativelanguage.{v1beta|v1alpha}.GenerativeService.BidiGenerateContent`, header `x-goog-api-key`.
- First message `{"setup": …}`. Shapes tried in order until the server replies `{"setupComplete":{}}` (the accepted one is remembered):
  1. `{"model":"models/gemini-3.5-transcribe-live","generationConfig":{"transcriptionConfig":{"mode","customVocabulary","languageCodes"}}}`
  2. `{"model":…,"generationConfig":{"responseModalities":["TEXT"]},"inputAudioTranscription":{}}`
  3. `{"model":…,"inputAudioTranscription":{}}`
  4. `{"model":…}`
  An API version whose handshake fails is skipped. A close reason mentioning the API key or quota stops immediately.
- Audio: `{"realtimeInput":{"audio":{"data":"<base64 PCM16LE>","mimeType":"audio/pcm;rate=16000"}}}` every 100 ms; end with `{"realtimeInput":{"audioStreamEnd":true}}`.
- Text: `serverContent.modelTurn.parts[].text` (preferred, skipping `thought` parts), else concatenated `serverContent.inputTranscription.text` chunks. Done on `turnComplete`/`generationComplete`, on close, or 1.5 s of silence after the end of audio if some text has arrived. Overall limit 8 s + audio length/10 (max 30 s).
- The log records every distinct server message *shape* (never text) and every rejected setup with its close reason, for diagnosis.
- After 2 failed dictations in a row (error, timeout or no text; cancelled ones don't count) live streaming pauses for 15 minutes and dictation goes straight to the normal engine. Changing the key, live model or API address ends the pause. This stops a network that blocks WebSockets from adding the live timeout to every dictation.

### 4.2 Engine "generate" (fallback): Flash-Lite via generateContent

```
POST {base}/v1beta/models/{generateModel}:generateContent
{
  "systemInstruction": {"parts":[{"text":"<clean-up prompt, see code: GeminiGenerateEngine.BuildPrompt>"}]},
  "contents": [{"role":"user","parts":[
      {"inlineData":{"mimeType":"audio/wav","data":"<base64>"}},      // clips ≤ 14 MB
      // or {"fileData":{"mimeType":"audio/wav","fileUri":"<file.uri>"}} for larger clips (uploaded as in 4.1)
      {"text":"<instruction incl. languages + vocabulary>"}]}],
  "generationConfig": {"temperature": 0}
}
→ {"candidates":[{"content":{"parts":[{"text":"…"}]},"finishReason":"STOP"}], …}
```

Text = concatenation of `candidates[0].content.parts[].text`, skipping parts with `"thought": true`. `promptFeedback.blockReason` or a missing candidate → unexpected-response error.
Default model: `gemini-3.5-flash-lite` (verified available and working with inline WAV).

### 4.3 Errors (verified shapes)

Two error body shapes exist and both must be parsed:

- Interactions API: `{"error":{"message":"…","code":"too_many_requests"}}` (`code` is a string: `too_many_requests`, `invalid_request`, `not_found`, `permission_denied`, …).
- Files / generateContent APIs: `{"error":{"code":403,"message":"…","status":"PERMISSION_DENIED","details":[…]}}`.

Rate limits: free tier for `gemini-3.5-transcribe` returned `429` with header `Retry-After: 43` and message
"Rate limit exceeded for model gemini-3.5-transcribe (limit: 3 requests per minute on Free Tier). Please retry in 43s …".
On 2026-09-30 the same API answered "(limit: 25 requests per day on Free Tier). Please retry in 17s", and a retry 30 s later succeeded, so the message can name the daily limit when a short window tripped.
A 429 is classified as:
- **daily quota** when a structured quota id (`QuotaFailure.violations[].quotaId`) mentions "PerDay";
- else **per-minute** when a quota id or the message says "per minute", or when `Retry-After` (header), `RetryInfo.retryDelay` (details) or "retry in Ns" (message) gives a delay ≤ 120 s — even if the message says "per day";
- else **daily quota**.

| Condition | Kind | User message | Audio |
|---|---|---|---|
| No key configured | `NoKey` | "Add your free Gemini API key in Settings to start." → Settings opens automatically (checked *before* recording) | — |
| 400 mentioning API key, 401, 403 on a non-file call | `InvalidKey` | "Your Gemini API key was not accepted. Please check it in Settings. Your recording is saved." → Settings opens automatically | kept |
| 429 per-minute | `RateLimited` | wait `Retry-After` (≤ 30 s) and retry once automatically, or use the backup engine (see below); if still failing: "Too many requests right now. Try Retry in a minute." | kept |
| 429 daily | `DailyQuota` | "Free daily limit reached. Resets at 12:30 PM IST." | kept |
| DNS / connection / TLS failure | `Network` | "No internet connection. Your recording is saved — use Retry when you're back online." | kept |
| Timeout | `Timeout` | "Google took too long to answer. Your recording is saved — try Retry." | kept |
| 5xx | `Server` | auto-retry twice (backoff 1 s, 2 s, ±50 % jitter), then "Google's service had a problem. Your recording is saved — try Retry." | kept |
| 404 model | `ModelNotFound` | "The speech model isn't available. Check the model name in Settings → Advanced." | kept |
| other 4xx | `BadRequest` | generic error | kept |
| 200 with unknown JSON shape | `UnexpectedResponse` | "Something unexpected came back from Google. Your recording is saved — try Retry." (sanitised JSON *shape* is logged) | kept |
| Microphone blocked by OS privacy | — | "Microphone access is turned off for desktop apps. Turn on 'Let desktop apps access your microphone' in Windows Settings." + button opening `ms-settings:privacy-microphone` [Windows] | — |

**Backup engine on rate limit** (`transcription.useBackupEngineWhenLimited`, default `true`): when the selected engine fails with `RateLimited` or `DailyQuota`, the same audio is sent once to the other engine. If that also fails, the original error is shown.

Timeouts: every request uses `requestTimeoutSeconds` (default 60). Uploads add 1 s per 64 KB; the transcription call adds `audioSeconds / 4`.

### 4.4 Daily quota reset time

Google resets the free daily quota at midnight Pacific time. Compute the next midnight in IANA zone `America/Los_Angeles` (DST-aware), convert to the device's local zone, and format as `h:mm tt` plus an abbreviation (e.g. "12:30 PM IST"). Examples: 2026-07-15 10:00 UTC (US summer) → 2026-07-16 07:00 UTC → 12:30 PM IST; 2026-01-15 10:00 UTC (US winter) → 2026-01-16 08:00 UTC → 1:30 PM IST.

## 5. Text insertion

- Transcript post-processing: trim; normalise line endings; append one space if `insertion.trailingSpace` (default on) and the text doesn't already end with whitespace.
- Always add to history first (if enabled).
- Method `paste` (default): save clipboard → set transcript → send paste shortcut → wait 250 ms → restore the saved clipboard (only if the clipboard still holds our text). Clipboard access is retried (10 × 50 ms). [Windows] The transcript is marked to be excluded from clipboard history / cloud clipboard.
- Method `type`: send the text as Unicode key events; line breaks are sent as Shift+Enter (so chat apps don't send the message).
- Before sending the paste shortcut, wait (≤ 1 s) until Ctrl/Alt/Shift/Win are released, so the stop hotkey doesn't turn Ctrl+V into Ctrl+Alt+V.
- If focus is on the taskbar/tray (dictation toggled by clicking the tray icon), focus is first returned to the last app window the user was in.
- If the clipboard was empty before, it is emptied again after pasting; if another app changed the clipboard during the 250 ms, it is left alone.
- If insertion fails, leave the transcript on the clipboard and show "Copied — press Ctrl+V".
- Retry from the tray menu copies the result to the clipboard ("Copied — press Ctrl+V") because the tray menu has taken focus away from the target app.
- [Windows] Known limitation: text cannot be sent into apps running as Administrator (UIPI). Plainspoken detects an elevated foreground window and falls back to "Copied — press Ctrl+V".

## 5a. What the user sees

- Mini button (idle, setting `showMiniButton`, default on): a 44×18 px dark pill with three small sound bars, bottom-centre above the taskbar of the screen with the focused app. Hover expands it to "Click or Ctrl+Alt+Space to dictate"; click starts dictation (focus stays in the user's app); drag moves it anywhere and the spot is remembered (`local.miniButtonPosition`); right-click: Start dictation, Move back to the bottom centre, Hide mini button, Settings…. It hides while a full-screen app (video, game, slideshow) is in front.
- Pill (≈ 264×44 logical px, grows from the mini button's position; never takes focus):
  - Listening: pulsing red dot, "Listening", m:ss timer, 5-bar level meter, green ✓ (stop and insert), ✕ cancel. "30 s left" replaces the label 30 s before the limit.
  - Transcribing: amber dots, "Transcribing…" or a status such as "Busy — trying backup engine…", ✕ (abort, keep audio).
  - Hints ("Didn't catch that", "Cancelled", "Copied — press Ctrl+V"): 2.5–6 s, click to dismiss. While busy, a hint (e.g. "Busy — still transcribing") is shown inside the active pill instead.
  - Errors: 5–9 s in the pill plus a system notification; clicking either runs the suggested action (Retry, open Settings, open microphone privacy settings).
- Tray icon (a speech bubble with sound bars): teal = ready, red = listening, amber = transcribing. Left-click toggles dictation. Menu: Start/Stop dictation (with hotkey), Retry last dictation (N saved), Recent transcripts (click copies), Settings…, About (version), Exit.
- Sounds: short rising chirp on start, falling chirp on stop (optional).

## 6. Settings

Stored as JSON with `schemaVersion` (currently 1). Exported files use the same shape without the `local` section; the schema is `shared/settings.schema.json`. Import merges the non-local sections and keeps local values (API key, microphone, start-with-Windows).

```json
{
  "schemaVersion": 1,
  "transcription": {
    "engine": "transcribe",
    "transcribeModel": "gemini-3.5-transcribe",
    "generateModel": "gemini-3.5-flash-lite",
    "apiBaseUrl": "https://generativelanguage.googleapis.com",
    "requestTimeoutSeconds": 60,
    "languages": "englishHindi",
    "mode": "smart",
    "useBackupEngineWhenLimited": true,
    "liveStreaming": true,
    "liveModel": "gemini-3.5-transcribe-live",
    "customVocabulary": ["WhatsApp", "Plainspoken"]
  },
  "recording": { "maxSeconds": 600, "sounds": true },
  "insertion": { "method": "paste", "trailingSpace": true },
  "history": { "enabled": true },
  "hotkey": "Ctrl+Alt+Space",
  "showMiniButton": true,
  "local": {
    "apiKeyProtected": "<platform-encrypted, base64>",
    "microphoneId": null,
    "startWithWindows": false,
    "firstRunCompleted": true,
    "miniButtonPosition": null
  }
}
```

| Field | Values | Default |
|---|---|---|
| `transcription.engine` | `transcribe`, `generate` | `transcribe` |
| `transcription.languages` | `englishHindi` → `["en-IN","hi-IN"]`; `auto` → omit `language_codes`; `english` → `["en-IN"]` | `englishHindi` |
| `transcription.mode` | `smart`, `verbatim` | `smart` |
| `transcription.requestTimeoutSeconds` | 10–600 | 60 |
| `transcription.customVocabulary` | ≤ 1000 terms, each trimmed, 1–100 chars, de-duplicated case-insensitively (first spelling wins), blank lines and lines starting with `#` dropped | seeded from `shared/vocabulary.default.txt` |
| `recording.maxSeconds` | 30–1800 | 600 |
| `insertion.method` | `paste`, `type` | `paste` |
| `hotkey` | `Mod+Mod+Key`, modifiers `Ctrl`, `Alt`, `Shift`, `Win`; at least one of Ctrl/Alt/Win | `Ctrl+Alt+Space` |

Unknown fields are ignored; missing fields take defaults; out-of-range numbers are clamped. The API key is never exported, never logged and never stored in plain text ([Windows] DPAPI CurrentUser; [Android] Android Keystore).

## 7. Storage [Windows paths]

| What | Where | Rules |
|---|---|---|
| Settings | `%APPDATA%\Plainspoken\settings.json` | atomic write (temp file + replace) |
| History | `%LOCALAPPDATA%\Plainspoken\history.json` | newest first, max 20; disable/clear in Settings; clearing also happens when disabled |
| Pending recordings | `%LOCALAPPDATA%\Plainspoken\pending\<yyyyMMdd-HHmmss-fff>.wav` + `.json` metadata | written before any network call, deleted after a transcript is obtained |
| Logs | `%LOCALAPPDATA%\Plainspoken\logs\plainspoken-yyyyMMdd.log` | one file per day, deleted after 7 days; never contain transcript text or the key |

History entry: `{"id": "<guid>", "createdUtc": "…", "text": "…", "durationSeconds": 4.2}`.
Pending metadata: `{"createdUtc":"…","durationSeconds":4.2,"lastError":"Network","attempts":1}`.

## 8. First run

A welcome dialog with three steps: (1) get a free key at https://aistudio.google.com/apikey, (2) paste it (with a Test button), (3) try the hotkey. The user must tick:
"This app uses Google's free tier. Google may use your recordings to improve its products. Do not dictate official, confidential or classified information."
The app is not usable until the notice is accepted.

## 9. Privacy

No telemetry, analytics or servers of our own. The only network calls go to the configured Gemini base URL. Logs hold timings, sizes, status codes and sanitised error shapes only.
