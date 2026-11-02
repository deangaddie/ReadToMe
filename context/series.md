# Book series

Use these terms exactly in code, tests, and discussion.

- **Series** — a named, ordered group of book projects in one Workspace, with an optional **poster**. Each book stays its own project, with its own folder, book, Characters and History; the series only groups them. A project is in at most one series, and series don't nest.
  _Avoid_: "collection", "saga", "universe" (no nesting)
- **Series position** — a book's place in its series, set by the user and shown as "Book N". Never parsed from the title.
  _Avoid_: "book number" for a number read from the title
- **Poster** — the series' picture on the Projects page. Uploaded by the user; without one, the cover of the book at the lowest Series position is shown.
  _Avoid_: "cover" (a single book's picture, exported into its m4b)
- **Series character** — a person who appears in more than one book of a series, owning the Voices those books share. It holds no name, aliases or Voice Rules of its own beyond a label; each book's Character keeps those.
- **Linked character** (series) — a book's Character tied to a Series character. Its Voices are exactly the series character's Voices, the same ones in every linked book, while its name, aliases, delivery cues and Voice Rules (the default rule included) stay the book's own. A book links at most one Character to a given Series character. Not the same as the narrator link's **linked character** ([attribution.md](attribution.md)); say "series-linked" where both could be meant.
- **Differs from series** — a Linked character whose Voices no longer match the other books' copies, after a write to one book failed or a book Restored an older Voice. The user resolves it by **pushing** this book's Voices to the series or **taking** the series' Voices into this book; nothing resolves it silently.
  _Avoid_: "out of sync", "conflict"
- **Link suggestion** — the series cast board's proposal that an unlinked Character is a Series character already linked in another book (a suggested cell), or that unlinked Characters in two or more books are one person not yet shared (a **suggested row**). Proposed only when names match one-to-one: a name or alias of one, with honorifics set aside, uses the same words as a name or alias of the other. The seed Narrator row matches only the seed Narrator row. Never applied without the user's *Link* or *Share and link*.
  _Avoid_: "auto-link", "match" for the proposal itself
- **Not the same** — the user's dismissal of a Link suggestion. It is kept by the series and stays until that Character is linked or deleted.
- **Series copy** — the version of a Series character's Voices that most of its linked books hold, ties going to the lowest Series position. It is what the series Voice editor shows and edits, and what a differing book *takes*.
