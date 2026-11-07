# Live hub (`/hubs/live`, SignalR)

Push channel for anything that changes without a request: queue roll-ups, per-node and per-item
status, mutation receipts, assembly / voice-batch / watchdog progress, LLM and audio-gen streams,
throughput, and settings changes. Payloads take the agent API's wire form (ADR 0017): camelCase JSON,
enums as PascalCase member names, nulls sent. Multi-kind families carry a `kind` enum. Records live
in `src/Read2Me.App/Live/LiveMessages.cs`, and each is a component of the OpenAPI document beside
the HTTP shapes: `LiveMessageMap` names every family's payload, `LiveHubMethod` the methods a client
invokes.

| Direction | Name | Notes |
|---|---|---|
| client → server | `JoinProject(folder)` / `LeaveProject(folder)` | joins `project:{folder}`; `JoinProject` returns a `ProjectSnapshot` (`folder`, `revision`, `nodes`, `paragraphs`, `items`, `folderAudioRemaining`) |
| client → server | `JoinStream("llm" \| "audio")` / `LeaveStream(kind)` | joins `stream:llm` / `stream:audio`; the current in-progress turn is replayed to the caller first |
| client → server | `GetSnapshot()` | `{ queue, assembly, voiceBatch, watchdog, serviceStatus, throughput, projects }` for the projects this connection joined |
| server → client | `queue` | `{ attribution, audio, escalation }` (`escalation` null unless one is active), debounced 250 ms, everyone |
| server → client | `nodeStatus`, `itemStatus` | per-project deltas (null value = entry gone), debounced 250 ms, project group only |
| server → client | `receipt` | `BookMutationReceipt` with `folder` flattened, `originId` untouched and `effects.facets` a list of single facets (`["Structure", "Audio"]`), project group only |
| server → client | `history` | a History step (`StepLanded` when it appears, `StepChanged` when a later commit extends it while open, `StepClosed` when it closes or its note changes once closed) with `folder` and the `step` row as `GET /history` lists it, project group only. A step recorded with no request or run open lands and closes together; a request's or Background run's step lands open and closes when it ends. `BusyChanged` carries no step: the project's busy `reason` moved ("Attribution: 128 paragraphs left"), or went idle, when `reason` is null. Only folders someone has joined get it, and only on a change; the `GET /history` list carries the busy answer for a fresh reader. `Restoring` brackets a Restore: `phase` `Started`, then `Finished` with the Restore `step` it added (null when it added none or failed) |
| server → client | `bookEdit` (`Dropped`) | a Restore dropped an Edit with AI session: `program` is its id, `reason` "The project was restored — run it again"; project group, unlike the run's other `bookEdit` messages, which go to the connection that started it |
| server → client | `assembly`, `voiceBatch`, `watchdog`, `settingsChanged` | pass-through (encode progress stepped at 1 %, batch progress debounced), everyone. A `voiceBatch` `VoiceUpdated` with fresh audio carries `referenceSeconds` / `referenceWarning` (the Reference Limit). Every `assembly` message names the run's `folder`; `Completed` carries `outputFileName`. The snapshot's `assembly.encodePercent` is 0–100 (the REST status keeps the 0–1 fraction) |
| server → client | `llm`, `audioGen` | stream groups only; LLM `Delta` messages are 100 ms batches of `thinking` + `content`; `audioGen` `ModelLoading` carries an all-zero (`Guid.Empty`) id, since the TTS gate sits below any item |
| server → client | `throughput` | `ThroughputSnapshot`, once a second while a run is active plus once when it ends |
| server → client | `llmTest` | how an LLM settings test send ended (`Done`, `Failed` with `reason`, `Cancelled`), sent only to the `connectionId` that started it; the tokens are on `stream:llm` |
| server → client | `bookEdit` | one AI book-edit proposal run (`Progress`, `Done`, `Failed`), sent only to the `connectionId` that started it; `Done` carries every row it landed, and `cancelled` says whether a cancel cut it short |
| server → client | `serviceStatus` | `{ name, status, op, ok, error }` — a managed service's last observed `AiServiceStatus`, after every probe, lifecycle op (then `op` names it, `Start` | `Restart` | `Shutdown`, with its outcome; null otherwise) and watchdog transition; everyone |
| server → client | `preflight` | one pre-flight run (`Stage` with `name` + `stage` + `error`, then `Done` with `ok` + `reason`), sent only to the `connectionId` that started it; every message carries the `run` id the 202 answered |
| server → client | `series` | `{ seriesId, kind: Changed \| Deleted }`, a "read again" signal with no payload, debounced 250 ms per series, sent on every registry write and on a member book's commit to its Characters, aliases, Voices or Voice Rules; everyone |
| server → client | `setupCheck` | the Setup check's Full check: `Progress` then `Done`, each carrying the whole `report` (the current step's rows `Checking`, `fullRun` where the run is; null on `Done`); sent to every client |

Invalid folders and unknown stream kinds fail the invocation with a `HubException`. Node status is
only computed for folders `NodeStatusService` has seeded — call `GET /api/projects/{folder}/status`
after joining (the web project shell does) to seed it. Example with the .NET client:

```csharp
var conn = new HubConnectionBuilder().WithUrl("http://localhost:5000/hubs/live").Build();
conn.On<JsonElement>("receipt", r => Console.WriteLine(r));
await conn.StartAsync();
var snapshot = await conn.InvokeAsync<JsonElement>("JoinProject", "dracula");
```
