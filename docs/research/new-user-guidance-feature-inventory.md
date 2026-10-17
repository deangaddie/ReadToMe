# New-user guidance: inventory of every user-facing action

Research for `.scratch/new-user-guidance/issues/02-feature-inventory.md` (map: `.scratch/new-user-guidance/map.md`). Read on 2026-10-17 from the web app source under `src/Read2Me.Web/src/app` plus `docs/agents/web.md` and `context/web.md`. Every row cites the file that renders the action; line numbers are as of commit `00e6b25` on `main`.

## Vocabulary used below

- **Pipeline steps** are the nine in the map's Notes: 1 Import, 2 Structure (*conditional*), 3 Cast and attribute, 4 Text polish, 5 Voices, 6 Titles (*conditional*), 7 Pauses (*conditional*), 8 Audio, 9 Export. A dash means the action supports the workflow rather than advancing a step (settings, navigation, theming).
- **Reader mode** is `read` / `speakers` / `audio` on the Book page (`context/web.md`; `pages/book/reader-rows.ts`). "Any" means the action does not depend on the mode.
- **Surface** names: *page toolbar*, *toolbar overflow* (the Book page's "Book actions" `more_vert` menu, `book-page.ts` `bookActions()`), *Node menu* (the shared `r2m-node-menu`, entries from `node-menu-entries.ts` `menuEntries()`), *selection bar* (`book-page.ts` `selectionBar()` / `audioSelectionBar()`), *row control* (a control inside a reader row, cast row or card), *dialog*, *settings page*, *stepper* (the Overview's `pipeline()` partial), *rail / app bar* (the shell).
- **Gate** lists what disables or hides the control and whether a `Preflight.ensureReady(kind)` sheet stands in front of the request (`shared/preflight.ts`; the kinds are `attribution`, `audio`, `voicePrompt`, `discovery`, `voiceDesign`, `transcription`, `bookEdit`, `docs/agents/web.md` "Adding a page" step 4).
- **Reason shown?** says whether the user can see *why* a disabled control is disabled (a `data-tooltip`, adjacent text or a status chip). The disabled attribute alone counts as "no".

Today's stepper is six steps (`import`, `cast`, `attribute`, `voices`, `audio`, `export`) in `pages/project/pipeline-steps.ts`; the table maps each stepper action onto the nine-step scheme.

## 1. Projects shelf (`/projects`, `pages/projects/projects-page.ts`)

| Label | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|
| New project | page header button (`projects__new`), repeated as the empty state's action | none; the dialog's Create is off until the form is valid or while the upload runs (`new-project-dialog.ts:127`) | n/a (dialog field errors show inline) | 1 |
| Open (card cover / title, and the `__open` menu item) | row control + card `role=menu` (`ui/project-card.ts:47,91`) | card disabled while its delete is in flight | no | — (navigation) |
| Delete project | card `role=menu` (`ui/project-card.ts:99`), destructive confirm | card `deleting` | no | — |

Empty state: "No projects yet — Import an epub or text file to create your first audiobook project." with the New project button (`projects-page.ts:76-83`). Names the filling action.

## 2. Project overview (`/projects/:folder`, `pages/project/overview-page.ts`, `pipeline-steps.ts`, `project-details-panel.ts`)

Stepper actions (`derivePipeline`, rendered by `ui/pipeline.ts`; every action is disabled while any action is busy, `pipeline.ts:56`):

