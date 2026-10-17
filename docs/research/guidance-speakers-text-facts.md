# Character reader, aliases and attribution outcomes: facts for the speakers guidance

Research for the Speakers and text guidance map (`.scratch/guidance-speakers-text`, ticket 01).
Read on 2026-10-17 at `00e6b25`. Every claim cites a file and line in this repo; nothing here
designs the feature.

## 1. Per-character facts available in one read

### The entity

`Character` has exactly three columns: `Id`, `Name`, `IsNarrator`, plus `Voices` and `Aliases`
navigations (`src/Read2Me.Data/Entities/Character.cs:3-11`; snapshot
`src/Read2Me.Data/Migrations/ProjectDbContextModelSnapshot.cs:98-112`). `CharacterAlias` is
`Id`, `CharacterId`, `Name` (`src/Read2Me.Data/Entities/CharacterAlias.cs:3-9`).

**There is no created-at timestamp and no provenance column on `Character` or `CharacterAlias`.**
The only `CreatedUtc` columns in the model are on `AudioReview` and `Voice`
(`ProjectDbContextModelSnapshot.cs:26`, `:294`; `src/Read2Me.Data/Entities/Voice.cs:17`). Nothing
records whether discovery, attribution or a human created a character.

### The reader (`ICharacterReader`)

`ICharacterReader` is the character split of `IProjectReader`
(`src/Read2Me.Services/IProjectReader.cs:211-265`), implemented in the partial class
`src/Read2Me.Services/ProjectReader.Characters.cs`. The one-read roster shapes:

| Read | Returns | Facts per character | Where |
|---|---|---|---|
| `GetCharacterSummariesAsync` | `List<CharacterSummary>` | `Id`, `Name`, `Aliases` (id + name, sorted), `LineCount`, `VoiceCount`, `ReadyVoiceCount`, `IsNarrator` | `ProjectReader.Characters.cs:217-235`; record `src/Read2Me.Core/Models/CharacterSummary.cs:34-41` |
| `GetCharactersWithAliasesAsync` | `List<Character>` with `Aliases` and `Voices` included | entity shape, no counts | `ProjectReader.Characters.cs:21-30` |
| `GetCharactersAsync` | `List<Character>` | entity shape, no aliases | `:15-19` |
| `GetCastCountsAsync` | `CastCounts(Characters, CharactersWithLines, ReadyVoices)` | three project-level integers, no names | `:325-350`; record `IProjectReader.cs:273` |

Facts on `CharacterSummary`, item by item:

- **Name, aliases**: yes (`CharacterSummary.cs:36-37`).
- **Line count**: yes; it is a count of `ParagraphItems` whose `CharacterId` is the character
  (`ProjectReader.Characters.cs:229`), i.e. items, not paragraphs. The same rule as
  `GetCharacterLinesAsync` (`:201-215`) so count and list agree (comment at `:227-228`).
- **Has a voice**: `VoiceCount` (planned) and `ReadyVoiceCount` (voices with an `AudioFileName`)
  (`:230-232`).
- **Narrator sentinel**: `IsNarrator` marks the seed Narrator row
  (`ProjectDbContext.NarratorId = 00000000-0000-0000-0000-000000000001`,
  `src/Read2Me.Data/ProjectDbContext.cs:8`, seeded at `:43`). Whether a *real* character is the
  linked narrator is **not** on the summary ("a project-level fact the caller overlays",
  `CharacterSummary.cs:31-32`); the endpoint overlays it as `NarratesBook` from
  `IProjectCatalogReader.GetNarratorAsync` + `NarratorIdentity.IsLinkedTo`
  (`src/Read2Me.App/Api/CharacterEndpoints.cs:69-81`; `src/Read2Me.Data/NarratorIdentity.cs:20`).
- **Created when / by what**: not available (no column, see above).

Ordering of every roster read is narrator first, then by `Name` (`ProjectReader.Characters.cs:18`,
`:27-28`, `:221-222`).

### The Cast page endpoints

`src/Read2Me.App/Api/CharacterEndpoints.cs:55-67`:

- `GET /api/projects/{folder}/characters/summary` → `CharacterSummaryDto(Id, Name, Aliases,
  LineCount, VoiceCount, ReadyVoiceCount, IsNarrator, NarratesBook)` (`:16-18`, `:69-82`).
- `GET .../characters/{id}/lines` → `CharacterLineDto(ItemId, ParagraphId, ChapterId, Text)`
  in book order (`:20-21`, `:84-91`; reader `ProjectReader.Characters.cs:201-215`).
- `GET .../paragraphs/{paragraphId}/context` (line context, `:93-110`), voice rules and preview.

