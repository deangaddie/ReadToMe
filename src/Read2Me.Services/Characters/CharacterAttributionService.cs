using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    public enum AttributionStatus { Resolved, Unknown, NoLlmConfigured, Failed, ServiceUnavailable, ModelLoading }

    /// <summary>
    /// Why a step's answer looks suspect and might be re-asked on a stronger config. Additive
    /// metadata computed alongside attribution; today's public API discards it (no behavior change
    /// until the chain loop in a later slice consumes it). <see cref="Inconsistent"/> is produced by
    /// the self-consistency check (slice 004) when two samples disagree. <see cref="DialogLost"/> is
    /// produced when the answer drops every dialog segment a paragraph previously had — see
    /// <see cref="SegmentEscalation.LosesDialog"/>.
    /// </summary>
    internal enum EscalationTrigger { None, Unknown, UnlistedName, ParseFailure, Inconsistent, DialogLost }

    /// <summary>An <see cref="AttributionOutcome"/> plus the quality trigger that classifies it.</summary>
    internal sealed record StepOutcome(AttributionOutcome Outcome, EscalationTrigger Trigger);

    /// <summary>
    /// Per-step config + flags for a chain step. <see cref="IsFinal"/> gates final-step behaviour
    /// (batch→single fallback on parse failure/missing index; unlisted-name acceptance).
    /// <see cref="SelfConsistency"/> gates double-sampling (slice 004): set only on non-final steps
    /// when the global toggle is on. <see cref="Thinking"/> is the chain entry's per-rung thinking
    /// flag — off by default (attribution's primary pass runs fast), opted into per rung.
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
        AttributionPromptStyle? Style = null)
    {
        /// <summary>The style to ask with: the rung's own, falling back to the config's.</summary>
        public AttributionPromptStyle EffectiveStyle => Style ?? Config.PromptStyle;
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

    internal static class LlmServerConfigExtensions
    {
        /// <summary>Shallow copy of the config with <see cref="LlmServerConfig.Temperature"/> replaced.</summary>
        public static LlmServerConfig WithTemperature(this LlmServerConfig config, double temperature) =>
            Copy(config, config.MaxTokens, temperature);

        /// <summary>
        /// Shallow copy set up to be sampled more than once for the same paragraph: the config's own
        /// temperature when &gt; 0, else 0.7. A null/0 temperature is greedy, which would make every
        /// resample a verbatim repeat of the first — useless to both self-consistency (it would
        /// always agree) and the same-rung parse-failure retry (it would fail identically).
        /// </summary>
        public static LlmServerConfig ForResample(this LlmServerConfig config) =>
            config.WithTemperature(config.Temperature is { } t && t > 0 ? t : 0.7);

        /// <summary>Shallow copy of the config with <see cref="LlmServerConfig.MaxTokens"/> replaced.</summary>
        public static LlmServerConfig WithMaxTokens(this LlmServerConfig config, int? maxTokens) =>
            Copy(config, maxTokens, config.Temperature);

        private static LlmServerConfig Copy(LlmServerConfig config, int? maxTokens, double? temperature) => new()
        {
            Id = config.Id,
            Name = config.Name,
            ApiType = config.ApiType,
            BaseUrl = config.BaseUrl,
            ApiKey = config.ApiKey,
            Model = config.Model,
            Temperature = temperature,
            TopP = config.TopP,
            MaxTokens = maxTokens,
            FrequencyPenalty = config.FrequencyPenalty,
            PresencePenalty = config.PresencePenalty,
            AttributionBatchSize = config.AttributionBatchSize,
            PromptStyle = config.PromptStyle,
        };
    }

    /// <summary>
    /// A paragraph's attribution answer: the full segment list the LLM re-segmented it into, with
    /// every segment's text already sliced from the original paragraph (never LLM text). Segments
    /// are non-null for <see cref="AttributionStatus.Resolved"/> and for an
    /// <see cref="AttributionStatus.Unknown"/> that carries an answer with unknown dialog speakers;
    /// they are null when there is nothing to apply (empty paragraph, infra/parse failure).
    /// <see cref="AttributionStatus.Unknown"/> means the answer left ≥1 dialog segment unattributed;
    /// whether the paragraph ends up unattributed is decided on apply, per item.
    /// </summary>
    public sealed record AttributionOutcome(
        AttributionStatus Status,
        IReadOnlyList<AttributionSegment>? Segments,
        string? FailureReason);

    /// <summary>
    /// Progress signals raised by the escalation-chain walk so a caller can mirror the chain's true
    /// in-flight set in the UI.
    /// <see cref="ChunkStarted"/> fires with each batch just before its LLM call.
    /// <see cref="ItemDeferred"/> fires for an item whose chunk answered it but left it suspect —
    /// it is no longer in flight and waits, un-decided, for the next escalation step.
    /// </summary>
    public sealed record AttributionQueueCallbacks(
        Action<IReadOnlyList<QueuedParagraph>>? ChunkStarted = null,
        Action<QueuedParagraph>? ItemDeferred = null);

    /// <summary>
    /// One config's run over a set of paragraphs — the "step" half of the escalation chain. Owns
    /// chapter-grouping, chunking by <see cref="LlmServerConfig.AttributionBatchSize"/>, the batch
    /// core, self-consistency, and trigger derivation; streams each item's <see cref="StepOutcome"/>
    /// the moment its chunk returns so a confident answer surfaces live. Fires
    /// <see cref="AttributionQueueCallbacks.ChunkStarted"/> per in-flight chunk. It never fires
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

    public class CharacterAttributionService(
        ILlmCompletionRunner runner,
        LlmPromptService prompts,
        IProjectReader reader,
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
                return await AttributeSampleCoreAsync(item, opts, ct);

            // Self-consistency: both samples use an effective temperature so sample 1 is not greedy.
            var sampleOpts = opts with { Config = opts.Config.ForResample() };

            var sample1 = await AttributeSampleCoreAsync(item, sampleOpts, ct);
            // A parse/infra failure on sample 1 stands on its own — no second sample can help it.
            if (sample1.Trigger == EscalationTrigger.ParseFailure || sample1.Outcome.Status.IsInfraFailure())
                return sample1;

            var sample2 = await AttributeSampleCoreAsync(item, sampleOpts, ct);
            var characters = await reader.GetCharactersWithAliasesAsync(item.Folder);
            return Reconcile(sample1, sample2, characters);
        }

        /// <summary>
        /// Compares two samples for a self-consistency step. A sample-2 parse/infra failure is swallowed
        /// (keep sample 1, no escalation from this check — the check must never worsen results).
        /// Agreement (segment-by-segment, per <see cref="SegmentEscalation.AnswersAgree"/>) → sample 1
        /// unchanged. Disagreement → sample 1 carried with an <c>Inconsistent</c> trigger so it escalates.
        /// </summary>
        private static StepOutcome Reconcile(
            StepOutcome sample1, StepOutcome sample2, IReadOnlyList<Data.Entities.Character> characters)
        {
            if (sample2.Trigger == EscalationTrigger.ParseFailure || sample2.Outcome.Status.IsInfraFailure())
                return sample1;

            // An answer with no segments (empty paragraph) has nothing to compare.
            if (sample1.Outcome.Segments is not { } a || sample2.Outcome.Segments is not { } b)
                return sample1;

            return SegmentEscalation.AnswersAgree(a, b, characters)
                ? sample1
                : sample1 with { Trigger = EscalationTrigger.Inconsistent };
        }

        /// <summary>
        /// One streamed attribution sample against the step's config. Returns the same
        /// <see cref="AttributionOutcome"/> the public path returned before the escalation refactor,
        /// tagged with the quality <see cref="EscalationTrigger"/> derived from the same facts.
        /// </summary>
        private async Task<StepOutcome> AttributeSampleCoreAsync(
            QueuedParagraph item, ChainStepOptions opts, CancellationToken ct)
        {
            var config = opts.Config;
            var (before, after) = await prompts.GetContextWindowAsync();

            var ctx = await reader.GetParagraphContextAsync(
                item.Folder, item.ChapterId, item.ParagraphId, before, after);

            if (ctx == null || string.IsNullOrWhiteSpace(ctx.Query.Text))
            {
                logger.LogInformation("Paragraph {ParagraphId} has no text — marking unknown", item.ParagraphId);
                return new StepOutcome(
                    new AttributionOutcome(AttributionStatus.Unknown, null, null),
                    EscalationTrigger.Unknown);
            }

            var project = await reader.GetProjectAsync(item.Folder);
            var characters = await reader.GetCharactersWithAliasesAsync(item.Folder);
            var characterNames = characters.Select(c => new { name = c.Name, aliases = c.Aliases.Select(a => a.Name).ToArray() });

            var template = await prompts.GetCharacterPromptAsync(opts.EffectiveStyle);
            var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                [PromptTemplates.ContextJson]     = PromptTemplates.BuildContextJson(ctx),
                [PromptTemplates.ResponseFormat]  = SegmentAttributionSchema.JsonExample,
            });

            logger.LogDebug("Sending character attribution prompt for paragraph {ParagraphId}", item.ParagraphId);

            var run = await runner.RunAsync<SegmentAttributionResult>(
                new LlmRunRequest(config, prompt, item.Preview,
                    SegmentAttributionSchema.JsonSchema, CompletionShape.Object,
                    DisableThinking: !opts.Thinking),
                TryParseSegments, ct);

            switch (run.Outcome)
            {
                case LlmRunOutcome.ParseFailed:
                    logger.LogWarning("Failed to parse LLM response for {ParagraphId}: {Raw}", item.ParagraphId, run.Raw);
                    return ParseFailure(run.Error);
                case LlmRunOutcome.ModelLoading:
                    // The model is still loading on a switchable endpoint. This is neither a quality
                    // suspect nor an infra failure: with a None trigger the chain short-circuits (it
                    // must not escalate — that would autoload a different model and evict the load we
                    // are waiting for) and, because ModelLoading is not usable, it never feeds the
                    // best-prior fallback. It surfaces to the queue, which requeues with backoff.
                    logger.LogInformation("Paragraph {ParagraphId} model still loading — deferring to queue backoff", item.ParagraphId);
                    return ModelLoading(run.Error);
                case LlmRunOutcome.Failed:
                case LlmRunOutcome.ServiceUnavailable:
                    // Infra failure is orthogonal to quality — it carries no EscalationTrigger.
                    logger.LogError("Error attributing paragraph {ParagraphId}: {Reason}", item.ParagraphId, run.Error);
                    return new StepOutcome(
                        new AttributionOutcome(
                            run.Outcome == LlmRunOutcome.ServiceUnavailable
                                ? AttributionStatus.ServiceUnavailable
                                : AttributionStatus.Failed,
                            null, run.Error),
                        EscalationTrigger.None);
            }

            // The query the LLM answered is ctx.Query.Text — align against that exact string, so the
            // stored segment texts are slices of the paragraph the prompt showed it.
            return Classify(item.ParagraphId, ctx.Query.Text, run.Value!.Segments, characters,
                AnswerProvenance.From(config, run.Raw), run.Value!.Reasoning, ctx.Query.Segments);
        }

        /// <summary>
        /// Validates one paragraph's answer against the text it was asked about and classifies it:
        /// fidelity/alignment failure → <see cref="EscalationTrigger.ParseFailure"/>; otherwise the
        /// segments are re-sliced from the original text and the answer carries the trigger derived
        /// from its speakers. Status is <see cref="AttributionStatus.Unknown"/> when a dialog segment
        /// is unattributed (the answer still applies — its known segments stamp), else Resolved.
        /// <paramref name="reasoning"/> is the model's own one-sentence account of why it split and
        /// attributed the way it did. It is logged, never stored or acted on: a confident-but-wrong
        /// attribution is otherwise untraceable, because the raw answer is only logged when parsing
        /// or alignment fails, and the reasoning exists nowhere else once the answer is classified.
        /// <paramref name="priorSegments"/> is the paragraph's split as it stood before this answer;
        /// it is the only evidence that the answer dropped the paragraph's dialog
        /// (<see cref="SegmentEscalation.LosesDialog"/>), and it costs nothing — the context reader
        /// already loads it for every paragraph, target or not, and only the prompt builders drop it.
        /// </summary>
        private StepOutcome Classify(
            Guid paragraphId, string originalText, IReadOnlyList<AttributionSegment> segments,
            IReadOnlyList<Data.Entities.Character> characters, AnswerProvenance provenance,
            string? reasoning, IReadOnlyList<ContextSegment>? priorSegments)
        {
            if (!SegmentAligner.TryAlign(originalText, segments, out var aligned))
            {
                // The raw answer and the exact text it was asked about are logged together because
                // nothing else persists them: the runner abandons the stream once the JSON scanner
                // completes, so the client's own response log never runs for a structured attribution
                // run. Without this pair a misalignment is unreadable after the fact — you cannot tell
                // a near-miss transcription drift from an answer about the wrong paragraph. Matches
                // the parse-failure site above, which already logs the raw.
                logger.LogWarning(
                    "Segment texts do not reconstruct paragraph {ParagraphId} — treating as a parse failure. "
                    + "Config {ConfigName} (model {Model}), {SegmentCount} segment(s). Reasoning: {Reasoning} "
                    + "Paragraph: {Original} Answer: {Raw}",
                    paragraphId, provenance.ConfigName, provenance.Model, segments.Count,
                    reasoning, originalText, provenance.Raw);
                return ParseFailure("Segment texts did not match the paragraph text.");
            }

            var trigger = SegmentEscalation.DeriveTrigger(aligned, characters);
            var status = SegmentEscalation.HasUnknownSpeaker(aligned)
                ? AttributionStatus.Unknown
                : AttributionStatus.Resolved;

            if (SegmentEscalation.LosesDialog(priorSegments, aligned))
            {
                // Logged at warning, unlike the ordinary classification line below: this answer
                // would otherwise pass as confident and fully resolved, and applying it destroys
                // the Character item it dropped. Rare enough in practice to be worth one line each.
                logger.LogWarning(
                    "Paragraph {ParagraphId} answer folded all dialog into narration — escalating. "
                    + "Config {ConfigName} (model {Model}). Reasoning: {Reasoning} Paragraph: {Original}",
                    paragraphId, provenance.ConfigName, provenance.Model, reasoning, originalText);
                trigger = EscalationTrigger.DialogLost;
            }

            // Speakers and reasoning together: the pair is what makes a wrong-but-confident answer
            // readable after the fact — the names it chose, and the account it gave for choosing them.
            logger.LogInformation(
                "LLM segmented paragraph {ParagraphId} into {Count} segment(s), status {Status}, trigger {Trigger}, "
                + "config {ConfigName} (model {Model}). Dialog speakers: {Speakers}. Reasoning: {Reasoning}",
                paragraphId, aligned.Count, status, trigger, provenance.ConfigName, provenance.Model,
                DialogSpeakers(aligned), reasoning);

            return new StepOutcome(new AttributionOutcome(status, aligned, null), trigger);
        }

        /// <summary>
        /// The answer's dialog speakers in segment order, for the log line. Narration is dropped —
        /// it is always "narrator" and would bury the names that matter.
        /// </summary>
        private static string DialogSpeakers(IReadOnlyList<AttributionSegment> segments) =>
            string.Join(", ", segments
                .Where(s => s.Type == AttributionSegmentType.Dialog)
                .Select(s => s.Speaker));

        private static StepOutcome ParseFailure(string? error) =>
            new(new AttributionOutcome(AttributionStatus.Failed, null, error), EscalationTrigger.ParseFailure);

        /// <summary>
        /// Model-still-loading outcome. A None trigger so the chain accepts it immediately (no
        /// escalation), and a non-usable status so it never feeds the best-prior fallback — the queue
        /// requeues the item with backoff until the model is ready.
        /// </summary>
        private static StepOutcome ModelLoading(string? error) =>
            new(new AttributionOutcome(AttributionStatus.ModelLoading, null, error), EscalationTrigger.None);

        private static bool TryParseSegments(
            string raw, out SegmentAttributionResult? parsed, out string? error)
        {
            if (SegmentAttributionParser.TryParse(raw, out var p))
            {
                parsed = p;
                error = null;
                return true;
            }
            parsed = null;
            error = "Could not parse LLM response.";
            return false;
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
                Config = opts.Config.ForResample(),
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
                if (step1.Trigger == EscalationTrigger.ParseFailure || step1.Outcome.Status.IsInfraFailure()
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

            var template = await prompts.GetBatchCharacterPromptAsync(opts.EffectiveStyle);
            var prompt = PromptTemplates.Render(template, new Dictionary<string, string>
            {
                [PromptTemplates.BookTitle]       = project?.BookTitle ?? string.Empty,
                [PromptTemplates.BookAuthor]      = project?.Author ?? string.Empty,
                [PromptTemplates.KnownCharacters] = JsonSerializer.Serialize(characterNames),
                [PromptTemplates.ContextJson]     = PromptTemplates.BuildBatchContextJson(ctx),
                [PromptTemplates.ResponseFormat]  = SegmentBatchAttributionSchema.JsonExample,
            });

            logger.LogDebug("Sending batch character attribution prompt for {Count} paragraphs", included.Count);

            // The answer copies every indexed paragraph back as segments, so the output budget has to
            // grow with the passage — a fixed config max_tokens truncates (and so fails to parse) a
            // batch of long paragraphs.
            var runConfig = config.WithMaxTokens(AttributionTokenBudget.ForPassage(
                config.MaxTokens,
                ctx.Entries.Where(e => e.TargetIndex is not null).Select(e => e.Text)));

            // The answer must cover every requested index; a missing one is a parse failure for the
            // whole chunk (escalation's unit is the paragraph, and a half-answered batch is not one).
            var requested = Enumerable.Range(0, included.Count).ToList();

            bool TryParseBatchSegments(
                string raw, out IReadOnlyDictionary<int, SegmentAttributionResult>? parsed, out string? error)
            {
                if (SegmentAttributionParser.TryParseBatch(raw, requested, out var p))
                {
                    parsed = p;
                    error = null;
                    return true;
                }
                parsed = null;
                error = "Could not parse batch LLM response.";
                return false;
            }

            var run = await runner.RunAsync<IReadOnlyDictionary<int, SegmentAttributionResult>>(
                new LlmRunRequest(runConfig, prompt, $"{included.Count} paragraphs: {first.Preview}",
                    SegmentBatchAttributionSchema.JsonSchema, CompletionShape.Array,
                    DisableThinking: !opts.Thinking),
                TryParseBatchSegments, ct);

            if (run.Outcome == LlmRunOutcome.ModelLoading)
            {
                // Model still loading — surface ModelLoading (None trigger) for every included item
                // so the chain short-circuits and the queue requeues with backoff. Not a quality
                // suspect and not an infra failure, so it neither escalates nor feeds best-prior.
                // Deferred items are returned untouched, as with an infra failure below.
                logger.LogInformation("Batch of {Count} paragraphs: model still loading — deferring to queue backoff", included.Count);
                var loading = ModelLoading(run.Error);
                foreach (var item in included)
                    outcomes.Add((item, loading));
                return new BatchCoreResult(outcomes, deferred);
            }

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
                    null, run.Error);
                foreach (var item in included)
                    outcomes.Add((item, new StepOutcome(outcome, EscalationTrigger.None)));
                return new BatchCoreResult(outcomes, deferred);
            }

            if (run.Outcome == LlmRunOutcome.ParseFailed)
            {
                // An unparseable response, or one missing a requested index (the parser rejects the
                // whole answer), fails the chunk. Final step: today's behaviour — fall back to
                // single-item attribution. Non-final: the chunk hands off to the next step as
                // ParseFailure suspects.
                if (opts.IsFinal)
                {
                    logger.LogWarning("Failed to parse batch LLM response — falling back to single attribution: {Raw}", run.Raw);
                    foreach (var item in included)
                        outcomes.Add((item, await AttributeCoreAsync(item, opts, ct)));
                }
                else
                {
                    logger.LogWarning("Failed to parse batch LLM response — escalating chunk: {Raw}", run.Raw);
                    foreach (var item in included)
                        outcomes.Add((item, ParseFailure(run.Error)));
                }
                return new BatchCoreResult(outcomes, deferred);
            }

            var parsed = run.Value!;
            // Each target's own query text — the same string the prompt showed for that index —
            // and its pre-answer split, which the reader populates for targets too even though the
            // prompt deliberately shows them raw text only.
            var targets = ctx.Entries.Where(e => e.TargetIndex is not null).ToList();
            var queryTexts = targets.ToDictionary(e => e.TargetIndex!.Value, e => e.Text);
            var priorSegments = targets.ToDictionary(e => e.TargetIndex!.Value, e => e.Segments);

            // One raw for the whole chunk — a per-paragraph misalignment is logged against the batch
            // answer it came out of, which is the only form the response ever existed in.
            var provenance = AnswerProvenance.From(runConfig, run.Raw);

            for (var i = 0; i < included.Count; i++)
                outcomes.Add((included[i],
                    Classify(included[i].ParagraphId, queryTexts[i], parsed[i].Segments, characters, provenance,
                        parsed[i].Reasoning, priorSegments[i])));

            return new BatchCoreResult(outcomes, deferred);
        }
    }
}
