# History

Every change to a project's **Project state** is recorded as a **History step** in the same
transaction as the change (ADR 0016). Results and progress (generated audio, audio reviews,
transcription scores, Pipeline checkpoints) never make a step, and nor does a write that changed
nothing.

One request is at most one step, whatever it commits: every write endpoint under
`/api/projects/{folder}` opens a **step scope** for its request, labelled in user terms ("Edit with
AI", "Split chapter", "New character", "Upload voice audio"; a `commands` request is labelled for
its command). An agent's requests are grouped exactly as the web app's are. A request that commits
nothing leaves no step.

A **Background run** is one step too: from the moment the Character Queue or the voice batch makes
the project busy until both have drained, been cancelled or failed, everything they commit
(including characters and aliases attribution creates for itself) is one step labelled
"Attribution", "Voice batch" or, on a step both wrote to, "Attribution and voice batch". Each step is
labelled from the work that landed in it, so a part holding only voice work says "Voice batch". A Background run that
changed nothing leaves no step, and results it commits never split it. Any other write committed
outside a request is a step of its own, labelled from the mutation.

Whoever is interrupted is split; nothing is merged. A request that commits while a Background run's step is
open closes it, so the Background run carries on in a new step: Attribution "part 1", the request's step,
Attribution "part 2". A Background run's commit landing in the middle of a multi-commit request splits the
request the same way.

```bash
curl -s http://localhost:5000/api/projects/{folder}/history
```

- `steps` are newest first. `sequence` is for display; the line is the `parentStepId` chain from
  `headStepId`, the tip.
- `summary` is what changed, in user terms: "4 paragraphs in Chapter 3", "12 paragraphs across 3
  chapters", or a title ("The Storm"). Null when the step changed nothing it can count or place, such
  as a roster or Voice change.
- `endedAt` is when the step closed or, while it is open, its last change, so every step has a time
  span; an interrupted step keeps the time of its last change. `isOpen`, not `endedAt`, says whether
  it is open.
- `isOpen` is true while the request or Background run that owns the step is still running;
  `StepLanded` then `StepClosed` on the live hub bracket it, with a `StepChanged` for each later
  commit in between.
- `note` says what is unusual about the step, or is null: "part 1", "part 2" for a split step, how a Background
  run ended early ("212 of 340 paragraphs, cancelled", "3 of 7 voices, stopped on an error") or with items that
  failed on their own ("3 paragraphs failed", "212 of 340 paragraphs, cancelled · 3 failed",
  "3 of 7 voices, stopped on an error · 2 failed"), and
  "interrupted" for a step the app stopped with open, closed on the next start. Pieces join with
  " · " ("part 2 · interrupted") and carry no brackets: a client shows one as "Attribution (part 2 ·
  interrupted)". A step whose note is set
  after it closed ("part 1", once part 2 lands) is sent again as `StepClosed`.