The Book page's own roster is a different, thinner read: `GET /api/projects/{folder}/characters`
and the overview's `Characters` list carry `CharacterDto(Id, Name, Aliases)` only
(`src/Read2Me.App/Api/BookEndpoints.cs:57`, `:62-65`, `:104`, `:204-209`); the reader's speaker
menus build from the overview (`src/Read2Me.Web/src/app/pages/book/book-page.ts:192-194`).

### The web cast store

`CastStore` (`src/Read2Me.Web/src/app/pages/cast/cast-store.ts:52`) loads the roster from
`characters.summary(folder)` (`:126`, `:164`; `src/Read2Me.Web/src/app/api/characters-api.ts:22`)
into `rows` (`:78`), with `selectedId`/`selected` (`:81`, `:105`) and the selected character's
lines, voices and voice rules loaded on selection (`:158-170`). The page sorts by `name`,
`lines` or `readiness` (`cast-rows.ts:4`, `:60-77`; `lines` is descending `lineCount`, `:70`),
default `name` (`cast-page.ts:65`).

## 2. How attribution creates a Character, and what provenance survives

### The path

1. The LLM answer's speaker strings reach `CharacterQueueProcessor.ApplyAttributionsAsync`
   (`src/Read2Me.App/Characters/CharacterQueueProcessor.cs:199-238`). Each is resolved by
   `ResolveSpeakerAsync` (`:253-263`): the `unknown` sentinel → `null` (nobody stamped); the
   `narrator` token → the linked character or `null`; **any other string →
   `CharacterResolver.ResolveOrCreateAsync(folder, speaker.Trim(), ct)`** (`:262`). The doc
   comment states the policy: "an unlisted name that survived the escalation chain is the chain's
   final answer, so it earns a new Character" (`:190-192`).
2. Sentinels: `AttributionWire.Unknown = "unknown"`, `IsUnknownSpeaker` and `IsNarrator` are
   trimmed, case-insensitive equality (`src/Read2Me.Services/Llm/AttributionWire.cs:22-30`).
3. `CharacterResolver.ResolveOrCreateWithOutcomeAsync` reads the roster with aliases, returns the
   first match, else commits `CreateCharacterMutation(folder, name)`
   (`src/Read2Me.Services/Characters/CharacterResolver.cs:59-80`, `:111-112`).
4. `CreateCharacterMutationImplementation` re-reads the roster inside the transaction and returns
   `Nothing` on a match, else inserts `new Character { Id, Name }`
   (`src/Read2Me.Services/Mutations/Implementations/CharacterLifecycleMutationImplementations.cs:107-121`).

The same mutation serves the agent `CreateCharacter` command
(`src/Read2Me.App/Api/CommandEndpoints.cs:16-18`; `src/Read2Me.Core/Models/BookCommands.cs:58`)
and discovery's `ApplyDiscoveredAsync` (`CharacterResolver.cs:92-104`, called from
`src/Read2Me.App/Api/DiscoveryEndpoints.cs:79`). All three producers land on one insert that
writes `Id` and `Name` only.

### Provenance

**None survives.** The LLM-written name *is* the `Name` (after `Trim()`,
`CharacterQueueProcessor.cs:262`); no run id, config, timestamp or "created by attribution"
marker is written (`CharacterLifecycleMutationImplementations.cs:116`). The mutation receipt
carries `CreatedId` (`:119`) for the live hub, but receipts are not persisted as provenance on
the row.

### Existing near-duplicate check at creation

The only check is `CharacterResolver.Matches` (`CharacterResolver.cs:14-16`): **exact string
equality, case-insensitive (`OrdinalIgnoreCase`), against the canonical name or any alias**. It
runs at resolve time (`:62`, `:112`) and again inside the mutation (`:113`). There is no
punctuation, whitespace-collapsing, honorific, token or prefix matching: "Mr. Baggins",
"Baggins" and "Bilbo Baggins" are three characters unless one is already an alias of another.
Aliases are the only mechanism that folds alternate strings onto one Character, and they are
written by discovery apply (`CharacterResolver.cs:98-101`), the merge's add-as-alias
(`CharacterLifecycleMutationImplementations.cs:241-242`, `:261-282`) or the user.

The adjacent advisory check, `AliasCollisions.Find`, is the opposite direction: it finds one
string owned by two characters (`src/Read2Me.Core/Models/AliasCollisions.cs:27-63`), is run only
on the discovery preview (`DiscoveryEndpoints.cs:55-57`), and blocks nothing (`:24-25`).

## 3. The merge mutation and the Cast page's merge dialog

### Host

