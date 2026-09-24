# Bwl.Murching

Local, offline subtitles for audio and video — and, next, local re-voicing (dubbing). C# / .NET 10, Windows first.

```
murch subs lecture.mp4                 # -> lecture.ru.srt (language auto-detected)
murch subs talk.mkv -f srt,vtt --json  # several formats + transcript with word timings
murch subs film.mp4 --embed            # soft-subs muxed into film.subbed.mp4
murch subs talk.mp4 --translate -m large-v3   # English subtitles for foreign speech (not with *turbo* models)
murch cues talk.en.murch.json --max-line-length 37   # re-layout cues from a saved transcript, no ASR
murch probe film.mp4                   # streams, duration, codecs
murch doctor                           # ffmpeg / GPU / CUDA / models check
murch setup                            # download ffmpeg, CUDA libraries, default models
murch models list | pull small | rm small
```

## How it works

```
ffmpeg ──► 16 kHz mono PCM ──► Silero VAD ──► speech chunks (≤30 s) ──► whisper.cpp (CUDA)
        │                                                               │
        └──────────────── probe (ffprobe) ──────────────────────┐        ▼
                                                               │   segments + tokens (DTW)
                                                               │        ▼
                                                               │   words ──► hallucination filter
                                                               │        ▼
                                                               │   cue builder (DP: sentences, pauses, 42×2, ≤7 s, ≤21 cps)
                                                               ▼        ▼
                                                        .srt / .vtt / .ass / .txt / .murch.json
```

* **ASR**: [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp) with the `large-v3-turbo` GGML model by default; CUDA 13 build on NVIDIA GPUs, CPU fallback.
* **VAD**: Silero v5 through whisper.cpp — the model only ever sees speech, which kills most "Thanks for watching" hallucinations, and long silences never reach the decoder.
* **Music fallback**: Silero ignores singing, rap over a beat and vocoded voices. Energetic gaps between speech chunks (RMS above −40 dBFS, ≥3 s) are transcribed anyway as *tentative* chunks and pass a strict filter (≥3 words, mean token probability ≥0.75, no repeated lines, no n-gram loops, no stock phrases like "I'm going to go"). Each tentative chunk is first run through Whisper's language detection and dropped unless it agrees with the file language (p ≥ 0.5): chanting, foreign choruses and pure music yield random low-probability languages, while real lyrics come back in the right language. Disable with `--no-music-fallback`.
* **Word timing**: token timestamps with DTW alignment (per-model alignment heads); words are re-assembled from byte-level BPE tokens with the segment text as ground truth (Cyrillic safe).
* **Cue building**: minimum-cost segmentation over words — prefers sentence ends, clause punctuation, Whisper segment ends and pauses; balanced two-line wrapping that breaks at punctuation and never strands an article/preposition; reading-speed aware display times with a minimum gap between cues.
* **ffmpeg** is downloaded on demand (BtbN portable build) if not on `PATH`.

## Requirements

* Windows 11 x64, .NET 10 SDK.
* NVIDIA GPU + recent driver (CUDA 13) for GPU inference. `murch setup --cuda` fetches the redistributable `cudart64_13.dll`, `cublas64_13.dll`, `cublasLt64_13.dll` into `%LOCALAPPDATA%\Bwl.Murching\cuda`; without them whisper.cpp runs on the CPU.
* Everything else (ffmpeg, models) is fetched by `murch setup`.

## Layout

```
src/Bwl.Murching.Core   library: Media (ffmpeg), Audio, Vad, Asr (Whisper), Subtitles (cues, writers), Pipeline, Models, Runtime
src/Bwl.Murching.Cli    `murch` command line (System.CommandLine + Spectre.Console)
tests/                  xunit
tools/                  dev-only: portable ffmpeg and CUDA DLLs (git-ignored)
samples/                test media (git-ignored, see samples/README.md)
```

Data lives in `%LOCALAPPDATA%\Bwl.Murching` (`models/`, `cuda/`, `ffmpeg/`); override with `MURCH_HOME`.

## Performance (RTX 3060 12 GB, Ryzen 9 5900X)

| Input | Speech | Wall time | Speed |
|-------|--------|-----------|-------|
| JFK Rice speech, 23:47 | 17:32 in 53 chunks | 41 s | 35× real time |
| Sintel trailer, 52 s | 8 s in 4 chunks | 3.9 s (1.4 s model load) | 13× |

Default settings: `large-v3-turbo`, beam 5, DTW word alignment on, VAD on.

Notes:
* `large-v3-turbo` was distilled for transcription and mostly ignores `--translate`; pick `large-v3` or `medium` for Whisper's built-in translation to English.
* `--json` writes `<name>.<lang>.murch.json` with segments and word timings; `murch cues` rebuilds subtitles from it in milliseconds, which is the fast way to tune `--max-line-length`, `--cps`, `--pause-split`.
* Music-heavy material: try `--vad-threshold 0.35` (default 0.5) so quiet speech over music is not skipped.

## Verifying against a cloud reference

`scripts/verify-elevenlabs.py` uploads the extracted audio to ElevenLabs Scribe, caches the response as `<name>.eleven.json` and prints WER plus median word-onset offset between the two transcripts (`set ELEVENLABS_API_KEY=...` first; the key never goes into the repo). On a mixed bag of 13 clips (documentary, game dialogue, Twitter rants, rap, Russian songs) clean speech came out at 2–3.5% WER against Scribe with a ~70 ms onset offset; music-mixed clips were where the VAD-only pipeline lost words, which is what the music fallback addresses.

## Roadmap

1. ✅ Subtitles (this).
2. Dubbing: transcript → local translation (Ollama) → local TTS (sherpa-onnx: Piper / Kokoro / Matcha voices) → fit to timing → mix over the original track with ducking → mux.
3. Speaker diarization (sherpa-onnx) for multi-voice dubbing and speaker-coloured subtitles.
