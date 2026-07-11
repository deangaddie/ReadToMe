# PRD — Character Discovery + splitting the general LLM from the attribution chain

Status: **locked, not built**
Depends on: `attribution-escalation-prd.md`, `attribution-queue-wide-escalation-prd.md` (both built)

---

## Problem Statement

Character attribution runs an escalation chain of LLM configs. Step 0 is deliberately a small, fast,
weak model — it should answer the easy lines cheaply and say "unknown" on the hard ones so a bigger
model picks them up.

Two things stop that working.

**1. Step 0 has no roster to match against.** The attribution prompt injects `{{known_characters}}` — the
characters that already exist in the project database. On a freshly imported book that list contains
only the seeded `Narrator`. The weak model therefore has nothing to recognise: it either answers
`unknown` or invents an unlisted name, and nearly every dialogue line escalates. The escalation chain
was built to handle the *hard* lines; today it handles almost all of them, which is slow and burns GPU
time swapping model presets.

**2. The "active" LLM config is overloaded.** `AppSettings.ActiveLlmConfigId` currently means two
unrelated things at once:

- the general-purpose LLM used by voice-prompt generation, AI book edits, container warm-up and the
  settings test panel; **and**
- step 0 of the attribution chain — the chain is resolved as `[active, ...escalationTail]`.

So a user who correctly nominates a tiny model as attribution step 0 silently gets that same tiny model
for voice design and AI book edits. There is no way to say "use the small model first for attribution,
but always use the big model for everything else". Any new general-purpose LLM feature — such as
discovering the character roster — would inherit the same problem.

## Solution

Two changes, in order.

**Split the attribution chain from the general LLM.** The attribution chain becomes a standalone ordered
list of LLM configs that includes its own first step. `ActiveLlmConfigId` reverts to meaning exactly one
thing: the default, general-purpose LLM. Nothing prepends it to the chain any more. Users configure the
two independently on the LLM settings page. If the chain is left empty, attribution falls back to the
general LLM as a single step, so nothing breaks for a user with one config who never opens the chain
panel.

This requires no new flag on the LLM server config entity — the distinction is a property of the
settings, not of the server.

**Add character discovery.** A "Discover characters" button on the Characters tab sends one
grammar-constrained request to the *general* LLM asking for the notable characters of the book, each with
their known aliases. The user is shown the returned list in a review dialog where they can rename a
character, add or remove aliases, and exclude any character they don't want. On accept, the characters
are created. Characters that already exist are recognised and skipped; new aliases for an existing
character are still added.

The result: before the attribution queue runs, the roster is populated with the book's notable
characters and their aliases, giving the weak step-0 model a real list to match against.

## User Stories

### Splitting the chain

1. As an audiobook producer, I want the attribution chain to be a list I build explicitly, so that I control which model runs first without that choice leaking into other features.
2. As an audiobook producer, I want the first model in the attribution chain to be reorderable and removable like every other step, so that the chain has no special hidden entry.
3. As an audiobook producer, I want to nominate a large model as the default LLM while a tiny model is attribution step 0, so that voice prompts and AI book edits keep their quality while attribution stays cheap.
4. As an audiobook producer, I want the LLM settings page to tell me what the default configuration is actually used for, so that I understand the split without reading the source.
5. As an audiobook producer with a single LLM configured, I want attribution to work without me ever visiting the chain panel, so that a fresh install is usable out of the box.
6. As an audiobook producer, I want the chain panel to tell me which configuration attribution will fall back to when the chain is empty, so that the fallback is not invisible behaviour.
7. As an audiobook producer, I want deleting an LLM configuration to remove it from the attribution chain, so that the chain never references a config that no longer exists.
8. As an audiobook producer, I want deleting the configuration that happened to be step 0 to shorten the chain rather than silently promote something else, so that the chain stays exactly what I built.
9. As an audiobook producer, I want a corrupted chain setting to degrade to "no chain" rather than crash attribution, so that a bad settings row never blocks a run.
10. As a developer, I want a single method that returns the resolved attribution chain, so that attribution has one place to ask "which models, in what order".
11. As a developer, I want every non-attribution LLM caller to keep using the active/default config unchanged, so that the split is a pure narrowing of meaning and not a migration for those callers.

### Character discovery

