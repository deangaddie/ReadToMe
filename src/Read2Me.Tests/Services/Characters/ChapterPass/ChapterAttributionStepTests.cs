using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Characters.ChapterPass;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass as an <see cref="IChainStep"/> (spec §4.2): one grammar-restricted call per
    /// dialog item, sequentially through the chapter, answers fed forward as labels, one
    /// <see cref="StepOutcome"/> per queued paragraph, and an infra failure fanned out to the rest
    /// of the chapter.
    /// </summary>
    public class ChapterAttributionStepTests
    {
        private static readonly ProjectFolderId Folder = new("chapter-book");
        private static readonly Guid Chapter = Guid.NewGuid();
        private const string ConfigName = "gemma";

        private static readonly LlmServerConfig Config =
            new() { Name = ConfigName, Model = "gemma-12b", MaxTokens = 8192, Temperature = 1.0 };

        private static ContextItem Narr(string text) =>
            new(Guid.NewGuid(), text, AttributionWire.Narration, AttributionWire.Narrator);

        private static ContextItem Dialog(string text, string speaker = AttributionWire.Unknown) =>
            new(Guid.NewGuid(), text, AttributionWire.Dialog, speaker);

        private static ChapterParagraph Para(params ContextItem[] items) => new(Guid.NewGuid(), items);

        private static QueuedParagraph Queued(ChapterParagraph p) =>
            new(Folder, p.ParagraphId, "preview", Chapter, Guid.NewGuid(), Guid.NewGuid());

        private sealed class FakeReader(IReadOnlyList<ChapterParagraph> snapshot) : ProjectReaderFakeBase
        {
            public int SnapshotReads { get; private set; }

            /// <summary>The roster; <see cref="FakeResolver"/> adds to it as the real create would.</summary>
            public List<Character> Characters { get; } =
            [
                new() { Id = ProjectDbContext.NarratorId, Name = ProjectDbContext.NarratorName, IsNarrator = true },
                new() { Id = Guid.NewGuid(), Name = "Kulgan" },
                new() { Id = Guid.NewGuid(), Name = "Pug" },
            ];

            public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) =>
                Task.FromResult<Project?>(new Project { BookTitle = "Magician", Author = "Feist" });

            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) =>
                Task.FromResult(Characters.ToList());

            public override Task<NarratorIdentity> GetNarratorAsync(ProjectFolderId folderId, CancellationToken ct = default) =>
                Task.FromResult(NarratorIdentity.Unlinked);

            public override Task<IReadOnlyList<ChapterParagraph>> GetChapterParagraphsForAttributionAsync(
                ProjectFolderId folderId, Guid chapterId)
            {
                SnapshotReads++;
                return Task.FromResult(OtherChapters.GetValueOrDefault(chapterId) ?? snapshot);
            }

            /// <summary>Snapshots for chapters other than <see cref="Chapter"/>.</summary>
            public Dictionary<Guid, IReadOnlyList<ChapterParagraph>> OtherChapters { get; } = [];
        }

        /// <summary>
        /// Resolves a name the way <see cref="CharacterResolver"/> does (name or alias, case-insensitive,
        /// else create), against the fake reader's roster, and records each call with how many LLM
        /// requests had been sent by then.
        /// </summary>
        private sealed class FakeResolver(FakeReader reader, SequenceCompletionRunner? runner = null)
            : CharacterResolver(null!, null!)
        {
            public List<(string Name, int RequestsBefore)> Calls { get; } = [];

            public override Task<Guid> ResolveOrCreateAsync(ProjectFolderId folder, string name, CancellationToken ct)
            {
                Calls.Add((name, runner?.Requests.Count ?? 0));
                if (reader.Characters.FirstOrDefault(c => Matches(c, name)) is { } existing)
                    return Task.FromResult(existing.Id);
                var created = new Character { Id = Guid.NewGuid(), Name = name };
                reader.Characters.Add(created);
                return Task.FromResult(created.Id);
            }
        }

        private static ChapterAttributionStep NewStep(
            ILlmCompletionRunner runner, IProjectReader reader, ILogger<ChapterAttributionStep>? logger = null,
            CharacterResolver? resolver = null) =>
            new(runner, reader,
                resolver ?? new FakeResolver((FakeReader)reader, runner as SequenceCompletionRunner),
                logger ?? NullLogger<ChapterAttributionStep>.Instance);

        private static async Task<List<(QueuedParagraph Item, StepOutcome Step)>> RunAsync(
            ChapterAttributionStep step, IReadOnlyList<QueuedParagraph> items,
            ChainStepOptions? opts = null, AttributionQueueCallbacks? callbacks = null)
        {
            var outcomes = new List<(QueuedParagraph, StepOutcome)>();
            await foreach (var pair in ((IChainStep)step).RunAsync(
                items, opts ?? new ChainStepOptions(Config, IsFinal: false, SelfConsistency: false),
                callbacks, CancellationToken.None))
                outcomes.Add(pair);
            return outcomes;
        }

        private static string Asked(LlmRunRequest r) =>
            r.Prompt[(r.Prompt.LastIndexOf("Who speaks ", StringComparison.Ordinal) + 11)..].Split(',')[0];

        [Fact]
        public async Task Asks_once_per_dialog_item_in_chapter_order()
        {
            var p0 = Para(Narr("Night."), Dialog("“A”"), Narr("said he."), Dialog("“B”"));
            var p1 = Para(Narr("Silence."));
            var p2 = Para(Dialog("“C”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm", "Kulgan |", "Pug | angry");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0, p1, p2])), [Queued(p0), Queued(p2)]);

            Assert.Equal(["⟦0.1⟧", "⟦0.3⟧", "⟦2.0⟧"], runner.Requests.Select(Asked));
            Assert.Equal([p0.ParagraphId, p2.ParagraphId], outcomes.Select(o => o.Item.ParagraphId));

            var first = outcomes[0].Step;
            Assert.Equal(AttributionStatus.Resolved, first.Outcome.Status);
            Assert.Equal(EscalationTrigger.None, first.Trigger);
            Assert.Equal(
                [new AttributedItem(1, "Pug", "calm"), new AttributedItem(3, "Kulgan", null)],
                first.Outcome.Answer!.Items);
            Assert.Equal([null, p0.Items[1].ItemId, null, p0.Items[3].ItemId], first.Outcome.Answer.ItemIds);
            Assert.Equal([new AttributedItem(0, "Pug", "angry")], outcomes[1].Step.Outcome.Answer!.Items);
        }

        [Fact]
        public async Task An_answer_is_fed_forward_as_a_label_in_the_next_call()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm", "Kulgan | dry");

            await RunAsync(NewStep(runner, new FakeReader([p0, p1])), [Queued(p0), Queued(p1)]);

            Assert.DoesNotContain("{Pug}", runner.Requests[0].Prompt);
            Assert.Contains("[0] {Pug} ⟦0.0⟧“A”\n", runner.Requests[1].Prompt);
        }

        [Fact]
        public async Task An_unknown_answer_stays_unlabelled_and_makes_the_paragraph_unknown()
        {
            var p0 = Para(Dialog("“A”"), Dialog("“B”"));
            var p1 = Para(Dialog("“C”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Unknown | sly", "Pug | calm", "Pug |");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0, p1])), [Queued(p0), Queued(p1)]);

            Assert.Contains("[0] ⟦0.0⟧“A” {Pug} ⟦0.1⟧“B”\n", runner.Requests[2].Prompt);
            var step = outcomes[0].Step;
            Assert.Equal(EscalationTrigger.Unknown, step.Trigger);
            Assert.Equal(AttributionStatus.Unknown, step.Outcome.Status);
            Assert.Equal(new AttributedItem(0, AttributionWire.Unknown, null), step.Outcome.Answer!.Items[0]);
        }

        [Theory]
        [InlineData(LlmRunOutcome.Failed, AttributionStatus.Failed)]
        [InlineData(LlmRunOutcome.ServiceUnavailable, AttributionStatus.ServiceUnavailable)]
        [InlineData(LlmRunOutcome.ModelLoading, AttributionStatus.ModelLoading)]
        public async Task An_infra_outcome_mid_chapter_fans_out_to_the_rest_and_stops_asking(
            LlmRunOutcome run, AttributionStatus status)
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"), Dialog("“C”"));
            var p2 = Para(Dialog("“D”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            runner.FailFor(ConfigName, run, "boom");

            var outcomes = await RunAsync(
                NewStep(runner, new FakeReader([p0, p1, p2])), [Queued(p0), Queued(p1), Queued(p2)]);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal([p0.ParagraphId, p1.ParagraphId, p2.ParagraphId], outcomes.Select(o => o.Item.ParagraphId));
            Assert.Equal(AttributionStatus.Resolved, outcomes[0].Step.Outcome.Status);
            Assert.All(outcomes.Skip(1), o =>
            {
                Assert.Equal(status, o.Step.Outcome.Status);
                Assert.Equal(EscalationTrigger.None, o.Step.Trigger);
                Assert.Null(o.Step.Outcome.Answer);
                Assert.Equal("boom", o.Step.Outcome.FailureReason);
            });
        }

        [Fact]
        public async Task An_unparseable_answer_fails_only_its_paragraph()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Tomas | calm", "Pug | calm");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0, p1])), [Queued(p0), Queued(p1)]);

            Assert.Equal(EscalationTrigger.ParseFailure, outcomes[0].Step.Trigger);
            Assert.Equal(AttributionStatus.Failed, outcomes[0].Step.Outcome.Status);
            Assert.Equal(AttributionStatus.Resolved, outcomes[1].Step.Outcome.Status);
        }

        [Fact]
        public async Task Every_request_is_greedy_short_thinking_off_and_grammar_restricted_whatever_the_options()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            var opts = new ChainStepOptions(
                Config, IsFinal: true, SelfConsistency: true, Thinking: true,
                Style: AttributionPromptStyle.Chapter, TemperatureOverride: 0.7);

            await RunAsync(NewStep(runner, new FakeReader([p0, p1])), [Queued(p0), Queued(p1)], opts);

            Assert.Equal(2, runner.Requests.Count);
            Assert.All(runner.Requests, r =>
            {
                Assert.True(r.DisableThinking);
                Assert.Equal(new LlmRunOverrides(MaxTokens: 48, Temperature: 0), r.Overrides);
                Assert.Equal(CompletionShape.None, r.Shape);
                Assert.Null(r.JsonSchema);
                Assert.Equal(RosterGrammar.ForRoster(["Kulgan", "Pug"]), r.Grammar);
                Assert.StartsWith("You identify who speaks", r.SystemPrompt);
                Assert.StartsWith("Passage (tail):\n", r.DisplayPrompt);
                Assert.Same(Config, r.Config);
            });
            Assert.Equal(runner.Requests[0].SystemPrompt, runner.Requests[1].SystemPrompt);
        }

        [Fact]
        public async Task Chunk_started_fires_per_paragraph_and_the_chapter_is_read_once()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var reader = new FakeReader([p0, p1]);
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            var started = new List<IReadOnlyList<QueuedParagraph>>();

            await RunAsync(NewStep(runner, reader), [Queued(p0), Queued(p1)],
                callbacks: new AttributionQueueCallbacks(ChunkStarted: started.Add));

            Assert.Equal([[p0.ParagraphId], [p1.ParagraphId]], started.Select(c => c.Select(i => i.ParagraphId).ToList()));
            Assert.Equal(1, reader.SnapshotReads);
        }

        /// <summary>The passage's first line number: where the front-trim currently starts.</summary>
        private static int FirstLine(LlmRunRequest r) =>
            int.Parse(r.Prompt["Passage:\n[".Length..r.Prompt.IndexOf(']', StringComparison.Ordinal)],
                System.Globalization.CultureInfo.InvariantCulture);

        private const string ContextOverflow =
            "LLM provider returned error (BadRequest): {\"error\":{\"code\":400,"
            + "\"message\":\"the request exceeds the available context size, try increasing it\"}}";

        [Fact]
        public async Task A_long_chapter_is_front_trimmed_so_every_request_fits_and_every_item_is_asked()
        {
            var paras = Enumerable.Range(0, 120)
                .Select(i => Para(Narr($"N{i}."), Dialog($"“{new string('x', 600)}”")))
                .ToList();
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader(paras)), [.. paras.Select(Queued)]);

            Assert.Equal(paras.Select((_, k) => $"⟦{k}.1⟧"), runner.Requests.Select(Asked));
            Assert.All(runner.Requests, r =>
                Assert.True(r.SystemPrompt!.Length + r.Prompt.Length <= ChapterPassBudget.MaxPromptChars));
            Assert.All(outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Step.Outcome.Status));

            var trims = runner.Requests.Select(FirstLine).Distinct().Count() - 1;
            Assert.InRange(trims, 1, 10);
            Assert.True(runner.Requests.Select(FirstLine).Zip(runner.Requests.Skip(1).Select(FirstLine))
                .All(p => p.First <= p.Second), "the trim start only moves forward");
        }

        [Fact]
        public async Task A_context_overflow_trims_once_more_and_retries_the_item()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var p2 = Para(Dialog("“C”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm", "Kulgan |");
            runner.FailFor(ConfigName, LlmRunOutcome.Failed, ContextOverflow);
            runner.ForConfig(ConfigName, "Pug | dry");

            var outcomes = await RunAsync(
                NewStep(runner, new FakeReader([p0, p1, p2])), [Queued(p0), Queued(p1), Queued(p2)]);

            Assert.Equal(["⟦0.0⟧", "⟦1.0⟧", "⟦2.0⟧", "⟦2.0⟧"], runner.Requests.Select(Asked));
            Assert.Equal([0, 0, 0, 1], runner.Requests.Select(FirstLine));
            Assert.All(outcomes, o => Assert.Equal(AttributionStatus.Resolved, o.Step.Outcome.Status));
            Assert.Equal([new AttributedItem(0, "Pug", "dry")], outcomes[2].Step.Outcome.Answer!.Items);
        }

        [Fact]
        public async Task A_second_context_overflow_fans_out_to_the_rest_of_the_chapter()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var p2 = Para(Dialog("“C”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            runner.FailFor(ConfigName, LlmRunOutcome.Failed, ContextOverflow);
            runner.FailFor(ConfigName, LlmRunOutcome.Failed, ContextOverflow);

            var outcomes = await RunAsync(
                NewStep(runner, new FakeReader([p0, p1, p2])), [Queued(p0), Queued(p1), Queued(p2)]);

            Assert.Equal(["⟦0.0⟧", "⟦1.0⟧", "⟦1.0⟧"], runner.Requests.Select(Asked));
            Assert.Equal(AttributionStatus.Resolved, outcomes[0].Step.Outcome.Status);
            Assert.All(outcomes.Skip(1), o => Assert.Equal(AttributionStatus.Failed, o.Step.Outcome.Status));
        }

        [Fact]
        public async Task A_failure_that_is_not_a_context_overflow_is_not_retried()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            runner.FailFor(ConfigName, LlmRunOutcome.Failed, "LLM provider returned error (InternalServerError): boom");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0, p1])), [Queued(p0), Queued(p1)]);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(AttributionStatus.Failed, outcomes[1].Step.Outcome.Status);
        }

        [Fact]
        public async Task One_summary_line_per_chapter_with_counts_and_the_prompt_n_median()
        {
            var p0 = Para(Dialog("“A”"), Narr("x"), Dialog("“B”"));
            var p1 = Para(Dialog("“C”"));
            var p2 = Para(Dialog("“D”"));
            var runner = new SequenceCompletionRunner()
                .ForConfig(ConfigName, "Pug | calm", "Unknown |", "Kulgan |");
            runner.FailFor(ConfigName, LlmRunOutcome.Failed, ContextOverflow);
            runner.ForConfig(ConfigName, "Pug |");
            int[] promptN = [1000, 100, 300, 900, 900, 200];
            runner.Timings = (_, n) => new LlmTimings(CacheN: 50, PromptN: promptN[n], null, PredictedN: 4, null);
            var logger = new CollectingLogger<ChapterAttributionStep>();

            await RunAsync(NewStep(runner, new FakeReader([p0, p1, p2]), logger),
                [Queued(p0), Queued(p1), Queued(p2)]);

            // Calls: [0.0]=1000 (first), [0.2]=100, [1.0]=300, [2.0] overflow=900, [2.0] retry=900
            // (first after a trim). The median counts 100, 300 and the overflowed 900 → 300.
            var summary = Assert.Single(logger.At(LogLevel.Information), m => m.StartsWith("Chapter pass '", StringComparison.Ordinal));
            Assert.Equal(
                $"Chapter pass '{Chapter}': 4 items, 5 calls (0 voice-only, 0 rule-tagged), 1 unknown, "
                + "0 characters created, prompt_n total 3200 / median 300 (excl. first call and post-trim calls), 1 trims",
                summary);
        }

        [Fact]
        public async Task The_summary_is_logged_for_a_chapter_stopped_by_an_infra_failure()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“B”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");
            runner.FailFor(ConfigName, LlmRunOutcome.ServiceUnavailable, "down");
            var logger = new CollectingLogger<ChapterAttributionStep>();

            await RunAsync(NewStep(runner, new FakeReader([p0, p1]), logger), [Queued(p0), Queued(p1)]);

            Assert.Single(logger.At(LogLevel.Information), m => m.StartsWith("Chapter pass '", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_queued_paragraph_missing_from_the_chapter_is_unknown_without_a_call()
        {
            var p0 = Para(Dialog("“A”"));
            var gone = Para(Dialog("“gone”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0])), [Queued(gone), Queued(p0)]);

            Assert.Single(runner.Requests);
            var missing = outcomes.Single(o => o.Item.ParagraphId == gone.ParagraphId).Step;
            Assert.Equal(AttributionStatus.Unknown, missing.Outcome.Status);
            Assert.Equal(EscalationTrigger.Unknown, missing.Trigger);
            Assert.Null(missing.Outcome.Answer);
        }

        [Fact]
        public async Task A_rule_tagged_item_gets_a_voice_only_call_and_keeps_the_tagged_name()
        {
            var p0 = Para(Dialog("“We must go,”"), Narr("said Pug."));
            // The voice-only grammar cannot produce another name; the parse must not trust one anyway.
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Kulgan | urgent");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0])), [Queued(p0)]);

            var request = Assert.Single(runner.Requests);
            Assert.Equal(RosterGrammar.ForName("Pug"), request.Grammar);
            Assert.EndsWith("Who speaks ⟦0.0⟧, and how is it delivered? Answer as Name | delivery.", request.Prompt);
            Assert.Equal(AttributionStatus.Resolved, outcomes[0].Step.Outcome.Status);
            Assert.Equal([new AttributedItem(0, "Pug", "urgent")], outcomes[0].Step.Outcome.Answer!.Items);
        }

        [Fact]
        public async Task An_untagged_item_still_gets_the_roster_grammar()
        {
            // Item 0 has a named tag; item 2 follows another character's beat, so the rules leave it.
            var p0 = Para(Dialog("“We must go,”"), Narr("said Pug. Kulgan frowned."), Dialog("“Now.”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | urgent", "Kulgan | curt");

            var outcomes = await RunAsync(NewStep(runner, new FakeReader([p0])), [Queued(p0)]);

            Assert.Equal(["⟦0.0⟧", "⟦0.2⟧"], runner.Requests.Select(Asked));
            Assert.Equal(RosterGrammar.ForName("Pug"), runner.Requests[0].Grammar);
            Assert.Equal(RosterGrammar.ForRoster(["Kulgan", "Pug"]), runner.Requests[1].Grammar);
            Assert.Equal(
                [new AttributedItem(0, "Pug", "urgent"), new AttributedItem(2, "Kulgan", "curt")],
                outcomes[0].Step.Outcome.Answer!.Items);
        }

        [Fact]
        public async Task Rule_tags_are_labels_from_the_very_first_call_but_never_over_a_stamp()
        {
            var p0 = Para(Dialog("“A”"));
            var queuedTagged = Para(Dialog("“We must go,”"), Narr("said Pug."));
            var unstampedTagged = Para(Dialog("“Sit down,”"), Narr("said Kulgan."));
            var stamped = Para(Dialog("“Hm,”", "Kulgan"), Narr("said Pug."));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Kulgan | calm", "Pug | urgent");

            await RunAsync(
                NewStep(runner, new FakeReader([p0, queuedTagged, unstampedTagged, stamped])),
                [Queued(p0), Queued(queuedTagged)]);

            var first = runner.Requests[0].Prompt;
            Assert.Equal("⟦0.0⟧", Asked(runner.Requests[0]));
            Assert.Contains("[1] {Pug} ⟦1.0⟧“We must go,” said Pug.\n", first);
            Assert.Contains("[2] {Kulgan} ⟦2.0⟧“Sit down,” said Kulgan.\n", first);
            Assert.Contains("[3] {Kulgan} ⟦3.0⟧“Hm,” said Pug.\n", first);
        }

        [Fact]
        public async Task Voice_only_calls_and_rule_tags_are_counted_and_logged_with_the_rule_id()
        {
            var p0 = Para(Dialog("“We must go,”"), Narr("said Pug. Kulgan frowned."), Dialog("“Now.”"));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | urgent", "Kulgan | curt");
            var logger = new CollectingLogger<ChapterAttributionStep>();

            await RunAsync(NewStep(runner, new FakeReader([p0]), logger), [Queued(p0)]);

            var debug = logger.At(LogLevel.Debug).ToList();
            Assert.Contains(debug, m => m.Contains("[0.0] voice T1-post-vs:", StringComparison.Ordinal));
            Assert.Contains(debug, m => m.Contains("[0.2] full:", StringComparison.Ordinal));
            var summary = Assert.Single(logger.At(LogLevel.Information), m => m.StartsWith("Chapter pass '", StringComparison.Ordinal));
            Assert.Contains("2 items, 2 calls (1 voice-only, 1 rule-tagged)", summary);
        }

        [Fact]
        public async Task An_unlisted_tag_name_is_created_before_the_first_call_and_offered_from_it()
        {
            var p0 = Para(Dialog("“A”"));
            var p1 = Para(Dialog("“We must go,”"), Narr("said Laurie."));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Pug | calm", "Laurie | urgent");
            var reader = new FakeReader([p0, p1]);
            var resolver = new FakeResolver(reader, runner);

            var outcomes = await RunAsync(NewStep(runner, reader, resolver: resolver), [Queued(p0), Queued(p1)]);

            Assert.Equal([("Laurie", 0)], resolver.Calls);
            var first = runner.Requests[0];
            Assert.Contains("- Laurie", first.SystemPrompt);
            Assert.Equal(RosterGrammar.ForRoster(["Kulgan", "Pug", "Laurie"]), first.Grammar);
            // with Laurie on the roster, the said-tag is now a rule tag
            Assert.Equal(RosterGrammar.ForName("Laurie"), runner.Requests[1].Grammar);
            Assert.Equal([new AttributedItem(0, "Laurie", "urgent")], outcomes[1].Step.Outcome.Answer!.Items);
        }

        [Fact]
        public async Task A_name_created_in_one_chapter_is_not_created_again_in_the_next()
        {
            var otherChapter = Guid.NewGuid();
            var p0 = Para(Dialog("“We must go,”"), Narr("said Laurie."));
            var p1 = Para(Dialog("“Wait,”"), Narr("said Laurie."));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Laurie | urgent", "Laurie | soft");
            var reader = new FakeReader([p0]);
            reader.OtherChapters[otherChapter] = [p1];
            var resolver = new FakeResolver(reader, runner);

            await RunAsync(
                NewStep(runner, reader, resolver: resolver),
                [Queued(p0), Queued(p1) with { ChapterId = otherChapter }]);

            Assert.Equal(["Laurie"], resolver.Calls.Select(c => c.Name));
            Assert.Equal(2, runner.Requests.Count);
        }

        [Fact]
        public async Task A_created_character_is_logged_with_its_example_and_counted_in_the_summary()
        {
            var p0 = Para(Dialog("“We must go,”"), Narr("said Laurie."));
            var runner = new SequenceCompletionRunner().ForConfig(ConfigName, "Laurie | urgent");
            var logger = new CollectingLogger<ChapterAttributionStep>();

            await RunAsync(NewStep(runner, new FakeReader([p0]), logger), [Queued(p0)]);

            var info = logger.At(LogLevel.Information).ToList();
            Assert.Contains($"Rules-discover created 'Laurie' in '{Chapter}' (1×, e.g. \"said Laurie.\")", info);
            var summary = Assert.Single(info, m => m.StartsWith("Chapter pass '", StringComparison.Ordinal));
            Assert.Contains("1 characters created", summary);
        }
    }
}
