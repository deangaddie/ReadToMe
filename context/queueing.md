# Queueing (shared)

Vocabulary common to the Character Queue and the Audio Queue. Each queue's own terms stay in its area file — [attribution.md](attribution.md) and [audio-pipeline.md](audio-pipeline.md).

Use these terms exactly in code, tests, and discussion.

- **Work outcome** — how one unit of queued work ended, reduced to what a retry decision needs: `Ok`, `Failed`, `Unavailable` (provider down — the watchdog is recovering it), `Busy` (provider alive but not ready; today only a llama endpoint still loading its model), plus a reason. Deliberately **narrow**: it says nothing about the *quality* of the work, only about how the provider behaved. Both queues report one; the Audio Queue never produces `Busy`.
  A queue's own richer outcome carries a Work outcome rather than replacing it — an **Attribution outcome** keeps its segments and its Resolved/Unknown distinction, and the audio pipeline keeps its recorded path. Whether a paragraph is *finished* is still decided after apply, from the items, never from the Work outcome.
  _Avoid_: treating `Ok` as "complete" (an `Ok` attribution answer can still leave items unattributed); adding quality states (`Unknown`, `ParseFailed`) to it — `ParseFailed` and a missing LLM config are both `Failed` as far as the queue is concerned; confusing it with **Run outcome** (`LlmRunOutcome`), which is one LLM call's ending and sits a layer below.

- **Disposition** — the terminal transition a queue module executes for one item: `Complete`, `Unfinished`, `Failed`, `RetryOnce`, `RetryAfter`. It is a *value*, decided by a pure function and run by the queue's `Apply(item, disposition)`. Total — every member executes, no arm throws. `Complete` and `Unfinished` both record a completion (both feed the rolling average); `Failed` records none.
  _Avoid_: a settle-kind enum shared by `Complete`/`Unfinished`/`Failed` — each case carries exactly its own data, so an elapsed figure on a `Failed` is not representable. Also avoid calling a `Disposition` an "outcome": an outcome is what the provider did, a disposition is what the queue does about it.

- **Plan** — the first phase's answer: `Now(Disposition)` or `ApplyFirst`. "Apply first" is not something the executor can run, so it lives here rather than as a sixth `Disposition` member. Both queues take `ApplyFirst` on a successful answer.
  _Avoid_: `Disposition?` with null meaning apply-first — it leaves the seam's most important branch unnamed.

- **Apply product** — what applying the work produced, and the only input the *second* phase reads: the count of still-unattributed items on the Character Queue, the recorded relative path on the Audio Queue. The two differ in kind, so phase 2 is per-queue (`CharacterDisposition.DecideApplied` / `AudioDisposition.DecideApplied`) while phase 1 is shared.
  _Avoid_: generalising it to "residual work" — the audio path has no residual figure and its product has nowhere else to ride.

- **Attempt state** — the two independent retry budgets a queued item carries: `Retries` (once-only, spent on `Unavailable` while the watchdog recovers) and `Busies` (unbounded with backoff, spent on `Busy`). It rides the work payload, so its lifetime is the message's — a fresh enqueue resets it, `CancelAll` drops it. Each disposition arm bumps exactly the counter its own decide arm reads, which is what keeps the two budgets independent.
  _Avoid_: a keyed store for attempt state (it needs an eviction rule on every terminal arm, or budgets leak into the next run of the same item); loose `bool Requeued` / `int LoadAttempts` fields with separate writers.