| Label | Stepper row | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Read book | Import (no content) | none | — | 1 |
| Reread… | Import (content) | destructive confirm; never disabled | — | 1 (and 2 when the splits were wrong) |
| Discover characters | Cast | `!content` | no (the row's detail text says "Find who speaks in the book", not why the button is off) | 3 |
| Open cast | Cast, Voices | never | — | 3 / 5 (navigation) |
| Attribute unprocessed | Attribute | `!content || attribution.remaining === 0`; Preflight `attribution`; one enqueue per volume | partly: the chip reads "0 remaining" when done, nothing when there is no content | 3 |
| Open in Speakers mode | Attribute | `!content` | no | 3 (navigation) |
| Generate needed audio | Audio | `!content || audioRemaining === 0`; Preflight `audio` | partly, as above | 8 |
| Open in Audio mode | Audio | `!content` | no | 8 (navigation) |
| Assemble | Export | `!content`; navigates to the Export page (the real gate lives there) | no | 9 |

Project details panel (`project-details-panel.ts`):

| Label | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Add a cover / Replace cover (file drop) | panel | `busy` on the drop; size/type rejections toast | yes (toast) | 9 (cover art lands in the m4b) |
| Remove cover | panel button | `busy` | no | 9 |
| Title / Author inline edit | `r2m-inline-edit` | none | — | 6 (book title metadata) |
| Narrator only (switch) | panel switch | `busy` | no (hint explains the policy, not the lock) | 5 / 8 |
| Cast (link beside "Narrated by") | panel link | none | — | 5 (navigation) |

No empty state: the stepper is always present once `/status` loads.

## 3. Book page (`/projects/:folder/book`, `pages/book/book-page.ts`)

### 3.1 Page toolbar

| Label | Mode | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|---|
| Hide / Show structure | any | toolbar icon button | none | — | — |
| Reader mode (Read / Speakers / Audio) | any | toolbar segmented control, mirrored to `?mode=` | none | — | 3 (speakers) / 8 (audio) |
| Refresh | any | stale banner (`book__stale`) | shown only when the store is stale | yes (the banner names the reason) | — |

### 3.2 Toolbar overflow ("Book actions", `bookActions()`, `book-page.ts:491-529`)

The trigger itself is disabled while `editor.locked()` or before the overview has loaded (`book-page.ts:515`), with no tooltip. Hidden entirely when the book has no content.

| Label | Mode | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Add book title | any | the trigger's lock only; command `AddBookTitle` | no | 6 |
| Add volume titles | any | shown only when `volumes > 1` | n/a (hidden, not disabled) | 6 |
| Add part titles | any | shown only when `totalParts > volumes` | n/a | 6 |
| Add chapter titles | any | shown only when `totalChapters > totalParts` | n/a | 6 |
| Add pauses | any | trigger lock; command `AddPauses` | no | 7 |
| Edit with AI… | any | trigger lock; full-screen dialog; Preflight `bookEdit` inside the dialog | no | 3 (repetitive speaker fixes) and 4 |
| Reread… | any | destructive confirm | — | 1 / 2 |
| Manual reread… | any | dialog; host validation surfaces in a `[role=alert]` | yes (inside the dialog) | 2 |

### 3.3 Selection action bar

Appears only while the selection is non-empty (`book-page.ts:316-322`).

| Label | Mode | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Attribute | read, speakers | `working || editor.locked()`; Preflight `attribution` | no | 3 |
| Bulk assign speaker (opens `r2m-speaker-menu`: pick / clear / create) | read, speakers | `working || locked || queueBusy`; preview + confirm in `SpeakerAssigner.assignSelection` | **yes** for the queue case (`data-tooltip="Bulk assign waits until attribution has finished"`, `book-page.ts:428`); no for the lock | 3 |
| Clear | read, speakers | never | — | 3 |
| Generate audio | audio | `working || generator.working() || editor.locked()`; Preflight `audio` | no | 8 |
| Clear | audio | never | — | 8 |

### 3.4 Row controls

| Label | Mode | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|---|
| Select paragraph (checkbox) | read, speakers | paragraph gutter (`paragraph-row.ts:94-106`) | rendered only for Character paragraphs; otherwise an empty spacer | no (a narration paragraph simply has no box) | 3 |
| Paragraph speaker chip → pick / clear / create | read | `r2m-speaker-chip` in the paragraph meta | chip is a button only when `selectable && !queued && roster.length > 0` (`paragraph-row.ts:64`); otherwise plain text | no | 3 |
| Item speaker chip → pick / clear / create | speakers | `r2m-speaker-chip` per item (`item-row.ts:108`) | button only when `mode === 'speakers' && !paragraphBusy && !busy && roster.length > 0` | no | 3 |
| Clear outcome (Failed / Unknown chip) | read, speakers | paragraph status column (`paragraph-row.ts:144`) | shown only for a settled outcome | yes (tooltip "… Click to clear") | 3 |
| Select item (checkbox) | audio | item line (`item-row.ts:97-104`) | `!voiced` | **yes** (`data-tooltip="Assign a speaker before generating audio"`) | 8 |
| Delivery cue (click to edit) | speakers, audio | `r2m-item__instructions` button (`item-row.ts:240`) | `editingBlocked` (item or paragraph queued, or editor locked) | no | 4 |
| Dismiss (review chip) | audio | `r2m-item__action` (`item-row.ts:192`) | `ctx.locked` | no | 8 |
| Retry (settled Failed / Unfinished take) | audio | `r2m-item__action` (`item-row.ts:205`) | shown only when voiced; `ctx.generating || ctx.locked`; Preflight `audio` | no | 8 |
| Play generated take | audio | `r2m-audio-player` | shown only when the item has audio | — | 8 |
| Node menu trigger (paragraph) | any | `nodeMenuTrigger` (`paragraph-row.ts:158`) | `queued || ctx.locked` | no (a lock icon appears beside the queue chip while queued, `paragraph-row.ts:156`, which is a hint but not a stated reason) | 2–4, 7 |
| Node menu trigger (item) | speakers, audio | `item-row.ts:122` | `editingBlocked` | no (lock icon while audio-queued) | 2–4, 7 |
| Node menu trigger (pause paragraph) | any | `book-page.ts:571` | `ctx.locked` | no | 7 |
| Node menu trigger (Volume / Part / Chapter heading) | any | `book-page.ts:599`, rule in `structure-node-menu.ts` | attribution processing or queued on the node, or editor locked | no | 2, 3, 6, 8 |

### 3.5 Structure tree (`pages/book/structure-tree.ts`)

| Label | Mode | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|---|
| Expand / collapse, open chapter | any | tree row / chevron | none | — | — |
| Select paragraphs / items of node (tri-state checkbox) | read, speakers (paragraphs); audio (items) | `tree__select` (`structure-tree.ts:200`) | never disabled | — | 3 / 8 |
| Node menu trigger | any | `structure-tree.ts:224` | `structureNodeMenu().disabled` (node busy or editor locked) | no | see 3.6 |

### 3.6 Node menu entries (`node-menu-entries.ts` `menuEntries()`, flows in `node-menu-commands.ts`)

The menu refuses to open while the editor is locked (`node-menu.ts` class doc). Entries are present or absent, never disabled. Node kinds: V = volume, P = part, C = chapter, ¶ = paragraph, I = item, ⏸ = pause paragraph.

| Entry | Node kinds | Mode condition | Dialog / confirm | Step |
|---|---|---|---|---|
| Select unprocessed | V, P, C | paragraph selection (read, speakers) | none | 3 |
| Attribute unprocessed | V, P, C | read, speakers; Preflight `attribution` | none | 3 |
| Release narration | P, C; ¶ when narration-only and selectable | read, speakers | preview + confirm (`SpeakerAssigner.releaseNarration`), skipped for a single paragraph without audio | 3 |
| Select needs audio | V, P, C | audio | none | 8 |
| Generate audio for this node | V, P, C | audio; Preflight `audio` | none | 8 |
| Edit title | V, P, C | any | text prompt | 2 / 6 |
| Edit text | I | speakers, audio | multiline prompt | 4 |
| Add delivery cue… / Edit delivery cue… / Remove delivery cue | I (not pause items) | speakers, audio | text prompt; destructive confirm when the item has audio | 4 |
| Split volume / Split part / Split chapter / Split paragraph | P / C / ¶ / I | any | title prompt (not for an item) | 2 |
| Merge with previous / Merge with next | V, P, C, ¶, I (not ⏸), hidden at the ends | any | none | 2 |
| Insert `<kind>` before / after / at start / at end | every kind the host's Structure rules allow (`ruleFor`) | any | title or text prompt (required flag from the rule) | 2 |
| Insert item before / after | I (not pause items) | speakers, audio | multiline prompt | 2 / 4 |
| Pause / Paragraph pause / Chapter pause / Part pause / Volume pause (before and after submenus) | I | speakers, audio | none | 7 |
| Delete `<kind>` | all; the lone volume loses it (`atMinimumCount`) | any | destructive confirm | 2 |

### 3.7 Edit with AI dialog (`pages/book/edit-with-ai/edit-with-ai-dialog.ts`)

Actions: Analyze (`busy || instruction blank`), Generate, Cancel, Back, Cancel proposing, Select all / none, open row, revert row (`!isUserEdited || retrying`), retry row (`retryingRowId !== null`), Apply (`busy || selection.count === 0`), Start over, Close review. Disabled states carry no tooltip; the phase itself is the explanation. Preflight `bookEdit`. Step 3 and 4.

Book page empty state: "The book has not been read in yet — Read it in from the project overview." with a "Go to overview" link (`book-page.ts:338-345`). Names the page, not the action ("Read book").

## 4. Cast page (`/projects/:folder/cast[/:characterId]`, `pages/cast/`)

### 4.1 Page toolbar (`cast-page.ts:149-188`)

All four buttons share one gate, `toolbarLocked = store.busy() || live.voiceBatch().isRunning` (`cast-page.ts:76`).

| Label | Gate | Reason shown? | Step |
|---|---|---|---|
| Discover | `toolbarLocked`; Preflight `discovery`; `?discover=1` auto-opens it from the stepper | no | 3 |
| Generate voice prompts | `toolbarLocked`; scope dialog when voices exist (Only missing / Regenerate all) + destructive confirm; Preflight `voicePrompt` | no (the tooltip describes what it does, not why it is off) | 5 |
| Generate audio | `toolbarLocked`; Preflight `voiceDesign`; 409 "already running" arrives as a toast | no (same) | 5 |
| Add character | `toolbarLocked`; text prompt | no | 3 |
| Search / Sort | never | — | — |

### 4.2 Narrator banner (`narrator-banner.ts`)

| Label | Gate | Reason shown? | Step |
|---|---|---|---|
| Narrated by (select) | `busy || eligible.length === 0` | no (an empty roster leaves a disabled select) | 5 |
| Change / Unlink | `busy`; Unlink behind a destructive confirm | no | 5 |

### 4.3 Character detail (`character-detail.ts`)

| Label | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Rename (inline edit) | header | hidden on the seed Narrator | — | 3 |
| Merge | header button | `busy || mergeTargets === 0`; merge dialog | no | 3 |
| Delete | header button | `busy`; destructive confirm (extra note when the character narrates) | no | 3 |
| Add alias / Remove alias | Aliases section | `busy` | no | 3 |
| Open line in reader (Speakers mode at the chapter) | Lines list (`r2m-character-lines`) | none | — | 3 |
| Show / Hide context, load more context | Lines list | host cap | — | 3 |

### 4.4 Voices section (`voices/voices-section.ts`, `voices/voice-card.ts`)

| Label | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Add voice | section header | `busy`; dialog (name + source) | no | 5 |
| Set as default (star) | card header | `busy` | no | 5 |
| Rename voice | card header inline edit | none | — | 5 |
| Edit audio (→ voice editor) | card header link | shown only when the voice has audio | — | 5 |
| Delete voice | card header | `busy`; destructive confirm | no | 5 |
| Save description | card body | `busy || !dirty` | no (dirty-gated, self-explanatory) | 5 |
| Voice source: Reference audio / Prompt | segmented control | `busy`; destructive confirm when switching drops data | no | 5 |
| Upload / Replace audio | reference block file drop | hidden while uploading | — | 5 |
| Save prompt | prompt block | `busy || !promptDirty` | no | 5 |
| Regenerate with AI | prompt block | `busy || regeneratingPrompt`; Preflight `voicePrompt`; generate-prompt dialog | no | 5 |
| Generate audio / Regenerate audio | prompt block | `busy || generating || !canGenerate` (blank prompt, `voice-logic.ts:44`); Preflight `voiceDesign`; confirm when the voice is edited | **no**: a blank prompt silently disables the primary button | 5 |
| Save (transcript) / Send to AI (transcribe) | Transcript sub-panel | `busy || !dirty`; `busy || transcribing`; Preflight `transcription`; Send to AI shown only with audio | no | 5 |
| Override tabs + Save override | Advanced sub-panel (`voice-overrides.ts`) | `busy`; note shown when no provider is active | yes for the no-provider case (`.voice-override__note`) | 5 |

Empty state: "No voices yet — Add one, or generate voice prompts for the whole cast from the toolbar." (`voices-section.ts:55-60`). Names both filling actions.

### 4.5 Voice rules section (`voice-rules/voice-rules-section.ts`)

Only rendered once the character has at least one rule (`:53`), so a character with no voices never sees it.

| Label | Gate | Reason shown? | Step |
|---|---|---|---|
| Add rule | `busy || voices.length === 0`; dialog | no | 5 |
| Move up / Move down | `busy || !ability.up/down` | no (end-of-list is self-evident) | 5 |
| Delete rule | `busy`; destructive confirm; hidden on the default rule | no | 5 |

Cast empty states: roster "No characters yet — Discover them with the LLM, or add one by hand." (`cast-page.ts:251`, names both toolbar actions by paraphrase, not label); detail "Select a character — Pick one on the left…" and "Character not found" with a Back link.

## 5. Voice editor (`/projects/:folder/voices/:voiceId/editor`, `pages/voice-editor/voice-editor-page.ts`)

| Label | Gate | Reason shown? | Step |
|---|---|---|---|
| Preview | `!anyTicked || busy` | no | 5 |
| Apply | `applyBlockedReason()` (busy / nothing ticked / no render / stale) | **yes**: the wrapper carries the reason as `data-tooltip` (`:170`), plus the stale hint text | 5 |
| Restore original | shown when edited; `busy` | no | 5 |
| Step ticks and dials | `busy` | no | 5 |
| Back | never | — | — |

Empty state only for a load failure ("Cannot edit this voice" + Back to the cast).

## 6. Export page (`/projects/:folder/export`, `pages/export/export-page.ts`)

| Label | Surface | Gate | Reason shown? | Step |
|---|---|---|---|---|
| Assemble / Assemble… | Readiness card | `!hasContent || assembly.isRunning || starting`; a host 409 for missing audio becomes the partial-build confirm | **partly**: another project's run shows a busy chip naming it (`:201-207`); no content or own run shows nothing; the label grows an ellipsis when audio is missing | 9 |
| Audio processing settings (link) | Readiness hint | none | — | 9 (ffmpeg path) |
| Cancel | Progress card | `cancelling`; shown only while this project's run is going | — | 9 |
| Download | Outputs row | none | — | 9 |
| Delete output | Outputs row icon button | destructive confirm | — | 9 |

Empty state: "No audiobooks yet — Assembled .m4b files appear here." (`:327-332`). Does not name Assemble.

## 7. Shell, navigation and activity centre

| Label | Surface | Gate | Step |
|---|---|---|---|
| Toggle navigation, brand link, breadcrumbs | app bar (`shell/shell.ts`) | none | — |
| Theme quick menu (Light / Dark / Follow system) | app bar `role=menu` | none | — |
| Settings (icon link) | app bar | none | — |
| Overview / Book / Cast / Export | rail context group (`shell-nav.ts`) | none | navigation to steps 1–9 |
| Projects / Settings | rail global group | none | — |
| Toggle activity drawer, job pills with Cancel | activity bar (`activity/activity-bar.ts`, `ui/job-pill.ts`) | none | — |
| Jobs tab: Cancel / Dismiss per job, Dismiss throughput | drawer (`activity-drawer.ts` `jobsTab`) | none | — |
| Services tab: Start / Restart / Shutdown / Refresh status per container, Manage services link | drawer (`dockerControls`, `ui/docker-controls.ts`) | `busy`, or the status forbids it (`canStartService` / `canStopService`) | — (the status chip beside the buttons is the reason) |

Drawer empty states: Jobs "No background work — Attribution, audio generation, voice batches and assembly show up here while they run." (informational, nothing to fill); Services "Services unavailable" / "No managed services".

## 8. Settings pages (`pages/settings/`)

Every settings page implements `hasUnsavedChanges()` and the router's `guardUnsaved` (`docs/agents/web.md`, Layout row for `pages/`). The master list is the shared `r2m-config-list` (`ui/config-list.ts`): Search, New (`busy`), per-row `role=menu` with Make active (disabled when already active), Duplicate, Delete. The editor side is `configEditorFrame` with Cancel / Save (Save gated on dirty + valid).

| Page | Actions beyond the shared list / frame | Gates worth noting | Serves |
|---|---|---|---|
| LLM (`llm/`) | Get models (`loadingModels`); chain card Move up / down / Remove / Add step (`busy`, first / last); self-consistency switch; test console Send (`!canSend`) / Stop | none explained | 3 (attribution, discovery, Edit with AI) |
| Prompts (`prompts/`) | token chips, template textarea, Reset to default (`!resettable || saving`, behind confirm), preview | Overridden chip and warning icon with tooltip | 3, 5 |
| Paragraph TTS / Voice design / Transcription / Similarity (`providers/`) | type select, schema fields, TTS text-processing steps and substitutions (Add / Remove substitution), per-area Test card (`running`, blank prompt, `!canCompare`), voice-design Save / Reset sample text (dirty, over the limit, default) | the sample-text hint explains the Reference Limit; otherwise none | 5, 8 |
| Audio processing (`audio/`) | seven cards each with Save (`!dirty || error || saving`), Test ffmpeg (`testing || saving`), Pick sample / Render preview (`!sample || rendering || !canRender`) | field hints explain values, not locks | 8, 9 |
| AI services (`services/`) | Refresh all (`refreshingAll`), per-card `dockerControls` | status chip beside each | — (preflight prerequisite) |
| Themes (`themes/`) | Follow system switch, New theme, Apply (`selected || busy`), Edit (built-in → "Duplicate and edit" tooltip), Delete (custom only, confirm) | yes: tooltips on Edit and the built-in lock | — |

Settings empty states: LLM and each provider area show "No <noun> configurations yet — Create one to get started." (the New button sits in the list beside it); Prompts "Select a prompt"; Themes "No themes — The host seeds the built-in themes on first request."

## 9. Dialogs opened by the actions above

| Dialog | Opened from | Notes |
|---|---|---|
| New project (`pages/projects/new-project-dialog.ts`) | Projects → New project | Create off until valid; refuses to close during upload |
| Manual reread (`pages/book/manual-reread-dialog.ts`) | Book overflow | host validation shown inline |
| Edit with AI (`pages/book/edit-with-ai/`) | Book overflow | full screen; Preflight `bookEdit` |
| Text prompt / Confirm (`ui/dialogs.ts`) | every Node menu flow, deletes, destructive toggles | shared |
| Preflight sheet (`ui/preflight-sheet.ts`) | every AI action via `Preflight.ensureReady` | bottom sheet, `closedBy: 'none'` |
| Discovery (`pages/cast/discovery-dialog.ts`) | Cast → Discover, stepper → Discover characters | Apply off until a row is included |
| Voice scope (`voices/voice-scope-dialog.ts`) | Cast → Generate voice prompts when voices exist | Only missing / Regenerate all |
| Add voice, Generate prompt, Add voice rule, Merge (`pages/cast/…`) | voice card, voice rules, character header | |
| Sample picker, Theme editor (`pages/settings/…`) | Audio processing, Themes | |

## Flag list A: actions that live only in an overflow or a Node menu

Nothing else on any page offers these; a new user must open a `more_vert` menu to find them.

Book toolbar overflow (`book-page.ts:519-528`), the trigger disabled with no tooltip while the editor is locked:

1. **Add book title** (step 6)
2. **Add volume titles / Add part titles / Add chapter titles** (step 6), and they are *hidden* rather than disabled when the level adds no navigation, so a one-volume book never shows "Add volume titles" and nothing says why
3. **Add pauses** (step 7)
4. **Edit with AI…** (steps 3 and 4)
5. **Manual reread…** (step 2): the only way to re-read with fixed splits, part titles or volume detection; the stepper's Reread… is the plain reread
6. **Reread…** also sits here, but is duplicated on the Overview stepper, so it is not overflow-only

Node menu only (`node-menu-entries.ts`):

7. Every **structure edit**: Split, Merge with previous / next, Insert `<kind>` …, Delete `<kind>` (step 2); the map's Notes already record this
8. **Edit title** for a Volume, Part or Chapter (steps 2 and 6): there is no inline edit on headings or in the tree
9. **Edit text** for an item (step 4)
10. **Add / Edit / Remove delivery cue** (step 4): the cue text itself is clickable once a cue exists (`item-row.ts:240`), but adding the first one is menu-only
11. **Pause before / after** submenus, the per-item pause insertion (step 7)
12. **Release narration** on a Part, Chapter or narration-only paragraph (step 3)
13. **Select unprocessed / Select needs audio** (steps 3 and 8): the tree checkbox selects everything under the node, the "unprocessed" and "needs audio" subsets are menu-only
14. **Attribute unprocessed / Generate audio for this node** on one Volume, Part or Chapter (steps 3 and 8): the Overview offers the whole-book version, the selection bar the hand-picked version, the node-scoped version is menu-only

Card or row menus (minor): **Delete project** (project card menu), **Make active / Duplicate / Delete** configuration (config list row menu).

## Flag list B: disabled states that give no visible reason

The three places that *do* explain themselves, for contrast: Bulk assign speaker while attribution runs (`book-page.ts:428`), the Audio-mode item checkbox for an unvoiced line (`item-row.ts:103`), and the voice editor's Apply (`voice-editor-page.ts:170`). The Export page's Assemble explains one of its three gates (another project's run).

| Control | Where | Silent gate(s) | Why it matters for a new user |
|---|---|---|---|
| Book actions overflow trigger | Book toolbar (`book-page.ts:515`) | `editor.locked()` (write in flight or stale view) or overview not loaded | the only route to titles, pauses, Edit with AI and manual reread goes dark with no explanation |
| Discover characters | Overview stepper | `!content` | the step's detail text does not say "read the book first" |
| Attribute unprocessed / Generate needed audio | Overview stepper | `!content`, or `remaining === 0` | when remaining is 0 the chip says so, but "Not started" with a dead button says nothing |
| Open in Speakers mode / Open in Audio mode / Assemble (stepper) | Overview stepper | `!content` | navigation buttons that die silently |
| Every stepper action while one is busy | Overview (`pipeline.ts:56`) | `busyAction !== null` | the spinner on the busy one is the only hint |
| Attribute (selection bar) | Book, read / speakers | `working || editor.locked()` | no tooltip |
| Generate audio (selection bar) | Book, audio | `working || generator.working() || editor.locked()` | no tooltip |
| Node menu triggers (tree, headings, paragraphs, items, pauses) | Book | node attribution queued / processing, item audio queued, editor locked | a lock icon appears on queued rows but no text; a locked editor shows nothing on the row |
| Speaker chips rendered as plain text | Book rows | `roster.length === 0` (no characters yet), paragraph queued, or wrong mode | a new user with an empty cast sees uninteractive chips and nothing says "add characters first" |
| Paragraph checkbox absent | Book, read / speakers | narration-only paragraph | an empty spacer instead of a box; Release narration is the fix but nothing points to it |
| Delivery cue button | Book rows | `editingBlocked` | no tooltip |
| Dismiss / Retry | Book, audio | `locked` / `generating || locked` | no tooltip |
| Discover / Generate voice prompts / Generate audio / Add character | Cast toolbar (`cast-page.ts:154-183`) | `store.busy() || voiceBatch.isRunning` | the two tooltips present describe the action, not the lock; a running voice batch is visible only in the activity bar |
| Narrated by (select) | Cast narrator banner | `busy || eligible.length === 0` | an empty cast leaves a disabled select with no hint |
| Change / Unlink | Cast narrator banner | `busy` | no tooltip |
| Merge | Character header | `busy || mergeTargets === 0` | a two-character cast (one being the Narrator) gets a dead Merge |
| Delete, Add alias, Remove alias | Character detail | `busy` | no tooltip |
| Add voice, Set as default, Delete voice, source radios, Save description / prompt / transcript | Voices section | `busy` (+ dirty for the saves) | `aria-busy` is set on the card but nothing visible says a write is in flight |
| Generate audio (voice card) | Voices section (`voice-card.ts:421`) | `!canGenerate` (blank prompt) | the primary button of a new prompt voice is dead until text exists; no hint |
| Regenerate with AI / Send to AI | Voices section | `regeneratingPrompt` / `transcribing` | the label changes to "Generating…" / "Transcribing…", which is a reason; busy from another write is silent |
| Add rule | Voice rules section | `busy || voices.length === 0` | the section is hidden without a rule anyway, so this mostly bites after a voice delete |
| Move up / Move down / Delete rule | Voice rules section | `busy`, list ends | end-of-list is self-evident; busy is not |
| Preview | Voice editor | `!anyTicked || busy` | Apply explains itself, Preview does not |
| Assemble | Export | `!hasContent`, own run in progress, `starting` | only the other-project case is explained |
| Config list New / row menu, chain card buttons, Get models, test buttons | Settings | `busy`, `loadingModels`, `running`, blank input | mostly self-explanatory form gating; none carries a tooltip |
| Remove cover, Narrator only switch | Overview details panel | `busy` | no tooltip |

## Flag list C: pages whose empty states do not name the action that fills them

| Page / region | Empty state text | Names the filling action? |
|---|---|---|
| Book page, no content | "The book has not been read in yet — Read it in from the project overview." + Go to overview | **Partly**: names the page, not the button ("Read book") |
| Export, Outputs card | "No audiobooks yet — Assembled .m4b files appear here." | **No**: Assemble is one card up but the empty state does not point at it |
| Cast roster | "No characters yet — Discover them with the LLM, or add one by hand." | Paraphrase only: the buttons are "Discover" and "Add character" |
| Cast detail (no selection) | "Select a character — Pick one on the left to see its aliases and lines." | n/a (navigation) |
| Voices section | "No voices yet — Add one, or generate voice prompts for the whole cast from the toolbar." | Yes (paraphrased: "Add voice", "Generate voice prompts") |
| Voice rules section | none: the section is not rendered until a rule exists | **No**: a user cannot learn that voice rules exist until a first voice creates the default rule |
| Overview stepper | none: steps render as "Not started" with a detail sentence | Partly: Import says "Read the book file into chapters and paragraphs." and offers Read book; Cast / Attribute / Audio say what the step is, not what to press when the button is disabled |
| Projects shelf | "No projects yet — Import an epub or text file…" + New project button | Yes |
| Activity drawer, Jobs | "No background work — Attribution, audio generation, voice batches and assembly show up here…" | n/a (nothing to fill; it does name the sources) |
| Activity drawer / AI services, Services | "Services unavailable" / "No managed services" | n/a |
| Settings: LLM, providers | "No <noun> configurations yet — Create one to get started." | Yes (New is in the adjacent list) |
| Settings: Prompts | "Select a prompt — Each kind is sent to the LLM at a different step." | n/a |
| Settings: Themes | "No themes — The host seeds the built-in themes on first request." | n/a |
| Audio mode with nothing voiced | no empty state: rows render with disabled checkboxes (tooltip on each) | Partly (per-row tooltip only) |
| Speakers mode with no characters | no empty state: chips render as plain text | **No** |

## Observations the map can use

- **Steps 2, 4, 6 and 7 have no stepper row and no toolbar button today.** Their every action is in the Book overflow (titles, pauses, manual reread, Edit with AI) or the Node menu (structure edits, title edits, text edits, cues, per-item pauses). Nothing in the app tells a user these exist until they open a `more_vert` menu.
- **One lock, many silent surfaces.** `BookEditor.locked` (write in flight or stale view, `book-editor.ts:37`) disables the overflow trigger, both selection bars, every Node menu trigger, the cue buttons and Dismiss / Retry at once. The stale case has a banner (`book__stale`); the in-flight case has nothing visible except the controls going grey.
- **Two gates already have the wording pattern the map wants**: the Bulk assign tooltip ("… waits until attribution has finished") and the voice editor's `applyBlockedReason()` table (`editor-logic.ts:60-66`), a pure function returning the sentence or null, rendered as a tooltip on a wrapper span. The second is a reusable shape for "disabled actions that say why".
- **Preflight is uniform.** Every AI action goes through `Preflight.ensureReady(kind)` (seven kinds), so service readiness is never the reason a button is disabled; it is a sheet after the click. The silent gates above are all project-state or in-flight gates.
- **The Overview's "Reread…" and the Book overflow's "Manual reread…" are different operations** with the same first word; only the Book overflow offers the manual one, and only the stepper puts a reread in front of a new user.
- **Deep links that exist today**: `?mode=speakers|audio`, `?chapter=<id>` on the Book page; `?discover=1` on the Cast page (opens the dialog on arrival, `cast-page.ts:119-125`); `cast/:characterId`; `voices/:voiceId/editor`. There is no deep link that opens the Book overflow or a Node menu, so "every step's actions deep-linking to the exact page, mode and menu" (map Notes) needs new link targets for steps 2, 4, 6 and 7.
