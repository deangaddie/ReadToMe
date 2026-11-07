# Series

A **Series** groups existing projects, each at a typed **Series position** (`context/series.md`, ADR 0018).
The registry is `{workspace}/.series/{id}/series.db`; books are addressed by folder. Registry writes change no
project: they make no History step and ignore `X-Origin-Id`, and each sends the `series` hub signal instead.

```bash
curl -s http://localhost:5000/api/series        # every series, its books in position order
curl -s -X POST http://localhost:5000/api/series -H 'content-type: application/json' \
  -d '{ "name": "Sherlock Holmes", "folder": "a-study-in-scarlet" }'   # → 201 { id }; with folder, that book is Book 1
curl -s -X PATCH http://localhost:5000/api/series/{id} -H 'content-type: application/json' -d '{ "name": "Holmes" }'   # → the series
curl -s -X PUT http://localhost:5000/api/series/{id}/books/{folder} -H 'content-type: application/json' -d '{}'   # join → { position }
curl -s -X PUT http://localhost:5000/api/series/{id}/books/{folder} -H 'content-type: application/json' -d '{ "move": true }'   # Move to
curl -s -X DELETE http://localhost:5000/api/series/{id}/books/{folder}   # leave → 204
curl -s -X PUT http://localhost:5000/api/series/{id}/positions -H 'content-type: application/json' \
  -d '{ "positions": { "a-study-in-scarlet": 1, "the-sign-of-the-four": 2 } }'   # the whole set → the series
curl -s -X DELETE http://localhost:5000/api/series/{id}           # → 204; every book keeps its Voices
curl -s -X PUT http://localhost:5000/api/series/{id}/poster -F file=@poster.jpg   # upload or replace → the series
curl -s -X DELETE http://localhost:5000/api/series/{id}/poster    # → 204, also with none; the fallback shows again
curl -s http://localhost:5000/api/series/{id}/poster -o poster    # the image; 404 when source is None
```

The poster follows the cover's rules (multipart `file`: jpg/jpeg/png/webp, at most 10 MB, else 400) and is stored
as `.series/{id}/poster.{ext}`, which `/workspace` doesn't serve. `poster.source` is `Uploaded`, else `Cover` (the
cover of the lowest-positioned book, `coverPosition` naming its position; only that book counts), else `None`
(`url` null; the client draws a placeholder). `url` is `/api/series/{id}/poster?v=…`, the version changing with
the file served. Upload and clear make no History step and send the `series` signal like other registry writes.

Names are trimmed, can't be blank (400) and are unique in the Workspace ignoring case (409). A position is 0 or
more with at most two decimals (else 400). A joining book gets the next whole number after the series' highest
(an empty series → 1; joining again answers its position); nothing is ever renumbered, and an empty series stays
until deleted. A book in another series is 409 naming it unless `move` is true. The position set's keys must be
exactly the members (else 409, out of date), and a tie is 409 naming both books. 404: unknown series or folder, or
a book not in the series. Deleting a project removes its registry entry; a folder gone outside the app is dropped
the next time its series is read.