12. As an audiobook producer, I want a "Discover characters" button on the Characters tab, so that I can seed the roster before running attribution.
13. As an audiobook producer, I want discovery to run on the default (large) LLM, so that the character list is as accurate as possible and is not degraded by the tiny attribution step-0 model.
14. As an audiobook producer, I want the discovery request to include the book title, author and chapter outline, so that a well-known published work is recognised from the model's own knowledge.
15. As an audiobook producer, I want the discovery request to include the characters I already have, so that the model does not waste output re-proposing them.
16. As an audiobook producer, I want each discovered character to come with their known aliases, so that the attribution prompt can match a character named several ways in the text.
17. As an audiobook producer, I want to see the discovery request stream live, so that I know the model is working and can see what it is thinking.
18. As an audiobook producer, I want to cancel a discovery request in flight, so that a slow or wedged model does not block me.
19. As an audiobook producer, I want to review the discovered characters before anything is created, so that a hallucinated character never silently enters my project.
20. As an audiobook producer, I want to edit a discovered character's main name in place, so that I can fix a model that returned "Mr. Bilbo Baggins" when I want "Bilbo".
21. As an audiobook producer, I want to add an alias to a discovered character in the review list, so that I can supply a nickname the model missed.
22. As an audiobook producer, I want to remove an alias from a discovered character in the review list, so that a wrong or duplicated alias is not created.
23. As an audiobook producer, I want to exclude a discovered character from the list, so that minor or wrongly-identified characters are not created.
24. As an audiobook producer, I want select-all and select-none controls, so that I can quickly accept or reject a long list.
25. As an audiobook producer, I want discovered characters that already exist in my project to be marked as existing, so that I can see at a glance what is new.
26. As an audiobook producer, I want accepting an already-existing character to be harmless, so that re-running discovery never creates duplicates.
27. As an audiobook producer, I want new aliases proposed for an existing character to still be added, so that discovery can enrich a roster I built by hand.
28. As an audiobook producer, I want to be told clearly when no LLM is configured, so that I don't wait for a request that will never happen.
29. As an audiobook producer, I want to be told when the LLM container is unreachable, distinct from the model returning nonsense, so that I know whether to restart a container or retry.
30. As an audiobook producer, I want the discovery prompt to be editable on the LLM prompts page like every other prompt, so that I can tune it for a genre or a stubborn model.
31. As an audiobook producer, I want to reset the discovery prompt to its built-in default, so that a bad edit is recoverable.
32. As an audiobook producer, I want pre-flight to check the LLM container before discovery runs, so that I get the same "start the container?" prompt I get for other AI tasks.
33. As an audiobook producer, I want the discovery button to be available even on a project whose only character is the narrator, so that discovery is usable at exactly the moment it is most valuable.
34. As an audiobook producer, I want to run attribution after discovery and see fewer lines escalate, so that the queue completes faster.

## Implementation Decisions

### The chain is a plain ordered list

- `AppSettings` grows one column holding a JSON array of LLM server config IDs representing the **whole**
  attribution chain, index 0 first. The existing escalation column is renamed to it; the escalation
  column has never shipped (its migration is untracked), so the migration is edited in place rather than
  stacked with a rename. Existing local development databases must be recreated or hand-patched.
- `ActiveLlmConfigId` is unchanged in shape and now means only "the default, general-purpose LLM".
- The self-consistency setting is unchanged.

### `LlmSettingsService` surface

Renamed, with changed semantics:

| Before | After | Change |
|---|---|---|
| get escalation config IDs → *tail only* | get attribution chain IDs → **full list** | now includes step 0 |
| set escalation config IDs | set attribution chain IDs | — |
| get escalation chain = `[active, ...tail]`, deduped | get attribution chain = resolved stored list | **no active prepend** |

Fallback rule, resolved inside the service so no caller has to know it:

- Stored chain resolves to one or more configs → return them, in order.
- Stored chain resolves to zero configs and an active config exists → return `[active]`.
- Otherwise → return empty. Callers already map empty to `NoLlmConfigured`.

Existing behaviour retained: lazy prune of dangling IDs on read (re-saving the pruned list), eager prune
on config delete, dedupe by ID, corrupted JSON deserialises to an empty list.

### Attribution service

One call-site rename. The chain-walk, the single-step short-circuit when the chain has length one, the
per-step selection of prompt style and batch size from that step's config, and self-consistency on
non-final steps are all unchanged. This is the whole point of the design: the chain resolution moves,
the chain consumption does not.

### Chain UI

- The escalation presenter loses its `Primary` / `Escalation` split and exposes a single flat `Chain`
  collection. Every row is reorderable and removable, including index 0. The presenter additionally
  exposes the active config purely so the panel can name the fallback.
- The panel loses its fixed read-only "primary (active)" row and its "select an active configuration
  above" alert. When the chain is empty it shows the fallback hint instead.
- The LLM settings page relabels the active chip and adds helper text stating that the default config is
  used for voice prompts, AI book edits and character discovery, while attribution uses the chain.

