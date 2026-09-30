# All TTS runs in one audio.cpp container; Chatterbox Turbo is dropped

## Status

accepted (2026-09-27). Decided in the Breeze TTS map (`.scratch/completed/breeze-tts/`, tickets 08 and 09).

## Context

Each TTS model ran in its own Python container, using the vendor's reference implementation:
chatterbox, chatterbox-turbo, qwen3-tts, qwen3-tts-base and voxcpm2. Adding Breeze TTS 2 brought in
[audio.cpp](https://github.com/0xShug0/audio.cpp), a C++/ggml runtime that serves most of these
model families from one server.

A blind parity run on the RTX 3070 (ticket 08) found:

- Quality was within noise between native and audio.cpp for VoxCPM2, Chatterbox and Qwen3 Base.
- audio.cpp was **2–3.5× faster** for each of them.
- audio.cpp's Chatterbox Turbo port cannot clone a voice.

## Decision

- **One audio.cpp container** (`read2me-audiocpp`) serves every TTS and voice-design model. It keeps
  one model loaded and switches on request, like llama autoload.
- The native Python TTS services are retired.
- **Per-model provider types stay** (VoxCPM2, Chatterbox, Qwen3 Base / VoiceDesign, Breeze). You pick a
  voice model with its own settings; the runtime is infrastructure, not a choice.
- **Chatterbox Turbo is dropped.** It has no cloning in audio.cpp, and nothing in the app writes its tags.
- An **app-side TTS gate per endpoint** sends requests one at a time and waits for the target model to
  load. A 503-retry is the backstop.

## Consequences

- The gate is required. audio.cpp answers an immediate 503 when a *different* model is requested while
  one is generating, and voice design runs outside the serial audio queue.
- Switching models costs 9–31 s on the first request. VoxCPM2 and Qwen3 peak near the 8 GB VRAM limit,
  so requests stay paragraph-sized.
- audio.cpp limits reference audio to a 5 MiB inline WAV. VoxCPM2 also caps its length (15 s by
  default; the server config raises this to 30 s). References are kept within these limits where they
  are created (upload and voice design), never trimmed at request time.
- VoxCPM2 loses its `normalize` and `denoise` options, which audio.cpp does not implement.
