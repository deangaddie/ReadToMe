# Release narration takes only speech-marked paragraphs

## Status

accepted (2026-10-11). Decided in the released-narration map (`.scratch/released-narration/`, decision ticket 07,
spec `.scratch/released-narration/spec.md`). Amends the Release narration gesture (`.scratch/release-narration/`,
built on the same branch). Respects [ADR 0005](0005-frozen-paragraph-item-boundaries.md),
[ADR 0006](0006-narration-is-a-speaker-not-an-item-type.md) and
[ADR 0007](0007-book-mutations-reconcile-book-view-projections.md).

## Context

Release narration, as first built, cleared the narrator stamp from **every** narration-only paragraph in a Chapter
or Part. A transcript chapter such as *Foundation* P1 Ch6 released its 72 `Q.`/`A.` paragraphs, and all the real
narration around them too. The attribution in use (the Chapter style on `gemma-12b`, [ADR 0013](0013-chapter-pass-attribution-opt-in.md))
has no Narrator answer, so it cannot hand real narration back. On the live check, the 11 released narration
paragraphs came back as 9 `unknown` and **2 wrong names**. On the labelled corpus (9 chapters, 435 released items),
the baseline made **33 silent errors, all on narration**. The wrong name was usually the character the narration
mentions. A wrong name is worse than `unknown`, and 296 `unknown` narration lines are a hand-repair chore that the
release itself created.

Three routes were prototyped and measured (silent errors on the corpus):

| Route | Silent errors | Narration resolved | Extra cost |
|---|---|---|---|
| Baseline (release everything) | 33 | 0 / 329 | — |
| Narrator answer in the grammar | 31 | 269 / 329 | prompt + grammar change |
| Classify step (speech vs narration) | 22 | 284 / 329 | +16% calls, +39% wall on Foundation |
| **Release filter** | **≈1** | 328 / 329 stay narrator | none — no LLM change |

The filter, measured with `corpus/filter.mjs --roster --min 5`:

- Corpus: paragraph precision 97/98, recall 97/105. 1 of 329 narration items released, 97 of 106 speech items
  released.
- Whole books (Foundation, Magician, Pride and Prejudice, Sherlock Holmes, Dracula, Dune, He Who Fights With
  Monsters 13; about 10.2k narration-only paragraphs): **0 false-positive paragraphs**. Only Foundation (`Q.` ×36,
  `A.` ×36) and Magician (`—…—` ×26) release anything.
- Without the roster gate, HWFWM releases 80 ability-list lines (`Iron:`, `Bronze:`, `Silver:`, `Gold:`, 20 each).
  With a minimum of 3 instead of 5, Dracula and Dune each release 3 em-dash parentheticals. That is why both gates
  exist.

## Decision

- **Selection happens at release time, with fixed rules.** A Chapter or Part release takes only the narration-only
  paragraphs that carry a **speech marker** repeated in at least **5** narration-only paragraphs of **their own
  chapter**. The three shapes are tried in order, and the first match is the paragraph's one key:
  - **Initial prefix**: `^[A-Z]{1,2}[.:]\s+\S`, keyed as written (`Q.` and `Q:` count separately).
  - **Name prefix**: 1–4 capitalised words, a colon and a space, where the words are a Character name or alias of
    the book (case-insensitive). Keyed as written (`Seldon:`).
  - **Em-dash speech**: `—` and a capital, up to 600 non-dash characters, then `—`. The opening dash must follow the
    paragraph start, whitespace, a letter or a comma. All matches share the single key `—…—`.

  A paragraph's text is its speech items' texts in item order, joined by one space. The rules live in one pure
  classifier (`SpeechMarkers`). The release selection (`NarrationRelease.SelectAsync`) feeds both the write and the
  preview, so the count a producer confirms is the count that is written.
- **A Paragraph release is unfiltered.** Picking one paragraph is the producer's explicit intent, and it is the
  escape hatch for anything the filter misses.
- **The thresholds are fixed constants.** There is no setting, no API parameter and no unfiltered Chapter/Part
  flag. The command shape is unchanged.
- **No LLM classifier, and no Narrator answer for released items.** Attribution is unchanged. This supersedes the
  narrator-answer meaning drafted in `.scratch/released-narration/issues/03-what-a-narrator-answer-means.md`, which
  was never built.
- **The import split stays the quote scan.** Speech markers are not detected at import ([ADR 0005](0005-frozen-paragraph-item-boundaries.md)):
  a release is still a speaker assignment ([ADR 0006](0006-narration-is-a-speaker-not-an-item-type.md)), done in one
  Book mutation ([ADR 0007](0007-book-mutations-reconcile-book-view-projections.md)).

## Consequences

- End to end with the baseline Chapter answers: about **1 silent error** (Magician 22 [89]: labelled narrator but
  holding em-dash thought-speech, so arguably right). This is an estimate. The baseline answers were made with
  everything released, so the context labels differ slightly.
- **Residual misses stay narrator, by design.** These are quoted speech inside narrator-stamped items (Magician ONE
  #23/#26), open-quote continuation paragraphs (Holmes II #13), unmarked verse or song (Dune #89–93), and any marker
  that repeats fewer than 5 times in its chapter. Leaving them as narrator is today's state, not a new error. The
  producer fixes them with a Paragraph release or the speaker chip.
- **`NAME:` recall is unproven.** The corpus has no play-script chapter. Only its false-positive gate is measured.
- Mixed `Q.`/`Q:` in one chapter count as separate keys, so such a chapter may fall short of the gate. This is
  conservative.
- Releasing reads the scope's narration-only paragraphs into memory, because the text rules don't translate to SQL.
  A Part is a few thousand paragraphs at most.
