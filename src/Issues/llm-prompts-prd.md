# PRD: Improve default LLM prompts — character attribution + voice design

## Problem

Two failure modes in the built-in LLM requests:

1. **Voice design ignores character aging / anchors to an arbitrary point in time.**
   The default voice prompt (`src/Read2Me.Services/Llm/PromptTemplates.cs:74-84`) passes only
   book title, author, and character name. The LLM invents age/gender/accent from prior book
   knowledge, anchored to whatever moment it recalls. Nothing instructs it that one voice must
   serve the whole book.
2. **Attribution returns "unknown" too often**, even when the speaker seems inferable.
   The default attribution prompts (`PromptTemplates.cs:23-72`) mention "unknown" three times,
   give zero inference heuristics (dialog alternation, vocatives, epithet→alias matching), and
   forbid any reasoning ("Return ONLY valid JSON") — small local models attribute much better
   with a short chain-of-thought first. Context window default is only 4 before / 2 after, but
   the attribution tag frequently sits *after* the quote.

## Decisions (agreed with user)

- Voice design fix is **prompt-text only** for now. Feeding character data (aliases via
  `GetCharactersWithAliasesAsync`, sample quotes via `ICharacterReader.GetCharacterLinesAsync`)
  into the template is **deferred** — no service/caller/placeholder changes in this PRD.
  (Note: `Character` entity has no description field; name/aliases/lines are the only inputs
  available when that follow-up happens.)
- Attribution responses gain a `reasoning` field, first in the JSON.
- Context window default bumps 4/2 → 6/4.

## Changes

### 1. Voice design — rewrite default template only

`src/Read2Me.Services/Llm/PromptTemplates.cs:74-84` — replace `DefaultVoicePrompt` with:

```
You are designing a distinctive speaking voice for a character in the
audiobook "{{book_title}}" by {{book_author}}.

Character: {{character_name}}

Write a single concise prompt (plain text, no JSON, no preamble) that can be
passed to a voice-design model to synthesise this character's voice. Describe
age, gender, accent, timbre, pace and emotional default in one paragraph.

Rules:
- One voice must serve the entire book. If the character ages or changes over
  the story, choose a single age and delivery that fits how the character
  speaks for the majority of the book — never a voice tied to one scene or to
  only the character's youngest or oldest moments.
- Prefer stable qualities (pitch, timbre, accent, pace) over transient emotion.
```

No new placeholders. No changes to `VoiceDesignPromptService`, `VoiceOrchestrator`,
`GeneratePromptsPhase`, or `CharacterPresenter`.

### 2. Attribution — reasoning field + heuristics

**`src/Read2Me.Services/Llm/CharacterAttributionResult.cs:16-27`** — response-format examples
gain `reasoning` first:

- Single: `{ "reasoning": "brief note on how you identified the speaker", "character": "Narrator", "voice_instructions": "calm, measured" }`
- Batch: same per entry, with `index`.

Add optional `Reasoning` string property to the result DTOs (System.Text.Json ignores unknown
members anyway, but capturing it enables logging/debug). No parser changes needed;
`JsonCompletionScanner` early-stop is unaffected (it waits for the object/array to close).

**`src/Read2Me.Services/Llm/PromptTemplates.cs`** — in BOTH the single (`:23-47`) and batch
(`:49-72`) templates, replace the Rules block with (wording adapted for batch — "for that
index", "each paragraph"):

```
How to identify the speaker:
- Attribution tags in or around the quote ("said X", "X replied") — these often
  appear AFTER the quote, or in a neighbouring paragraph.
- Vocatives: a character addressed by name inside the quote ("Well, John?") is
  usually NOT the speaker; the character who replies next often is.
- Two-person conversations normally alternate speakers — use the speakers of
  surrounding attributed paragraphs to infer the pattern.
- Match epithets and descriptions ("the old man", "his mother") to the known
  characters list, including aliases.
- Content clues: what is said, who would know it, and each character's manner
  of speaking.

Rules:
- A paragraph has at most one speaking character.
- First write one short sentence in "reasoning" explaining who speaks and why,
  then give the answer.
- Never return pronouns (he, she, they) as the speaker — resolve them to a
  known character.
- Return "unknown" only after using ALL of the above and finding that two or
  more characters remain equally plausible, or the speaker genuinely never
  appears in the text.
- Return ONLY valid JSON. No markdown fences, no text outside the JSON.
- JSON format: {{response_format}}
```

### 3. Context window default 4/2 → 6/4

`src/Read2Me.Services/Llm/PromptTemplates.cs:20-21`:
`DefaultContextParagraphsBefore = 6`, `DefaultContextParagraphsAfter = 4`.
DB-stored user values still win (`LlmPromptService.GetContextWindowAsync`) — only the default
changes.

### 4. Tests

- `src/Read2Me.Tests/Services/PromptTemplatesTests.cs` — update template-text / default-value
  assertions (including 6/4).
- `src/Read2Me.Tests/Services/LlmPromptServiceTests.cs` — update if it asserts 4/2 defaults.
- `src/Read2Me.Tests/Services/Characters/CharacterAttributionServiceTests.cs` — add a case:
  response containing `reasoning` parses correctly (single + batch).
- Fix any test asserting the old voice/attribution default templates verbatim.

## Verification

1. `dotnet build src/Read2Me.App` (warnings-as-errors on).
2. `dotnet test src/Read2Me.Tests`.
3. Manual: run app; Characters tab → generate voice prompt, confirm rendered prompt contains
   whole-book-voice rule. Run attribution on a chapter with previously-unknown paragraphs;
   confirm responses parse (reasoning field present) and unknown rate drops.
