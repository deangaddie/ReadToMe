# Character attribution

Use these terms exactly in code, tests, and discussion.

- **Character attribution** — deciding which Character speaks a given Character ParagraphItem, via the LLM. A paragraph with unattributed Character items is **unprocessed**; once a Character is assigned it is **processed**.
- **Character Queue** — the background pipeline. User adds paragraphs; `CharacterQueueWorker` drains it, calling `CharacterAttributionService` per paragraph. `CharacterQueueService` holds queue state keyed by `(folder, paragraphId)`.
