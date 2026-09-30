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

## G. Start with Windows

- [ ] Settings → Options → tick "Start Plainspoken when Windows starts" → Save → restart Windows → Plainspoken's tray icon appears after login and the hotkey works.
- [ ] Untick it → restart → Plainspoken does not start.

## H. Display scaling

- [ ] Windows Settings → Display → Scale **125 %**. Restart Plainspoken. The pill is sharp, sized properly and sits above the taskbar; Settings and Welcome windows fit on screen and nothing is cut off.
- [ ] Back to 100 %: same checks.
- [ ] (If you have a second monitor) the pill appears on the monitor of the app you're dictating into.

## I. Exit

- [ ] Tray → Exit: the icon disappears; Ctrl+Alt+Space no longer does anything; the microphone privacy indicator (if shown) goes away.
