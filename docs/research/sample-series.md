# Sample series: Gutenberg books with recurring characters

Research for issue #209 (map #204, Book series). Question: which two or more public-domain
Project Gutenberg books form a series with recurring characters, suiting Book series tests,
specs and hands-on trials?

## Recommendation

**Sherlock Holmes, by Arthur Conan Doyle**, in publication order:

| # | Book | Gutenberg | Epub (no images / EPUB3) | Plain text | Words (approx.) |
|---|------|-----------|--------------------------|------------|-----------------|
| 1 | *A Study in Scarlet* (1887) | [#244](https://www.gutenberg.org/ebooks/244) | 188 kB / 376 kB | 262 kB | ~43,000 |
| 2 | *The Sign of the Four* (1890) | [#2097](https://www.gutenberg.org/ebooks/2097) | 183 kB / 357 kB | 255 kB | ~43,000 |
| 3 | *The Adventures of Sherlock Holmes* (1892) — **already bundled** | [#1661](https://www.gutenberg.org/ebooks/1661) | 304 kB / 371 kB | 593 kB | ~104,000 |

Each ebook page lists "Copyright: Public domain in the USA." Sizes are from the ebook pages'
download tables (fetched 2026-11-01). Word counts are `wc -w` over each `pg<id>.txt`, less
roughly 3,000 words of Gutenberg header and licence.

Why this series:

- **It fits alongside what the repo already bundles.** `tools/browse/books/` already holds
  `the-adventures-of-sherlock-holmes.epub` (#1661, listed in `tools/browse/README.md`). Adding
  the two short novels makes a three-book series out of one bundled book plus two new ones.
  Other bundled books are #11 Alice, #1342 Pride and Prejudice, #345 Dracula and #215 The Call
  of the Wild. No `.epub` lives under `src/`; test fixtures build books in code
  (`BookHierarchyBuilder`, `WorkspaceSeeder`).
- **Short books.** #244 and #2097 are the two shortest Holmes novels, each about the size of the
  bundled *Call of the Wild*. A two-book series (#244 + #2097) is enough for most tests. #1661
  adds a third book with no extra download.
- **Narrator who is also a character.** Dr John H. Watson narrates in the first person and is a
  speaking character in every book.
- **Narration edge cases for free.** *A Study in Scarlet* Part I is Watson's first-person
  account. Part II, "The Country of the Saints", is a third-person flashback in which Watson does
  not appear. *The Sign of the Four* has a long story-within-a-story told in the first person by
  Jonathan Small. That tests a second, book-local first-person narrator.

## Recurring and book-local characters

Counts are name occurrences in each book's plain text (grep, case-sensitive). A low count usually
means a mention, not an appearance.

| Character | Study (#244) | Sign (#2097) | Adventures (#1661) | Role in a series test |
|-----------|-------------:|-------------:|-------------------:|-----------------------|
| Sherlock Holmes | 94 | 137 | 461 | recurs in all three |
| Dr Watson (narrator) | 6 by name; narrates | 24 by name; narrates | 81 by name; narrates | recurring narrator-character |
| Inspector Lestrade | 47 | 1 (mention) | 38 | recurs in 1 and 3; only a mention in 2 |
| Inspector Gregson | 43 | 1 (mention) | 0 | in book 1 only |
| Wiggins (Baker Street Irregulars) | 4 | 6 | 0 | minor, recurs in 1 and 2 |
| Mrs Hudson | 0 (landlady unnamed) | 6 | 4 | named from book 2: an unnamed-then-named case |
| Stamford | 11 | 0 | 0 | book 1 only |
| Jefferson Hope, Drebber, Stangerson, John and Lucy Ferrier | 32 / 62 / 47 / 60 (Ferrier) | Hope: 3 (mention) | 0 | book 1 only |
| Mary Morstan | 0 | 70 | 0 by surname | book 2 only (Watson's wife in book 3, rarely named) |
| Thaddeus and Bartholomew Sholto | 0 | 78 (Sholto) | 2 (mention) | book 2 only |
| Jonathan Small (embedded narrator) | 0 | 14 | 0 | book 2 only |
| Athelney Jones | 0 | 19 | 0 | book 2 only |
| Toby (the dog) | 0 | 26 | 0 | book 2 only |
| Irene Adler | 0 | 0 | 12 | book 3 only |

## Candidates considered and set aside

All are public domain in the USA according to their Gutenberg ebook pages.

| Series | Books (Gutenberg id, plain text size) | Why not first choice |
|--------|----------------------------------------|----------------------|
| Holmes, later novel | *The Hound of the Baskervilles* [#2852](https://www.gutenberg.org/ebooks/2852), 379 kB | Watson narrates, but it is longer than #244 and #2097. A good fourth book if needed. *The Memoirs* [#834](https://www.gutenberg.org/ebooks/834) (599 kB) is as long as #1661. |
| Alice | *Alice's Adventures in Wonderland* [#11](https://www.gutenberg.org/ebooks/11), 170 kB (bundled); *Through the Looking-Glass* [#12](https://www.gutenberg.org/ebooks/12), 192 kB | The shortest pair, with #11 already bundled. But the narrator is third-person and few characters recur (Alice, Dinah, Hatter/Hatta, March Hare/Haigha under new names). Worth keeping as a fallback minimal pair. |
| Oz | *The Wonderful Wizard of Oz* [#55](https://www.gutenberg.org/ebooks/55), 232 kB; *The Marvelous Land of Oz* [#54](https://www.gutenberg.org/ebooks/54), 263 kB | Good recur/one-book split (Scarecrow and Tin Woodman recur; Dorothy is in book 1 only; Tip in book 2 only). But the narrator is third-person, and #54's EPUB3 is 26.2 MB with images (the no-images epub is 222 kB). |
| Anne | *Anne of Green Gables* [#45](https://www.gutenberg.org/ebooks/45), 592 kB; *Anne of Avonlea* [#47](https://www.gutenberg.org/ebooks/47), 520 kB | Third-person narrator, and each book is over twice the size of the Holmes novels. |

## Sources

- Gutenberg ebook pages (title, "Copyright" row, download sizes):
  <https://www.gutenberg.org/ebooks/244>, <https://www.gutenberg.org/ebooks/2097>,
  <https://www.gutenberg.org/ebooks/1661>, <https://www.gutenberg.org/ebooks/2852>,
  <https://www.gutenberg.org/ebooks/834>, <https://www.gutenberg.org/ebooks/11>,
  <https://www.gutenberg.org/ebooks/12>, <https://www.gutenberg.org/ebooks/55>,
  <https://www.gutenberg.org/ebooks/54>, <https://www.gutenberg.org/ebooks/45>,
  <https://www.gutenberg.org/ebooks/47>.
- Word and name counts: the plain-text files
  `https://www.gutenberg.org/cache/epub/<id>/pg<id>.txt` for 244, 2097 and 1661, streamed through
  `wc`/`grep` and not kept.
- Repo: `tools/browse/books/`, `tools/browse/README.md` (bundled book list),
  `CLAUDE.md` ("Sample books" rule).

Epub download URLs follow the pattern `https://www.gutenberg.org/ebooks/<id>.epub.noimages`
(the form the bundled Pride and Prejudice uses) or `.epub3.images`. No book files were added by
this research.
