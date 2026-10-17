# What the import pipeline knows for structure guidance

Research for the Import and structure guidance map (`.scratch/guidance-import-structure`, ticket 01).
Facts only, with file and line pointers at commit `00e6b25`; no design. Paths are relative to the
repo root.

## 1. The epub navigation document / NCX: seen at import, not kept

**Library.** The epub parser is `VersOne.Epub` 3.3.6 (`src/Directory.Packages.props:24`). The
reader calls `EpubReader.ReadBookAsync(filePath)` (`src/Read2Me.Services/Books/EpubFileReader.cs:16`)
and takes `epub.Navigation` (line 31). The library documents that property as "a list of navigation
elements of the book (typically the table of contents) or `null` if the book doesn't have navigation
information" (`VersOne.Epub.xml`, `P:VersOne.Epub.EpubBook.Navigation`); its `NavigationReader` has
overloads over both `Schema.Epub3NavDocument` (the EPUB 3 nav document) and `Schema.Epub2Ncx` (the
EPUB 2 NCX), so either source fills `Navigation`. Each `EpubNavigationItem` carries `Type` (header
or link), `Title`, `Link` (content file path + anchor, null for a header) and `NestedItems`.

**What the reader does with it** (`EpubFileReader.ReadAsync`, lines 12-55):

- Every spine file is parsed into paragraphs first, titled from its `<title>` element or else its
  file path (lines 18-29, `ExtractHtmlTitle` 325-331).
- With a nav (`navPoints is { Count: > 0 }`, line 34): `BuildAnchoredContent` (59-112) slices files
  that several **leaf** nav entries point into (`GetLeafNavItems` 161-180 keeps only items with a
  link and no children); `TryBuildFromNav` (182-220) then shapes the tree by nav depth:
  - flat nav, no anchors: returns `null` (line 196) and the reader falls through to the flat path;
  - flat nav with anchors: one Volume titled `epub.Title ?? "Volume 1"`, one Part with a `null`
    title, one Chapter per nav entry that resolved to at least one paragraph (198-202);
  - two levels: top entries become Parts (title = nav title), children become Chapters, all under
    one Volume (218-219, `BuildPart` 255-280);
  - three levels: top = Volume, middle = Part, bottom = Chapter (209-215, `BuildVolume` 222-253),
    then `EpubOrphanPolicy.Apply` (`EpubOrphanPolicy.cs:8-39`) adds each spine file the nav never
    names as its own Volume, titled by its `<title>` (lines 16-27), ordered by spine index.
  - `BuildPart` maps each child with `ResolveChapter` (266) and never descends further, so a fourth
    nav level contributes no node of its own; `ResolveChapter` (282-301) takes the nav title over the
    file's own, and an entry whose link is missing yields an empty chapter (287, 300). Only the flat
    path filters empty chapters out (200); the two- and three-level paths keep them.
- Without a nav, or when `TryBuildFromNav` returned null: one Chapter per spine file that has
  paragraphs, titled by `<title>`/path, under `Volume 1` and an untitled Part (46-54).

**What is kept.** Only the resulting `BookContent` (`src/Read2Me.Core/Models/BookContent.cs:3-11`):
Volume title (`string`), Part title (`string?`), Chapter title (`string?`), paragraph texts. The
persister writes exactly those plus the item split (`src/Read2Me.Services/BookContentPersister.cs:19-53`).
The nav itself (entry count, nesting depth, header-type entries, link anchors, which nav entries
were merged or dropped, spine index) is discarded after `ReadAsync` returns; nothing on `Project`
(`src/Read2Me.Data/Entities/Project.cs:5-22`) or any other entity records it. Epub metadata used:
`Title` (volume title fallback) and `CoverImage` (`EpubFileReader.cs:37, 41`).

**It can be re-read.** The source file stays in the project folder (`Project.Filename`, resolved by
`BookReadingService.ResolveBookFileAsync`, `src/Read2Me.Services/BookReadingService.cs:119-133`), and
`FlattenFromFileAsync` already re-opens the epub with `EpubReader.ReadBookAsync` on every manual
reread (line 51). A text project has no table of contents at all.

## 2. What the plain auto-split reread does

