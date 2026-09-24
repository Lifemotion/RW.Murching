#!/usr/bin/env python3
"""
TTS sidecar for murch dubbing. Long-lived process: loads a voice-cloning model once, then serves requests as
JSON lines on stdin/stdout.

    python tts_worker.py --engine xtts --device cuda

Protocol (one JSON object per line):
  -> {"event": "ready", "engine": "xtts", "sample_rate": 24000}            once the model is loaded
  <- {"id": "1", "text": "...", "language": "ru", "reference": "ref.wav", "output": "out.wav", "speed": 1.0}
  -> {"id": "1", "ok": true, "duration": 2.34, "sample_rate": 24000}
  -> {"id": "1", "ok": false, "error": "..."}
  <- {"cmd": "quit"}

All diagnostics go to stderr; stdout carries only protocol lines.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time
import traceback
import wave

os.environ.setdefault("COQUI_TOS_AGREED", "1")  # XTTS-v2 is CPML licensed: non-commercial use, see coqui.ai/cpml


def log(msg: str) -> None:
    print(f"[tts_worker] {msg}", file=sys.stderr, flush=True)


def emit(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def wav_duration(path: str) -> float:
    with wave.open(path, "rb") as w:
        return w.getnframes() / float(w.getframerate())


class XttsEngine:
    """Coqui XTTS-v2 through the coqui-tts package. Per-request speaker reference -> per-segment expression."""

    name = "xtts"
    sample_rate = 24000

    def __init__(self, device: str):
        import torch
        from TTS.api import TTS

        t0 = time.time()
        self.device = device if (device != "cuda" or torch.cuda.is_available()) else "cpu"
        if self.device != device:
            log("CUDA not available, falling back to CPU (slow)")
        self.tts = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to(self.device)
        self.model = self.tts.synthesizer.tts_model
        self._latents: dict[str, tuple] = {}
        log(f"XTTS-v2 loaded on {self.device} in {time.time() - t0:.1f}s")

    def _conditioning(self, reference: str):
        key = os.path.abspath(reference)
        if key not in self._latents:
            gpt_cond_latent, speaker_embedding = self.model.get_conditioning_latents(
                audio_path=[reference], gpt_cond_len=30, gpt_cond_chunk_len=6, max_ref_length=30
            )
            if len(self._latents) > 64:
                self._latents.clear()
            self._latents[key] = (gpt_cond_latent, speaker_embedding)
        return self._latents[key]

    def synthesize(self, text: str, language: str, reference: str, output: str, speed: float) -> float:
        import torch
        import torchaudio

        gpt_cond_latent, speaker_embedding = self._conditioning(reference)
        lang = language.split("-")[0].lower()
        if lang == "zh":
            lang = "zh-cn"
        out = self.model.inference(
            text,
            lang,
            gpt_cond_latent,
            speaker_embedding,
            speed=max(0.7, min(1.6, speed)),
            temperature=0.65,
            length_penalty=1.0,
            repetition_penalty=5.0,
            top_k=50,
            top_p=0.85,
            enable_text_splitting=True,
        )
        wav = torch.tensor(out["wav"]).unsqueeze(0)
        torchaudio.save(output, wav, self.sample_rate)
        return wav.shape[1] / self.sample_rate


class ChatterboxEngine:
    """Resemble AI Chatterbox Multilingual (MIT): zero-shot cloning, 23 languages, exaggeration control."""

    name = "chatterbox"

    def __init__(self, device: str):
        import torch
        import torchaudio  # noqa: F401
        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

        t0 = time.time()
        self.device = device if (device != "cuda" or torch.cuda.is_available()) else "cpu"
        self.model = ChatterboxMultilingualTTS.from_pretrained(device=self.device)
        self.sample_rate = self.model.sr
        log(f"Chatterbox multilingual loaded on {self.device} in {time.time() - t0:.1f}s")

    def synthesize(self, text: str, language: str, reference: str, output: str, speed: float) -> float:
        import torchaudio

        wav = self.model.generate(text, language_id=language.split("-")[0].lower(), audio_prompt_path=reference, exaggeration=0.5, cfg_weight=0.5)
        torchaudio.save(output, wav, self.sample_rate)
        return wav.shape[-1] / self.sample_rate


ENGINES = {"xtts": XttsEngine, "chatterbox": ChatterboxEngine}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--engine", choices=sorted(ENGINES), default="xtts")
    ap.add_argument("--device", default="cuda")
    args = ap.parse_args()

    try:
        engine = ENGINES[args.engine](args.device)
    except Exception as e:  # noqa: BLE001
        emit({"event": "fatal", "error": f"{type(e).__name__}: {e}"})
        traceback.print_exc(file=sys.stderr)
        return 1

    emit({"event": "ready", "engine": engine.name, "sample_rate": engine.sample_rate, "device": engine.device})

    try:
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # noqa: BLE001
        pass

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except json.JSONDecodeError as e:
            emit({"ok": False, "error": f"bad json: {e}"})
            continue
        if req.get("cmd") == "quit":
            break
        rid = req.get("id")
        try:
            t0 = time.time()
            out_dir = os.path.dirname(os.path.abspath(req["output"]))
            os.makedirs(out_dir, exist_ok=True)
            duration = engine.synthesize(req["text"], req.get("language", "en"), req["reference"], req["output"], float(req.get("speed", 1.0)))
            emit({"id": rid, "ok": True, "duration": round(duration, 3), "sample_rate": engine.sample_rate, "elapsed": round(time.time() - t0, 2)})
        except Exception as e:  # noqa: BLE001
            traceback.print_exc(file=sys.stderr)
            emit({"id": rid, "ok": False, "error": f"{type(e).__name__}: {e}"})

    return 0


if __name__ == "__main__":
    sys.exit(main())
