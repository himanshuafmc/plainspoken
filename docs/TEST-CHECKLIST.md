# Plainspoken manual test checklist (Windows laptop, 1366×768)

Tick each box. If something fails, note what you did and attach the newest file from
`%LOCALAPPDATA%\Plainspoken\logs\` (logs never contain your words or your key).

Version tested: ______ Date: ______ Windows 10 / 11 (circle)

## A. First run

- [ ] Unzip `Plainspoken-win-x64-<version>.zip` to `Documents\Plainspoken`, double-click `Plainspoken.exe`.
- [ ] SmartScreen appears → **More info → Run anyway** works.
- [ ] Welcome window fits on screen; **Open Google AI Studio** opens the browser at the key page.
- [ ] **Finish** stays disabled until a key is pasted **and** "I understand" is ticked.
- [ ] Wrong key → **Test key** says it was not accepted. Right key → "Key works."
- [ ] After Finish, the teal speech-bubble tray icon is visible; hovering shows "press Ctrl+Alt+Space to dictate".
- [ ] Launch `Plainspoken.exe` a second time → no second tray icon; the Settings window of the running copy opens.

## B. Apps × languages (5-second dictations)

Say, for example: English — *"Um, please send the report to the team by the fifth of October."*
Hindi — *"मुझे कल सुबह दस बजे अस्पताल जाना है।"* Hinglish — *"Kal ki meeting cancel ho gayi, please inform everyone."*

| App | English | Hindi | Hinglish |
|---|---|---|---|
| Notepad | [ ] | [ ] | [ ] |
| Word | [ ] | [ ] | [ ] |
| Chrome — Gmail compose | [ ] | [ ] | [ ] |
| WhatsApp Desktop (chat box) | [ ] | [ ] | [ ] |
| Outlook (new email) | [ ] | [ ] | [ ] |

Also dictate two or three sentences with short pauses in between (e.g. *"I will call you tomorrow … morning. Please wait."*): [ ] every word is separated by a space, [ ] there is a space after each full stop.

For each: [ ] text appears at the cursor, [ ] "um" removed and punctuation added, [ ] a space is added after the text,
[ ] whatever was on the clipboard before is still there afterwards (paste with Ctrl+V somewhere to check).

## B2. Speed and live streaming

- [ ] 5-second English dictation with live streaming on (the default): time from stopping until the text appears: ______ s.
- [ ] Settings → Advanced → untick "Live streaming", Save, dictate again: ______ s. Text still appears (the normal way). Tick it again afterwards.
- [ ] Settings → Advanced → change the live model name to `no-such-model`, Save, dictate → the text still appears (fallback), just a little later. Put the name back.
- [ ] If live streaming was slower or failed: attach the newest log file (it records what Google answered, never your words).

## B3. Mini button

- [ ] After start, a small dark button with three sound bars sits just above the taskbar, bottom-centre.
- [ ] Pointing at it expands it to "Click or Ctrl+Alt+Space to dictate"; moving away shrinks it.
- [ ] Click in Notepad, then click the mini button → listening starts, and **Notepad keeps its blinking cursor**; the text is inserted there.
- [ ] Drag the button to another place → it stays there, also after restarting Plainspoken. Right-click → "Move back to the bottom centre" works.
- [ ] Full-screen YouTube video or PowerPoint slideshow → the button disappears; exit full screen → it comes back.
- [ ] Right-click → "Hide mini button" hides it; tray menu → "Show mini button" brings it back.
- [ ] While listening, the stop button is a green ✓.

## C. Recording behaviour

- [ ] Start sound plays; the "Listening" pill appears just above the taskbar, bottom-centre; timer counts; the level meter moves when you speak.
- [ ] While the pill is showing, keep typing in the app — **the app keeps focus** (the pill never takes it).
- [ ] Click the green **✓** on the pill → stops and transcribes. Click **✕** → cancels, nothing typed.
- [ ] **Esc** while listening cancels; nothing typed. After that, Esc works normally in other apps again.
- [ ] Press the hotkey while "Transcribing…" → a short "Busy" note, no second recording.
- [ ] Tap the hotkey twice quickly (under half a second) → "Didn't catch that", no text.
- [ ] Record 3 seconds of silence → "Didn't catch that".
- [ ] Left-click the tray icon to start, speak, left-click again to stop → text goes into the app you were using before.
- [ ] Tray icon colour: teal (ready), red (listening), amber (transcribing).

## D. Long dictation

- [ ] 3-minute dictation (read a newspaper paragraph aloud) → full text inserted, timer correct.
- [ ] Settings → Options → Longest recording = 1 minute. Record > 1 minute: "30 s left" appears at 0:30, recording stops by itself at 1:00 and transcribes. (Set it back to 10 afterwards.)

## E. Failures and retry

- [ ] Turn Wi-Fi off, dictate → "No internet connection… saved" message. Turn Wi-Fi on, tray → **Retry last dictation** → "Copied — press Ctrl+V"; Ctrl+V pastes the text.
- [ ] Settings → General: change the key to a wrong one, Save, dictate → message says the key was not accepted and Settings opens. Put the right key back.
- [ ] (If it happens naturally) daily limit message shows the reset time in your local time (in India, e.g. "Resets at 12:30 PM IST").
- [ ] Windows Settings → Privacy → Microphone → turn off "Let desktop apps access your microphone"; press the hotkey → clear message; clicking it opens the microphone privacy page. Turn it back on.
- [ ] Unplug a USB/Bluetooth mic while listening (if you have one) → Plainspoken stops and transcribes what it heard, no crash.

## F. Settings

- [ ] Settings window fits the screen; tabs scroll if the window is made smaller; it can be resized.
- [ ] **Change hotkey**: Change… → press Ctrl+Shift+D → Save → the new hotkey works, the old one doesn't. Try Shift+A → it refuses ("Include Ctrl, Alt or Win").
- [ ] Pick a hotkey used by another app (e.g. one you set in another tool) → clear "already used" message; the old hotkey keeps working.
- [ ] Vocabulary: add an unusual name (e.g. a friend's surname), save, dictate it → spelled as in the list.
- [ ] Languages "English only" and "Auto-detect" both work. Mode "Verbatim" keeps "um".
- [ ] Insertion "Type it out": works in Notepad; a two-line dictation doesn't send a WhatsApp message early.
- [ ] Sounds off → no sounds.
- [ ] History: tray → Recent transcripts lists recent texts; clicking one copies it. Clear history empties it. Turning history off hides new entries.
- [ ] Export settings → open the file in Notepad → **no API key inside**. Import it back → vocabulary restored.
- [ ] Microphone dropdown lists your microphones; choosing one works.

## F2. Speech services, clean-up and extras (Windows 0.5.0)

- [ ] Upgrade from 0.4.0: unzip over the old folder and start → the Gemini key, vocabulary and history are still there; Settings → General shows Gemini as the speech service and the general Gemini model as the first backup.
- [ ] Settings → General → First backup → Sarvam → **Set up…** → paste a wrong key → **Test key** says it was not accepted; the right key → "Key works". OK → Save. The line under the services says every chosen service has a key.
- [ ] Repeat for each service you use (OpenAI-compatible, Deepgram, ElevenLabs). For Groq, set the address to `https://api.groq.com/openai/v1` and the model to `whisper-large-v3-turbo`.
- [ ] Make each service the main one in turn and dictate English, Hindi and Hinglish (5–10 s, with an "um" and a self-correction like "at 5, no, at 6") into Notepad: fillers gone, only the correction kept, punctuation right, Hinglish not translated. Time from stop to text: Sarvam ____ s, OpenAI ____ s, Deepgram ____ s, ElevenLabs ____ s.
- [ ] Sarvam and Deepgram with **Stream while you speak** on and off (Set up…): compare the time to text. With Sarvam, on is no slower than off, and a long dictation with pauses is quicker; the log doesn't say "live: no text returned; using the normal engine".
- [ ] Hindi words in: Roman letters vs Devanagari → the text follows it (with clean-up on).
- [ ] Clean-up tab → **Off**: raw text from Sarvam/Deepgram with only "um"/"uh" removed. Back to Gemini Flash.
- [ ] Clean-up → OpenAI-compatible with a chat key → Test key → dictate with Sarvam → tidy text. Back to Gemini Flash.
- [ ] Give the main service a wrong key and keep a working backup: dictation still works (backup used); after two dictations the main one is skipped (the log says "paused"). Fix the key → the main service is used again at once.
- [ ] Unplug the network cable / turn Wi-Fi off right after you stop (or pick a slow service as main): with "Speed" at 4 seconds the pill says "Still working — also asking …" and the first answer is used.
- [ ] Long dictation with Sarvam as the main service (over 1 minute) → the whole text arrives.
- [ ] **Hold to talk:** in Notepad, hold Right Ctrl, speak, let go → text inserted. A quick tap of Right Ctrl does nothing. Right Ctrl+C / Right Ctrl+V still copy and paste and never start a recording. Start with Ctrl+Alt+Space, then tap and hold Right Ctrl → the recording isn't stopped by it. Settings → Hold to talk → Right Alt / Off work as described.
- [ ] Hold to talk in Word, a browser text box and WhatsApp desktop: the paste after release works (Ctrl isn't stuck).
- [ ] Snippets tab: add "insert my address" → saying just that types the address; inside a sentence only that part is replaced.
- [ ] **Learning:** dictate "Please call Samir tomorrow" into Notepad, change "Samir" to "Sameer" by hand, then dictate again in the same window → after that dictation the green pill offers "Add “Sameer” to your vocabulary?" → click → it's in Settings → Vocabulary. In another app, nothing is offered. With "Offer to add words I correct" off, nothing is offered.
- [ ] Learning in Word, a browser (Gmail) and WhatsApp desktop: note which apps offer corrections: ________.
- [ ] Tray → **Speed and cost…**: each service shows dictations, wait times and, after entering a price under Set up…, this month's cost. Clear empties it.
- [ ] The log shows "compress: … → … bytes" for Sarvam/OpenAI/Deepgram/ElevenLabs (smaller uploads) and no errors about the encoder.
- [ ] With streaming off, wait 3 s before speaking and 3 s after → the log says "silence trimmed: …s sent", and the first and last words are all there.
- [ ] Export settings: the file has the service order, snippets and clean-up settings, **and no keys**. Import it on the phone → the same services and snippets.
- [ ] First run on a fresh Windows user: tick the notice → **Use another service…** → Settings opens; set up Sarvam as the main service → dictation works without a Gemini key.

## G. Start with Windows

- [ ] Settings → Options → tick "Start Plainspoken when Windows starts" → Save → restart Windows → Plainspoken's tray icon appears after login and the hotkey works.
- [ ] Untick it → restart → Plainspoken does not start.

## H. Display scaling

- [ ] Windows Settings → Display → Scale **125 %**. Restart Plainspoken. The pill is sharp, sized properly and sits above the taskbar; Settings and Welcome windows fit on screen and nothing is cut off.
- [ ] Back to 100 %: same checks.
- [ ] (If you have a second monitor) the pill appears on the monitor of the app you're dictating into.

## I. Exit

- [ ] Tray → Exit: the icon disappears; Ctrl+Alt+Space no longer does anything; the microphone privacy indicator (if shown) goes away.

---

# Android checklist (phone)

Phone and Android version: ______________ Plainspoken version: ______ Date: ______

When something fails, attach the log: Plainspoken app → Settings → **Export the log** (never contains your words or your key).

## J. Install and set-up

- [ ] Download `Plainspoken-android-<version>.apk` from the Releases page on the phone and open it. Android asks to allow installs from the browser/file manager → allow → **Install** (if Play Protect warns, **Install anyway**).
- [ ] Open Plainspoken: the checklist shows 6 steps. **Open Google AI Studio** opens the key page.
- [ ] Paste a wrong key → **Test and save** says it was not accepted. Right key → "Key works" and step 2 ticks.
- [ ] Notice: **Continue** refuses until "I understand" is ticked.
- [ ] **Allow microphone** → Android's permission dialog → Allow → step ticks.
- [ ] **Open keyboard settings** → switch on Plainspoken (Android's standard warning appears) → back → step ticks.
- [ ] **Choose keyboard** → pick Plainspoken → "You're all set ✓".

## K. Dictation in apps (5-second dictations, English / Hindi / Hinglish)

| App | English | Hindi | Hinglish |
|---|---|---|---|
| WhatsApp | [ ] | [ ] | [ ] |
| Gmail (compose) | [ ] | [ ] | [ ] |
| Chrome (search box) | [ ] | [ ] | [ ] |
| Notes / Keep | [ ] | [ ] | [ ] |

For each: [ ] tap mic → it turns into a green ✓ with a moving halo and "Listening 0:03", [ ] tap ✓ → "Transcribing…" → text appears in the box, [ ] "um" removed and punctuation added, [ ] words are separated by spaces and there is a space after full stops, [ ] a second dictation right after the first starts with a space.

## L. Keyboard behaviour

- [ ] ✕ while listening → "Cancelled", nothing inserted. ✕ while transcribing → "Stopped — recording saved", **Retry** appears; Retry inserts the text.
- [ ] Tap the mic and stay silent for 3 s, then ✓ → "Didn't catch that".
- [ ] Space, comma, full stop work; delete deletes one character and repeats while held; enter sends in WhatsApp (or adds a new line where there is no action).
- [ ] 🌐 returns to the previous keyboard; long-press 🌐 shows the keyboard list.
- [ ] ⚙ opens the Plainspoken app.
- [ ] In a password box the mic is grey and says voice typing is off.
- [ ] Press Home while listening → the recording is transcribed and the text copied ("Copied"); paste it back.
- [ ] Rotate the phone while idle and while listening → keyboard still works.
- [ ] Dark mode on/off → keyboard and app are readable in both.

## M. Speed, failures and limits

- [ ] 5-second dictation: time from ✓ to text: ______ s (live streaming on). Settings → Advanced → turn live streaming off → ______ s. Turn it back on.
- [ ] Airplane mode, dictate → "No internet connection… saved", **Retry** key appears. Turn airplane mode off → Retry → text inserted.
- [ ] Settings → remove the key, dictate → message with **Open app**.
- [ ] Revoke the microphone permission in Android settings, tap the mic → message with **Allow**, which opens the app and asks again.
- [ ] Long dictation (2 minutes) → full text inserted.

## N. App screens

- [ ] Recent transcripts: newest first; tapping one copies it; **Clear history** empties it. Turning history off in Settings clears it and adds nothing new.
- [ ] Settings: language, style, vocabulary (Save words), options and advanced changes are kept after closing and reopening the app.
- [ ] Export settings → open the file → **no key inside**. Import a file exported from Plainspoken for Windows → vocabulary and options match.
- [ ] Export the log → the file contains timings and error codes, not your words.
- [ ] Uninstall and reinstall → the key has to be entered again (it was encrypted with a key tied to the old install).

## O. Speech services, clean-up and extras (0.4.0)

- [ ] Settings → Speech services → **Add a backup service** → pick Sarvam → its screen opens. Paste a wrong key → **Test and save** says it was not accepted; the right key → "Key works".
- [ ] Repeat for each service you use (OpenAI-compatible, Deepgram, ElevenLabs). For Groq, set the address to `https://api.groq.com/openai/v1` and the model to `whisper-large-v3-turbo`.
- [ ] **Move up** makes a service the main one; **Remove** takes it out. The keyboard uses the new main service on the next dictation.
- [ ] With each service as the main one, dictate English, Hindi and Hinglish (5–10 s, with an "um" and a self-correction like "at 5, no, at 6"): text appears, fillers gone, only the correction kept, punctuation right, Hinglish not translated. Time from ✓ to text: Sarvam ____ s, OpenAI ____ s, Deepgram ____ s, ElevenLabs ____ s.
- [ ] Sarvam and Deepgram with **Stream while you speak** on and off: compare the time to text. With Sarvam, on is no slower than off, and a long dictation with pauses is quicker; the log doesn't say "live: no text returned; using the normal engine".
- [ ] Hindi words setting: Roman letters vs Devanagari → the text follows it (with clean-up on).
- [ ] Clean-up → **Off**: raw text from Sarvam/Deepgram, with only "um"/"uh" removed. Back to Gemini.
- [ ] Give the main service a wrong key and keep a working backup: dictation still works (backup used); after two dictations the main one is skipped (the log says "paused").
- [ ] Turn on airplane mode for 2 seconds right after tapping ✓ (or pick a slow service as main): with "Speed" at 4 seconds the backup is asked too and the first answer is used.
- [ ] Long dictation with Sarvam as the main service (over 1 minute) → the whole text arrives (Sarvam takes 30 s per request; the app splits).
- [ ] Hold the mic, speak, let go → text inserted. A quick tap still starts listening until ✓.
- [ ] Snippets: add "insert my address" → saying just that types the address; saying it inside a sentence replaces only that part.
- [ ] Dictate a name that comes out wrong, fix it with your usual keyboard, switch back to Plainspoken → "Add “…” to your vocabulary?" → **Add** → the word is in Settings → Vocabulary.
- [ ] Settings → Speed and cost: each service shows dictations, typical time to text and, after entering a price on its screen, this month's cost.
- [ ] Export settings: the file has the service order, snippets and clean-up settings, **and no keys**.

## Q. Offline fallback (0.6.0)

Windows (Settings → Offline) and Android (Settings → Offline fallback) — tick each for both.

- [ ] Download the default model (Windows Medium, Android Small) on Wi-Fi → progress climbs, then "Downloaded — ready to use". Close Settings halfway on Android → the download carries on; on Windows, Cancel → "Download stopped"; Download again → it resumes, not from zero.
- [ ] **No internet** (Wi-Fi and mobile data off / airplane mode): dictate a short English sentence → text goes in, "Typed offline" shows, History/Recent marks it "offline". Note how long it took: Windows ____ s, Android ____ s.
- [ ] Dictate a Hinglish sentence offline → note the script (Roman / Devanagari) and accuracy: ________.
- [ ] **Wrong key** (Settings → set up the main service with a wrong key, no backup): dictate → offline text goes in. Put the right key back.
- [ ] **No key at all** (fresh install, nothing set up, model downloaded): dictation starts and works offline; without the model, the app asks for a key as before.
- [ ] Normal use with internet → never "Typed offline"; the log has no "offline:" lines.
- [ ] Switch offline off → with no internet you get the usual error and "Retry" again.
- [ ] Delete the model → space is freed (Windows: `%LOCALAPPDATA%\Plainspoken\models`; Android: Settings shows "Not downloaded yet").
- [ ] Windows: after unzipping, the folder has `runtimes\win-x64\whisper.dll` next to `Plainspoken.exe`; the log after an offline dictation says "offline: medium took … ms".
- [ ] Android: the log after an offline dictation says "offline: small took … ms"; the phone doesn't get hot or crash. Note the model and Android version if it says "Not available on this phone".

## P. Floating mic (0.5.0)

- [ ] **Standard APK** (`Plainspoken-android-0.5.0.apk`): download it on the phone and install over 0.4.0 → it installs (no "App not installed"). The checklist has no floating-mic step; Settings → Floating mic says it's in the floating-mic edition. The keyboard works as in 0.4.0.
- [ ] **Floating-mic APK**, following README → Floating mic (Android): Play Store → Play Protect → ⚙ → scanning off → install the APK from the phone → scanning back on. (Or over USB: `adb devices` lists the phone → `adb install -r …-floating-mic.apk` says Success.) The app keeps the key, settings and history.
- [ ] Open Plainspoken → the checklist shows "Turn on the floating mic". Open Accessibility settings → Plainspoken floating mic → On (Android 13+ after a phone install: App info → ⋮ → Allow restricted settings first). The checklist ticks it off.
- [ ] With Gboard (or Samsung Keyboard) as your keyboard, tap a text box in WhatsApp → the mic appears at the right, above the keyboard. Close the keyboard → it disappears. Tap the box again → it's back. In a password box → no mic.
- [ ] Tap the message box in the **ChatGPT** and **Claude** apps → the mic appears (these apps are built differently from WhatsApp); dictate → the text goes in once, not twice, with the cursor after it.
- [ ] **Tap** the mic → it turns green ✓ with a level halo, a short vibration; speak → **tap** again → spinner → text appears at the cursor in WhatsApp, without the grey "Message" placeholder in front of it; the cursor is after the text. Dictate again → the two join with a space.
- [ ] **Hold** the mic, speak, let go → text inserted. Hold for under half a second → it keeps listening until tapped.
- [ ] **Drag** the mic up and to the left edge → it snaps to the left; open another app's text box → it's on the left at the same height above the keyboard. Settings → Floating mic → Move it back → right side again. A drag never leaves a recording running.
- [ ] Text goes in correctly in: WhatsApp ☐, Gmail compose ☐, Chrome (a website's text box) ☐, Google Keep / Notes ☐, Messages ☐, Instagram/LinkedIn ☐, ChatGPT ☐, Claude ☐. Note any app where it was copied instead: ________.
- [ ] Type a few words, put the cursor in the middle, dictate → the text goes in at the cursor, nothing else changes.
- [ ] Dictate "Please call Samir tomorrow", change "Samir" to "Sameer" with Gboard, then dictate again in the same chat → after it, the label beside the mic offers "Add “Sameer” to your vocabulary?" → tap → it's in Settings → Vocabulary.
- [ ] Airplane mode → dictate → the label says no internet with "Tap to retry" → turn airplane mode off → tap the label → the text goes into the box.
- [ ] Switch to the Plainspoken keyboard → the floating mic hides (the keyboard has its own mic). Switch back to Gboard → it returns.
- [ ] Settings → Floating mic → "Show the floating mic" off → it never appears; on → it's back.
- [ ] **Android 13+:** copy a word, dictate into WhatsApp with the floating mic, then paste somewhere → you get the copied word, not the dictation; the log says "floating mic: text typed". In a WhatsApp group, write *bold* text and an @mention, put the cursor after them and dictate → both stay as they were.
- [ ] Play music or a video, then dictate with the floating mic and with the keyboard → it pauses while you speak and carries on afterwards.
- [ ] With streaming off, wait 3 s before speaking and 3 s after → the log says "silence trimmed: …s sent", and the first and last words are all there.
- [ ] If the label ever says Android didn't let the floating mic use the microphone: note the phone model and Android version, export the log and report it.
- [ ] Install the standard APK from the phone over the floating-mic edition → it installs, the floating mic is gone, settings are kept.
