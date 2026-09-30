#!/usr/bin/env bash
# Makes the synthetic test clips used by the live smoke test. Needs espeak-ng.
# The clips are generated, never recorded, and *.wav is git-ignored: no real voices in this repo.
#
#   ./make-clips.sh [output-dir]      (default: ./clips)
#   dotnet run --project windows/tools/Plainspoken.Smoke -- --engine both clips/*.wav
set -euo pipefail
out="${1:-clips}"
mkdir -p "$out"
espeak-ng -v en-us -s 150 -w "$out/en-shopping.wav" \
  "Um, so, please add milk, eggs and, uh, bread to the shopping list. The total is two thousand five hundred rupees."
espeak-ng -v hi -s 140 -w "$out/hinglish-meeting.wav" \
  "कल सुबह दस बजे meeting है, please send the WhatsApp message to everyone."
ls -l "$out"
