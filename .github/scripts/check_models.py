"""Checks the offline models' pinned sizes and SHA-256s against whisper.cpp's file list on Hugging Face.

Run by CI. Prints what Hugging Face lists for each model, and fails if a pinned value differs or is missing.
"""
import json
import re
import sys
import urllib.request

LISTING = "https://huggingface.co/api/models/ggerganov/whisper.cpp/tree/main"
SOURCES = {
    "Android": "android/core/src/main/kotlin/app/plainspoken/core/offline/Offline.kt",
    "Windows": "windows/src/Plainspoken.Core/Offline/OfflineModels.cs",
}
# OfflineModel("id", "file", "label", size, "sha256") — the same shape in Kotlin and C#.
PINNED = re.compile(r'OfflineModel\(\s*"([^"]+)",\s*"([^"]+)",\s*"[^"]*",\s*([0-9_]+)L?,\s*"([0-9a-f]*)"')


def main() -> int:
    with urllib.request.urlopen(LISTING, timeout=60) as response:
        listed = {f["path"]: f for f in json.load(response) if f.get("type") == "file"}

    failed = False
    for platform, path in SOURCES.items():
        try:
            text = open(path, encoding="utf-8").read()
        except FileNotFoundError:
            print(f"{platform}: {path} not found, skipped")
            continue

        for model_id, file, size, sha in PINNED.findall(text):
            entry = listed.get(file)
            if entry is None or "lfs" not in entry:
                print(f"::error::{platform} {model_id}: {file} is not on Hugging Face")
                failed = True
                continue

            real_size, real_sha = entry["lfs"]["size"], entry["lfs"]["oid"]
            print(f"{platform} {model_id}: {file} size={real_size} sha256={real_sha}")
            if int(size.replace("_", "")) != real_size or sha != real_sha:
                print(f"::error::{platform} {model_id}: pinned size/sha256 differ from Hugging Face")
                failed = True

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
