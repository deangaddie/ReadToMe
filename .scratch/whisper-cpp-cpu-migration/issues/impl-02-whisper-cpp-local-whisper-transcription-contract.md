# impl-02 — Whisper.CPP Local Whisper transcription contract

**What to build:** Local Whisper remains a stable app capability at its existing configuration URL: users receive ordinary transcript text and the audio pipeline receives valid word-level alignment from the Whisper.CPP service.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Transcript and alignment requests use the pinned Whisper.CPP inference contract while preserving the existing Local Whisper settings, MIME handling, cancellation, and managed-service failure reporting.
- [x] Transcript results are trimmed from the server's JSON text response; alignment returns ordered `TranscribedWord` values in seconds from nested word records.
- [x] Whitespace-only tokens are omitted, standalone punctuation is folded into the preceding word, and non-empty transcripts with missing, invalid, or descending word timing fail rather than fabricate alignment.
- [x] Automated tests cover every required multipart control, transcript parsing, alignment normalization and rejection cases, plus managed and remote failure behaviour.
