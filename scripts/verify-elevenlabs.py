#!/usr/bin/env python3
"""
Compare murch transcripts against ElevenLabs Scribe (cloud STT) as an independent reference.

    set ELEVENLABS_API_KEY=...               (never put the key in the repo)
    python scripts/verify-elevenlabs.py out/shelest/*.murch.json --media-dir samples/shelest

For every <name>.<lang>.murch.json the script
  1. finds the media file <name>.* in --media-dir,
  2. extracts 16 kHz mono MP3 with ffmpeg (small upload),
  3. calls POST https://api.elevenlabs.io/v1/speech-to-text (model scribe_v1, word timestamps),
     caching the JSON response next to the transcript as <name>.eleven.json,
  4. reports WER of murch vs. Scribe (both normalised: lower-case, punctuation stripped)
     and the median absolute word-onset offset for words matched by the alignment.

Scribe is not ground truth either, so treat disagreements as places to listen to, not as errors.
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
import urllib.error
import urllib.request
import uuid
from difflib import SequenceMatcher
from pathlib import Path

API_URL = "https://api.elevenlabs.io/v1/speech-to-text"
WORD_RE = re.compile(r"[\w']+", re.UNICODE)


def find_ffmpeg(repo: Path) -> str:
    for cand in [repo / "tools/ffmpeg/bin/ffmpeg.exe", Path(os.environ.get("LOCALAPPDATA", "")) / "Bwl.Murching/ffmpeg/bin/ffmpeg.exe"]:
        if cand.exists():
            return str(cand)
    return "ffmpeg"


def extract_mp3(ffmpeg: str, media: Path, target: Path) -> None:
    if target.exists():
        return
    subprocess.run([ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", str(media), "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", str(target)], check=True)


def scribe(api_key: str, audio: Path, language: str | None) -> dict:
    boundary = uuid.uuid4().hex
    parts: list[bytes] = []

    def field(name: str, value: str) -> None:
        parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())

    field("model_id", "scribe_v1")
    field("timestamps_granularity", "word")
    field("diarize", "false")
    field("tag_audio_events", "false")
    if language and language not in ("auto", "und"):
        field("language_code", language)
    parts.append(f"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{audio.name}\"\r\nContent-Type: audio/mpeg\r\n\r\n".encode())
    parts.append(audio.read_bytes())
    parts.append(f"\r\n--{boundary}--\r\n".encode())
    body = b"".join(parts)

    req = urllib.request.Request(API_URL, data=body, method="POST")
    req.add_header("xi-api-key", api_key)
    req.add_header("Content-Type", f"multipart/form-data; boundary={boundary}")
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise SystemExit(f"ElevenLabs HTTP {e.code}: {e.read().decode('utf-8', 'replace')[:500]}")


def normalise(text: str) -> list[str]:
    text = text.replace("ё", "е").replace("Ё", "Е")
    return [w.lower() for w in WORD_RE.findall(text)]


def wer(ref: list[str], hyp: list[str]) -> tuple[float, int, int, int]:
    """Levenshtein on word lists -> (wer, substitutions, deletions, insertions)."""
    n, m = len(ref), len(hyp)
    if n == 0:
        return (0.0 if m == 0 else 1.0, 0, 0, m)
    prev = list(range(m + 1))
    ops_prev = [(0, 0, j) for j in range(m + 1)]
    for i in range(1, n + 1):
        cur = [i] + [0] * m
        ops_cur = [(0, i, 0)] + [(0, 0, 0)] * m
        for j in range(1, m + 1):
            if ref[i - 1] == hyp[j - 1]:
                cur[j] = prev[j - 1]
                ops_cur[j] = ops_prev[j - 1]
            else:
                sub, dele, ins = prev[j - 1] + 1, prev[j] + 1, cur[j - 1] + 1
                best = min(sub, dele, ins)
                cur[j] = best
                if best == sub:
                    s, d, k = ops_prev[j - 1]
                    ops_cur[j] = (s + 1, d, k)
                elif best == dele:
                    s, d, k = ops_prev[j]
                    ops_cur[j] = (s, d + 1, k)
                else:
                    s, d, k = ops_cur[j - 1]
                    ops_cur[j] = (s, d, k + 1)
        prev, ops_prev = cur, ops_cur
    s, d, k = ops_prev[m]
    return (prev[m] / n, s, d, k)


def timing_offsets(murch_words: list[dict], eleven_words: list[dict]) -> list[float]:
    """Onset differences (murch - eleven) for words matched by a sequence alignment on normalised text."""
    mw = [(normalise(w["text"]), w["start"]) for w in murch_words]
    ew = [(normalise(w["text"]), w["start"]) for w in eleven_words if w.get("type", "word") == "word"]
    mw = [(t[0], s) for t, s in mw if t]
    ew = [(t[0], s) for t, s in ew if t]
    sm = SequenceMatcher(a=[t for t, _ in mw], b=[t for t, _ in ew], autojunk=False)
    offsets = []
    for a, b, size in sm.get_matching_blocks():
        for k in range(size):
            offsets.append(mw[a + k][1] - ew[b + k][1])
    return offsets


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("transcripts", nargs="+", help="murch transcript JSON files (globs ok)")
    ap.add_argument("--media-dir", required=True, help="folder with the source media files")
    ap.add_argument("--no-upload", action="store_true", help="only use cached ElevenLabs responses")
    ap.add_argument("--show-diff", type=int, default=0, help="print up to N differing regions per file")
    args = ap.parse_args()

    api_key = os.environ.get("ELEVENLABS_API_KEY")
    if not api_key and not args.no_upload:
        print("ELEVENLABS_API_KEY is not set", file=sys.stderr)
        return 2

    repo = Path(__file__).resolve().parent.parent
    ffmpeg = find_ffmpeg(repo)
    media_dir = Path(args.media_dir)
    files = [Path(p) for pattern in args.transcripts for p in glob.glob(pattern)]
    if not files:
        print("no transcripts matched", file=sys.stderr)
        return 2

    rows = []
    for tpath in sorted(files):
        murch = json.loads(tpath.read_text(encoding="utf-8"))
        stem = tpath.name[: -len(".murch.json")]
        base = stem.rsplit(".", 1)[0] if "." in stem else stem  # strip .<lang>
        candidates = [p for p in media_dir.iterdir() if p.stem == base]
        if not candidates:
            print(f"!! media for {tpath.name} not found in {media_dir}")
            continue
        media = candidates[0]
        cache = tpath.with_name(base + ".eleven.json")
        if cache.exists():
            eleven = json.loads(cache.read_text(encoding="utf-8"))
        elif args.no_upload:
            print(f"!! no cached ElevenLabs result for {base}")
            continue
        else:
            mp3 = tpath.with_name(base + ".16k.mp3")
            extract_mp3(ffmpeg, media, mp3)
            print(f".. uploading {base} ({mp3.stat().st_size / 1e6:.1f} MB)")
            eleven = scribe(api_key, mp3, murch.get("language"))
            cache.write_text(json.dumps(eleven, ensure_ascii=False, indent=1), encoding="utf-8")

        murch_text = " ".join(s["text"] for s in murch["segments"])
        ref = normalise(eleven.get("text", ""))
        hyp = normalise(murch_text)
        w, s, d, i = wer(ref, hyp)
        murch_words = [wd for seg in murch["segments"] for wd in seg.get("words", [])]
        offs = timing_offsets(murch_words, eleven.get("words", []))
        med = statistics.median([abs(o) for o in offs]) if offs else float("nan")
        bias = statistics.median(offs) if offs else float("nan")
        rows.append((base, murch.get("language"), eleven.get("language_code"), len(ref), len(hyp), w, s, d, i, med, bias, len(offs)))

        if args.show_diff:
            sm = SequenceMatcher(a=ref, b=hyp, autojunk=False)
            shown = 0
            for tag, a0, a1, b0, b1 in sm.get_opcodes():
                if tag == "equal":
                    continue
                print(f"   {tag:8} eleven: {' '.join(ref[a0:a1])!r:60.60}  murch: {' '.join(hyp[b0:b1])!r}")
                shown += 1
                if shown >= args.show_diff:
                    break

    print()
    print(f"{'file':50} {'murch':5} {'11labs':6} {'ref':>5} {'hyp':>5} {'WER':>6} {'sub':>4} {'del':>4} {'ins':>4} {'|dt|med':>8} {'bias':>7} {'matched':>7}")
    for r in rows:
        base, ml, el, nref, nhyp, w, s, d, i, med, bias, n = r
        print(f"{base[:50]:50} {str(ml):5} {str(el):6} {nref:5d} {nhyp:5d} {w:6.1%} {s:4d} {d:4d} {i:4d} {med:7.2f}s {bias:+6.2f}s {n:7d}")
    if rows:
        total_ref = sum(r[3] for r in rows)
        weighted = sum(r[5] * r[3] for r in rows) / total_ref if total_ref else 0
        print(f"\nweighted WER over {len(rows)} files: {weighted:.1%}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
