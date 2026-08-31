# Read2Me — Domain Context (index)

Domain vocabulary for ReadToMe. Use these terms exactly in code, tests, and discussion. The glossary is split by area under [context/](context/) — **load only the file(s) for the area you're working in**, not all of them. When a new load-bearing concept is named, add it to the matching file (or add a new file + an index line here).

| Area | File | Covers |
| ---- | ---- | ------ |
| Book structure, view, selection, status | [context/book-structure.md](context/book-structure.md) | Volume…ParagraphItem hierarchy, speech vs pause + `NarrationRule`, Book Hierarchy, Alias, Book View Mode, Character paragraph, Folder/Audio Item Selection, Roll-up, Generatable item, Node Status Badge/roll-up |
| Voice rules | [context/voice-rules.md](context/voice-rules.md) | Voice, Voice Rule, default rule, Position, Anchor, evaluation, effective Character, `VoiceResolver`, `NodeOrderTables`, `AnchorSpanResolver`, dangling anchor, rule editor, resolved preview |
| Character attribution | [context/attribution.md](context/attribution.md) | Character attribution, unattributed, processed/unprocessed, Character Queue + what it asks about, item attribution/frozen boundaries/chunk, escalation chain, narrator link + linked character + `narrator` wire alias |
| LLM infrastructure | [context/llm.md](context/llm.md) | Constrained completion, Completion Runner, completion scanner stop, Run outcome, Health streak |
| Audio pipeline | [context/audio-pipeline.md](context/audio-pipeline.md) | Normalisation, Canonical WAV, Audio Queue, Audio Gen Stream, Audio Retry, pipeline/resolver/recorder seams, outcomes, Sentence Chunking |
| Queueing (shared) | [context/queueing.md](context/queueing.md) | Work outcome, Disposition, Plan, apply product, Attempt state — the shared queue-disposition vocabulary |
| Semantic verification | [context/semantic-verification.md](context/semantic-verification.md) | Semantic Similarity Check, Semantic Rescue, `ISemanticVerifier` |
| Audiobook assembly & live-event infra | [context/assembly.md](context/assembly.md) | Audiobook Assembly, manifest, pauses, concat/chapters/cover, `EventBroadcaster<T>`, `VoiceBatchRunner`, Sweep Phase |
| Container health dashboard | [context/container-health-dashboard.md](context/container-health-dashboard.md) | Service Adapter, shared operator-console boundary |
