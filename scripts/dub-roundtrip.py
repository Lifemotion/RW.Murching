#!/usr/bin/env python3
"""
Objective sanity check for a dub: transcribe the dubbed video with murch (forcing the target language) and compare
the recognised text with the subtitles that were voiced. A low WER means the synthetic speech is intelligible and
landed roughly where the subtitles say.

    python scripts/dub-roundtrip.py out/x.dub.ru.mp4 --subs out/x.ru.srt [--murch path\\to\\murch.exe]
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

WORD_RE = re.compile(r"[\w']+", re.UNICODE)


def normalise(text: str) -> list[str]:
    return [w.lower().replace("ё", "е") for w in WORD_RE.findall(text)]


def wer(ref: list[str], hyp: list[str]) -> float:
    n, m = len(ref), len(hyp)
    if n == 0:
        return 0.0 if m == 0 else 1.0
    prev = list(range(m + 1))
    for i in range(1, n + 1):
        cur = [i] + [0] * m
        for j in range(1, m + 1):
            cur[j] = prev[j - 1] if ref[i - 1] == hyp[j - 1] else 1 + min(prev[j - 1], prev[j], cur[j - 1])
        prev = cur
    return prev[m] / n


def parse_srt(path: Path) -> list[tuple[float, float, str]]:
    cues = []
    block: list[str] = []
    for line in path.read_text(encoding="utf-8").splitlines() + [""]:
        if line.strip():
            block.append(line)
            continue
        if len(block) >= 2:
            m = re.match(r"(\d+):(\d+):(\d+)[,.](\d+) --> (\d+):(\d+):(\d+)[,.](\d+)", block[1])
            if m:
                t = [int(x) for x in m.groups()]
                start = t[0] * 3600 + t[1] * 60 + t[2] + t[3] / 1000
                end = t[4] * 3600 + t[5] * 60 + t[6] + t[7] / 1000
                cues.append((start, end, " ".join(block[2:])))
        block = []
    return cues


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("video")
    ap.add_argument("--subs", required=True, help="the translated .srt that was voiced")
    ap.add_argument("--language", default=None, help="target language (default: from the srt name, e.g. x.ru.srt)")
    ap.add_argument("--murch", default=None)
    args = ap.parse_args()

    repo = Path(__file__).resolve().parent.parent
    murch = args.murch or str(repo / "src/Bwl.Murching.Cli/bin/Debug/net10.0/win-x64/murch.exe")
    subs = Path(args.subs)
    lang = args.language or subs.stem.rsplit(".", 1)[-1]

    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([murch, "subs", args.video, "-l", lang, "--json", "-o", tmp + os.sep, "-q", "--preview", "0", "--no-music-fallback"], check=True)
        transcript = json.loads(Path(glob.glob(os.path.join(tmp, "*.murch.json"))[0]).read_text(encoding="utf-8"))

    ref_cues = parse_srt(subs)
    ref = normalise(" ".join(t for _, _, t in ref_cues))
    hyp = normalise(" ".join(s["text"] for s in transcript["segments"]))
    print(f"voiced words: {len(ref)}   recognised words: {len(hyp)}   WER: {wer(ref, hyp):.1%}")

    # Timing: for each subtitle cue find recognised words that overlap in time and report how much speech landed inside.
    words = [(w["start"], w["end"], w["text"]) for s in transcript["segments"] for w in s.get("words", [])]
    inside = 0
    for start, end, _ in ref_cues:
        inside += sum(1 for ws, we, _ in words if ws >= start - 0.3 and we <= end + 1.5)
    print(f"recognised words inside their subtitle slot (±0.3 s / +1.5 s tail): {inside}/{len(words)} = {inside / max(1, len(words)):.0%}")

    for (s, e, t), seg in zip(ref_cues[:8], transcript["segments"][:8]):
        print(f"  sub  [{s:6.2f}-{e:6.2f}] {t}")
        print(f"  asr  [{seg['start']:6.2f}-{seg['end']:6.2f}] {seg['text']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