`POST /api/projects/{folder}/import` with `reread=true` (`src/Read2Me.App/Api/ProjectEndpoints.cs:27, 179-190`)
calls `BookUseCases.ImportAsync` (`src/Read2Me.Services/UseCases/BookUseCases.cs:39-50`), which reads
with `BookReadingService.ReadBookAsync` (`BookReadingService.cs:24-40`) and commits one
`ImportBookContentMutation` that clears and repopulates in one transaction
(`src/Read2Me.Services/Mutations/Implementations/ImportMutationImplementations.cs:28-57`). Finding
chapters, parts and volumes is entirely the per-type reader's job:

- **Epub**: the nav-shaped tree from section 1. No heading detection on the HTML: `ParseHtml`
  (`EpubFileReader.cs:303-323`) splits on opening `p|div|h1-6|section|article|blockquote|hr` tags
  (`BlockSplitRegex` 364) and keeps every non-blank block as a paragraph, so an `<h1>` heading's
  text becomes an ordinary paragraph (test `ParseHtml_H1Heading_ReturnsSingleEntry`,
  `src/Read2Me.Tests/Services/Books/EpubFileReaderTests.cs:214`).
- **Text**: `TextFileReader.ReadAsync` (`src/Read2Me.Services/Books/TextFileReader.cs:8-32`) makes
  one paragraph per non-empty line and wraps all of them in one Volume `"Volume 1"` > one Part
  (`null` title) > **one Chapter** titled with the file name without extension (line 22). There is
  no split detection of any kind for text.

**Fallback when nothing is found.** Epub with no usable nav: one chapter per spine file with
paragraphs (`EpubFileReader.cs:46-52`), so a single-file epub lands as exactly one chapter. Text:
always exactly one chapter. Volume title `"Volume 1"` and a `null` Part title are the defaults in
both (`EpubFileReader.cs:52`, `TextFileReader.cs:26-27`).

**Manual reread** (`/import/manual`, `ProjectEndpoints.cs:30, 192-205`; body `ManualImportRequest`,
`src/Read2Me.App/Api/ManualImportRequest.cs:10-58`; options `ManualReadOptions` / `SectionSplitRule` /
`SplitDetectionMode { Prefix, Number, RomanNumeral }`, `src/Read2Me.Core/Models/ManualReadOptions.cs:3-16`)
flattens the source file to lines (`BookUseCases.cs:66`, `FlattenFromFileAsync`) and re-splits them
in `ManualBookReader` (`src/Read2Me.Services/Books/ManualBookReader.cs`): a line matches a rule by
`StartsWith(prefix, OrdinalIgnoreCase)`, `^\d+$`, or a Roman-numeral regex on the trimmed line
(138-156). A matching line becomes the node title and is **removed from the paragraphs** (122-123,
85-90, 48-54); lines before the first match form a chapter with a `null` title (108, 120-121,
130); with no match at all the whole text is one `null`-titled chapter (130), and only an empty
line list yields the literal `"Chapter 1"` (132-133, 99-100, 62-63).

## 3. Which heading signals survive into the database

Entities (`src/Read2Me.Data/Entities/`): `Volume.Title` is a non-null `string` (`Volume.cs:6`),
`Part.Title` and `Chapter.Title` are `string?` (`Part.cs:7`, `Chapter.cs:7`); `Paragraph` has no
text column, only `Order` (`Paragraph.cs:5-11`); text lives on `ParagraphItem.Text` with
`ItemType` and `CharacterId` (`ParagraphItem.cs:5-18`). The persister copies titles as-is
(`BookContentPersister.cs:19, 25, 31`).

Survives:

- Node titles, including `null` (nav-less Part, manual chapter before the first marker) and `""`
  (`BuildVolume` when a nav item has no title, `EpubFileReader.cs:228`; an orphan file without a
  `<title>`, `EpubOrphanPolicy.cs:24`). The wire exposes `NodeDto(Guid Id, string? Title)`
  (`src/Read2Me.App/Api/BookEndpoints.cs:18`), and the web app treats null or blank alike as a
  placeholder title `"Untitled <level> <n>"` (`src/Read2Me.Web/src/app/pages/book/book-tree.ts:57-62`);
  the placeholder is never stored (`context/book-structure.md:27`).
- Heading **text** from epub HTML, as a narration paragraph (one Speech item stamped with the
  narrator by `NarrationClassifier`, `BookContentPersister.cs:41-53`), and chapter/part label lines
  from a text file likewise (auto reread only; a manual reread consumes matched lines as titles).