`MergeCharactersMutation(FolderId, SurvivorId, MergedId, AddNameAsAlias)`
(`src/Read2Me.Services/Mutations/BookMutation.cs:281-284`), reached by the agent command
`MergeCharactersCommand(FolderId, SurvivorId, MergedId, AddNameAsAlias)`
(`src/Read2Me.Core/Models/BookCommands.cs:102`;
handler `src/Read2Me.Services/Commands/Handlers/CharacterHandlers.cs:87-93`) via
`POST /api/projects/{folder}/commands` with `type: "MergeCharacters"`.

`MergeCharactersMutationImplementation.ApplyAsync`
(`CharacterLifecycleMutationImplementations.cs:205-254`), in order:

- refuses when either side is the seed Narrator (`:210-211`) or both ids are equal (`:213-215`);
- **lines**: `ExecuteUpdate` every `ParagraphItem.CharacterId == MergedId` → `SurvivorId` (`:231-233`);
- **aliases**: repoints the merged character's alias rows to the survivor (`:235-237`);
- **voices and voice rules of the merged side are deleted**, rules first because of the Restrict FK
  (`:239`, helper `:77-94`); the survivor keeps its own;
- when `AddNameAsAlias`, adds the merged `Name` and each moved alias as survivor aliases, skipping
  any string the survivor already answers to, case-insensitively (`:241-242`, `:261-282`);
- a narrator link pointing at the merged side follows the survivor (`:245-247`);
- deletes the merged `Character` row (`:249-251`);
- reports `WholeProject` scope with facets derived from the row counts (`:253`, `:61-69`).

Two-at-a-time only: there is no multi-merge mutation.

### Web

- `MergeDialogData { folder, merged: CharacterSummaryDto, rows }` →
  `MergeChoice { survivorId, addNameAsAlias }`
  (`src/Read2Me.Web/src/app/pages/cast/merge-dialog.ts:15-24`). Candidates are every row that is
  not the merged one and not the seed Narrator (`merge-options.ts:14-19`); the initial form
  preselects the **first candidate in roster order** with the alias box on (`:22-24`); the only
  validation is "a survivor is selected and is a candidate" (`:27-36`); a warning names the voices
  that die with the merged character (`:42-56`, dialog `:46-54`).
- The Merge button lives on the character detail header and is disabled when `mergeTargets() === 0`,
  i.e. no other non-narrator row (`character-detail.ts:46`, `:53-55`, `:83-85`). `merge()` opens the
  dialog with the **currently selected character as the merged side** and the store's rows, then
  runs the `MergeCharacters` command and navigates to the survivor (`:212-227`).

### Routes and deep links

- Routes: `cast` and `cast/:characterId`, both rendering `r2m-cast-page`
  (`src/Read2Me.Web/src/app/app.routes.ts:58-68`); `castPath(folder, characterId?)` builds them
  (`cast-path.ts:1-5`); the page reads `characterId` from router params (`cast-page.ts:70-71`).
- `?discover=1` is the consumed-on-arrival precedent: a computed over the router query (`:73-74`)
  and an effect that waits for the roster, then navigates with `{ query: { discover: null },
  replace: true }` and runs discovery (`:118-124`).
- **There is no route, query parameter or API shape that opens the merge dialog or preselects a
  pair.** The dialog is opened only from the detail header's button. A `?merge=` link would need a
  new parameter following the `discover` pattern; the dialog's input shape already takes a
  `merged` row and the roster, so the missing piece is a preselected `survivorId`
  (`initialMergeForm` hard-codes `candidates[0]`, `merge-options.ts:22-24`).

## 4. Candidate duplicate rules: computable today, and the cheapest shape

All four rules need `Name` and `Aliases` per character; "very few lines" also needs `LineCount`.
`GetCharacterSummariesAsync` returns all of those in one query
(`ProjectReader.Characters.cs:217-235`), so every rule below is **computable in memory from one
roster read**, pairwise over N characters (casts are tens, rarely hundreds).

| Rule | Inputs on the summary | Computable today | Notes |
|---|---|---|---|
| Shared surname / name token | `Name`, `Aliases` | yes | tokenise on whitespace, compare last token or any token, `OrdinalIgnoreCase`; nothing in the host tokenises names today (`Matches` is whole-string, `CharacterResolver.cs:14-16`) |
| One name a prefix/suffix of another ("Baggins" / "Bilbo Baggins") | `Name`, `Aliases` | yes | `StartsWith`/`EndsWith` on token boundary; also whole-string |
| Honorific variants ("Mr. Baggins") | `Name`, `Aliases` | yes | needs a small honorific list to strip (none exists in the codebase; `AttributionWire` only knows `unknown`/`narrator`, `AttributionWire.cs:22-30`) before the token rule |
| Few lines next to a similar name | `LineCount` + any rule above | yes | `LineCount` is items, not paragraphs (`:229`); a character created by attribution from one stray answer typically has 1 |

