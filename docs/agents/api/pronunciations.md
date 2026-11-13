# Pronunciation list

A book's **Pronunciations** respell made-up words for TTS: wherever an item's text has the written
form, TTS is asked to say the spoken form (the item's **Spoken text**); the book text never changes.
Whole words, any case (the match's case carries over to the spoken form), the longer written form
wins where two overlap. Verify scores the transcript against the text and the spoken text and keeps
the lower WER.

```bash
curl -s http://localhost:5000/api/projects/{folder}/pronunciations          # sorted by written form; honours ?at=<stepId>
curl -s 'http://localhost:5000/api/projects/{folder}/pronunciations/matches?chapterIds=…&chapterIds=…'  # honours ?at=
# The Book view's marks: spans in book order in the named chapters, offsets into the item's text; the
# entries they name, sorted by written form. Unknown chapters match nothing.
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations \
  -H 'content-type: application/json' -d '{ "writtenForm": "Watson", "spokenForm": "WAT-sun" }'
# → 201 with the entry plus audioDiscardCount and needsAudioCount. Both forms trimmed, inner whitespace
# collapsed. 422: written form not 1–100 characters or not starting and ending with a letter or digit,
# spoken form not 1–200 characters; 409: another entry has that written form, ignoring case.
# One History step: Pronunciation: add "Watson" (action Pronunciation).
curl -s -X PUT http://localhost:5000/api/projects/{folder}/pronunciations/{id} \
  -H 'content-type: application/json' -d '{ "writtenForm": "Watson", "spokenForm": "WOT-sun" }'
# → 200, same answer and refusals as the add; 404 unknown id. Unchanged forms write nothing, no step.
# Step: Pronunciation: edit "Watson" (action Pronunciation).
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/pronunciations/{id}
# → { audioDiscardCount, needsAudioCount }; 404 unknown id. Step: Pronunciation: delete "Watson" (action Delete).
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations/impact \
  -H 'content-type: application/json' -d '{ "id": null, "writtenForm": "Watson", "spokenForm": "WAT-sun", "delete": false }'
# A dry run, no step: audioDiscardCount is what saving the draft discards; each overlap's draftWins says
# whether the draft's form wins there; each match gives its sentence as written and as spoken.
# 422 an invalid draft (the add's refusal), 404 unknown id.
curl -s -X POST http://localhost:5000/api/projects/{folder}/pronunciations/try -o try.wav \
  -H 'content-type: application/json' \
  -d '{ "itemId": "…", "sentenceStart": 9, "draft": { "id": null, "writtenForm": "Watson", "spokenForm": "WAHT-sun" },
        "asWritten": false, "voice": { "voiceId": "…" } }'
# → the WAV body (audio/wav): the item's sentence starting at sentenceStart (an impact match's), with the list
# (the draft in its entry's place) applied unless asWritten, in the voice named or "item" (the item's own
# resolved voice). Active TTS config and the item's delivery cue, as for its take. Nothing stored, no step.
# 400 no sentence starts there / bad draft or voice; 404 unknown item or voice; 409 "Can't play while
# Attribution is running: it has the GPU." while attribution, audio generation or a voice batch is queued or
# running in any project; 422 no voice resolves, it has no audio or no TTS config is active; 503 TTS busy or down.
```

Any change to the list discards the audio (WAV reference and audio review) of every item that has
audio and whose Spoken text it alters, as a text edit does: new matches on an add, the entry's
matches on a spoken-form edit, old and new matches on a written-form edit, items where an overlap's
winner changes what is said, the old matches on a delete. Items without audio change silently. Add,
edit and delete answer 409 while audio generation is queued or running for the project (a take in
flight would land with the old Spoken text). The receipt carries the `Pronunciations` facet, so
open pages refetch the list.
