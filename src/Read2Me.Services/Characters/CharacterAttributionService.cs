using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Data;
using Read2Me.Services.Llm;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Characters
{
    public enum AttributionStatus { Resolved, Unknown, NoLlmConfigured, Failed, ServiceUnavailable, ModelLoading }

    /// <summary>
    /// Why a step's answer looks suspect and might be re-asked on a stronger config.
    /// <see cref="Inconsistent"/> is produced by the self-consistency check when two samples
    /// disagree; <see cref="Unknown"/> covers every dialog item the answer left unstamped,
    /// unanswered ones included (see <see cref="ItemAttributionEscalation.DeriveTrigger"/>).
    /// <para>
    /// There is no "the answer lost the paragraph's dialog" trigger: per ADR 0005 an answer cannot
    /// delete an item, so the defect it existed for is unreachable. Do not re-add it.
    /// </para>
    /// </summary>
    internal enum EscalationTrigger { None, Unknown, UnlistedName, ParseFailure, Inconsistent }

    /// <summary>An <see cref="AttributionOutcome"/> plus the quality trigger that classifies it.</summary>
    internal sealed record StepOutcome(AttributionOutcome Outcome, EscalationTrigger Trigger);

    /// <summary>
    /// Per-step config + flags for a chain step. <see cref="IsFinal"/> gates final-step behaviour
    /// (an unparseable chunk is re-asked as chunks of 1, one extra ask per paragraph; unlisted-name
    /// acceptance).
    /// <see cref="SelfConsistency"/> gates double-sampling (slice 004): set only on non-final steps
    /// when the global toggle is on. <see cref="Thinking"/> is the chain entry's per-rung thinking
    /// flag — off by default, opted into per rung: attribution answers ride the schema and the
    /// prompt's context, so hidden thinking adds minutes per chunk (measured at 75-95% of
    /// generation) without changing the speakers it picks.
    /// <see cref="Style"/> is the rung's <em>effective</em> attribution prompt style, already
    /// resolved against the config's own by <see cref="LlmSettingsService.GetAttributionChainAsync"/>;
    /// it is the only place the style should be read from, since a rung may deliberately ask with a
    /// different style than its config's default (a Simple cold rung on a Full config).
    /// </summary>
    internal sealed record ChainStepOptions(
        LlmServerConfig Config,
        bool IsFinal,
        bool SelfConsistency,
        bool Thinking = false,
        AttributionPromptStyle? Style = null,
        double? TemperatureOverride = null)
    {
        /// <summary>The style to ask with: the rung's own, falling back to the config's.</summary>
        public AttributionPromptStyle EffectiveStyle => Style ?? Config.PromptStyle;

        /// <summary>
        /// A repeat ask of the same paragraph: off greedy, so a second answer to an identical prompt
        /// cannot be a verbatim first. Uses the config's own temperature when &gt; 0, else 0.7 — a
        /// null/0 temperature is greedy, useless to both self-consistency (it would always agree) and
        /// the same-rung parse-failure retry (it would fail identically). Rides the request as an
        /// override, never a config copy.
        /// </summary>
        public ChainStepOptions Resampled() => this with
        {
            TemperatureOverride = Config.Temperature is { } t && t > 0 ? t : 0.7,
        };
    }

    /// <summary>
    /// Which server answered, and what it actually said. Carried into classification purely so an
    /// answer that fails alignment can be logged in full — the raw is otherwise unrecoverable, and
    /// which model produced it is not recorded anywhere else.
    /// </summary>
    internal sealed record AnswerProvenance(string ConfigName, string Model, string? Raw)
    {
        public static AnswerProvenance From(LlmServerConfig config, string? raw) =>
            new(config.Name, string.IsNullOrWhiteSpace(config.Model) ? "(server default)" : config.Model, raw);
    }

    /// <summary>
    /// One paragraph's attribution answer, ready to apply: who the LLM said speaks each item, by the
    /// index the prompt gave it, and the map that turns those indices back into items. Boundaries are
    /// frozen (ADR 0005), so an answer carries no text and can neither add nor remove an item.
    /// <para>
    /// The two halves are one value because neither is usable alone: an index means nothing without
    /// the map, and the map is stale beside any other paragraph's answer. Building them together also
    /// makes "index-aligned" un-forgettable at the one site that knows both.
    /// </para>
    /// </summary>
    /// <param name="ItemIds">
    /// The item behind each prompt index, recorded when the prompt was built — <c>null</c> where the
    /// index names a narration item, which nothing may stamp (spec §2). Recorded rather than
    /// re-resolved, because the apply happens after the answer leaves the chain and the user may have
    /// edited the paragraph meanwhile.
    /// </param>
    public sealed record AttributionAnswer(
        IReadOnlyList<AttributedItem> Items,
        IReadOnlyList<Guid?> ItemIds)
    {
        /// <summary>The model's <paramref name="answered"/> indices against the items it was asked about.</summary>
        public static AttributionAnswer For(
            IReadOnlyList<AttributedItem> answered, IReadOnlyList<ContextItem> items) =>
            new(answered, [.. items.Select(i => i.IsDialog ? i.ItemId : (Guid?)null)]);
    }

    /// <summary>
    /// A paragraph's attribution result. <see cref="Answer"/> is non-null for
    /// <see cref="AttributionStatus.Resolved"/> and for an <see cref="AttributionStatus.Unknown"/>
    /// that carries an answer, and null when there is nothing to apply (empty paragraph, infra/parse
    /// failure). <see cref="AttributionStatus.Unknown"/> means the answer left ≥1 dialog item
    /// unstamped; whether the paragraph ends up unattributed is decided on apply, per item.
    /// </summary>
    public sealed record AttributionOutcome(
        AttributionStatus Status,
        AttributionAnswer? Answer,
        string? FailureReason)
    {
        /// <summary>
        /// This answer reduced to provider behaviour — what the queue decides retries and settles
        /// from. Quality stays on <see cref="Status"/>: both <see cref="AttributionStatus.Resolved"/>
        /// and <see cref="AttributionStatus.Unknown"/> are <see cref="WorkOutcome.Ok"/>, because an
        /// answer that left a speaker unidentified is still an answer — whether the paragraph is
        /// finished is decided after apply, from the items.
        /// A missing LLM config and a parse failure are both <see cref="WorkOutcome.Failed"/>.
        /// </summary>
        public WorkOutcome Work => Status switch
        {
            // Resolved carries no reason by construction — nothing went wrong to report.
            AttributionStatus.Resolved => new WorkOutcome.Ok(null),
            AttributionStatus.Unknown => new WorkOutcome.Ok(FailureReason),
            AttributionStatus.NoLlmConfigured => new WorkOutcome.Failed(FailureReason),
            AttributionStatus.Failed => new WorkOutcome.Failed(FailureReason),
            AttributionStatus.ServiceUnavailable => new WorkOutcome.Unavailable(FailureReason),
            AttributionStatus.ModelLoading => new WorkOutcome.Busy(FailureReason),
            // Deliberately not a fall-through to Failed: a seventh status must be translated
            // explicitly, not silently swallowed as a failure. The exhaustiveness test in
            // CharacterDispositionTests fires here first.
            _ => throw new ArgumentOutOfRangeException(
                nameof(Status), Status, "No WorkOutcome translation for this AttributionStatus."),
        };
    }

    /// <summary>
    /// Progress signals raised by the escalation-chain walk so a caller can mirror the chain's true
    /// in-flight set in the UI.
    /// <see cref="ChunkStarted"/> fires with each chunk — one paragraph or many — just before its
    /// LLM call.
    /// <see cref="ItemDeferred"/> fires for an item whose chunk answered it but left it suspect —
    /// it is no longer in flight and waits, un-decided, for the next escalation step.
    /// </summary>
    public sealed record AttributionQueueCallbacks(
        Action<IReadOnlyList<QueuedParagraph>>? ChunkStarted = null,
        Action<QueuedParagraph>? ItemDeferred = null);

    /// <summary>
    /// One config's run over a set of paragraphs — the "step" half of the escalation chain. Owns
    /// chapter-grouping, chunking by <see cref="LlmServerConfig.AttributionBatchSize"/>, the chunk
    /// pipeline, self-consistency, and trigger derivation; streams each item's
    /// <see cref="StepOutcome"/> the moment its chunk returns so a confident answer surfaces live.
    /// Fires <see cref="AttributionQueueCallbacks.ChunkStarted"/> per in-flight chunk. It never fires
    /// <see cref="AttributionQueueCallbacks.ItemDeferred"/> nor decides escalation — that is the walk's.
    /// </summary>
    internal interface IChainStep
    {
        IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunAsync(
            IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks,
            CancellationToken ct);
    }

    /// <summary>
    /// One chunk pipeline, where a single paragraph is a chunk of 1: every ask goes through
    /// <see cref="RunOnce"/>, and the two branches that can add a second ask — self-consistency and
    /// the final-rung parse-failure fallback — sit above it in <see cref="RunChunkAsync"/>. Request
    /// construction (context load, roster, template, budget) belongs to
    /// <see cref="AttributionRequestBuilder"/>; what is left here is control flow plus the
    /// classification of an answer that arrived.
    /// </summary>
    internal class CharacterAttributionService(
        ILlmCompletionRunner runner,
        AttributionRequestBuilder builder,
        ILogger<CharacterAttributionService> logger)
        : IChainStep
    {
        /// <inheritdoc/>
        /// <remarks>Runs one config across the given items: <see cref="GroupByChapter"/> →
        /// <see cref="RunStepGroupAsync"/> per group, streaming.</remarks>
        async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> IChainStep.RunAsync(
            IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var group in GroupByChapter(items))
                await foreach (var outcome in RunStepGroupAsync(group, opts, callbacks, ct))
                    yield return outcome;
        }

        /// <summary>Groups paragraphs by (folder, chapter), preserving book order within each group.</summary>
        private static IEnumerable<List<QueuedParagraph>> GroupByChapter(IReadOnlyList<QueuedParagraph> items) =>
            items.GroupBy(i => (i.Folder, i.ChapterId)).Select(g => g.ToList());

        /// <summary>
        /// Runs one chain step over a single-chapter group: re-groups it in book order into chunks of
        /// the step config's batch size, runs each chunk through the one chunk pipeline, looping on
        /// intra-step context-trim deferrals until every item in the group has an outcome.
        /// Streams each chunk's outcomes the moment that chunk returns rather than buffering the whole
        /// group: only one chunk is ever in flight, so the caller can retire the previous chunk's items
        /// (yield a terminal outcome, or defer them) before the next chunk is marked as processing.
        /// Buffering here would leave every chunk of the group stuck in a processing state until the
        /// last one finished.
        /// </summary>
        private async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunStepGroupAsync(
            IReadOnlyList<QueuedParagraph> group, ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks, [EnumeratorCancellation] CancellationToken ct)
        {
            var pending = new Queue<QueuedParagraph>(group);
            var batchSize = Math.Max(1, opts.Config.AttributionBatchSize);

            while (pending.Count > 0)
            {
                var chunk = new List<QueuedParagraph>();
                while (chunk.Count < batchSize && pending.Count > 0)
                    chunk.Add(pending.Dequeue());

                // Signal the in-flight chunk so the UI flips exactly these items to Processing
                // (batch size, e.g. 3) rather than the whole drained queue.
                callbacks?.ChunkStarted?.Invoke(chunk);

                var core = await RunChunkAsync(chunk, opts, isFallback: false, ct);

                foreach (var outcome in core.Outcomes)
                    yield return outcome;

                foreach (var d in core.Deferred)
                    pending.Enqueue(d);
            }
        }

        /// <summary>
        /// One chunk's answer: the single ask of <see cref="RunOnceAsync"/>, plus the only two branches
        /// that can spend a second ask on the same paragraphs — self-consistency (non-final rungs) and
        /// the final-rung parse-failure fallback. The two cannot co-occur:
        /// <see cref="ChainStepOptions.SelfConsistency"/> is only ever set on a non-final rung and the
        /// fallback only fires on the final one, so there is no ordering question between them.
        /// <para>
        /// The fallback answers an unparseable <em>run</em> (<see cref="ChunkResult.Run"/>) — the only
        /// thing a re-ask can fix. It is one level and flag-terminated, for 2 asks per paragraph at
        /// most. A failed chunk of N re-asks each paragraph on its own with the config as-is — the
        /// template shape changes, which is the point. A failed chunk of 1 has no shape left to change,
        /// so it re-asks off greedy (<see cref="ChainStepOptions.Resampled"/>), an identical prompt at
        /// temperature 0 being certain to return the identical unparseable answer. Either way
        /// <paramref name="isFallback"/> marks the second ask, so a parse failure there stands.
        /// </para>
        /// </summary>
        private async Task<ChunkResult> RunChunkAsync(
            IReadOnlyList<QueuedParagraph> chunk, ChainStepOptions opts, bool isFallback, CancellationToken ct)
        {
            var result = opts.SelfConsistency
                ? await SelfConsistentChunkAsync(chunk, opts, ct)
                : await RunOnceAsync(chunk, opts, ct);

            if (!opts.IsFinal || result.Run != LlmRunOutcome.ParseFailed || isFallback)
                return result;

            // The unparseable run stamped every included item; unaskable items in the same chunk
            // already have their Unknown and are left alone.
            var failed = result.Outcomes
                .Where(o => o.Step.Trigger == EscalationTrigger.ParseFailure)
                .Select(o => o.Item)
                .ToList();

            logger.LogWarning(
                "Failed to parse the answer for a chunk of {Count} paragraph(s) on the final config — re-asking {Mode}",
                failed.Count, failed.Count > 1 ? "each on its own" : "once off greedy");

            // A chunk of 1 never defers (the context trim has nothing to trim), so a re-ask's own
            // deferrals are empty and the chunk's deferrals stay with the result they came from.
            var reaskOpts = failed.Count > 1 ? opts : opts.Resampled();
            var singles = new List<(QueuedParagraph Item, StepOutcome Step)>();
            foreach (var item in failed)
                singles.AddRange((await RunChunkAsync([item], reaskOpts, isFallback: true, ct)).Outcomes);

            return result.Replacing(singles);
        }

        /// <summary>
        /// Self-consistency for a chunk: sample the same prompt twice and escalate the paragraphs the
        /// two samples disagree about (<see cref="EscalationTrigger.Inconsistent"/>), always carrying
        /// sample 1 as the answer — the check may only ever add a trigger, never change an answer.
        /// <para>
        /// The sample-1 guard is chunk-wide because it tests the <em>run</em>
        /// (<see cref="ChunkResult.Answered"/>), which is chunk-wide by construction: a parse, infra or
        /// still-loading outcome is what the one call returned, and no second call can improve on it.
        /// A per-item quality trigger does not suppress sample 2; <see cref="Reconcile"/> compares
        /// each paragraph on its own.
        /// </para>
        /// <para>
        /// Sample 2 is matched by paragraph id, never positionally; an unmatched item degrades to
        /// sample 1. Deferrals pass through from sample 1 — the context trim is deterministic, so both
        /// samples cover the same included set. The roster comes back on the sample itself, so the
        /// reconcile compares against provably the roster the prompt was built from.
        /// </para>
        /// </summary>
        private async Task<ChunkResult> SelfConsistentChunkAsync(
            IReadOnlyList<QueuedParagraph> chunk, ChainStepOptions opts, CancellationToken ct)
        {
            // Both samples resample: a greedy sample 1 would guarantee agreement and make the check a
            // pure cost. RunOnceAsync never reads SelfConsistency, so this cannot recurse.
            var scOpts = opts.Resampled();

            var sample1 = await RunOnceAsync(chunk, scOpts, ct);
            if (!sample1.Answered)
                return sample1;

            var sample2 = await RunOnceAsync(chunk, scOpts, ct);
            var byId = sample2.Outcomes.ToDictionary(o => o.Item.ParagraphId, o => o.Step);

            var reconciled = sample1.Outcomes
                .Select(o => byId.TryGetValue(o.Item.ParagraphId, out var step2)
                    ? (o.Item, Reconcile(o.Step, step2, sample1.Characters, sample1.Narrator))
                    : o)
                .ToList();

            return sample1 with { Outcomes = reconciled };
        }

        /// <summary>
        /// Compares two samples for a self-consistency step. A sample-2 parse/infra failure is swallowed
        /// (keep sample 1, no escalation from this check — the check must never worsen results).
        /// Agreement (index by index, per <see cref="ItemAttributionEscalation.AnswersAgree"/>) →
        /// sample 1 unchanged. Disagreement → sample 1 carried with an <c>Inconsistent</c> trigger so
        /// it escalates.
        /// </summary>
        private static StepOutcome Reconcile(
            StepOutcome sample1, StepOutcome sample2, IReadOnlyList<Data.Entities.Character> characters,
            NarratorIdentity narrator)
        {
            if (sample2.Trigger == EscalationTrigger.ParseFailure || sample2.Outcome.Status.IsInfraFailure())
                return sample1;

            // An answer that never happened (empty paragraph) has nothing to compare.
            if (sample1.Outcome.Answer is not { } a || sample2.Outcome.Answer is not { } b)
                return sample1;

            return ItemAttributionEscalation.AnswersAgree(a, b, characters, narrator)
                ? sample1
                : sample1 with { Trigger = EscalationTrigger.Inconsistent };
        }

        /// <summary>
        /// One ask for one chunk, at any chunk size: build the request, resolve the unaskable without
        /// an LLM call, run, route the run outcome, classify what came back. This is the only place
        /// that calls the runner, and it never reads
        /// <see cref="ChainStepOptions.SelfConsistency"/> — which is what makes the two-ask branches
        /// above impossible to re-enter.
        /// </summary>
        private async Task<ChunkResult> RunOnceAsync(
            IReadOnlyList<QueuedParagraph> chunk, ChainStepOptions opts, CancellationToken ct)
        {
            var req = await builder.Build(chunk, opts);
            var steps = new Dictionary<Guid, StepOutcome>();
            LlmRunOutcome? runOutcome = null;

            foreach (var item in req.Unaskable)
            {
                // Blank/whitespace text, or no content item at all: nothing to attribute and nothing
                // an LLM could add. Unknown with an Unknown trigger, so the walk keeps escalating it
                // like any other unresolved paragraph — the bin is a property of the text, not the ask.
                logger.LogInformation("Paragraph {ParagraphId} has no text — marking unknown", item.ParagraphId);
                steps[item.ParagraphId] = new StepOutcome(
                    new AttributionOutcome(AttributionStatus.Unknown, null, null),
                    EscalationTrigger.Unknown);
            }

            if (req.Request is not null)
            {
                logger.LogDebug("Sending character attribution prompt for {Count} paragraph(s)", req.Included.Count);

                var run = await runner.RunAsync(req.Request, req.Parser!, ct);
                runOutcome = run.Outcome;

                if (RouteRunOutcome(run, req.Included.Count, opts) is { } routed)
                {
                    foreach (var item in req.Included)
                        steps[item.ParagraphId] = routed;
                }
                else
                {
                    var parsed = run.Value!;
                    // One raw for the whole chunk — a per-paragraph misalignment is logged against the
                    // answer it came out of, which is the only form the response ever existed in.
                    var provenance = AnswerProvenance.From(opts.Config, run.Raw);
                    for (var i = 0; i < req.Included.Count; i++)
                        steps[req.Included[i].ParagraphId] = Classify(
                            req.Included[i].ParagraphId, req.QueryItems[i], parsed[i].Items,
                            req.Characters, req.Narrator, provenance, parsed[i].Reasoning);
                }
            }

            // Book order, as the chunk was handed to us. Deferred items have no outcome yet — the
            // group's pending queue re-asks them in a later chunk.
            var outcomes = chunk
                .Where(i => steps.ContainsKey(i.ParagraphId))
                .Select(i => (i, steps[i.ParagraphId]))
                .ToList();

            return new ChunkResult(outcomes, req.Deferred, req.Characters, req.Narrator, runOutcome);
        }

        /// <summary>
        /// The one copy of the non-answer routing: everything a run can come back as that is not an
        /// answer to classify, fanned to every included item. Returns null when the run parsed and the
        /// caller should classify it.
        /// </summary>
        private StepOutcome? RouteRunOutcome(
            LlmRunResult<IReadOnlyDictionary<int, ItemAttributionResult>> run, int count, ChainStepOptions opts)
        {
            switch (run.Outcome)
            {
                case LlmRunOutcome.ParseFailed:
                    // An unparseable answer, or one missing a requested index (the parser rejects the
                    // whole answer), fails the chunk: escalation's unit is the paragraph, and a
                    // half-answered chunk is not one.
                    logger.LogWarning(
                        "Failed to parse the LLM answer for {Count} paragraph(s) on config {ConfigName}: {Raw}",
                        count, opts.Config.Name, run.Raw);
                    return ParseFailure(run.Error);

                case LlmRunOutcome.ModelLoading:
                    // The model is still loading on a switchable endpoint. This is neither a quality
                    // suspect nor an infra failure: with a None trigger the chain short-circuits (it
                    // must not escalate — that would autoload a different model and evict the load we
                    // are waiting for) and, because ModelLoading is not usable, it never feeds the
                    // best-prior fallback. It surfaces to the queue, which requeues with backoff.
                    logger.LogInformation(
                        "{Count} paragraph(s): model still loading — deferring to queue backoff", count);
                    return ModelLoading(run.Error);

                case LlmRunOutcome.Failed:
                case LlmRunOutcome.ServiceUnavailable:
                    // Infra failure is orthogonal to quality — it carries no EscalationTrigger.
                    logger.LogError(
                        "Error attributing {Count} paragraph(s) on config {ConfigName}: {Reason}",
                        count, opts.Config.Name, run.Error);
                    return new StepOutcome(
                        new AttributionOutcome(
                            run.Outcome == LlmRunOutcome.ServiceUnavailable
                                ? AttributionStatus.ServiceUnavailable
                                : AttributionStatus.Failed,
                            null, run.Error),
                        EscalationTrigger.None);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Classifies one paragraph's answer against the items it was asked about. There is no
        /// fidelity check left to make: the answer names existing items by index and carries no text,
        /// so it cannot drift from the paragraph — an index nothing matches is simply dropped on
        /// apply. The trigger is derived from the answered speakers, and the status is
        /// <see cref="AttributionStatus.Unknown"/> when the answer leaves a dialog item unstamped —
        /// unanswered included — else Resolved. The answer still applies either way: the items it
        /// does name stamp, and the rest stay queue-eligible.
        /// <paramref name="reasoning"/> is the model's own one-sentence account of why it attributed
        /// the way it did. It is logged, never stored or acted on: a confident-but-wrong attribution
        /// is otherwise untraceable, because the raw answer is only logged when parsing fails and the
        /// reasoning exists nowhere else once the answer is classified.
        /// </summary>
        /// <param name="items">
        /// The paragraph's items as the prompt numbered them, so index <c>i</c> of this list is the
        /// item the answer's index <c>i</c> names.
        /// </param>
        private StepOutcome Classify(
            Guid paragraphId, IReadOnlyList<ContextItem> items, IReadOnlyList<AttributedItem> answer,
            IReadOnlyList<Data.Entities.Character> characters, NarratorIdentity narrator,
            AnswerProvenance provenance, string? reasoning)
        {
            var trigger = ItemAttributionEscalation.DeriveTrigger(answer, items, characters, narrator);
            var status = ItemAttributionEscalation.HasUnknownSpeaker(answer, items, narrator)
                ? AttributionStatus.Unknown
                : AttributionStatus.Resolved;

            // Speakers and reasoning together: the pair is what makes a wrong-but-confident answer
            // readable after the fact — the names it chose, and the account it gave for choosing them.
            logger.LogInformation(
                "LLM attributed {Count} of paragraph {ParagraphId}'s {ItemCount} item(s), status {Status}, "
                + "trigger {Trigger}, config {ConfigName} (model {Model}). Speakers: {Speakers}. "
                + "Reasoning: {Reasoning}",
                answer.Count, paragraphId, items.Count, status, trigger, provenance.ConfigName,
                provenance.Model, AnsweredSpeakers(answer), reasoning);

            return new StepOutcome(
                new AttributionOutcome(status, AttributionAnswer.For(answer, items), null), trigger);
        }

        /// <summary>The answer's speakers, index-tagged, for the log line.</summary>
        private static string AnsweredSpeakers(IReadOnlyList<AttributedItem> answer) =>
            string.Join(", ", answer.Select(a => $"{a.Index}={a.Speaker}"));

        private static StepOutcome ParseFailure(string? error) =>
            new(new AttributionOutcome(AttributionStatus.Failed, null, error), EscalationTrigger.ParseFailure);

        /// <summary>
        /// Model-still-loading outcome. A None trigger so the chain accepts it immediately (no
        /// escalation), and a non-usable status so it never feeds the best-prior fallback — the queue
        /// requeues the item with backoff until the model is ready.
        /// </summary>
        private static StepOutcome ModelLoading(string? error) =>
            new(new AttributionOutcome(AttributionStatus.ModelLoading, null, error), EscalationTrigger.None);

        /// <summary>
        /// One chunk's worth of answers: an outcome per item that was answered (or resolved without
        /// an ask), the items the context trim pushed back to the group's pending queue, the roster
        /// the prompt was built from — carried so a reconcile never has to refetch it — the narrator
        /// link that goes with that roster, and how the one LLM call went, <c>null</c> when the chunk
        /// needed no call at all.
        /// </summary>
        private sealed record ChunkResult(
            IReadOnlyList<(QueuedParagraph Item, StepOutcome Step)> Outcomes,
            IReadOnlyList<QueuedParagraph> Deferred,
            IReadOnlyList<Data.Entities.Character> Characters,
            NarratorIdentity Narrator,
            LlmRunOutcome? Run)
        {
            /// <summary>
            /// True when the call produced an answer to work with — or when there was nothing to ask.
            /// The one call's outcome covers the whole chunk, so this is the chunk-wide question a
            /// per-item trigger cannot answer.
            /// </summary>
            public bool Answered => Run is null or LlmRunOutcome.Completed;

            /// <summary>Overlays re-asked outcomes onto this chunk's, by paragraph id, keeping book order.</summary>
            public ChunkResult Replacing(IReadOnlyList<(QueuedParagraph Item, StepOutcome Step)> replacements)
            {
                var byId = replacements.ToDictionary(o => o.Item.ParagraphId, o => o.Step);
                return this with
                {
                    Outcomes = [.. Outcomes.Select(o =>
                        byId.TryGetValue(o.Item.ParagraphId, out var step) ? (o.Item, step) : o)],
                };
            }
        }
    }
}