Exclusions the data already supports: skip `IsNarrator` (the seed row); the linked narrator is a
real character and is **not** on the summary, so a rule that should spare it needs the endpoint's
`NarratesBook` overlay (`CharacterEndpoints.cs:81`) or `IProjectCatalogReader.GetNarratorAsync`.

A pair that is already an alias of each other cannot occur as two rows: `Matches` folds an exact
name/alias hit at creation (`CharacterLifecycleMutationImplementations.cs:112-114`). So any rule
can assume the two names are not equal and not each other's alias, case-insensitively.

### Is the roster on the project-status path?

**No.** `GET /api/projects/{folder}/status` calls `characters.GetCastCountsAsync`
(`src/Read2Me.App/Api/ProjectStatusEndpoints.cs:63`), which runs three aggregate queries — a
non-narrator count, distinct speaker ids over speech items, and distinct characters with a ready
voice — and returns `CastCounts(Characters, CharactersWithLines, ReadyVoices)`
(`ProjectReader.Characters.cs:325-350`). No names, aliases or per-character counts are loaded.
`ProjectStatusDto` exposes those three integers plus item/attribution/audio/review counts, volume
ids, node summaries and the revision (`ProjectStatusEndpoints.cs:26-37`). The live hub's node
summaries carry `AttributionRemaining`, `AudioRemaining`, `Review`, `AttributionProcessing`,
`AttributionQueued` only (`src/Read2Me.Services/NodeStatus/NodeStatusService.cs:19-27`).

So a host-side duplicate signal on the status path is **one extra query away**
(`GetCharacterSummariesAsync`, or a narrower `Name`/`Aliases`/`LineCount` projection), not
already loaded. Client-side, the roster is loaded on the Cast page (`cast-store.ts:126`) and the
Book page overview (names and aliases only, `BookEndpoints.cs:57`), but not on the project
overview, whose pipeline rows read only the status counts
(`src/Read2Me.Web/src/app/pages/project/pipeline-steps.ts:100`, `:117-120`, `:182`, `:230-231`).

## 5. Settled Failed / Unknown outcomes: storage and counting

### Storage

Outcomes are **in-memory, per host process, not persisted.** `CharacterQueueService` (a singleton,
`src/Read2Me.App/Configuration/ServiceRegistrationExtensions.cs:190`) keeps a
`QueueStateStore<ParagraphKey, ParagraphOutcome>` (`src/Read2Me.Services/Characters/CharacterQueueService.cs:47`),
whose outcomes live in a `ConcurrentDictionary<TKey, TOutcome>`
(`src/Read2Me.Services/Queueing/QueueStateStore.cs:21`). The key is `(ProjectFolderId, ParagraphId)`
(`CharacterQueueService.cs:40`); the value is `ParagraphOutcome(Kind, Reason)` with
`ParagraphOutcomeKind { Failed, Unfinished }` (`:20-22`). A host restart forgets them; nothing
on `Paragraph` or `ParagraphItem` records an outcome (`src/Read2Me.Data/Entities/Paragraph.cs:3-11`,
`ParagraphItem.cs:5-16`). The durable trace of an Unknown answer is only the item's
`CharacterId == null`, which the status path already counts as "unattributed".

How they are set (`CharacterQueueService.Apply`, `:152-187`):

- `Disposition.Unfinished` → `_store.Settle(key, Unfinished(reason))` (`:166-170`);
- `Disposition.Failed` → `_store.Abandon(key, Failed(reason))` (`:171`);
- `Disposition.Complete` settles with no outcome (`:163`).

Where "Unknown" comes from: `AttributionStatus.Unknown` maps to `WorkOutcome.Ok(FailureReason)`
(`src/Read2Me.Services/Characters/CharacterAttributionService.cs:126`); `QueueDisposition.Decide`
turns an `Ok` with nothing applicable into `Disposition.Unfinished(reason)`
(`src/Read2Me.Services/Queueing/QueueDisposition.cs:40`), and an applied answer that still leaves
unstamped Character items also ends `Unfinished` (`CharacterDisposition.DecideApplied`,
`src/Read2Me.Services/Characters/CharacterDisposition.cs:23-26`). The web labels the `Unfinished`
kind **"Unknown"** on paragraph rows (`paragraph-row.ts:47` passes `'Unknown'` as the
`unfinishedLabel` to `queueChip`, `reader-rows.ts:112-125`); `Failed` renders as "Failed".