- `kind` says what opened the step: `Request`, `BackgroundRun`, `Restore`, `Started` (the "History
  started" step) or `Import` (an import or reread of the book). `action` says what kind of action a
  `Request` or `BackgroundRun` step is: `Edit`, `EditWithAi`, `Insert`, `Split`, `Merge`, `Delete`,
  `Title`, `Pause`, `Character`, `Voice` (a voice batch alone, too), `Pronunciation` or `Attribution` (a Background
  run with attribution in it); null on the other kinds. A write committed outside any request or Background run is
  a `Request` step whose action is `Edit`. Tell steps apart by these, never by `label`:
  labels are wording and may change. The hub's `StepLanded`, `StepChanged` and `StepClosed` carry
  them too.
- `busy` names what is queued or running for the project ("Attribution: 128 paragraphs left"), or
  is null when it is idle. `restoring` is true while a Restore of the project runs.
- History starts the first time a project is opened (`GET /api/projects/{folder}` or this call) or
  written. An existing project starts with a "History started" step; a new one starts at its
  import step.
- 404 for an unknown project.

## Viewing a step: `?at=`

Every project read of Project state accepts `?at=<stepId>` and answers exactly as it would have
right after that step for the Book, characters and aliases, Voices, Voice Rules, the Pronunciation
list and project metadata.
Nothing else about the call changes, so an agent inspects the past with the calls it already uses.

```bash
curl -s http://localhost:5000/api/projects/{folder}/history/{stepId}
# → the step, as GET /history lists it, plus restorePreview: { audioDiscardCount } (null for a cantOpen step);
#   404 unknown step, 409 while the project is busy or restoring, 503 when it failed to open for now

curl -s "http://localhost:5000/api/projects/{folder}/characters?at={stepId}"
```

- `?at=` reads: `GET /api/projects/{folder}`, `/status`, `/book`, `/characters`,
  `/characters/summary`, `/characters/{id}/lines`, `/characters/{id}/voices`,
  `/characters/{id}/voice-rules` (and `/preview`), `/voices/{voiceId}`,
  `/paragraphs/{id}/context`, `/pronunciations` (and `/matches`) and the `/nodes/...` reads. The OpenAPI document lists `at` on each.
- 404 for a step the project's History doesn't have, 409 while anything is queued or running for
  the project (`detail` carries the reason, as `busy.reason` does) or while it is restoring
  (`detail` is "Project is restoring", which wins when both hold), 422 for a step marked
  `cantOpen`, 400 when `at` is not a step id. A step recorded before a schema upgrade is migrated
  as it opens. One whose chunks fail to replay (a damaged patchset), or whose old schema fails to
  migrate, is marked `cantOpen` for good: the read that found out answers 422, and the hub sends
  `StepChanged` with the row. A failure that passes (an IO error, a full disk, a busy database)
  answers 503 "try again" and marks nothing; reading the step again may work. There is no retry
  action: nothing clears `cantOpen`.
- State files read at a step too: `/workspace/{folder}/{path}?at={stepId}` serves a Voice's
  reference audio, the cover or the source book file as it was after that step, and
  `/voices/{voiceId}/original.wav?at=` the voice editor's original. 404 when the file didn't
  exist then (an item's take is a result, so it never exists at a step), with the same 400/404/409
  as the other `?at=` reads. Without `?at=` the live file is served as before.
- Reads of live results, progress or scratch refuse `?at=` with 400, saying why: `/history`, audio
  reviews, queue status, Edit with AI runs, Assembly outputs and `/preview-source`. A Voice's
  `referenceSeconds` is still read from its live WAV. `revision` on `/book` and `/status` is the
  current one, not the step's.
- Writes only ever go to the current project: any write with `?at=` answers 400 and writes nothing.
- At a step, `/status` reports nothing in flight and no Pipeline checkpoints or reviews, since
  results and progress are not History. For the same reason an item's `audioFileName` at a step
  is not a record of its audio then: generating audio never makes a step. The live project stays
  writable while a step is read.

## Restore

**Restore** makes the project as it stood after a step current, as a **new** step. Every later step
stays in History, so nothing is lost: a Restore can itself be restored over, and Undo restore is a
Restore to the step that was the tip before.

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/history/{stepId}/restore
# → { step, audioDiscardCount, needsAudioCount }; 404 unknown step, 409 busy or restoring (detail says why), 422 cantOpen,
#   503 when opening the step failed for now (try again)
```

- The project changes exactly as the equivalent hand edits would. Records keep their original ids,
  so Voice Rules and other links to them still hold. Items whose text, speaker or delivery cue
  changes lose their audio and its review; items in both states that didn't change keep their
  current audio; items brought back return without audio. The Pipeline checkpoints it disturbs go
  stale as the equivalent edits would stale them (Structure, Titles and Pauses for a changed Volume,
  Part or Chapter; Text for changed item text).
- Voices, their reference audio (and the voice editor's original), the cover and the source book
  file come back as they were. App-wide LLM and TTS settings are not part of a project and are
  untouched.
- `step` is the Restore step: labelled "Restore to 'Split chapter' (14:02)" (the step's label and
  local time), `restoredFromStepId` the step brought back, and `summary` the audio it cost
  ("Discarded audio on 60 items", null when none). It lands and closes on the hub like any step.
  `step` is null when the project already was that state: nothing changed, and no step is added.
- `audioDiscardCount` is the items whose audio the Restore discarded; `needsAudioCount` is every
  spoken item without audio after it, which adds the items brought back and any that had none
  already: what needs generating. Pauses never count. Both are 0 when `step` is null. `step.parentStepId` is the tip before the
  Restore, the step Undo restore restores.
- `GET /history/{stepId}` answers `restorePreview.audioDiscardCount` beforehand, so a client can say
  what a Restore costs before the click; the Restore counts again as it applies.
- Refused with 409 while anything is queued or running for the project (`detail` is the busy
  reason) or a Restore of it is already running. While one runs, every other write and enqueue for
  the project answers 409 "Project is restoring" at once, and `GET /history` says `restoring: true`.
- `DELETE /api/projects/{folder}` asks the same gate (#346): 409 while the project is busy or
  restoring (`detail` says why), and nothing is cancelled. While a delete runs, every write,
  enqueue, step read and Restore for the project answers 409 "Project is being deleted". Deleting
  is never reported as `restoring` and has no hub event.
- Open Edit with AI sessions for the project are dropped: they were planned against the project as
  it was, so their apply can't write over the restored text blind. Their dialogs hear it on the hub
  (`bookEdit` `Dropped`).
- The receipt is whole-project, so every open view rebuilds.
