# Read2Me — Domain Context (index)

Domain vocabulary for ReadToMe. Use these terms exactly in code, tests, and discussion. The glossary is split by area under [context/](context/) — **load only the file(s) for the area you're working in**, not all of them. When a new load-bearing concept is named, add it to the matching file (or add a new file + an index line here).

| Area | File | Covers |
| ---- | ---- | ------ |
| Book structure, view, selection, status | [context/book-structure.md](context/book-structure.md) | Volume…ParagraphItem hierarchy, Book Hierarchy, Alias, Book View Mode, Folder/Audio Item Selection, Roll-up, Generatable item, Node Status Badge/roll-up |
| Voice rules | [context/voice-rules.md](context/voice-rules.md) | Voice, Voice Rule, default rule, Position, Anchor, evaluation, `VoiceResolver`, `NodeOrderTables`, `AnchorSpanResolver`, dangling anchor, rule editor, resolved preview |
| Character attribution | [context/attribution.md](context/attribution.md) | Character attribution, processed/unprocessed, Character Queue |
| Audio pipeline | [context/audio-pipeline.md](context/audio-pipeline.md) | Normalisation, Canonical WAV, Audio Queue, Audio Gen Stream, Audio Retry, pipeline/resolver/recorder seams, outcomes, Sentence Chunking |
| Semantic verification | [context/semantic-verification.md](context/semantic-verification.md) | Semantic Similarity Check, Semantic Rescue, `ISemanticVerifier` |
| Audiobook assembly & live-event infra | [context/assembly.md](context/assembly.md) | Audiobook Assembly, manifest, pauses, concat/chapters/cover, `EventBroadcaster<T>`, `VoiceBatchRunner`, Sweep Phase |
