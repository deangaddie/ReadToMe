# Setup check sample clip

`sample-clip.wav` speaks the exact text in `sample-clip.txt`. The Full check uses it as the
transcription input, the similarity check's known text and the paragraph TTS reference audio.
Both files are embedded in the host assembly.

## Provenance and license

- The text is the opening sentence of Jane Austen's *Pride and Prejudice*, Project Gutenberg
  eBook #1342 (<https://www.gutenberg.org/ebooks/1342>), public domain.
- Made locally on 2026-10-30 with the audio.cpp container (`Infra/audiocpp/server.json`), in
  two steps, both on Apache-2.0 Qwen3-TTS models (`Infra/README.md`, "Licences"; not Breeze,
  whose licence covers its output):
  1. **Voice design** with `qwen3-design`: the instruction "A clear, warm adult female narrator
     with a neutral British accent, reading a novel aloud at a calm, even pace, close to the
     microphone in a quiet studio.", speaking the book's second sentence ("However little known
     the feelings or views of such a man may be …"), `language` `english`, seed `1813`.
  2. **Cloning** that designed voice with `qwen3-base`: the designed audio as `voice_ref`, the
     second sentence as `reference_text`, speaking the text in `sample-clip.txt`, seed `1813`.
- The leading 1.05 s of silence was trimmed with ffmpeg; the result is 24 kHz mono PCM16,
  7.67 s. Whisper (`ggml-base.en`) transcribes it word for word.
- No recording of a real person is included. The clip is dedicated to the public domain under
  [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/).