### Character discovery service

- Modelled directly on the existing AI book-edit planner: same dependency set, same live-stream event
  publishing so the shared LLM stream view works, same early-stop JSON completion scanning, same
  mapping of an infrastructure failure to a distinct `ServiceUnavailable` status via the AI service
  reporter.
- Resolves its model with **get active config** — never the chain. This is the reason the split ships
  first.
- One request per invocation. Grammar-constrained response with `reasoning` first, mirroring the
  attribution schema convention.
- Response shape: an object carrying `reasoning` and a `characters` array of `{ name, aliases[] }`. A
  parser exposes a `TryParse(raw, out characters, out error)` returning a flat `DiscoveredCharacter`
  record list.
- Outcome shape mirrors the edit planner: `{ Ok, NoLlmConfigured, Failed, ServiceUnavailable }` plus the
  characters and a reason.

### Prompt

- Prompt context is **title + author + chapter outline + known characters only**. No paragraph text is
  sampled. Rationale: keeps the request tiny and leans on the model's world knowledge of published
  works, which is the common case. Sampling body text for obscure or original manuscripts is a possible
  follow-up, deliberately deferred.
- The outline-building routine currently living inside the book-edit planner is extracted to a shared
  builder so discovery and edit-planning use one implementation. Its twenty-chapter cap is retained.
- The discovery template gains a nullable column on the prompt-settings entity, a getter/setter/reset on
  the prompt service, and a section on the prompts page — following the seven templates that already
  work this way. It is deliberately **not** modelled on the book-edit prompts, which are constants with
  no override path.

### Review dialog

- Two phases: discovering, then review. There is no instruct phase — the button is the instruction.
- Discovering shows an indeterminate progress bar, a cancel control backed by a cancellation token, and
  the collapsible shared LLM stream view.
- Review binds each proposed character to a mutable view model carrying name, alias list, an include
  flag, and an "already exists" flag. The exists flag is computed against the loaded roster using the
  existing character resolver's name-or-alias match, testing both the proposed name and each proposed
  alias.
- Alias editing uses the closable-chip plus inline text field idiom already used on the character detail
  panel. The scrolling review list and its select-all / select-none controls follow the book-edit review
  dialog.
- The dialog returns the accepted view models; it performs no writes itself.

### Apply

**No new command and no new handler.** The accepted rows are applied by looping the existing commands:

- Create-character is already idempotent — it returns the existing character's ID when the name matches
  an existing character's name *or* any of its aliases.
- Add-character-alias already deduplicates against the target character's name and existing aliases.

Together these give "skip existing characters, add only new ones, but still enrich an existing character
with new aliases" for free. The loop lives on the character presenter as a single method so that it is
testable without a dialog, following the presenter's existing execute-and-reload idiom.

### Pre-flight

A new AI task kind for character discovery, mapped by the task-requirements resolver to the active LLM's
base URL — the same URL the attribution and voice-prompt kinds already resolve to. Pre-flight is invoked
from the Characters tab before the dialog opens, not from inside the dialog, matching the existing note
there that the batch runner is a singleton while the dialog service is per-circuit.

### Known gaps left untouched

Container warm-up and pre-flight both consider only the active config. After this change, an attribution
chain step pointing at a different endpoint is still neither warmed nor pre-flighted. This is a
pre-existing gap, made no worse and no better here. It deserves its own issue.

## Testing Decisions

A good test here asserts observable behaviour at a public boundary: what the settings service returns for
a given persisted state, what the discovery service returns for a given fake LLM response, and which
commands the presenter dispatches for a given set of accepted rows. Tests do not reach into private
chain-walk internals, do not assert on log lines, and do not assert prompt text verbatim — only that the
prompt *contains* the facts it is contractually required to carry.

### Seams

Prefer the seams that already exist. There are three, and one new one.

1. **`LlmSettingsService` against a real in-memory/SQLite `Read2MeDbContext`.** The existing settings
   tests already use this seam. Chain semantics are tested entirely here.
2. **`ILlmClient`, faked.** The existing attribution-chain tests and book-edit planner tests both fake
   the LLM client and assert on the prompt handed to it and the outcome produced from a canned response.
   Discovery tests use this seam identically.
3. **`LlmSettingsService` subclassed with virtual overrides.** The existing attribution-chain tests
   already override the chain getter; the rename propagates. No new seam.
4. **`CharacterPresenter.ApplyDiscoveredCharactersAsync` over a fake command handler.** *New,* but placed
   at the highest available point: the presenter already funnels every mutation through the command
   handler, so asserting on the dispatched command sequence tests the whole apply behaviour without a
   dialog, without a database and without a UI. The dialog itself is left as a thin, untested mapping
   layer over this method.

