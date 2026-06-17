# Read2Me — Domain Context

Domain vocabulary for ReadToMe. Use these terms exactly in code, tests, and discussion. When a new load-bearing concept is named, add it here.

## Book structure

- **Volume / Part / Chapter / Paragraph / ParagraphItem** — the book hierarchy, top to leaf. A Paragraph holds ordered ParagraphItems. An item is one of: **Character** (spoken dialog needing attribution), **Narration**, or a **Pause** kind.
- **Book Hierarchy** — the in-memory `BookHierarchy` that plans structural mutations (split / merge / title / pause insertion) and returns a `HierarchyMutation` (ToAdd / ToDelete / ToUpdate). The deep planning module; `BookCommandHandler` dispatches commands to it.

## Characters

- **Alias** — an alternate name for a Character (e.g. "Mr. Baggins" for "Bilbo Baggins"). Stored in the `CharacterAliases` table. Aliases are injected into the LLM attribution prompt alongside the canonical name and matched case-insensitively by the queue worker, so an alternate name returned by the LLM resolves to the existing canonical Character instead of creating a duplicate.

## Character attribution

- **Character attribution** — deciding which Character speaks a given Character ParagraphItem, via the LLM. A paragraph with unattributed Character items is **unprocessed**; once a Character is assigned it is **processed**.
- **Character Queue** — the background pipeline. The user adds paragraphs to it; `CharacterQueueWorker` drains it and calls `CharacterAttributionService` per paragraph. `CharacterQueueService` holds queue state (queued / processing / outcome) keyed by `(folder, paragraphId)`.

## Selection

- **Folder Selection** (`FolderSelection`) — the set of Character paragraphs the user has selected (per project folder) for adding to the Character Queue. The single source of truth is the set of selected paragraph ids plus each one's ancestry `(VolumeId, PartId, ChapterId)`.
- **Roll-up** — a node's tri-state (Unchecked / Indeterminate / Checked) is **derived** from how many of its selectable Character paragraphs are selected versus its total count. Node check-state is never stored; it is computed on read. Selecting a node selects all (or, for **unprocessed-only**, the unprocessed subset of) its Character paragraphs; selection is additive and idempotent.
- **Book Node Level** (`BookNodeLevel { Volume, Part, Chapter }`, in Core) — the selectable triad of the hierarchy. The single enum used by both the reader (to scope Character-paragraph queries) and Folder Selection (to derive roll-up). Replaces the old `SelectionNodeKind`.
- **Selectable node** — a Volume / Part / Chapter containing at least one Character paragraph. Only selectable nodes show a checkbox. Node total counts (`NodeCharacterParagraphCounts`) are seeded on load and held by `FolderSelection`. Selection is always cleared on any structural change (split / merge / reread), so derived state never mixes counts from one structure with selections from another.