- Node order (`Order` keys, `BookContentPersister.cs:15`) and counts.

Lost:

- Heading tag level (`h1` vs `h3` vs `p`): flattened by `StripTags` (`EpubFileReader.cs:333-354`).
- The nav: count, nesting depth, header-only entries, anchors, and which title came from nav,
  `<title>` or file path (section 1).
- Whether a text-file line was a marker, and the manual rules used (nothing records the
  `ManualReadOptions` of the last reread).
- Spine index / source file of each chapter.

Also note that the app itself inserts title paragraphs after import: `AddChapterTitles` puts the
chapter title as its first paragraph, and `AddVolumeTitles`/`AddPartTitles`/`AddBookTitle` create a
leading one-paragraph Chapter per node (`src/Read2Me.Services/Mutations/Implementations/StructuralAdditionMutationImplementations.cs:33-44, 47-66, 88-101`;
`TitleInserter`, `src/Read2Me.Services/Books/TitleInserter.cs:11-41`). So a paragraph whose text is a
chapter label, and a tiny chapter, are both shapes the Titles step produces on purpose.

## 4. The five candidate signals

Reader seams: `IProjectReader` is a composite of `IProjectCatalogReader`, `IBookContentReader`,
`ICharacterReader`, `IAudioItemReader` (`src/Read2Me.Services/IProjectReader.cs:120-335`), with
partial-class implementations in `ProjectReader.*.cs`. The status endpoint already injects
`IBookContentReader`, `IAudioItemReader` and `ICharacterReader` (`src/Read2Me.App/Api/ProjectStatusEndpoints.cs:49-52`).

| Signal | Computable from | Cheapest shape today |
|---|---|---|
| (a) exactly one chapter | **Database today** | `db.Chapters.CountAsync()`: `IBookContentReader.GetTotalChapterCountAsync` (`IProjectReader.cs:157`, `ProjectReader.Book.cs:160-164`); `BookOverview.TotalChapters` carries the same figure (`ProjectReader.Book.cs:29, 57`). |
| (b) chapter-size variance | **Database today** | Per-chapter speech-item counts already exist: `IAudioItemReader.GetNodeAudioItemCountsAsync` returns a dictionary keyed by chapter, part and volume id (`ProjectReader.Audio.cs:166-189`; one row per speech item, aggregated in memory) and is already read by the status endpoint (`ProjectStatusEndpoints.cs:62`). Per-chapter paragraph count: the status seed rows (one per paragraph with a speech item, carrying `ChapterId`, `ProjectReader.Audio.cs:123-164`) grouped by `ChapterId`, also already in memory there (line 60). Character count needs a new aggregate (`SUM(LENGTH(Text))` grouped by `Paragraph.ChapterId` over speech items); no such query exists. Natural seam: `IBookContentReader` for a structure measure, or `IAudioItemReader` if it rides the existing item-count query. |
| (c) a paragraph whose whole text is a chapter or part label | **Database today** (auto split only) | Text is on `ParagraphItem.Text`; a paragraph's whole text is the join of its items in `Order` (as `FlattenFromDbAsync` does, `BookReadingService.cs:91-98`). EF Core cannot translate .NET regex to SQLite, so either load texts (`GetBookTextAsync` already loads every item text with no chapter key, `ProjectReader.Book.cs:166-175`) or prefilter in SQL (`Text.Length < n`, `LIKE 'Chapter %'`) and regex in memory. The label regexes exist only as private members of `ManualBookReader` (`ManualBookReader.cs:151-156`); the Prefix rule is a plain `StartsWith`. After a manual reread the matched label lines are gone (section 2), and the Titles step re-creates label paragraphs deliberately (section 3). Seam: `IBookContentReader`. |
| (d) every heading a placeholder title | **Database today** | `db.Chapters.AllAsync(c => c.Title == null \|\| c.Title.Trim() == "")` (both `null` and `""` occur; the web placeholder rule trims, `book-tree.ts:59`). Per level: a `null` Part title is the normal auto-import result (`EpubFileReader.cs:52, 202, 252`, `TextFileReader.cs:27`) and the tree hides a lone volume or lone part (`book-tree.ts:64-65`), so a lone untitled Part is not a signal; volume titles are `"Volume 1"`, `epub.Title` or a nav title and blank only via `EpubFileReader.cs:228` / `EpubOrphanPolicy.cs:24`. Seam: `IBookContentReader` (titles are structure). |
| (e) source table of contents has more entries than the tree has nodes | **Not from the database.** Import-time data that is discarded (section 1). | Two routes, neither built: persist a nav summary at import (would need a new field through `BookContent`/`ImportBookContentMutation` and a `Project` column with a migration in `src/Read2Me.Data/Migrations`, applied by `ProjectDbContextFactory.cs:18` / `Program.cs:49`; or a sidecar file in the project folder), or recompute on read by re-opening the epub from disk the way `FlattenFromFileAsync` does (`BookReadingService.cs:51`, a full archive parse). Text projects have no table of contents. "Entries" needs a definition: the reader counts leaves (`GetLeafNavItems`), header-type entries have no link, nav entries with no content are dropped on the flat path only, and front-matter entries (cover, title page) become chapters like any other, so nav count > chapter count is already a normal outcome of the current reader. |

