# Setup check sample clip

`sample-clip.wav` speaks the exact text in `sample-clip.txt`. The Full check uses it as the
transcription input, the similarity check's known text and, later, the paragraph TTS
reference audio. Both files are embedded in the host assembly.

## Provenance and license

- The text is the opening sentence of Jane Austen's *Pride and Prejudice*, Project Gutenberg
  eBook #1342 (<https://www.gutenberg.org/ebooks/1342>), public domain.
- Synthesized locally on 2026-10-30 with the Windows `System.Speech` API and the installed
  `Microsoft David Desktop` voice, 24 kHz mono PCM16, about 7.7 s. No third-party recording
  or personal voice data is included.
- The recording is dedicated to the public domain under
  [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/), as the
  `Infra/ContainerHealth/test-assets` clip is.