### Reaching the web and clearing

- Hub snapshot: `ProjectStatusSource.Paragraphs(folder)` walks `attribution.KnownParagraphs(folder)`
  and emits `ParagraphStatusEntry(Status, Outcome)` per paragraph
  (`src/Read2Me.App/Live/ProjectStatusSource.cs:37-47`; record `LiveMessages.cs:49`). `KnownParagraphs`
  is tracked keys ∪ outcome keys filtered to the folder (`CharacterQueueService.cs:202-204`).
- Client: `ProjectStore.paragraphs` is that map, patched by hub deltas
  (`src/Read2Me.Web/src/app/pages/project/project-store.ts:61`, `:74`, `:168`). The chip is a button
  with tooltip "… — Click to clear" that calls `SpeakerAssigner.clearOutcome`
  (`paragraph-row.ts:143-151`, `:83-84`; `speaker-assigner.ts:106-110`) →
  `DELETE /api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome`
  (`src/Read2Me.App/Api/AttributionEndpoints.cs:32-33`, `:65-73`) → `_store.ClearOutcome`
  (`CharacterQueueService.cs:212-216`). Per-paragraph `GET .../attribution/paragraphs/{id}` returns
  the same pair (`AttributionEndpoints.cs:30-31`, `:75-84`).
- Assigning a speaker by hand clears the paragraph's outcome afterwards (`speaker-assigner.ts:58`,
  `:67`); bulk assign clears only paragraphs the store still remembers an outcome for (`:140-146`).
- **`CancelAll` does not clear outcomes**: `QueueStateStore.ClearAll` clears `_status` only
  (`QueueStateStore.cs:130-134`, called from `CharacterQueueService.cs:67`). There is no bulk
  "clear all outcomes" endpoint; `ClearOutcome` is per paragraph.

### Counting

- **Not on `ProjectStatusDto`.** Its `Attribution` is `AttributionCountsDto(Remaining, Processing,
  Queued)` (`ProjectStatusEndpoints.cs:16`, `:79-82`), from the persisted unattributed seed and the
  in-flight queue; `QueueSnapshot` has no outcome count either (`CharacterQueueService.cs:24-37`;
  its comment says settled outcomes "are not busy").
- **Zero queries away, one in-memory pass**: the host can count
  `_store.OutcomeKeys()` filtered by folder (the shape `KnownParagraphs` already uses,
  `CharacterQueueService.cs:202-204`), optionally per `Kind`; `ProjectStatusSource.Paragraphs`
  already materialises exactly this map per folder (`ProjectStatusSource.cs:37-47`).
- **Client-side today**: `project.paragraphs()` holds the same map, and
  `speaker-assigner.ts:142-144` already filters it by `statuses[id]?.outcome`, so a count of settled
  outcomes is `Object.values(paragraphs).filter(e => e.outcome).length` over state the overview page
  already receives from the hub.

Caveat for a "zero settled outcomes" condition: because outcomes are process memory, the count is
zero after any host restart regardless of what the book looks like, and a `Failed` outcome whose
paragraph was later attributed by hand is cleared only through the assign paths above.

## Summary of answers

1. One read (`GetCharacterSummariesAsync` / `GET …/characters/summary`) gives name, aliases, item
   line count, planned/ready voice counts and the seed-narrator flag; the linked-narrator flag is
   overlaid by the endpoint. No creation time and no creator: the entity has three columns.
2. Attribution creates characters through `CharacterResolver.ResolveOrCreateAsync` →
   `CreateCharacterMutation`, writing `Id` and the trimmed LLM string as `Name`. No provenance
   survives. The only duplicate check is case-insensitive exact match on name or alias.
3. Merge moves lines and aliases to the survivor, deletes the merged side's voices and rules,
   optionally adds its names as aliases, follows the narrator link, deletes the row. The dialog
   takes the selected character as merged plus the roster and returns `{survivorId,
   addNameAsAlias}`; the first candidate is preselected. Routes: `cast`, `cast/:characterId`;
   `?discover=1` is the only consumed query param; nothing deep-links into merge.
4. All four rules are computable in memory from the summary read; none is on the status path,
   which loads only three aggregate counts (`GetCastCountsAsync`). One extra roster query.
5. Outcomes are in-memory `(folder, paragraphId) → {Failed|Unfinished, reason}` in the queue
   singleton; "Unknown" is the web label for `Unfinished`. Not on `ProjectStatusDto`; countable
   with one in-memory pass on the host, or client-side from the hub's `paragraphs` map today.
