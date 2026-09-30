# Assets

- `plainspoken.svg` — the app icon source (original artwork, GPL-3.0 like the rest of the project).
- `plainspoken.ico`, `plainspoken-256.png` — generated from the SVG: `python3 assets/make_icon.py` (needs Chromium).
- `social-preview.svg` → `social-preview.png` (1280×640) — the picture shown when the repository link is shared. Generate it with `python3 assets/make_icon.py --social` and upload it under the repository's **Settings → General → Social preview**.
- `demo.gif` — not recorded yet. It will show one dictation from hotkey to inserted text.
