# Assemble the m4b

```bash
curl -s -X POST http://localhost:5000/api/projects/{folder}/assembly \
  -H 'content-type: application/json' -d '{ "allowPartial": false }'
# 409 with audioRemainingCount → items still need audio (or pass allowPartial:true)
# poll /api/assembly/status (phases: Gather, Silence, ProbeConcat, Encode, Finalize);
# it names the job's folder, and outputFileName once the job completed
curl -s -X POST http://localhost:5000/api/assembly/cancel      # stop the running assembly: 204 (isRunning → false; the hub sends AssemblyCancelled)

curl -s http://localhost:5000/api/projects/{folder}/assembly/outputs   # newest first
curl -s -OJ http://localhost:5000/api/projects/{folder}/assembly/outputs/{fileName}   # audio/mp4 attachment, Range supported
curl -s -X DELETE http://localhost:5000/api/projects/{folder}/assembly/outputs/{fileName}   # 204
```

`{fileName}` must be one of the listed `.m4b` names; anything else — a path, a `.tmp` encode in
flight, a missing file — is a 404.

Output lands at `<workspace>/{folder}/output/<book title>.m4b`
(`_partial_<date>` suffix for partial builds). Requires a valid ffmpeg path in
`/api/settings/audio-processing`.
