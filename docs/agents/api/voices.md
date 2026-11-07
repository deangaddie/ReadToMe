# Voices

```bash
# plan voices + design prompts for every character (one LLM call each):
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/prompts \
  -H 'content-type: application/json' -d '{ "regenerateAll": false }'
# poll /api/voice-batch/status until isRunning=false

# synthesise reference audio for every planned voice:
curl -s -X POST http://localhost:5000/api/projects/{folder}/voice-batch/audio
# poll /api/voice-batch/status; stop a running batch early (voices done so far stay):
curl -s -X POST http://localhost:5000/api/voice-batch/cancel   # 204

# inspect / regenerate one voice:
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices
curl -s http://localhost:5000/api/projects/{folder}/voices/{voiceId}
# referenceSeconds is read from the stored WAV header; referenceWarning is set over the 15 s soft Reference Limit
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio # 503 "TTS busy, try again" = retry later
# warned over 15 s; 422 over the 30 s hard Reference Limit, nothing stored — shorten the voice-design sample text
# with an unsaved prompt: designs from it instead of the stored one and stores it with the take, so the
# prompt edit and its audio are one request (a failed take stores neither); no body uses the stored prompt
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio \
  -H 'content-type: application/json' -d '{ "designPrompt": "A gravelly old voice, slow and dry." }'

# reference audio: upload/replace (multipart 'file', 200 MB max; normalised + committed; 422 over the 30 s / 5 MiB
# hard Reference Limit with nothing stored — never trimmed), then transcribe:
curl -s -X PUT http://localhost:5000/api/projects/{folder}/voices/{voiceId}/audio -F 'file=@sample.wav'
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/transcribe   # also stored on the voice

# design prompt without persisting: render the template, edit, send to the LLM, then SetVoiceDesignPrompt:
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/design-prompt/render
curl -s -X POST http://localhost:5000/api/projects/{folder}/characters/{characterId}/design-prompt/generate \
  -H 'content-type: application/json' -d '{ "prompt": "<rendered or edited>" }'

# per-voice settings overrides are sparse patches keyed by the provider's settingsJson names;
# the schema per provider type (ranges, defaults) drives an editor:
curl -s 'http://localhost:5000/api/settings/paragraph-tts/schema?type=VoxCpm2'   # VoxCpm2|Chatterbox|Qwen3Base|Breeze
curl -s 'http://localhost:5000/api/settings/voice-design/schema?type=Qwen3'      # VoxCpm2|Qwen3|Breeze
# an option's null label shows its value; then: SetVoiceTtsSettingsOverride / SetVoiceSettingsOverride commands with json = '{"cfg_value":3.5}' (null clears)

# voice audio editor: clean up a voice's reference audio with post-process steps. The catalog lists
# the voice-scope steps in chain order with their dials (settings-form fields) and defaults; preview
# renders the steps you name (any order — the host orders them) over the voice's ORIGINAL audio and
# answers one playable stage per step; apply names the previewId so what is written is what was heard.
curl -s 'http://localhost:5000/api/audio/steps/catalog?scope=voice'   # de-plosive, denoise, hiss-reduce, consonant-soften, silence-trim
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/preview \
  -H 'content-type: application/json' \
  -d '{ "steps": [ { "stepId": "denoise", "settings": { "strength": 30 } }, { "stepId": "silence-trim", "settings": { "thresholdDb": -40, "padMs": 100 } } ] }'
# each stage's url is /api/previews/{previewId}/{stepId}.wav, valid 30 min; a stage with applied false carries its reason
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/apply \
  -H 'content-type: application/json' -d '{ "previewId": "<from preview>" }'      # → VoiceDto with isEdited=true; 422 if expired / another voice's
curl -s http://localhost:5000/api/projects/{folder}/voices/{voiceId}/original.wav  # the pre-edit audio (404 while unedited)
curl -s -X POST http://localhost:5000/api/projects/{folder}/voices/{voiceId}/editor/restore   # → VoiceDto, original back, edit forgotten

# voice rules: which voice a character speaks in where. The first voice brings the default rule;
# CreateVoiceRule adds an anchored one (fromLevel Volume|Part|Chapter|Paragraph|ParagraphItem +
# fromNodeId; leave to* out for "from here on", repeat the anchor for "just this node"). Rules are
# evaluated in list order and the last passing rule wins; MoveVoiceRule Up|Down reorders the
# non-default ones, DeleteVoiceRule removes one (never the default).
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voice-rules   # *Dangling = the anchor node was deleted; the rule is skipped
curl -s http://localhost:5000/api/projects/{folder}/characters/{characterId}/voice-rules/preview
# in book order, the voice at each chapter's start (null = none resolves; a rule anchored inside a
# chapter is below this grain — see the chapter voices read in [audio.md](audio.md))
```
