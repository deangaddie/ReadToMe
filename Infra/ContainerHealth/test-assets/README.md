# Live acceptance speech fixture

`read2me-acceptance.wav` contains the exact speech in `read2me-acceptance.txt`. It is a
non-sensitive reference and transcription fixture for the dashboard's nine-service live
acceptance pass.

## Provenance and license

- Created 2026-07-16 from text authored for this repository.
- Synthesized locally with the Windows `System.Speech` API and the installed
  `Microsoft David Desktop` voice. No third-party recording or personal voice data is
  included.
- The authored text and generated recording are dedicated to the public domain under
  [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/).

The committed acceptance test verifies that the WAV is 8–10 seconds long, 24 kHz mono
PCM16, and that the transcript is BOM-free UTF-8 with the exact expected text.
