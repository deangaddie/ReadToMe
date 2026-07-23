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
    /// run across a set of items (grouping, chunking, batch core, self-consistency, trigger
    /// derivation); the walk owns policy — the step-0-vs-steps-1..n split, best-prior fallback, the
    /// <see cref="EscalationTrigger"/> routing, the <see cref="AttributionStatus.ModelLoading"/>
    /// short-circuit, the <see cref="EscalationStarted"/> publish, and the
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
        /// paragraph — grouped and batched per chapter — before any paragraph escalates; confident
        /// step-0 answers are yielded immediately (live progress) while collected suspects from the
        /// whole queue escalate together, one model burst per chain step. Reuses the existing cores
        /// (batch core, per-step runner, self-consistency, final-step accept, best-prior fallback).
        /// <paramref name="callbacks"/> reports each in-flight batch just before its LLM call, so a
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

            // Best usable (non-infra) answer seen so far, keyed by paragraph id — last-entry fallback.
            var best = new Dictionary<Guid, StepOutcome>();
            // Suspects collected from the whole queue, in book order, carried into the next step.
            var suspects = new List<QueuedParagraph>();

            // ── Step 0 ── run the primary across every chapter group before any escalation. A
            // single-entry chain runs step 0 as the final step, yields every outcome, no escalation.
            var step0Opts = new ChainStepOptions(
                chain[0].Config,
                IsFinal: isSingleEntry,
                SelfConsistency: selfConsistency && !isSingleEntry,
                Thinking: chain[0].Thinking);
            foreach (var group in GroupByChapter(queued))
            {
                await foreach (var (item, stepOutcome) in step.RunAsync(group, step0Opts, callbacks, ct))
                {
                    if (IsUsable(stepOutcome.Outcome.Status)) best[item.ParagraphId] = stepOutcome;

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
                    Thinking: entry.Thinking);
                var nextSuspects = new List<QueuedParagraph>();

                foreach (var group in GroupByChapter(suspects))
                {
                    await foreach (var (item, stepOutcome) in step.RunAsync(group, opts, callbacks, ct))
                    {
                        if (stepOutcome.Trigger == EscalationTrigger.None && IsInfraFailure(stepOutcome.Outcome.Status))
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

                        if (IsUsable(stepOutcome.Outcome.Status)) best[item.ParagraphId] = stepOutcome;

                        if (isFinal || !IsQualitySuspect(stepOutcome))
                        {
                            yield return (item, Accept(stepOutcome, chain).Outcome);
                        }
                        else
                        {
                            // Still suspect after this step — back out of the in-flight set until
                            // the next config picks it up. See the step-0 branch above.
                            nextSuspects.Add(item);
                            callbacks?.ItemDeferred?.Invoke(item);
                        }
                    }
                }

                suspects = nextSuspects;
            }
        }

        /// <summary>Groups paragraphs by (folder, chapter), preserving book order within each group.</summary>
        private static IEnumerable<List<QueuedParagraph>> GroupByChapter(IReadOnlyList<QueuedParagraph> items) =>
            items.GroupBy(i => (i.Folder, i.ChapterId)).Select(g => g.ToList());

        private static bool IsUsable(AttributionStatus status) =>
            status is AttributionStatus.Resolved or AttributionStatus.Unknown;

        /// <summary>
        /// How a rung reads to a human. The same config can appear as both a fast and a thinking rung,
        /// so the thinking one carries a suffix to stay distinguishable in reasons, logs, and events.
        /// </summary>
        private static string StepName(ResolvedChainStep entry) =>
            entry.Thinking ? $"{entry.Config.Name} (thinking)" : entry.Config.Name;

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

        private static bool IsInfraFailure(AttributionStatus status) =>
            status is AttributionStatus.ServiceUnavailable or AttributionStatus.Failed;

        /// <summary>A successful-status answer with a non-None quality trigger (unknown/unlisted/parse).</summary>
        private static bool IsQualitySuspect(StepOutcome step) =>
            step.Trigger != EscalationTrigger.None;
    }
}
