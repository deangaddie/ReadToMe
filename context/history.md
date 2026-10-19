# Project history

Use these terms exactly in code, tests, and discussion.

- **History** — a project's ordered, append-only record of the changes a user has made to it. Each entry is a **History step**. Nothing is ever removed from History by using it: a Restore adds a step, it never cuts later steps off. History is project-wide; there is no per-item history or per-item rollback.
  _Avoid_: "undo stack", "versions" (reserved for a later, user-named step), "checkpoint" (already a Pipeline term, see [web.md](web.md))
- **History step** — one user action, however many records it touches: an accepted Edit with AI is one step, not one per paragraph; one attribution run is one step, not one per paragraph it commits. A step is labelled by what the action was and what it touched, and it fixes the whole **project state** as it stood after that action.
  _Avoid_: "microversion" (Onshape's word), "revision" (the in-memory Book mutation receipt counter, [book-structure.md](book-structure.md))
- **Project state** — everything a History step fixes: the book content, characters, voices and their reference audio, voice rules, narrator and project settings, cover and metadata. It is the user's *choices*; generated per-item TTS takes are results, not state, so after a Restore a take that no longer matches its item is **stale** and needs regenerating.
- **View a step** — open the whole project read-only as it stood at a History step. Changing anything requires a Restore first.
- **Restore** — make an older History step's project state the current one by adding a **new** History step whose state equals it; the steps after the restored one stay in History and can themselves be restored.
  _Avoid_: "rollback" as a destructive operation, "revert"
