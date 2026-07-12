using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    public enum AttributionStatus { Resolved, Unknown, NoLlmConfigured, Failed, ServiceUnavailable }

    /// <summary>
    /// Why a step's answer looks suspect and might be re-asked on a stronger config. Additive
    /// metadata computed alongside attribution; today's public API discards it (no behavior change
    /// until the chain loop in a later slice consumes it). <see cref="Inconsistent"/> is produced by
    /// the self-consistency check (slice 004) when two samples disagree.
    /// </summary>
    internal enum EscalationTrigger { None, Unknown, UnlistedName, ParseFailure, Inconsistent }

    /// <summary>An <see cref="AttributionOutcome"/> plus the quality trigger that classifies it.</summary>
    internal sealed record StepOutcome(AttributionOutcome Outcome, EscalationTrigger Trigger);

    /// <summary>
    /// Per-step config + flags for a chain step. <see cref="IsFinal"/> gates final-step behaviour
    /// (batch→single fallback on parse failure/missing index; unlisted-name acceptance).
    /// <see cref="SelfConsistency"/> gates double-sampling (slice 004): set only on non-final steps
    /// when the global toggle is on.
    /// </summary>
    internal sealed record ChainStepOptions(LlmServerConfig Config, bool IsFinal, bool SelfConsistency);

    internal static class LlmServerConfigExtensions
    {
        /// <summary>Shallow copy of the config with <see cref="LlmServerConfig.Temperature"/> replaced.</summary>
        public static LlmServerConfig WithTemperature(this LlmServerConfig config, double temperature) => new()
        {
            Id = config.Id,
            Name = config.Name,
            ApiType = config.ApiType,
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey,
            Model = config.Model,
            Temperature = temperature,
            TopP = config.TopP,
            MaxTokens = config.MaxTokens,
            FrequencyPenalty = config.FrequencyPenalty,
            PresencePenalty = config.PresencePenalty,
            AttributionBatchSize = config.AttributionBatchSize,
            PromptStyle = config.PromptStyle,
        };
    }

    public sealed record AttributionOutcome(
        AttributionStatus Status,
        string? Character,
        string? VoiceInstructions,
        string? FailureReason);

    /// <summary>
    /// Progress signals raised by <see cref="CharacterAttributionService.AttributeQueueAsync"/> so a
    /// caller can mirror the chain's true in-flight set in the UI.
    /// <see cref="ChunkStarted"/> fires with each batch just before its LLM call.
    /// <see cref="ItemDeferred"/> fires for an item whose chunk answered it but left it suspect —
    /// it is no longer in flight and waits, un-decided, for the next escalation step.
    /// </summary>
    public sealed record AttributionQueueCallbacks(
        Action<IReadOnlyList<QueuedParagraph>>? ChunkStarted = null,
        Action<QueuedParagraph>? ItemDeferred = null);

    /// <summary>
    /// Result of a multi-paragraph attribution request. <see cref="Deferred"/> holds items trimmed
    /// off the contiguous run (an unassigned character paragraph outside the batch sat between
    /// them); the caller should process them as a fresh batch.
    /// </summary>
    public sealed record BatchAttributionResult(
        IReadOnlyList<(QueuedParagraph Item, AttributionOutcome Outcome)> Outcomes,
        IReadOnlyList<QueuedParagraph> Deferred);

    public class CharacterAttributionService(
        ILlmCompletionRunner runner,
        LlmSettingsService settings,
        LlmPromptService prompts,
        IProjectReader reader,
        ILogger<CharacterAttributionService> logger,
        EventBroadcaster<LlmStreamEvent> broadcaster)
    {
        public virtual async Task<AttributionOutcome> AttributeAsync(QueuedParagraph item, CancellationToken ct)
        {
            var chain = await settings.GetAttributionChainAsync();
            if (chain.Count == 0)
            {
                logger.LogWarning("No active LLM config — skipping paragraph {ParagraphId}", item.ParagraphId);
                return new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null,
                    "No active LLM server configured");
            }

            // Single-entry chain (no escalation) is byte-identical to today: one step, no telemetry,
            // no tooltip reason — the zero-risk adoption guarantee.
            if (chain.Count == 1)
            {
                var only = await AttributeCoreAsync(item, new ChainStepOptions(chain[0], IsFinal: true, false), ct);
                return only.Outcome;
            }

            // Walk the chain. A suspect (non-None trigger) answer re-asks on the next config; the
            // best usable (non-infra) answer seen so far survives a last-entry infra failure.
            var selfConsistency = await settings.GetSelfConsistencyAsync();
            logger.LogInformation(
                "Paragraph {ParagraphId} entering escalation chain of {Steps} configs ({Chain}), selfConsistency={SelfConsistency}",
                item.ParagraphId, chain.Count, string.Join(" → ", chain.Select(c => c.Name)), selfConsistency);
            StepOutcome? best = null;
            for (var i = 0; i < chain.Count; i++)
            {
                var isFinal = i == chain.Count - 1;
                var opts = new ChainStepOptions(chain[i], isFinal, selfConsistency && !isFinal);

                if (i >= 1)
                {
                    logger.LogInformation(
                        "Paragraph {ParagraphId} escalating to step {Step} config '{Config}'{Final}",
                        item.ParagraphId, i, chain[i].Name, isFinal ? " (final)" : string.Empty);
                    broadcaster.Publish(new EscalationStarted(i, chain[i].Name, 1));
                }

                var step = await AttributeCoreAsync(item, opts, ct);

                // Infra failure (SU/Failed *status* with no quality trigger) is orthogonal to quality.
                // A parse failure carries Trigger=ParseFailure and is a quality escalation, not infra.
                if (step.Trigger == EscalationTrigger.None && IsInfraFailure(step.Outcome.Status))
                {
                    // Mid-chain infra failure carries the same item to the next entry (reporter/stream
                    // already fired inside the core). On the last entry, fall back to the best prior
                    // usable answer, else surface the infra failure.
                    if (!isFinal) continue;
                    return best is not null ? Accept(best, chain).Outcome : step.Outcome;
                }

                // Track the best *usable* answer (a real Resolved/Unknown) for last-entry fallback.
                // A parse failure at a non-final step is not usable — it just escalates.
                if (IsUsable(step.Outcome.Status))
                    best = step;

                if (step.Trigger == EscalationTrigger.None || isFinal)
                {
                    var accepted = Accept(step, chain);
                    logger.LogInformation(
                        "Paragraph {ParagraphId} attributed by config '{Config}' (step {Step})",
                        item.ParagraphId, chain[i].Name, i);
                    return accepted.Outcome;
                }
            }

            // Unreachable — the final iteration always returns (accept or infra fallback).
            throw new InvalidOperationException("Attribution chain fell through without an outcome.");
        }

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
                var outcome = new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null,
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
            var step0Opts = new ChainStepOptions(chain[0], IsFinal: isSingleEntry, selfConsistency && !isSingleEntry);
            foreach (var group in GroupByChapter(queued))
            {
                await foreach (var (item, step) in RunStepGroupAsync(group, step0Opts, callbacks, ct))
                {
                    if (IsUsable(step.Outcome.Status)) best[item.ParagraphId] = step;

                    // Non-suspect (confident answer, or a step-0 infra failure keeping today's
                    // semantics) is decided now and yielded live. In a single-entry chain every item
                    // is final, so nothing is suspect.
                    if (isSingleEntry || !IsQualitySuspect(step))
                    {
                        yield return (item, Accept(step, chain).Outcome);
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
                var config = chain[stepIndex];
                var isFinal = stepIndex == chain.Count - 1;
                logger.LogInformation(
                    "Escalation step {Step} config '{Config}'{Final}: {Count} suspect item(s) across the queue",
                    stepIndex, config.Name, isFinal ? " (final)" : string.Empty, suspects.Count);
                broadcaster.Publish(new EscalationStarted(stepIndex, config.Name, suspects.Count));

                var opts = new ChainStepOptions(config, isFinal, selfConsistency && !isFinal);
                var nextSuspects = new List<QueuedParagraph>();

                foreach (var group in GroupByChapter(suspects))
                {
                    await foreach (var (item, step) in RunStepGroupAsync(group, opts, callbacks, ct))
                    {
                        if (step.Trigger == EscalationTrigger.None && IsInfraFailure(step.Outcome.Status))
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
                                : step.Outcome);
                            continue;
                        }

                        if (IsUsable(step.Outcome.Status)) best[item.ParagraphId] = step;

                        if (isFinal || !IsQualitySuspect(step))
                        {
                            yield return (item, Accept(step, chain).Outcome);
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

        /// <summary>
        /// Runs one chain step over a single-chapter group: re-groups it in book order into chunks of
        /// the step config's batch size, runs each chunk on the existing batch core, looping on
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

                var core = chunk.Count == 1
                    ? new BatchCoreResult([(chunk[0], await AttributeCoreAsync(chunk[0], opts, ct))], [])
                    : await AttributeBatchCoreAsync(chunk, opts, ct);

                foreach (var outcome in core.Outcomes)
                    yield return outcome;

                foreach (var d in core.Deferred)
                    pending.Enqueue(d);
            }
        }

        private static bool IsUsable(AttributionStatus status) =>
            status is AttributionStatus.Resolved or AttributionStatus.Unknown;

        /// <summary>Chain length ≥ 2. Names in escalation order.</summary>
        private static bool DidEscalate(IReadOnlyList<LlmServerConfig> chain) => chain.Count >= 2;

        /// <summary>
        /// Final-step acceptance for a suspect answer. UnlistedName → Resolved (new character);
        /// Unknown → Unknown carrying an escalation reason (only when the chain actually escalated);
        /// everything else stands. A None-trigger answer is returned unchanged.
        /// </summary>
        private static StepOutcome Accept(StepOutcome step, IReadOnlyList<LlmServerConfig> chain)
        {
            if (step.Trigger == EscalationTrigger.Unknown &&
                step.Outcome.Status == AttributionStatus.Unknown &&
                DidEscalate(chain))
            {
                var names = string.Join(" → ", chain.Select(c => c.Name));
                var reason = $"Speaker unknown after escalating through {chain.Count} models ({names})";
                return step with { Outcome = step.Outcome with { FailureReason = reason } };
            }
            return step;
        }

        private static bool IsInfraFailure(AttributionStatus status) =>
            status is AttributionStatus.ServiceUnavailable or AttributionStatus.Failed;

        /// <summary>
        /// Config-parameterized single-item attribution. When <see cref="ChainStepOptions.SelfConsistency"/>
        /// is set, samples the LLM twice with the same prompt and escalates on disagreement
        /// (<see cref="EscalationTrigger.Inconsistent"/>), carrying sample 1 as the answer. Otherwise
        /// runs a single sample (verbatim today's behaviour).
        /// </summary>
        private async Task<StepOutcome> AttributeCoreAsync(
            QueuedParagraph item, ChainStepOptions opts, CancellationToken ct)
        {
            if (!opts.SelfConsistency)
                return await AttributeSampleCoreAsync(item, opts.Config, ct);

            // Self-consistency: both samples use an effective temperature so sample 1 is not greedy.
            var config = opts.Config.WithTemperature(EffectiveTemperature(opts.Config));

            var sample1 = await AttributeSampleCoreAsync(item, config, ct);
            // A parse/infra failure on sample 1 stands on its own — no second sample can help it.
            if (sample1.Trigger == EscalationTrigger.ParseFailure || IsInfraFailure(sample1.Outcome.Status))
                return sample1;

            var sample2 = await AttributeSampleCoreAsync(item, config, ct);
            var characters = await reader.GetCharactersWithAliasesAsync(item.Folder);
            return Reconcile(sample1, sample2, characters);
        }

        /// <summary>
        /// Compares two samples for a self-consistency step. A sample-2 parse/infra failure is swallowed
        /// (keep sample 1, no escalation from this check — the check must never worsen results).
        /// Agreement → sample 1 unchanged. Disagreement → sample 1 carried with an <c>Inconsistent</c>
        /// trigger so it escalates.
        /// </summary>
        private static StepOutcome Reconcile(
            StepOutcome sample1, StepOutcome sample2, IReadOnlyList<Data.Entities.Character> characters)
        {
            if (sample2.Trigger == EscalationTrigger.ParseFailure || IsInfraFailure(sample2.Outcome.Status))
                return sample1;

            return AnswersAgree(sample1.Outcome, sample2.Outcome, characters)
                ? sample1
                : sample1 with { Trigger = EscalationTrigger.Inconsistent };
        }

        /// <summary>
        /// Two answers agree when their canonicalized character names match (alias → owner name,
        /// then trimmed OrdinalIgnoreCase). Two <c>Unknown</c> answers (null character) agree; an
        /// Unknown and a named answer disagree. Two unlisted names compare raw (canonicalization is a
        /// no-op for names not in the list).
        /// </summary>
        private static bool AnswersAgree(
            AttributionOutcome a, AttributionOutcome b, IReadOnlyList<Data.Entities.Character> characters)
        {
            var na = Canonicalize(a.Character, characters);
            var nb = Canonicalize(b.Character, characters);
            if (na is null || nb is null) return na is null && nb is null;
            return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Maps an alias to its owner character's name; returns other names trimmed; null → null.</summary>
        private static string? Canonicalize(string? name, IReadOnlyList<Data.Entities.Character> characters)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();
            foreach (var c in characters)
            {
                if (c.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return c.Name.Trim();
                foreach (var alias in c.Aliases)
                    if (alias.Name.Trim().Equals(trimmed, StringComparison.OrdinalIgnoreCase)) return c.Name.Trim();
            }
            return trimmed;
        }

        /// <summary>Config temperature when &gt; 0, else 0.7 (a null/0 temp would make sample 1 greedy).</summary>
        private static double EffectiveTemperature(LlmServerConfig config) =>
            config.Temperature is { } t && t > 0 ? t : 0.7;

        /// <summary>
        /// One streamed attribution sample against the given config. Returns the same
        /// <see cref="AttributionOutcome"/> the public path returned before the escalation refactor,
        /// tagged with the quality <see cref="EscalationTrigger"/> derived from the same facts.
        /// </summary>
        private async Task<StepOutcome> AttributeSampleCoreAsync(
            QueuedParagraph item, LlmServerConfig config, CancellationToken ct)
        {
            var (before, after) = await prompts.GetContextWindowAsync();

            var ctx = await reader.GetParagraphContextAsync(
                item.Folder, item.ChapterId, item.ParagraphId, before, after);

            if (ctx == null || string.IsNullOrWhiteSpace(ctx.Query.Text))
            {
                logger.LogInformation("Paragraph {ParagraphId} has no text — marking unknown", item.ParagraphId);
                return new StepOutcome(
                    new AttributionOutcome(AttributionStatus.Unknown, null, null, null),
                    EscalationTrigger.Unknown);
            }

            var project = await reader.GetProjectAsync(item.Folder);
            var characters = await reader.GetCharactersWithAliasesAsync(item.Folder);
            var characterNames = characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() });

            var template = await prompts.GetCharacterPromptAsync(config.PromptStyle);
            var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                [PromptTemplates.ContextJson]     = PromptTemplates.BuildContextJson(ctx),
                [PromptTemplates.ResponseFormat]  = CharacterAttributionSchema.JsonExample,
            });

            logger.LogDebug("Sending character attribution prompt for paragraph {ParagraphId}", item.ParagraphId);

            var run = await runner.RunAsync<CharacterAttributionResult>(
                new LlmRunRequest(config, prompt, item.Preview,
                    CharacterAttributionSchema.JsonSchema, CompletionShape.Object),
                TryParseAttribution, ct);

            switch (run.Outcome)
            {
                case LlmRunOutcome.ParseFailed:
                    logger.LogWarning("Failed to parse LLM response for {ParagraphId}: {Raw}", item.ParagraphId, run.Raw);
                    return new StepOutcome(
                        new AttributionOutcome(AttributionStatus.Failed, null, null, run.Error),
                        EscalationTrigger.ParseFailure);
                case LlmRunOutcome.Failed:
                case LlmRunOutcome.ServiceUnavailable:
                    // Infra failure is orthogonal to quality — it carries no EscalationTrigger.
                    logger.LogError("Error attributing paragraph {ParagraphId}: {Reason}", item.ParagraphId, run.Error);
                    return new StepOutcome(
                        new AttributionOutcome(
                            run.Outcome == LlmRunOutcome.ServiceUnavailable
                                ? AttributionStatus.ServiceUnavailable
                                : AttributionStatus.Failed,
                            null, null, run.Error),
                        EscalationTrigger.None);
            }

            var parsed = run.Value!;
            if (string.IsNullOrWhiteSpace(parsed.Character) ||
                parsed.Character.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("LLM returned unknown for paragraph {ParagraphId}", item.ParagraphId);
                return new StepOutcome(
                    new AttributionOutcome(AttributionStatus.Unknown, null, null, null),
                    EscalationTrigger.Unknown);
            }

            logger.LogInformation("LLM attributed paragraph {ParagraphId} to '{Character}'",
                item.ParagraphId, parsed.Character);
            var trigger = IsKnownName(parsed.Character, characters)
                ? EscalationTrigger.None
                : EscalationTrigger.UnlistedName;
            return new StepOutcome(
                new AttributionOutcome(AttributionStatus.Resolved,
                    parsed.Character, parsed.VoiceInstructions, null),
                trigger);
        }

        private static bool TryParseAttribution(
            string raw, out CharacterAttributionResult? parsed, out string? error)
        {
            if (CharacterAttributionParser.TryParse(raw, out var p))
            {
                parsed = p;
                error = null;
                return true;
            }
            parsed = null;
            error = "Could not parse LLM response.";
            return false;
        }

        /// <summary>
        /// Attributes several queued paragraphs (same chapter, book order) in one LLM request.
        /// A single-item batch delegates to <see cref="AttributeAsync"/> so batch size 1 behaves
        /// exactly like the single-paragraph flow. An unparseable batch response, or an index
        /// missing from an otherwise valid response, falls back to the single-paragraph path for
        /// the affected items.
        /// </summary>
        public virtual async Task<BatchAttributionResult> AttributeBatchAsync(
            IReadOnlyList<QueuedParagraph> batch, CancellationToken ct)
        {
            if (batch.Count == 1)
                return new BatchAttributionResult(
                    [(batch[0], await AttributeAsync(batch[0], ct))], []);

            var chain = await settings.GetAttributionChainAsync();
            if (chain.Count == 0)
            {
                logger.LogWarning("No active LLM config — skipping batch of {Count} paragraphs", batch.Count);
                var outcome = new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null,
                    "No active LLM server configured");
                return new BatchAttributionResult([.. batch.Select(b => (b, outcome))], []);
            }

            // Single-entry chain (no escalation) is byte-identical to today: run the drained batch
            // once on the sole config as the final step, no telemetry.
            if (chain.Count == 1)
            {
                var only = await AttributeBatchCoreAsync(
                    batch, new ChainStepOptions(chain[0], IsFinal: true, false), ct);
                return new BatchAttributionResult(
                    [.. only.Outcomes.Select(o => (o.Item, o.Step.Outcome))], only.Deferred);
            }

            var selfConsistency = await settings.GetSelfConsistencyAsync();
            return await OrchestrateChainBatchAsync(batch, chain, selfConsistency, ct);
        }

        /// <summary>
        /// Runs the drained batch through the escalation chain. Step 0 runs as today (its deferrals
        /// pass straight through to the caller unchanged). Suspects — items whose step-0 answer has a
        /// non-None quality trigger — escalate, re-grouped in book order into chunks of each next
        /// step's batch size, running strictly one step at a time until every suspect is decided.
        /// </summary>
        private async Task<BatchAttributionResult> OrchestrateChainBatchAsync(
            IReadOnlyList<QueuedParagraph> batch,
            IReadOnlyList<LlmServerConfig> chain,
            bool selfConsistency,
            CancellationToken ct)
        {
            // Final accepted StepOutcome per item, in book order (step-0 order preserved).
            var final = new List<(QueuedParagraph Item, StepOutcome Step)>();
            // Best usable (non-infra) answer seen so far, keyed by paragraph id — for last-entry fallback.
            var best = new Dictionary<Guid, StepOutcome>();
            // Suspects carried into the next step, in book order.
            var suspects = new List<QueuedParagraph>();

            // ── Step 0 ── runs the drained batch exactly as today (self-consistency when toggled;
            // step 0 is never final here — the orchestrator only runs for chains of length ≥ 2).
            var step0 = await AttributeBatchCoreAsync(
                batch, new ChainStepOptions(chain[0], IsFinal: false, selfConsistency), ct);

            foreach (var (item, step) in step0.Outcomes)
            {
                if (IsUsable(step.Outcome.Status)) best[item.ParagraphId] = step;

                if (IsQualitySuspect(step))
                    suspects.Add(item);
                else
                    final.Add((item, step)); // None trigger, or an infra failure (step-0 semantics: stands).
            }
            LogAttributions(step0.Outcomes, chain[0].Name, 0);
            logger.LogInformation(
                "Step 0 (config '{Config}') drained: {Resolved} decided, {Suspects} suspect(s) to escalate through {Remaining} more config(s)",
                chain[0].Name, final.Count, suspects.Count, chain.Count - 1);

            // ── Steps 1..n ── escalate suspects sequentially.
            for (var stepIndex = 1; stepIndex < chain.Count && suspects.Count > 0; stepIndex++)
            {
                var config = chain[stepIndex];
                var isFinal = stepIndex == chain.Count - 1;
                logger.LogInformation(
                    "Escalation step {Step} config '{Config}'{Final}: {Count} suspect item(s)",
                    stepIndex, config.Name, isFinal ? " (final)" : string.Empty, suspects.Count);
                broadcaster.Publish(new EscalationStarted(stepIndex, config.Name, suspects.Count));

                var nextSuspects = new List<QueuedParagraph>();

                foreach (var (item, step) in await RunStepAsync(suspects, config, isFinal, selfConsistency, ct))
                {
                    if (step.Trigger == EscalationTrigger.None && IsInfraFailure(step.Outcome.Status))
                    {
                        // Infra failure at this step: item was not usably answered here. Carry it on.
                        if (!isFinal) { nextSuspects.Add(item); continue; }
                        // Last entry infra failure: resolve from best prior usable answer, else Failed.
                        final.Add((item, best.TryGetValue(item.ParagraphId, out var prior)
                            ? Accept(prior, chain)
                            : step));
                        continue;
                    }

                    if (IsUsable(step.Outcome.Status)) best[item.ParagraphId] = step;

                    if (isFinal || !IsQualitySuspect(step))
                        final.Add((item, Accept(step, chain)));
                    else
                        nextSuspects.Add(item);
                }

                LogAttributions(final.Where(f => suspects.Contains(f.Item)), config.Name, stepIndex);
                suspects = nextSuspects;
                if (suspects.Count > 0)
                    logger.LogInformation(
                        "Step {Step} config '{Config}' drained: {Remaining} item(s) still suspect, carrying to next config",
                        stepIndex, config.Name, suspects.Count);
            }

            // A single-entry chain never reaches here, so any leftover suspects mean the loop above
            // decided them (isFinal path) — nothing remains. Assemble in original step-0 order.
            var byItem = final.ToDictionary(f => f.Item, f => f.Step);
            var ordered = new List<(QueuedParagraph, AttributionOutcome)>();
            foreach (var (item, _) in step0.Outcomes)
                if (byItem.TryGetValue(item, out var step))
                    ordered.Add((item, step.Outcome));

            return new BatchAttributionResult(ordered, step0.Deferred);
        }

        /// <summary>
        /// Runs one escalation step over the given suspects: re-groups them in book order into chunks
        /// of the step config's batch size and runs each chunk, looping on intra-step deferrals until
        /// every suspect has an outcome. Returns per-suspect <see cref="StepOutcome"/>s.
        /// </summary>
        private async Task<List<(QueuedParagraph Item, StepOutcome Step)>> RunStepAsync(
            IReadOnlyList<QueuedParagraph> suspects, LlmServerConfig config, bool isFinal,
            bool selfConsistency, CancellationToken ct)
        {
            var results = new List<(QueuedParagraph, StepOutcome)>();
            var pending = new Queue<QueuedParagraph>(suspects);
            var batchSize = Math.Max(1, config.AttributionBatchSize);
            var opts = new ChainStepOptions(config, isFinal, selfConsistency && !isFinal);

            while (pending.Count > 0)
            {
                var chunk = new List<QueuedParagraph>();
                while (chunk.Count < batchSize && pending.Count > 0)
                    chunk.Add(pending.Dequeue());

                var core = chunk.Count == 1
                    ? new BatchCoreResult(
                        [(chunk[0], await AttributeCoreAsync(chunk[0], opts, ct))], [])
                    : await AttributeBatchCoreAsync(chunk, opts, ct);

                results.AddRange(core.Outcomes);
                // Intra-step deferrals loop back into this step's pending queue.
                foreach (var d in core.Deferred)
                    pending.Enqueue(d);
            }
            return results;
        }

        /// <summary>A successful-status answer with a non-None quality trigger (unknown/unlisted/parse).</summary>
        private static bool IsQualitySuspect(StepOutcome step) =>
            step.Trigger != EscalationTrigger.None;

        private void LogAttributions(
            IEnumerable<(QueuedParagraph Item, StepOutcome Step)> outcomes, string configName, int step)
        {
            foreach (var (item, _) in outcomes)
                logger.LogInformation(
                    "Paragraph {ParagraphId} attributed by config '{Config}' (step {Step})",
                    item.ParagraphId, configName, step);
        }

        private sealed record BatchCoreResult(
            IReadOnlyList<(QueuedParagraph Item, StepOutcome Step)> Outcomes,
            IReadOnlyList<QueuedParagraph> Deferred);

        /// <summary>
        /// Self-consistency for a batch step: samples the batch twice (same prompt, effective
        /// temperature), then reconciles per index. Only disagreeing indices become
        /// <see cref="EscalationTrigger.Inconsistent"/>; agreeing indices keep their sample-1 outcome.
        /// A sample-1 parse/infra failure per item stands (a second sample can't help it). Deferred
        /// items pass through from sample 1 unchanged.
        /// </summary>
        private async Task<BatchCoreResult> SelfConsistentBatchAsync(
            IReadOnlyList<QueuedParagraph> batch, ChainStepOptions opts, CancellationToken ct)
        {
            var effOpts = opts with
            {
                Config = opts.Config.WithTemperature(EffectiveTemperature(opts.Config)),
                SelfConsistency = false,
            };

            var sample1 = await AttributeBatchCoreAsync(batch, effOpts, ct);
            var sample2 = await AttributeBatchCoreAsync(batch, effOpts, ct);
            var characters = await reader.GetCharactersWithAliasesAsync(batch[0].Folder);

            // Match sample 2 by paragraph id; the trimmed context is deterministic so the two runs
            // cover the same included items. A missing match falls back to sample 1 unchanged.
            var byId = sample2.Outcomes.ToDictionary(o => o.Item.ParagraphId, o => o.Step);

            var reconciled = new List<(QueuedParagraph, StepOutcome)>();
            foreach (var (item, step1) in sample1.Outcomes)
            {
                if (step1.Trigger == EscalationTrigger.ParseFailure || IsInfraFailure(step1.Outcome.Status)
                    || !byId.TryGetValue(item.ParagraphId, out var step2))
                {
                    reconciled.Add((item, step1));
                    continue;
                }
                reconciled.Add((item, Reconcile(step1, step2, characters)));
            }

            return new BatchCoreResult(reconciled, sample1.Deferred);
        }

        /// <summary>
        /// Config-parameterized batch attribution. Behaves verbatim as today: builds one batch
        /// request, and on parse failure or a missing index falls back to single-item attribution
        /// for the affected items. Returns per-item <see cref="StepOutcome"/>s carrying the same
        /// outcomes plus their quality triggers.
        /// </summary>
        private async Task<BatchCoreResult> AttributeBatchCoreAsync(
            IReadOnlyList<QueuedParagraph> batch, ChainStepOptions opts, CancellationToken ct)
        {
            if (opts.SelfConsistency)
                return await SelfConsistentBatchAsync(batch, opts, ct);

            var config = opts.Config;
            var outcomes = new List<(QueuedParagraph Item, StepOutcome Step)>();

            var (before, after) = await prompts.GetContextWindowAsync();
            var first = batch[0];

            var ctx = await reader.GetParagraphBatchContextAsync(
                first.Folder, first.ChapterId, [.. batch.Select(b => b.ParagraphId)], before, after);

            if (ctx == null)
            {
                // First paragraph not found — let the single path give each item its usual outcome.
                foreach (var item in batch)
                    outcomes.Add((item, await AttributeCoreAsync(item, opts, ct)));
                return new BatchCoreResult(outcomes, []);
            }

            var byId = batch.ToDictionary(b => b.ParagraphId);
            var included = ctx.IncludedIds.Select(id => byId[id]).ToList();
            var deferred = new List<QueuedParagraph>([.. ctx.DeferredIds.Select(id => byId[id])]);

            if (included.Count == 1)
            {
                outcomes.Add((included[0], await AttributeCoreAsync(included[0], opts, ct)));
                return new BatchCoreResult(outcomes, deferred);
            }

            var project = await reader.GetProjectAsync(first.Folder);
            var characters = await reader.GetCharactersWithAliasesAsync(first.Folder);
            var characterNames = characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() });

            var template = await prompts.GetBatchCharacterPromptAsync(config.PromptStyle);
            var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                [PromptTemplates.ContextJson]     = PromptTemplates.BuildBatchContextJson(ctx),
                [PromptTemplates.ResponseFormat]  = CharacterBatchAttributionSchema.JsonExample,
            });

            logger.LogDebug("Sending batch character attribution prompt for {Count} paragraphs", included.Count);

            var run = await runner.RunAsync<IReadOnlyDictionary<int, CharacterAttributionResult>>(
                new LlmRunRequest(config, prompt, $"{included.Count} paragraphs: {first.Preview}",
                    CharacterBatchAttributionSchema.JsonSchema, CompletionShape.Array),
                TryParseBatchAttribution, ct);

            if (run.Outcome is LlmRunOutcome.Failed or LlmRunOutcome.ServiceUnavailable)
            {
                // Same failure semantics as the single path, applied to every included item.
                // Deferred items are returned untouched; the caller retries them and they hit the
                // same failure (and its requeue handling) individually. Infra failure is orthogonal
                // to quality — it carries no EscalationTrigger.
                logger.LogError("Error attributing batch of {Count} paragraphs: {Reason}", batch.Count, run.Error);
                var outcome = new AttributionOutcome(
                    run.Outcome == LlmRunOutcome.ServiceUnavailable
                        ? AttributionStatus.ServiceUnavailable
                        : AttributionStatus.Failed,
                    null, null, run.Error);
                foreach (var item in included)
                    outcomes.Add((item, new StepOutcome(outcome, EscalationTrigger.None)));
                return new BatchCoreResult(outcomes, deferred);
            }

            if (run.Outcome == LlmRunOutcome.ParseFailed)
            {
                if (opts.IsFinal)
                {
                    // Final step: today's behaviour — fall back to single-item attribution.
                    logger.LogWarning("Failed to parse batch LLM response — falling back to single attribution: {Raw}", run.Raw);
                    foreach (var item in included)
                        outcomes.Add((item, await AttributeCoreAsync(item, opts, ct)));
                }
                else
                {
                    // Non-final: the whole chunk hands off to the next step as ParseFailure
                    // suspects rather than falling back to single.
                    logger.LogWarning("Failed to parse batch LLM response — escalating chunk: {Raw}", run.Raw);
                    foreach (var item in included)
                        outcomes.Add((item, new StepOutcome(
                            new AttributionOutcome(AttributionStatus.Failed, null, null, run.Error),
                            EscalationTrigger.ParseFailure)));
                }
                return new BatchCoreResult(outcomes, deferred);
            }

            var parsed = run.Value!;
            for (var i = 0; i < included.Count; i++)
            {
                var item = included[i];
                if (!parsed.TryGetValue(i, out var entry))
                {
                    if (opts.IsFinal)
                    {
                        logger.LogWarning("Batch response missing index {Index} — falling back to single attribution for {ParagraphId}",
                            i, item.ParagraphId);
                        outcomes.Add((item, await AttributeCoreAsync(item, opts, ct)));
                    }
                    else
                    {
                        // Non-final: a missing index is a parse-format failure for that item —
                        // escalate it rather than falling back to single.
                        logger.LogWarning("Batch response missing index {Index} — escalating {ParagraphId}",
                            i, item.ParagraphId);
                        outcomes.Add((item, new StepOutcome(
                            new AttributionOutcome(AttributionStatus.Failed, null, null, "Batch response missing index"),
                            EscalationTrigger.ParseFailure)));
                    }
                }
                else if (entry.Character.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("LLM returned unknown for paragraph {ParagraphId}", item.ParagraphId);
                    outcomes.Add((item, new StepOutcome(
                        new AttributionOutcome(AttributionStatus.Unknown, null, null, null),
                        EscalationTrigger.Unknown)));
                }
                else
                {
                    logger.LogInformation("LLM attributed paragraph {ParagraphId} to '{Character}'",
                        item.ParagraphId, entry.Character);
                    var trigger = IsKnownName(entry.Character, characters)
                        ? EscalationTrigger.None
                        : EscalationTrigger.UnlistedName;
                    outcomes.Add((item, new StepOutcome(
                        new AttributionOutcome(AttributionStatus.Resolved,
                            entry.Character, entry.VoiceInstructions, null),
                        trigger)));
                }
            }

            return new BatchCoreResult(outcomes, deferred);
        }

        private static bool TryParseBatchAttribution(
            string raw, out IReadOnlyDictionary<int, CharacterAttributionResult>? parsed, out string? error)
        {
            if (CharacterBatchAttributionParser.TryParse(raw, out var p))
            {
                parsed = p;
                error = null;
                return true;
            }
            parsed = null;
            error = "Could not parse batch LLM response.";
            return false;
        }

        /// <summary>
        /// True when <paramref name="name"/> matches a known character name or any alias
        /// (case-insensitive, trimmed). A resolved answer outside this set is an unlisted name.
        /// </summary>
        private static bool IsKnownName(string name, IReadOnlyList<Data.Entities.Character> characters)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in characters)
            {
                known.Add(c.Name);
                foreach (var a in c.Aliases)
                    known.Add(a.Name);
            }
            return known.Contains(name.Trim());
        }
    }
}