The review dialog is not given its own test seam. Its logic — exists detection, alias mutation, include
filtering — either lives in the view-model record or is a direct call into the presenter, and is covered
there.

### Modules under test

- **Settings service.** Chain resolution no longer prepends the active config; an empty chain falls back
  to the active config; an empty chain with no active config yields empty; a chain containing a deleted
  config prunes and re-saves; deleting a config removes it from the chain including at index 0;
  corrupted JSON yields an empty chain.
- **Escalation presenter.** The flat chain replaces primary/escalation; index 0 can be moved down and
  removed; the fallback config is surfaced when the chain is empty.
- **Attribution service.** No new tests — the existing chain tests must continue to pass unchanged after
  the rename. That is the regression signal for "chain consumption did not change".
- **Discovery service.** No active config yields `NoLlmConfigured`; a well-formed response yields the
  parsed characters; malformed JSON yields `Failed`; a transport exception reported by the AI service
  reporter yields `ServiceUnavailable`; cancellation propagates; the rendered prompt contains the book
  title, the author, the outline and the known characters.
- **Discovery parser.** Empty character array; a character with no aliases; a missing required field;
  outright junk.
- **Character presenter.** Included rows produce exactly one create command each, plus one add-alias
  command per alias; excluded rows produce nothing; a row whose only change is a new alias on an
  existing character still produces the alias command.

### Prior art

- Settings-service tests over a real context: the existing LLM settings service tests.
- Faked LLM client plus canned response plus prompt assertion: the book-edit planner tests.
- Chain-walk behaviour with a subclassed settings service: the attribution chain tests.
- Presenter tests over a fake command handler: the existing character presenter tests.
- A panel presenter tested without any Blazor rendering: the attribution escalation presenter tests.
- End-to-end panel interaction: the existing attribution escalation panel E2E test, which needs its
  selectors and labels updated.

## Out of Scope

- **Sampling book text into the discovery prompt.** Discovery relies on title, author, outline and the
  model's world knowledge. Obscure or original manuscripts will discover poorly. Feeding sampled
  dialogue-bearing paragraphs is a follow-up, and needs a sampling strategy and a token budget.
- **Discovering per-chapter or per-scene casts.** One whole-book request only.
- **Assigning voices to discovered characters.** Discovery creates characters and aliases; the existing
  batch voice-prompt generation handles voices.
- **Merging a discovered character into an existing one under a different name.** The existing merge
  dialog covers that after the fact. Discovery only creates and enriches.
- **Warming or pre-flighting attribution chain steps that live on a different endpoint from the active
  config.** Pre-existing gap, separately tracked.
- **Any change to how the attribution chain is walked** — escalation triggers, self-consistency,
  queue-wide ordering, per-step prompt style and batch size all stay exactly as built.
- **Removing the fallback.** An empty chain falls back to the active config by design; requiring an
  explicit chain was considered and rejected as hostile to a fresh install.

## Further Notes

**Slicing.** Four slices, in order. Slices one and two ship user value on their own: they stop the weak
attribution model leaking into voice design and AI book edits, which is a bug in its own right,
independent of discovery ever being built.

1. Chain split — settings entity, migration, settings service, the single attribution call-site, service
   tests.
2. Chain UI — presenter, panel, settings page labels, presenter and E2E tests.
3. Discovery service — outline builder extraction, prompt template and prompt-settings migration,
   discovery service, schema, parser, tests.
4. Discovery UI — the new pre-flight task kind, the dialog, the presenter apply method, the toolbar
   button, tests.

**Migration caveat.** Because the escalation migration is edited in place rather than superseded, any
local application database that already applied it carries the old column name and will not pick up the
rename. Recreate the local application database, or rename the column by hand, before running.

**Verification.** With two LLM configs — one small, one large — nominate the large one as default and
build the chain as `[small, large]`. Confirm the large config does not auto-appear in the chain and that
emptying the chain surfaces the fallback hint. Run discovery and confirm from the llama container log
that the *large* preset was loaded. Confirm the review dialog flags the seeded narrator as existing, that
editing a name, removing an alias and unchecking a row all behave, and that accepting creates exactly the
included characters. Re-run discovery: everything is now flagged existing and accepting is a no-op. Then
run attribution and confirm step 0 loads the *small* preset and that fewer items escalate than before
seeding. Finally run voice-prompt generation and an AI book edit and confirm both hit the *large* model —
that regression is the reason the split exists.