**Series characters** share Voices across the books they are linked in (ADR 0018, #208). Each book keeps its own
copy of every shared Voice under the same Voice Id; the registry records only which book's Character is linked to
which Series character. The Narrator is linked like any other Character.

```bash
curl -s http://localhost:5000/api/series/{id}/cast   # the series cast board (below)
curl -s -X POST http://localhost:5000/api/series/{id}/characters -H 'content-type: application/json' \
  -d '{ "folder": "a-study-in-scarlet", "characterId": "…" }'   # Share as new → 201 { seriesCharacterId }
curl -s 'http://localhost:5000/api/series/{id}/characters/{scId}/links/{folder}/preview?characterId=…'   # → { bookGains, seriesGains }
curl -s -X PUT http://localhost:5000/api/series/{id}/characters/{scId}/links/{folder} -H 'content-type: application/json' \
  -d '{ "characterId": "…" }'   # Link → 204
curl -s -X DELETE http://localhost:5000/api/series/{id}/characters/{scId}/links/{folder}   # Unlink → 204; the book keeps its copy
curl -s -X POST http://localhost:5000/api/series/{id}/characters/share-and-link/preview -H 'content-type: application/json' \
  -d '{ "members": [{ "folder": "…", "characterId": "…" }, …] }'   # → { books: [{ folder, title, gains }] }; writes nothing
curl -s -X POST http://localhost:5000/api/series/{id}/characters/share-and-link -H 'content-type: application/json' \
  -d '{ "members": [{ "folder": "…", "characterId": "…" }, …] }'   # Share and link → 201 { seriesCharacterId }
curl -s -X POST http://localhost:5000/api/series/{id}/not-the-same -H 'content-type: application/json' \
  -d '{ "seriesCharacterId": "…", "folder": "…", "characterId": "…" }'   # a suggested cell → 204; or { "members": [...] }, a suggested row
```

The cast read has one row per Series character, Narrator first then by label, with a cell per book
(`linked`, `suggested`, or both null), then `suggestedRows`, each book's `unlinked` Characters, and
`suggestionCount`. A row's `label` is its Character's name in the lowest-positioned linked book.
`differs` is described below. `characters/summary` rows carry `seriesLink` (null when not linked).

**Link suggestions** (#212) are computed on each cast read from current names and aliases, never stored
(`SeriesNameMatch`). A Character's forms are its name and aliases, case-folded, punctuation dropped (apostrophes
and hyphens inside a word kept) and honorifics set aside; all-lowercase aliases ("the doctor") and honorific-only
forms ("Doctor") take no part. Two forms match on the same set of other words, with honorifics agreeing where both
carry them ("Dr." agrees with "Doctor"): "Sherlock Holmes" is "Holmes, Sherlock", "John" is not "John Watson", "Mr. Hudson" is not "Mrs.
Hudson". A Series character's side is every form of every Character linked to it. The seed Narrator row matches
only the seed Narrator row, by its flag, and isn't offered by a book whose narrator link is set. A cell's
`suggested` is an unlinked Character that matches just that Series character, which matches just that Character
in the book; each unlinked Character's `nameMatches` lists every Series character with an empty cell in its book
that it matches (the picker lists those first). `suggestedRows: [{ label, members: [{ folder, characterId, name }]
}]` join Characters that match no Series character across two or more books, each pair one-to-one, lowest
position first, labelled by the first one's name. `suggestionCount` is the cells plus the rows.

Share and link makes the Series character from the lowest-positioned member and links the rest in position order,
each link the Voice merge below; a link refused part-way leaves those before it. Its preview names the Voices
each book gains (every member ends up with every member's Voices). *Not the same* (a cell, or a row's every pair)
is kept in `series.db` until a Character it names is linked or deleted, with no History; the picker still lists a
dismissed Character. 400: a not-the-same body that is neither form, fewer than two members, or a book named twice;
409: a Character already linked.
Linking runs the Voice merge: the linking book's Character gains every Voice the linked books hold that it lacks,
then each linked book, in Series position order, gains this Character's Voices it lacks, reference audio and
stored original copied with each. Each book's gain is a History step "Link series character" there; a Character
with no Voices takes its default rule from the first one gained, and one with Voices keeps its own. If the linking
book's gain fails, nothing is linked; a later book's failure is logged and the link stands. Unlinking copies
nothing. A Series character left with no linked book (its last link unlinked, or its last book leaving) is
removed, and a link whose Character is gone from its book is dropped on the next read. 404: unknown series,
Series character, folder or Character, a book not in the series, or (unlink) nothing in that book linked to it.
409: the Character is already linked, the book already links another Character to that Series character (merge
them first), or a Voice of the series is held by another Character of the book. 400: a body without its ids.

**Shared Voice edits** (#221). After any commit that changes a Voice of a series-linked Character through a
per-book route (`/commands` CreateVoice, UpdateVoice, DeleteVoice, the design prompt, settings overrides,
transcript, source; `generate-audio`, `upload`, `transcribe`, the editor's apply and restore-original), the host
copies that Voice's whole state, reference audio included, into every other linked book in the same request, in
Series position order, under the same Voice Id. Each copy is a History step there with the edit's label ("Edit
voice", "Generate voice audio", …); a failed copy is logged, the rest carry on and the edit still answers as
success (that book then *differs*). A History Restore is never copied. Deleting a shared Voice deletes it in every
linked book and moves each book's default to its oldest remaining Voice; it is 409 when another book's positional
Voice Rules target it, or when it is the last Voice of another book that holds it, with `reason` (`PositionalRules` | `LastVoice`) and `books: [{ folder, title, ruleCount }]` extensions.
`DeleteCharacter` on a linked Character unlinks it first; its Voices go in that book only. `MergeCharacters` whose
merged (removed) Character is linked is 409 ("Unlink … first, or merge the other way"). Any commit in a member book
that changes its Characters, aliases, Voices or Voice Rules sends the `series` signal for its series.

```bash
curl -s http://localhost:5000/api/series/{id}/characters/{scId}   # the inline series Voice editor's read
```

`books` come in Series position order. `voices` and `seriesCopyFolder` are the **series copy**'s; the
editor writes through that book's per-book routes and the copies carry each edit to the rest.
`deleteBlocks`, keyed by Voice id, is the 409 a delete of that Voice would answer. 404: unknown series or
Series character.

**Differs from series** (#222). A linked book *differs* when its copy of the Series character's Voices (every
shared field, and the reference audio by content hash) is not the series copy: the version most linked books
hold, a tie going to the lowest Series position. It is found on each read, not stored, so a copy that failed to
write and a Restore to older Voices end the same way. `differs` is filled in the cast read's cells, the editor
read's books and `seriesLink`. An edit made in the series copy's book (where the editor writes) brings each
differing book back in line: that book takes the whole set, as a step with the edit's label. A Voice delete
doesn't, nor does an edit made in a differing book, and a book that would lose positional Voice Rules is left
differing (nothing asks first there): push or take resolves it.

```bash
curl -s -X POST http://localhost:5000/api/series/{id}/characters/{scId}/push -H 'content-type: application/json' \
  -d '{ "folder": "a-study-in-scarlet" }'   # this book's Voices into every other linked book → 204
curl -s -X POST http://localhost:5000/api/series/{id}/characters/{scId}/take -H 'content-type: application/json' \
  -d '{ "folder": "a-study-in-scarlet" }'   # the series copy into this book → 204
curl -s 'http://localhost:5000/api/series/{id}/characters/{scId}/push/preview?folder=a-study-in-scarlet'   # → { losses }
curl -s 'http://localhost:5000/api/series/{id}/characters/{scId}/take/preview?folder=a-study-in-scarlet'   # → { losses }
```

Each replaces the target books' Voices of that Character with the source's whole set (added, overwritten, and
those it lacks removed), as a History step "Push series voices" or "Take series voices" in each target book. Only
Voices are copied: Voice Rules stay each book's own, apart from positional rules on a removed Voice, which go with
it, and a default on one, which moves to the oldest Voice left. The previews, for the confirms, answer `losses: [{ folder, title,
voices, ruleCount }]`: each target book that would lose Voices (names) and the positional rules on them; they
refuse as the write would. 409 when the book doesn't differ. 422 when a copy
couldn't be written, naming the books (a push writes the rest first). 404 as Unlink; 400 without `folder`.
