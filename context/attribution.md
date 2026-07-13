# Character attribution

Use these terms exactly in code, tests, and discussion.

- **Character attribution** — deciding which Character speaks a given Character ParagraphItem, via the LLM. A paragraph with unattributed Character items is **unprocessed**; once a Character is assigned it is **processed**.
- **Character Queue** — the background pipeline. User adds paragraphs; `CharacterQueueWorker` drains it, calling `CharacterAttributionService` per paragraph. `CharacterQueueService` holds queue state keyed by `(folder, paragraphId)`.
- **Segment** — one narration-or-dialog slice of a paragraph as the LLM answers it: `{ text, type, speaker, voice_instructions }`. Attribution asks for a paragraph's whole segment list (re-segmentation is in scope — the imported item split may be wrong), and the answer is reconciled against the existing ParagraphItems. The wire strings live in one place, `SegmentWire`: types `narration` / `dialog`, reserved speakers `narrator` (every narration segment) and `unknown` (a dialog segment whose speaker is not known).
  _Avoid_: "chunk", "line", treating a paragraph as having one speaker
- **Fully attributed** — every Character item of a paragraph carries a Character. A paragraph with some stamped and some unstamped Character items is **partly attributed**: still queue-eligible, and never fed to the LLM as context (its unknown segments would poison the surrounding speakers).