## 5. Per-chapter size measures and the project-status read path

**Existing per-chapter measures.** No chapter size is stored. Derived, today:

- Speech items per chapter/part/volume: `GetNodeAudioItemCountsAsync` (`ProjectReader.Audio.cs:166-189`, on `IAudioItemReader`).
- Paragraphs with a speech item per chapter: the status seed rows carry `ChapterId`, `PartId`,
  `VolumeId` per paragraph (`ParagraphStatusSeedRow`, `src/Read2Me.Services/NodeStatus/NodeStatusService.cs:29-36`;
  query `ProjectReader.Audio.cs:123-164`, grouped in SQL).
- Dialog paragraphs per node: `BookOverview.NodeCharacterParagraphCounts` (`ProjectReader.Book.cs:33-55`), on the book overview read, not the status read.
- Character count (text length): nothing.
- Pause paragraphs are excluded from all of the above (`ParagraphItemKinds.IsSpeechExpression`,
  `src/Read2Me.Data/ParagraphItemKinds.cs:37-38`; kinds `src/Read2Me.Data/Enums/ParagraphItemType.cs:11-19`).

**Status read path.** `GET /api/projects/{folder}/status` → `GetProjectStatusAsync`
(`ProjectStatusEndpoints.cs:49-88`) builds `ProjectStatusDto` (lines 26-37). In order: stamp the
revision (59); `SeedAsync` (60, 108-114) runs `IAudioItemReader.GetNodeStatusSeedAsync` and
`NodeStatusService.Seed` (`NodeStatusService.cs:69-93`), which upserts per-paragraph counters and
raises `Changed`, which `LiveRelay` subscribes to (`src/Read2Me.App/Live/LiveRelay.cs:156`) to push
`nodeStatus` deltas to hub members (`src/Read2Me.App/Live/ILiveClient.cs:15-16`); then
`GetVolumesAsync` (61), `GetNodeAudioItemCountsAsync` (62), `GetCastCountsAsync` (63),
`HasBookContentAsync` (74), and per-node roll-ups from the in-memory service (65-66,
`StatusForNode` `NodeStatusService.cs:141-166`). Each reader call opens the project DB through
`_session.OpenAsync` (e.g. `ProjectReader.Audio.cs:125, 168`); the two heavy reads already load one
row per speech paragraph and one row per speech item.

**Affordability.** Chapter paragraph counts and chapter item counts are already in memory on this
path (`seed` and `itemCounts`), so a summary built from them costs a `GroupBy` over lists the
endpoint holds. Chapter order is not in them: ordering needs chapter rows with their part and volume
`Order` (one query like `GetAssemblyManifestAsync`'s ordering, `ProjectReader.Audio.cs:196-200`, or
`GetVolumesAsync` + `GetChildrenAsync` per level as `ChapterOutlineBuilder` does,
`src/Read2Me.Services/Llm/ChapterOutlineBuilder.cs:15-44`). A text-length aggregate would be one
extra `GROUP BY ChapterId` query, small next to the existing per-item row load. The DTO has no
structure field today; `Nodes` is keyed by Volume/Part/Chapter id and patched by hub deltas
(`ProjectStatusEndpoints.cs:21-25`), and the web Overview triggers both rereads from this page
(`src/Read2Me.Web/src/app/pages/project/overview-page.ts:155-175`, `projects-api.ts:55-62`).
