# Generate paragraph audio

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/audio/enqueue \
  -H 'content-type: application/json' \
  -d '{ "level": "Chapter", "nodeId": "<chapterId>", "needsAudioOnly": true }'
# or an explicit selection / a single retry (unknown ids ignored; enqueued = what was queued;
# 409 when no paragraph TTS service is active):
curl -s -X POST http://localhost:5000/api/projects/{folder}/audio/enqueue-items \
  -H 'content-type: application/json' \
  -d '{ "itemIds": ["<itemId>", "<itemId>"] }'
# the speech items under a node that have a speaker to read them, with their
# paragraph/chapter/part/volume ids (what an audio selection holds); needsAudioOnly=true keeps
# those still missing a WAV, narratorOnlyMode=true counts unattributed lines as readable:
curl -s 'http://localhost:5000/api/projects/{folder}/nodes/Chapter/{chapterId}/item-ids?needsAudioOnly=true'
# poll /api/audio/queue; per-item:
curl -s http://localhost:5000/api/projects/{folder}/audio/items/{itemId}

# which voice each speech item of a chapter will be spoken in (null voiceName = none resolves;
# narratedBy = the linked narrator's name on narration items):
curl -s http://localhost:5000/api/projects/{folder}/nodes/Chapter/{chapterId}/voices   # keyed by item id

# items whose audio failed normalize/verify, keyed by item id (sparse — absent = passed; state NeedsReview | Dismissed):
curl -s http://localhost:5000/api/projects/{folder}/audio/reviews
```

Enqueueing is idempotent (already-queued items dedupe) and `needsAudioOnly: true`
skips items that already have audio — safe to re-run after failures.
`POST /api/audio/cancel` clears the queue (204).
