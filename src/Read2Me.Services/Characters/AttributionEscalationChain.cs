using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// The escalation-chain <em>walk</em>: traverses the configured chain over a single
    /// <see cref="IChainStep"/>, deciding each paragraph's final outcome. The step owns one config's
    /// run across a set of items (grouping, chunking, the chunk pipeline, self-consistency, trigger
    /// derivation); the walk owns policy — the step-0-vs-steps-1..n split, the same-rung
    /// parse-failure retry, best-prior fallback, the <see cref="EscalationTrigger"/> routing, the
    /// <see cref="AttributionStatus.ModelLoading"/> short-circuit, the
    /// <see cref="EscalationStarted"/> publish, and the
    /// <see cref="AttributionQueueCallbacks.ItemDeferred"/> fire.
    /// </summary>
    internal class AttributionEscalationChain(
        IChainStep step,
        LlmSettingsService settings,
        EventBroadcaster<LlmStreamEvent> broadcaster,
        ILogger<AttributionEscalationChain> logger)
    {
        /// <summary>
        /// Queue-wide streaming attribution. Owns the whole drained set and yields each paragraph's
        /// final outcome the moment it is decided. The primary config (step 0) runs across every queued
        /// paragraph — grouped and chunked per chapter — before any paragraph escalates; confident
        /// step-0 answers are yielded immediately (live progress) while collected suspects from the
        /// whole queue escalate together, one model burst per chain step. Every ask goes through the
        /// step's one chunk pipeline; the walk adds only policy (same-rung retry, final-step accept,
        /// best-prior fallback).
        /// <paramref name="callbacks"/> reports each in-flight chunk just before its LLM call, so a
        /// caller can flip exactly the working items to a processing state rather than the whole
        /// drained queue, and reports each item left suspect by its chunk so the caller can take it
        /// back out of that processing state until its next escalation step picks it up.
        /// </summary>
        public virtual async IAsyncEnumerable<(QueuedParagraph Item, AttributionOutcome Outcome)>
            AttributeQueueAsync(
                IReadOnlyList<QueuedParagraph> queued,
                AttributionQueueCallbacks? callbacks,
                [EnumeratorCancellation] CancellationToken ct)
        {
            if (queued.Count == 0)
                yield break;

            var chain = await settings.GetAttributionChainAsync();
            if (chain.Count == 0)
            {
                logger.LogWarning("No active LLM config — skipping {Count} queued paragraph(s)", queued.Count);
                var outcome = new AttributionOutcome(AttributionStatus.NoLlmConfigured, null,
                    "No active LLM server configured");
                foreach (var item in queued)
                    yield return (item, outcome);
                yield break;
            }

            var selfConsistency = await settings.GetSelfConsistencyAsync();
            var isSingleEntry = chain.Count == 1;

            // Best usable (non-infra) answer seen so far, keyed by paragraph id — last-entry
            // fallback. "Best" is by answer quality (see Rank), not by recency: a later, stronger
            // rung that regresses to "unknown" must not throw away an earlier rung's named answer.
            var best = new Dictionary<Guid, StepOutcome>();
            // Suspects collected from the whole queue, in book order, carried into the next step.
            var suspects = new List<QueuedParagraph>();

            // ── Step 0 ── run the primary across every chapter group before any escalation. A
            // single-entry chain runs step 0 as the final step, yields every outcome, no escalation.
            var step0Opts = new ChainStepOptions(
                chain[0].Config,
                IsFinal: isSingleEntry,
                SelfConsistency: selfConsistency && !isSingleEntry,
                Thinking: chain[0].Thinking,
                Style: chain[0].Style);
            await foreach (var (item, stepOutcome) in RunRungAsync(queued, step0Opts, callbacks, ct))
            {
                Remember(best, item.ParagraphId, stepOutcome);

                // Non-suspect (confident answer, or a step-0 infra failure keeping today's
                // semantics) is decided now and yielded live. In a single-entry chain every item
                // is final, so nothing is suspect.
                if (isSingleEntry || !IsQualitySuspect(stepOutcome))
                {
                    yield return (item, Accept(stepOutcome, chain).Outcome);
                }
                else
                {
                    // Answered but suspect: it leaves the in-flight set and waits, undecided,
                    // for the next step's model burst. Tell the caller so its status can drop
                    // out of Processing rather than sticking there until that step decides it.
                    suspects.Add(item);
                    callbacks?.ItemDeferred?.Invoke(item);
                }
            }

            // ── Steps 1..n ── escalate the whole-queue suspect set, one model burst per step.
            for (var stepIndex = 1; stepIndex < chain.Count && suspects.Count > 0; stepIndex++)
            {
                var entry = chain[stepIndex];
                var name = StepName(entry);
                var isFinal = stepIndex == chain.Count - 1;
                logger.LogInformation(
                    "Escalation step {Step} config '{Config}'{Final}: {Count} suspect item(s) across the queue",
                    stepIndex, name, isFinal ? " (final)" : string.Empty, suspects.Count);
                broadcaster.Publish(new EscalationStarted(stepIndex, name, suspects.Count));

                var opts = new ChainStepOptions(
                    entry.Config,
                    IsFinal: isFinal,
                    SelfConsistency: selfConsistency && !isFinal,
                    Thinking: entry.Thinking,
                    Style: entry.Style);
                var nextSuspects = new List<QueuedParagraph>();

                await foreach (var (item, stepOutcome) in RunRungAsync(suspects, opts, callbacks, ct))
                {
                    if (stepOutcome.Trigger == EscalationTrigger.None && stepOutcome.Outcome.Status.IsInfraFailure())
                    {
                        // Infra failure: item not usably answered here. Carry it on, or on the
                        // last entry resolve from the best prior usable answer, else surface it.
                        if (!isFinal)
                        {
                            nextSuspects.Add(item);
                            callbacks?.ItemDeferred?.Invoke(item);
                            continue;
                        }
                        yield return (item, best.TryGetValue(item.ParagraphId, out var prior)
                            ? Accept(prior, chain).Outcome
                            : stepOutcome.Outcome);
                        continue;
                    }

                    Remember(best, item.ParagraphId, stepOutcome);

                    if (isFinal || !IsQualitySuspect(stepOutcome))
                    {
                        // On the last rung the decision is the best answer the whole walk
                        // produced, not merely the last one: a stronger model that comes back
                        // "unknown" (or fails to parse) must not discard an earlier rung's named
                        // answer. Ranking ties go to the later rung, so a final answer that is no
                        // worse than everything before it still wins. ModelLoading is exempt —
                        // it is a retry signal, not an answer, so it must reach the queue
                        // untouched rather than be settled from history.
                        var decided =
                            isFinal &&
                            stepOutcome.Outcome.Status != AttributionStatus.ModelLoading &&
                            best.TryGetValue(item.ParagraphId, out var b)
                                ? b
                                : stepOutcome;
                        yield return (item, Accept(decided, chain).Outcome);
                    }
                    else
                    {
                        // Still suspect after this step — back out of the in-flight set until
                        // the next config picks it up. See the step-0 branch above.
                        nextSuspects.Add(item);
                        callbacks?.ItemDeferred?.Invoke(item);
                    }
                }

                suspects = nextSuspects;
            }
        }

        /// <summary>
        /// One rung's run over <paramref name="items"/>, with a single same-rung retry for parse
        /// failures. A parse failure is not evidence the model is too weak for the paragraph — the
        /// observed cause is the model garbling one chunk's answer (malformed JSON, or repeating the
        /// previous chunk's answer wholesale), which the very next call
        /// usually gets right. Escalating instead spends a slower model on a problem a re-ask
        /// solves, so the failures are held back, re-asked once against the same config, and only
        /// then routed by the caller.
        /// <para>
        /// The retry re-asks the failures on their own, so a garbled answer is not merely repeated:
        /// the chunk composition differs, and <see cref="ChainStepOptions.Resampled"/>
        /// keeps sampling off greedy so an identical prompt cannot return an identical answer. One
        /// retry only — the second answer stands, parse failure or not.
        /// </para>
        /// <para>
        /// The final rung is exempt: it already re-asks each parse failure as a chunk of 1, via the
        /// step's own final-rung fallback, so retrying there would be a third ask of the same
        /// paragraph. The retry exists to avoid escalating, and the final rung has nowhere to
        /// escalate to.
        /// </para>
        /// </summary>
        private async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunRungAsync(
            IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var retry = new List<QueuedParagraph>();

            await foreach (var pair in step.RunAsync(items, opts, callbacks, ct))
            {
                if (!opts.IsFinal && pair.Step.Trigger == EscalationTrigger.ParseFailure)
                {
                    retry.Add(pair.Item);
                    continue;
                }
                yield return pair;
            }

            if (retry.Count == 0)
                yield break;

            logger.LogInformation(
                "Re-asking config '{Config}' for {Count} paragraph(s) it failed to parse before escalating",
                opts.Config.Name, retry.Count);

            var retryOpts = opts.Resampled();
            await foreach (var pair in step.RunAsync(retry, retryOpts, callbacks, ct))
                yield return pair;
        }

        /// <summary>
        /// Records <paramref name="step"/> as the paragraph's best answer so far when it is usable
        /// and ranks at least as high as what is held. Ties go to the newer (later, normally
        /// stronger) rung.
        /// </summary>
        private static void Remember(Dictionary<Guid, StepOutcome> best, Guid paragraphId, StepOutcome step)
        {
            if (!IsUsable(step.Outcome.Status))
                return;
            if (best.TryGetValue(paragraphId, out var held) && Rank(held) > Rank(step))
                return;
            best[paragraphId] = step;
        }

        /// <summary>
        /// Answer quality, high to low: a confident answer beats one naming an unlisted character
        /// (still a full attribution — the final accept creates the character), which beats a
        /// self-inconsistent one, which beats one leaving a speaker unattributed. Only usable
        /// statuses reach this, so parse/infra failures rank below everything by never being held.
        /// </summary>
        private static int Rank(StepOutcome step) => step.Trigger switch
        {
            EscalationTrigger.None => 4,
            EscalationTrigger.UnlistedName => 3,
            EscalationTrigger.Inconsistent => 2,
            EscalationTrigger.Unknown => 1,
            _ => 0,
        };

        private static bool IsUsable(AttributionStatus status) =>
            status is AttributionStatus.Resolved or AttributionStatus.Unknown;

        /// <summary>
        /// How a rung reads to a human. The same config can appear as several rungs differing only in
        /// thinking and prompt style, so those carry suffixes to stay distinguishable in reasons,
        /// logs, and events. Only the non-default halves are named: a plain full-prompt rung reads as
        /// the bare config name, as it always has.
        /// </summary>
        private static string StepName(ResolvedChainStep entry)
        {
            var suffixes = new List<string>(2);
            if (entry.Style == AttributionPromptStyle.Simple) suffixes.Add("simple");
            if (entry.Thinking) suffixes.Add("thinking");
            return suffixes.Count == 0
                ? entry.Config.Name
                : $"{entry.Config.Name} ({string.Join(", ", suffixes)})";
        }

        /// <summary>True when the chain has an escalation tail (length ≥ 2).</summary>
        private static bool DidEscalate(IReadOnlyList<ResolvedChainStep> chain) => chain.Count >= 2;

        /// <summary>
        /// Final-step acceptance for a suspect answer. UnlistedName → Resolved (new character);
        /// Unknown → Unknown carrying an escalation reason (only when the chain actually escalated);
        /// everything else stands. A None-trigger answer is returned unchanged.
        /// </summary>
        private static StepOutcome Accept(StepOutcome step, IReadOnlyList<ResolvedChainStep> chain)
        {
            if (step.Trigger == EscalationTrigger.Unknown &&
                step.Outcome.Status == AttributionStatus.Unknown &&
                DidEscalate(chain))
            {
                var names = string.Join(" → ", chain.Select(StepName));
                var reason = $"Speaker unknown after escalating through {chain.Count} models ({names})";
                return step with { Outcome = step.Outcome with { FailureReason = reason } };
            }
            return step;
        }

        /// <summary>A successful-status answer with a non-None quality trigger (unknown/unlisted/parse).</summary>
        private static bool IsQualitySuspect(StepOutcome step) =>
            step.Trigger != EscalationTrigger.None;
    }
}
