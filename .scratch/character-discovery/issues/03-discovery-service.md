# 03 — Discovery service: prompt, LLM call, parser

**Status:** Done

## Parent

`src/Issues/character-discovery-prd.md` (Character Discovery PRD)

## What to build

A character-discovery service that sends one grammar-constrained request to the **general (active) LLM** — never the attribution chain — asking for the book's notable characters with their known aliases, and returns a parsed, typed result. Verifiable by tests alone; the editable prompt is additionally demoable on the LLM prompts page.

Details, all from the PRD's implementation decisions:

- **Prefactor first:** the chapter-outline-building routine currently inside the AI book-edit planner is extracted to a shared builder so discovery and edit-planning use one implementation. Its twenty-chapter cap is retained.
- Prompt context is title + author + chapter outline + known characters only. No paragraph text is sampled (deliberately deferred — see PRD out-of-scope).
- The discovery prompt template is user-editable like the seven templates that already work this way: a nullable column on the prompt-settings entity, getter/setter/reset on the prompt service, and a section on the prompts page. Deliberately **not** modelled on the book-edit prompts (constants with no override path).
- The service is modelled directly on the existing AI book-edit planner: same dependency set, same live-stream event publishing so the shared LLM stream view works, same early-stop JSON completion scanning, same mapping of infrastructure failure to a distinct `ServiceUnavailable` status via the AI service reporter.
- Resolves its model with the active-config getter — never the chain. One request per invocation. Grammar-constrained response with `reasoning` first, mirroring the attribution schema convention.
- Response shape: an object carrying `reasoning` and a `characters` array of `{ name, aliases[] }`. A parser exposes `TryParse(raw, out characters, out error)` returning a flat `DiscoveredCharacter` record list.
- Outcome mirrors the edit planner: `{ Ok, NoLlmConfigured, Failed, ServiceUnavailable }` plus the characters and a reason.

## Acceptance criteria

- [x] Outline builder is shared; book-edit planner uses the extracted builder and its existing tests still pass.
- [x] Discovery prompt template is editable and resettable on the prompts page, backed by a nullable prompt-settings column with migration.
- [x] No active config yields `NoLlmConfigured`.
- [x] A well-formed canned response yields the parsed characters (`Ok`).
- [x] Malformed JSON yields `Failed`; a transport exception reported by the AI service reporter yields `ServiceUnavailable`; cancellation propagates.
- [x] The rendered prompt contains the book title, the author, the chapter outline and the known characters — asserted as *contains*, not verbatim.
- [x] Service streams tokens through the shared LLM stream broadcaster.
- [x] Parser tests cover: empty character array; a character with no aliases; a missing required field; outright junk.
- [x] Service tests use a faked `ILlmClient` with canned responses, following the book-edit planner tests.

## Blocked by

- 01 — Chain split (discovery must resolve the active config *after* it stops doubling as attribution step 0; that ordering is the reason the split ships first).
