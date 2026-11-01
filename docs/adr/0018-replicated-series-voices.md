# Series Voices are replicated into each book, not stored once

## Status

accepted (2026-11-01). Decided on [Series storage: where series data and shared Voices live](https://github.com/deangaddie/ReadToMe/issues/205), part of the map [Book series](https://github.com/deangaddie/ReadToMe/issues/204). The resolution comment there holds the full list of decisions.

## Context

A book series shares Characters, and the user wants those Characters to share Voices across books. A project is self-contained today: its Voices and Voice Rules are rows in its own `project.db`, their reference audio is in its own `voices/` folder, `VoiceResolver` and `AudioItemResolver` read only that project, and History copies reference audio into the project's `history/blobs/` so a Restore can rewrite it (ADR 0016). Nothing reads one project from another except the project listing.

## Decision

**Each linked book keeps its own full copy of every shared Voice, under the same Voice Id. A Series records only which books and Characters are linked.**

- An edit to a shared Voice commits in the book where it was made, then in each other linked book in Series position order, as an ordinary Book mutation and History step in each. A failure doesn't roll anything back: that book is left **differs from series**.
- A Restore stays local to its book. If it brings back an older shared Voice, the book is **differs from series**, the same as a failed write. The user then pushes this book's Voices or takes the series' Voices. Nothing resolves it silently.
- The series registry lives in a dot-folder, `{workspace}/.series/{seriesId}/`, holding `series.db` (name, ordered member project Ids, Series characters and their linked `(projectId, characterId)` pairs) and the poster. A dot-folder is never listed as a project. Membership is recorded only there; a missing project's entry is skipped and cleaned up. The registry has no History.

## Considered options

- **One central store per series**, which books resolve across projects. Rejected: every resolver, History's file capture and Restore would need to learn a second store, a book would stop being a portable, self-contained folder, and a Restore in one book would silently change the others.
- **Registry tables in `app.db`**. Rejected: `app.db` holds only settings and has no place for the poster file.

## Consequences

- The cost is a write across several projects with no atomic commit, plus a divergence check (Voice fields and the reference audio's content hash) to detect and surface **differs from series**.
- Unlinking a Character, or a book leaving its series, needs no copying: the book already holds its Voices.
- Generated item audio carries no voice fingerprint, so changing a shared Voice leaves existing audio in every book as it is, the same as changing a Voice in one book today.
