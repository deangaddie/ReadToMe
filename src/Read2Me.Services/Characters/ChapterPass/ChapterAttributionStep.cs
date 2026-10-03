using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Read2Me.AppData.Entities;
using Read2Me.Data;
using Read2Me.Services.Characters.Rules;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass (<see cref="AttributionPromptStyle.Chapter"/>, spec §4.2): per chapter, read
    /// the roster and the chapter once, then ask about one dialog item at a time, in chapter order,
    /// with the whole chapter so far as context and every earlier answer shown as a label. The answer
    /// is grammar-restricted to a roster name or <c>Unknown</c> plus a delivery cue. Streams one
    /// <see cref="StepOutcome"/> per queued paragraph; the walk and the processor apply it exactly as
    /// they apply the existing step's.
    /// <para>
    /// First, rules-discover creates a Character for each name a speech tag uses that the roster
    /// lacks, so the roster read for the chapter (and its grammar) can offer it.
    /// </para>
    /// <para>
    /// Before the ask loop, <see cref="SpeechTagRules"/> tags the items whose speaker an explicit
    /// speech tag names; those get a voice-only call (the grammar fixes the name) and show their
    /// name as a label from the first call on.
    /// </para>
    /// <para>
    /// The rung's <see cref="ChainStepOptions.SelfConsistency"/>,
    /// <see cref="ChainStepOptions.TemperatureOverride"/>, <see cref="ChainStepOptions.Thinking"/>
    /// and final-rung re-ask are ignored: every request is greedy, short and thinking-off.
    /// </para>
    /// </summary>
    internal sealed class ChapterAttributionStep(
        ILlmCompletionRunner runner,
        IProjectReader reader,
        CharacterResolver resolver,
        ILogger<ChapterAttributionStep> logger)
        : IChainStep
    {
        /// <summary>Room for a name and a 60-char cue; the grammar ends the answer well before it.</summary>
        public const int MaxTokens = 48;

        private static readonly LlmRunOverrides Greedy = new(MaxTokens: MaxTokens, Temperature: 0);

        /// <inheritdoc/>
        async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> IChainStep.RunAsync(
            IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var group in items.GroupBy(i => (i.Folder, i.ChapterId)))
                await foreach (var outcome in RunChapterAsync([.. group], opts, callbacks, ct))
                    yield return outcome;
        }

        /// <summary>
        /// One chapter: roster, snapshot, then the sequential ask loop over the queued paragraphs in
        /// chapter order. Nothing else is asked in between, so llama's one slot keeps the prefix.
        /// </summary>
        private async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunChapterAsync(
            IReadOnlyList<QueuedParagraph> group, ChainStepOptions opts,
            AttributionQueueCallbacks? callbacks, [EnumeratorCancellation] CancellationToken ct)
        {
            var first = group[0];
            var project = await reader.GetProjectAsync(first.Folder);
            var narrator = await reader.GetNarratorAsync(first.Folder, ct);
            var snapshot = await reader.GetChapterParagraphsForAttributionAsync(first.Folder, first.ChapterId);
            var stats = new ChapterStats();
            // Rules-discover (spec §4.2 step 1) may add Characters; it changes no items, so the
            // snapshot stands and only the roster is read again.
            stats.Created = await DiscoverAsync(first, snapshot, ct);
            var characters = await reader.GetCharactersWithAliasesAsync(first.Folder);

            foreach (var reserved in characters.Where(c => RosterGrammar.IsReserved(c.Name)))
                logger.LogWarning(
                    "Character '{Name}' collides with the Unknown answer and is left out of the chapter pass roster",
                    reserved.Name);

            var index = snapshot.Select((p, k) => (p.ParagraphId, k)).ToDictionary(x => x.ParagraphId, x => x.k);
            var prompt = new ChapterPassPrompt(
                project?.BookTitle ?? string.Empty, project?.Author ?? string.Empty, characters, snapshot,
                group.Select(i => i.ParagraphId).ToHashSet());
            var full = new Ask("full", RosterGrammar.ForRoster(prompt.Names), RosterParser(prompt.Names), VoiceOnly: false);

            // Rules pre-tag (spec §4.2 step 4): every tag is a label from the first call on, so tags
            // feed forward like answers; a tagged item in a queued paragraph gets a voice-only call.
            var tags = SpeechTagRules.TagChapter(snapshot, RulesRoster(characters));
            for (var k = 0; k < snapshot.Count; k++)
                for (var ii = 0; ii < snapshot[k].Items.Count; ii++)
                    if (tags.TryGetValue(snapshot[k].Items[ii].ItemId, out var tag))
                        prompt.SeedLabel(k, ii, tag.Speaker);

            // A queued paragraph the chapter no longer has (deleted, or no speech item left) has
            // nothing to ask: Unknown with an Unknown trigger, like the existing step's unaskable bin.
            foreach (var item in group.Where(i => !index.ContainsKey(i.ParagraphId)))
            {
                logger.LogInformation("Paragraph {ParagraphId} has no text — marking unknown", item.ParagraphId);
                yield return (item, new StepOutcome(
                    new AttributionOutcome(AttributionStatus.Unknown, null, null), EscalationTrigger.Unknown));
            }

            var queued = group.Where(i => index.ContainsKey(i.ParagraphId)).OrderBy(i => index[i.ParagraphId]).ToList();
            for (var q = 0; q < queued.Count; q++)
            {
                var item = queued[q];
                var k = index[item.ParagraphId];
                var paragraphItems = snapshot[k].Items;
                callbacks?.ChunkStarted?.Invoke([item]);

                var answered = new List<AttributedItem>();
                StepOutcome? failed = null;
                for (var ii = 0; ii < paragraphItems.Count && failed is null; ii++)
                {
                    if (!paragraphItems[ii].IsDialog)
                        continue;

                    stats.Items++;
                    var ask = full;
                    if (tags.TryGetValue(paragraphItems[ii].ItemId, out var tag))
                    {
                        stats.RuleTagged++;
                        ask = VoiceOnly(tag);
                    }

                    while (prompt.NeedsTrim(k, ii) && prompt.Trim(k))
                        Trimmed(stats, first.ChapterId, prompt, k, ii, "over budget");

                    var run = await AskAsync(prompt, k, ii, item, opts, ask, stats, ct);
                    if (run.Outcome == LlmRunOutcome.Failed && IsContextOverflow(run.Error) && prompt.Trim(k))
                    {
                        // The character budget is an estimate of tokens; llama's 400 is the truth.
                        // One more trim and one retry; a second overflow falls through to the fan-out.
                        Trimmed(stats, first.ChapterId, prompt, k, ii, "context overflow");
                        run = await AskAsync(prompt, k, ii, item, opts, ask, stats, ct);
                    }

                    switch (run.Outcome)
                    {
                        case LlmRunOutcome.Completed:
                            var answer = run.Value!;
                            answered.Add(new AttributedItem(ii, answer.Name, answer.Delivery));
                            if (AttributionWire.IsUnknownSpeaker(answer.Name))
                                stats.Unknown++;
                            else
                                prompt.SetLabel(k, ii, answer.Name);
                            break;

                        case LlmRunOutcome.ParseFailed:
                            logger.LogWarning(
                                "Failed to parse the chapter-pass answer for [{K}.{Ii}] on config {ConfigName}: {Raw}",
                                k, ii, opts.Config.Name, run.Raw);
                            failed = new StepOutcome(
                                new AttributionOutcome(AttributionStatus.Failed, null, run.Error),
                                EscalationTrigger.ParseFailure);
                            break;

                        default:
                            // Infra or still-loading: the rest of the chapter is one unit of failure,
                            // as a chunk is on the existing step. Stop asking and fan the outcome out.
                            var routed = InfraOutcome(run.Outcome, run.Error, opts.Config.Name, queued.Count - q);
                            LogSummary(first.ChapterId, stats);
                            for (var rest = q; rest < queued.Count; rest++)
                                yield return (queued[rest], routed);
                            yield break;
                    }
                }

                yield return (item, failed ?? Classify(item.ParagraphId, paragraphItems, answered, characters, narrator, opts));
            }

            LogSummary(first.ChapterId, stats);
        }

        /// <summary>
        /// Creates a Character for each name the chapter's speech tags use that the roster does not
        /// know, silently (spec Q3): the cast list shows it through the create's receipt, and the user
        /// merges a phantom there. The resolver re-checks names and aliases, so nothing is duplicated.
        /// </summary>
        /// <returns>How many Characters were created.</returns>
        private async Task<int> DiscoverAsync(
            QueuedParagraph first, IReadOnlyList<ChapterParagraph> snapshot, CancellationToken ct)
        {
            var roster = await reader.GetCharactersWithAliasesAsync(first.Folder);
            var discovered = SpeechTagRules.DiscoverNames(snapshot, RulesRoster(roster));
            var known = roster.Select(c => c.Id).ToHashSet();
            var created = 0;
            foreach (var name in discovered)
            {
                if (!known.Add(await resolver.ResolveOrCreateAsync(first.Folder, name.Name, ct)))
                    continue;
                created++;
                logger.LogInformation(
                    "Rules-discover created '{Name}' in '{Chapter}' ({Count}×, e.g. \"{Example}\")",
                    name.Name, first.ChapterId, name.Count, name.Example);
            }
            return created;
        }

        /// <summary>One request for item <paramref name="ii"/> of paragraph <paramref name="k"/>, counted and logged.</summary>
        private async Task<LlmRunResult<ChapterAnswer>> AskAsync(
            ChapterPassPrompt prompt, int k, int ii, QueuedParagraph item, ChainStepOptions opts,
            Ask ask, ChapterStats stats, CancellationToken ct)
        {
            var request = new LlmRunRequest(
                opts.Config, prompt.UserMessage(k, ii), $"[{k}.{ii}] {item.Preview}",
                Shape: CompletionShape.None, DisableThinking: true, Overrides: Greedy,
                SystemPrompt: prompt.SystemText, Grammar: ask.Grammar, DisplayPrompt: prompt.DisplayTail(k, ii));
            var run = await runner.RunAsync<ChapterAnswer>(request, ask.Parser, ct);
            stats.Called(run.Timings?.PromptN);
            if (ask.VoiceOnly)
                stats.VoiceOnly++;
            LogTimings(item.ChapterId, k, ii, ask.Kind, run.Timings);
            return run;
        }

        /// <summary>
        /// One kind of request: the roster call (<c>full</c>), or the voice-only call for a rule tag
        /// (<c>voice &lt;rule&gt;</c>) whose grammar fixes the name.
        /// </summary>
        private sealed record Ask(string Kind, string Grammar, TryParse<ChapterAnswer> Parser, bool VoiceOnly);

        private static Ask VoiceOnly(RuleTag tag) =>
            new($"voice {tag.Rule}", RosterGrammar.ForName(tag.Speaker), VoiceParser(tag.Speaker), VoiceOnly: true);

        /// <summary>
        /// The roster the rules match mentions against: the prompt's (no seed Narrator row, no name
        /// that collides with <c>Unknown</c>), with aliases, so every tag is an answerable name.
        /// </summary>
        private static List<RosterEntry> RulesRoster(IReadOnlyList<Data.Entities.Character> characters) =>
        [
            .. characters
                .Where(c => c.Id != ProjectDbContext.NarratorId && !RosterGrammar.IsReserved(c.Name))
                .Select(c => new RosterEntry(c.Name, [.. c.Aliases.Select(a => a.Name)])),
        ];

        /// <summary>llama's 400 for a prompt longer than the context: "…exceeds the available context size…".</summary>
        private static bool IsContextOverflow(string? error) =>
            error is not null
            && error.Contains("exceed", StringComparison.OrdinalIgnoreCase)
            && error.Contains("context", StringComparison.OrdinalIgnoreCase);

        private void Trimmed(ChapterStats stats, Guid chapterId, ChapterPassPrompt prompt, int k, int ii, string reason)
        {
            stats.Trimmed();
            logger.LogInformation(
                "Chapter pass {ChapterId}: passage trimmed to start at [{TrimStart}] before [{K}.{Ii}] ({Reason})",
                chapterId, prompt.TrimStart, k, ii, reason);
        }

        /// <summary>The per-chapter line of spec §4.6: what the pass did and whether the prompt cache held.</summary>
        private void LogSummary(Guid chapterId, ChapterStats stats) =>
            logger.LogInformation(
                "Chapter pass '{Chapter}': {Items} items, {Calls} calls ({VoiceOnly} voice-only, {RuleTagged} rule-tagged), "
                + "{Unknown} unknown, {Created} characters created, prompt_n total {PromptTotal} / median {PromptMedian} "
                + "(excl. first call and post-trim calls), {Trims} trims",
                chapterId, stats.Items, stats.Calls, stats.VoiceOnly, stats.RuleTagged, stats.Unknown, stats.Created,
                stats.PromptTotal, stats.PromptMedian, stats.Trims);

        /// <summary>
        /// Counters for one chapter's summary. The prompt_n median leaves out the chapter's first call
        /// and the first call after each trim: those prefill from cold by design, and the median is
        /// the check that every other call reused the prefix.
        /// </summary>
        private sealed class ChapterStats
        {
            private readonly List<int> _warm = [];
            private bool _cold = true;

            public int Items { get; set; }
            public int Calls { get; private set; }
            public int VoiceOnly { get; set; }
            public int RuleTagged { get; set; }
            public int Unknown { get; set; }
            public int Created { get; set; }
            public int Trims { get; private set; }
            public long PromptTotal { get; private set; }

            /// <summary>The middle warm prompt_n (the mean of the two middles when even); null with none.</summary>
            public int? PromptMedian
            {
                get
                {
                    if (_warm.Count == 0)
                        return null;
                    var sorted = _warm.Order().ToList();
                    var mid = sorted.Count / 2;
                    return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
                }
            }

            public void Called(int? promptN)
            {
                Calls++;
                if (promptN is { } n)
                {
                    PromptTotal += n;
                    if (!_cold)
                        _warm.Add(n);
                }
                _cold = false;
            }

            public void Trimmed()
            {
                Trims++;
                _cold = true;
            }
        }

        /// <summary>The answer as the grammar shapes it: a roster name or the unknown sentinel, and a cue.</summary>
        private sealed record ChapterAnswer(string Name, string? Delivery);

        /// <summary>
        /// The voice-only answer: the name is the rule's whatever the model wrote (the grammar
        /// allows only that name anyway); only the delivery is read.
        /// </summary>
        private static TryParse<ChapterAnswer> VoiceParser(string name) =>
            (string raw, out ChapterAnswer? value, out string? error) =>
            {
                value = new ChapterAnswer(name, RosterGrammar.ParseDelivery(raw));
                error = null;
                return true;
            };

        private static TryParse<ChapterAnswer> RosterParser(IReadOnlyList<string> roster)
        {
            var names = roster.ToHashSet(StringComparer.Ordinal);
            return (string raw, out ChapterAnswer? value, out string? error) =>
            {
                if (RosterGrammar.TryParse(raw, names, out var name, out var delivery))
                {
                    value = new ChapterAnswer(name!, delivery);
                    error = null;
                    return true;
                }
                value = null;
                error = "Could not parse chapter-pass answer.";
                return false;
            };
        }

        /// <summary>
        /// The paragraph's answers judged as the existing step judges them, so the walk treats a
        /// chapter-pass outcome exactly like any other.
        /// </summary>
        private StepOutcome Classify(
            Guid paragraphId, IReadOnlyList<ContextItem> items, IReadOnlyList<AttributedItem> answer,
            IReadOnlyList<Data.Entities.Character> characters, NarratorIdentity narrator, ChainStepOptions opts)
        {
            var trigger = ItemAttributionEscalation.DeriveTrigger(answer, items, characters, narrator);
            var status = ItemAttributionEscalation.HasUnknownSpeaker(answer, items, narrator)
                ? AttributionStatus.Unknown
                : AttributionStatus.Resolved;

            logger.LogInformation(
                "Chapter pass attributed paragraph {ParagraphId}: {Count} dialog item(s), status {Status}, "
                + "trigger {Trigger}, config {ConfigName}. Speakers: {Speakers}",
                paragraphId, answer.Count, status, trigger, opts.Config.Name,
                string.Join(", ", answer.Select(a => $"{a.Index}={a.Speaker}")));

            return new StepOutcome(
                new AttributionOutcome(status, AttributionAnswer.For(answer, items), null), trigger);
        }

        /// <summary>
        /// A run that produced no answer, as the existing step routes it: still loading → a None
        /// trigger the walk short-circuits on; failed or unavailable → infra, also None.
        /// </summary>
        private StepOutcome InfraOutcome(LlmRunOutcome outcome, string? error, string configName, int count)
        {
            if (outcome == LlmRunOutcome.ModelLoading)
            {
                logger.LogInformation(
                    "{Count} paragraph(s): model still loading — deferring to queue backoff", count);
                return new StepOutcome(
                    new AttributionOutcome(AttributionStatus.ModelLoading, null, error), EscalationTrigger.None);
            }

            logger.LogError(
                "Error attributing {Count} paragraph(s) by chapter pass on config {ConfigName}: {Reason}",
                count, configName, error);
            return new StepOutcome(
                new AttributionOutcome(
                    outcome == LlmRunOutcome.ServiceUnavailable
                        ? AttributionStatus.ServiceUnavailable
                        : AttributionStatus.Failed,
                    null, error),
                EscalationTrigger.None);
        }

        /// <summary>The cache check (spec §4.6): how much of each prompt llama had to prefill.</summary>
        private void LogTimings(Guid chapterId, int k, int ii, string kind, LlmTimings? timings) =>
            logger.LogDebug(
                "Chapter pass {ChapterId} [{K}.{Ii}] {Kind}: prompt_n {PromptN}, cache_n {CacheN}, predicted_n {PredictedN}",
                chapterId, k, ii, kind, timings?.PromptN, timings?.CacheN, timings?.PredictedN);
    }
}
