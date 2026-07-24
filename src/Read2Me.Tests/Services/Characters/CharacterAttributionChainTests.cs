using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Step mechanics and walk+step integration, tested through the production walk
    /// (<see cref="AttributionEscalationChain.AttributeQueueAsync"/>) over the real
    /// <see cref="IChainStep"/> and <see cref="LlmSettingsService"/> on the in-memory DB, with a
    /// config-recording fake LLM: prompt tiers, batch orchestration, grouping/chunking, self-consistency,
    /// trigger derivation, and that the walk composes over the real step end to end. Pure walk policy
    /// (best-prior, ModelLoading short-circuit, Accept, EscalationStarted, ItemDeferred) is covered
    /// without an LLM/DB/reader in <see cref="AttributionEscalationChainTests"/>.
    /// </summary>
    public class CharacterAttributionChainTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("chain-book");

        private LlmSettingsService NewSettings() => new(Factory, NullLogger<LlmSettingsService>.Instance);
        private LlmPromptService NewPrompts() => new(Factory, NullLogger<LlmPromptService>.Instance);

        private CharacterAttributionService NewService(
            ILlmCompletionRunner runner, IProjectReader reader, LlmSettingsService settings,
            EventBroadcaster<LlmStreamEvent>? broadcaster = null) =>
            new(runner, NewPrompts(), reader,
                NullLogger<CharacterAttributionService>.Instance);

        private static async Task<LlmServerConfig> AddConfigAsync(
            LlmSettingsService svc, string name, int batchSize = 8)
        {
            var config = new LlmServerConfig
            {
                Name = name,
                BaseUrl = $"http://localhost/{name}",
                Model = name,
                AttributionBatchSize = batchSize,
            };
            return await svc.CreateConfigAsync(config);
        }

        /// <summary>Registers a chain: first config active, and the whole chain (index 0 first) stored.</summary>
        private static async Task<List<LlmServerConfig>> RegisterChainAsync(
            LlmSettingsService svc, params (string Name, int BatchSize)[] configs)
        {
            var created = new List<LlmServerConfig>();
            foreach (var (name, batchSize) in configs)
                created.Add(await AddConfigAsync(svc, name, batchSize));
            await svc.SetActiveConfigAsync(created[0].Id);
            await svc.SetAttributionChainEntriesAsync(
                created.Select(c => new AttributionChainEntry(c.Id, Thinking: false)).ToList());
            return created;
        }

        private static QueuedParagraph MakeItem(string preview = "P") =>
            new(Folder, Guid.NewGuid(), preview, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        /// <summary>
        /// Wraps the service (as the <see cref="IChainStep"/> it implements) in the production walk,
        /// so these integration tests drive the same entry point <see cref="CharacterQueueProcessor"/>
        /// now uses. When an <paramref name="broadcaster"/> is supplied it must be the same instance
        /// the service was built with, so escalation events land on the observed broadcaster.
        /// </summary>
        private static AttributionEscalationChain Walk(
            CharacterAttributionService svc, LlmSettingsService settings,
            EventBroadcaster<LlmStreamEvent>? broadcaster = null) =>
            new(svc, settings, broadcaster ?? new EventBroadcaster<LlmStreamEvent>(),
                NullLogger<AttributionEscalationChain>.Instance);

        /// <summary>Drains the streaming attribution entry into a book-order list of outcomes.</summary>
        private static async Task<List<(QueuedParagraph Item, AttributionOutcome Outcome)>> DrainStreamAsync(
            CharacterAttributionService svc, LlmSettingsService settings,
            IReadOnlyList<QueuedParagraph> queued,
            EventBroadcaster<LlmStreamEvent>? broadcaster = null)
        {
            var results = new List<(QueuedParagraph, AttributionOutcome)>();
            await foreach (var pair in
                Walk(svc, settings, broadcaster).AttributeQueueAsync(queued, callbacks: null, CancellationToken.None))
                results.Add(pair);
            return results;
        }

        private static (List<QueuedParagraph> Batch, ParagraphBatchContext Ctx) MakeBatch(int count)
        {
            var chapterId = Guid.NewGuid();
            var batch = Enumerable.Range(0, count)
                .Select(i => new QueuedParagraph(Folder, Guid.NewGuid(), $"P{i}", chapterId, Guid.NewGuid(), Guid.NewGuid()))
                .ToList();
            var entries = batch.Select((_, i) => new BatchContextEntry(QueryText, [], i)).ToList();
            var ctx = new ParagraphBatchContext(entries, [.. batch.Select(b => b.ParagraphId)], []);
            return (batch, ctx);
        }

        private static ParagraphContext DefaultContext() =>
            new(new ContextParagraph("Hello world", []), [], []);

        private static Project DefaultProject() =>
            new() { Id = Guid.NewGuid(), Title = "Book", BookTitle = "The Book", Author = "Author", Filename = "b.epub" };

        /// <summary>
        /// Every paragraph in these tests reads "Hello world" (single and batch alike), so an answer
        /// is one dialog segment covering it — segment texts have to reconstruct the paragraph they
        /// answer or the aligner rejects them, which is a different test's job.
        /// </summary>
        private const string QueryText = "Hello world";

        private static string Segment(string speaker) =>
            $$"""{ "text": "{{QueryText}}", "type": "dialog", "speaker": "{{speaker}}", "voice_instructions": "" }""";

        private static string Resolved(string name) =>
            $$"""{ "reasoning": "r", "segments": [ {{Segment(name)}} ] }""";

        private static readonly string Unknown = Resolved("unknown");

        /// <summary>Batch answer: one segment per requested index, each with the given speaker.</summary>
        private static string BatchJson(params (int Index, string Speaker)[] entries) =>
            "[" + string.Join(",", entries.Select(e =>
                $$"""{ "index": {{e.Index}}, "reasoning": "r", "segments": [ {{Segment(e.Speaker)}} ] }""")) + "]";

        /// <summary>The speaker of a single-segment answer.</summary>
        private static string? Speaker(AttributionOutcome outcome) =>
            outcome.Segments is { Count: > 0 } s ? s[0].Speaker : null;

        // Known-character list: only "Alice" is listed, so any other resolved name is UnlistedName.
        private static List<Character> KnownAlice() =>
            [new Character { Id = Guid.NewGuid(), Name = "Alice", Aliases = [] }];

        /// <summary>
        /// Reader that, for a batch request, builds a context including exactly the paragraph ids it
        /// was asked for (indices 0..n-1), unless a fixed <paramref name="batchCtx"/> is supplied to
        /// exercise deferral. This mirrors the real reader, which returns context for the ids passed —
        /// essential once the orchestrator re-chunks suspects into smaller batches per step.
        /// </summary>
        private sealed class ChainReader(ParagraphContext? ctx, ParagraphBatchContext? batchCtx, List<Character> chars)
            : ProjectReaderFakeBase
        {
            public override Task<ParagraphContext?> GetParagraphContextAsync(
                ProjectFolderId f, Guid c, Guid p, int b, int a) => Task.FromResult(ctx);

            public override Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
                ProjectFolderId f, Guid c, IReadOnlyList<Guid> ids, int b, int a)
            {
                if (batchCtx != null) return Task.FromResult<ParagraphBatchContext?>(batchCtx);
                var entries = ids.Select((_, i) => new BatchContextEntry(QueryText, [], i)).ToList();
                return Task.FromResult<ParagraphBatchContext?>(new ParagraphBatchContext(entries, [.. ids], []));
            }

            public override Task<Project?> GetProjectAsync(ProjectFolderId f) => Task.FromResult<Project?>(DefaultProject());
            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId f) => Task.FromResult(chars);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Single-item chain walk
        // ─────────────────────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────────────────────
        // Prompt tier per config
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Phrase present only in the strict (Simple) attribution prompt.</summary>
        private const string SimpleMarker = "The ONLY acceptable evidence is an attribution tag";

        /// <summary>Phrase present only in the Full attribution prompt's inference heuristics.</summary>
        private const string FullMarker = "Vocatives:";

        [Fact]
        public async Task SimpleStyleConfig_SendsStrictPrompt()
        {
            var settings = NewSettings();
            var config = await AddConfigAsync(settings, "Small", batchSize: 1);
            config.PromptStyle = AttributionPromptStyle.Simple;
            await settings.UpdateConfigAsync(config);
            await settings.SetActiveConfigAsync(config.Id);

            var llm = new SequenceCompletionRunner().ForConfig("Small", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, [MakeItem()]);

            var prompt = Assert.Single(llm.Calls).Prompt;
            Assert.Contains(SimpleMarker, prompt);
            Assert.DoesNotContain(FullMarker, prompt);
        }

        [Fact]
        public async Task SimpleThenFull_EscalatesAndSendsEachConfigsOwnPrompt()
        {
            var settings = NewSettings();
            var chain = await RegisterChainAsync(settings, ("Small", 1), ("Big", 1));
            chain[0].PromptStyle = AttributionPromptStyle.Simple;
            await settings.UpdateConfigAsync(chain[0]);

            var llm = new SequenceCompletionRunner()
                .ForConfig("Small", Unknown)
                .ForConfig("Big", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", Speaker(result));

            var small = llm.Calls.Single(c => c.Config.Name == "Small").Prompt;
            var big = llm.Calls.Single(c => c.Config.Name == "Big").Prompt;
            Assert.Contains(SimpleMarker, small);
            Assert.Contains(FullMarker, big);
            Assert.DoesNotContain(SimpleMarker, big);
        }

        [Fact]
        public async Task SimpleStyleConfig_BatchMode_SendsStrictBatchPrompt()
        {
            var settings = NewSettings();
            var config = await AddConfigAsync(settings, "Small", batchSize: 4);
            config.PromptStyle = AttributionPromptStyle.Simple;
            await settings.UpdateConfigAsync(config);
            await settings.SetActiveConfigAsync(config.Id);

            var (batch, _) = MakeBatch(2);
            var llm = new SequenceCompletionRunner()
                .ForConfig("Small", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, batch);

            var prompt = Assert.Single(llm.Calls).Prompt;
            Assert.Contains("Return one entry per index", prompt);   // batch template, not the single one
            Assert.Contains(SimpleMarker, prompt);
            Assert.DoesNotContain(FullMarker, prompt);
        }

        /// <summary>
        /// The whole point of the per-rung style: one server, one model, asked strictly on the cold
        /// rung and with the full heuristics on escalation. Both rungs are the same config, so the
        /// config's own <see cref="LlmServerConfig.PromptStyle"/> cannot be what distinguishes them.
        /// </summary>
        [Fact]
        public async Task SameConfigTwice_DiffersOnlyByRungStyle_SendsEachRungsOwnPrompt()
        {
            var settings = NewSettings();
            var config = await AddConfigAsync(settings, "Only", batchSize: 1);
            await settings.SetActiveConfigAsync(config.Id);
            await settings.SetAttributionChainEntriesAsync(new[]
            {
                new AttributionChainEntry(config.Id, Thinking: false, AttributionPromptStyle.Simple),
                new AttributionChainEntry(config.Id, Thinking: false, AttributionPromptStyle.Full),
            });

            // Cold rung answers unknown (as the strict prompt is meant to), escalation resolves it.
            var llm = new SequenceCompletionRunner().ForConfig("Only", Unknown, Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", Speaker(result));
            Assert.Equal(2, llm.Calls.Count);
            Assert.Contains(SimpleMarker, llm.Calls[0].Prompt);
            Assert.DoesNotContain(FullMarker, llm.Calls[0].Prompt);
            Assert.Contains(FullMarker, llm.Calls[1].Prompt);
            Assert.DoesNotContain(SimpleMarker, llm.Calls[1].Prompt);
        }

        /// <summary>
        /// A rung's style overrides its config's, in the direction that matters least intuitively:
        /// a Simple-styled config asked with the full prompt on its escalation rung.
        /// </summary>
        [Fact]
        public async Task RungStyle_OverridesConfigStyle()
        {
            var settings = NewSettings();
            var config = await AddConfigAsync(settings, "Small", batchSize: 1);
            config.PromptStyle = AttributionPromptStyle.Simple;
            await settings.UpdateConfigAsync(config);
            await settings.SetActiveConfigAsync(config.Id);
            await settings.SetAttributionChainEntriesAsync(new[]
            {
                new AttributionChainEntry(config.Id, Thinking: false, AttributionPromptStyle.Full),
            });

            var llm = new SequenceCompletionRunner().ForConfig("Small", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, [MakeItem()]);

            var prompt = Assert.Single(llm.Calls).Prompt;
            Assert.Contains(FullMarker, prompt);
            Assert.DoesNotContain(SimpleMarker, prompt);
        }

        [Fact]
        public void WithTemperature_PreservesPromptStyle()
        {
            var config = new LlmServerConfig { Name = "Small", PromptStyle = AttributionPromptStyle.Simple };
            var resampled = config.WithTemperature(0.7);
            Assert.Equal(AttributionPromptStyle.Simple, resampled.PromptStyle);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Model still loading: batch-core propagation (walk-policy short-circuit
        // itself is covered in AttributionEscalationChainTests).
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Batch_ModelLoading_SurfacedForEveryItem_NoEscalation()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));

            var (batch, ctx) = MakeBatch(2);
            // The step-0 batch call reports the model still loading → every included item surfaces
            // ModelLoading and nothing escalates to B.
            var llm = new SequenceCompletionRunner()
                .FailFor("A", LlmRunOutcome.ModelLoading, "still loading")
                .ForConfig("B", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), ctx, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, batch);

            Assert.Equal(2, results.Count);
            Assert.All(results, o => Assert.Equal(AttributionStatus.ModelLoading, o.Outcome.Status));
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "A"));
            Assert.DoesNotContain(llm.Configs, c => c.Name == "B");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Queue-wide streaming escalation (the fix)
        // ─────────────────────────────────────────────────────────────────────

        private static QueuedParagraph MakeChapterItem(Guid chapterId, string preview = "P") =>
            new(Folder, Guid.NewGuid(), preview, chapterId, Guid.NewGuid(), Guid.NewGuid());

        [Fact]
        public async Task Queue_PrimaryDrainsWholeQueue_BeforeAnyEscalation_TwoChaptersBatchSize1()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 1), ("B", 1));

            var chA = Guid.NewGuid();
            var chB = Guid.NewGuid();
            var queued = new List<QueuedParagraph>
            {
                MakeChapterItem(chA), MakeChapterItem(chA),
                MakeChapterItem(chB), MakeChapterItem(chB),
            };
            // Primary (A) returns unknown for every paragraph → all four escalate to B.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Unknown)
                .ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, queued);

            Assert.Equal(4, results.Count);
            // The regression: every primary-config call precedes the first escalation-config call.
            var names = llm.Configs.Select(c => c.Name).ToList();
            var lastA = names.LastIndexOf("A");
            var firstB = names.IndexOf("B");
            Assert.Equal(4, names.Count(n => n == "A"));
            Assert.Equal(4, names.Count(n => n == "B"));
            Assert.True(lastA < firstB,
                $"Primary must finish the whole queue before escalating; got {string.Join(",", names)}");
        }

        [Fact]
        public async Task Queue_CrossChapterSuspects_EscalateSameBurst_GroupedPerChapter()
        {
            var settings = NewSettings();
            // B batch size 8 → each chapter's suspects fit one call; two chapters → two B calls.
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));

            var chA = Guid.NewGuid();
            var chB = Guid.NewGuid();
            var queued = new List<QueuedParagraph>
            {
                MakeChapterItem(chA), MakeChapterItem(chA),
                MakeChapterItem(chB), MakeChapterItem(chB),
            };
            // Every A answer unknown → all four escalate to B in one burst, grouped per chapter.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", BatchJson((0, "unknown"), (1, "unknown")))
                .ForConfig("B", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, queued);

            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.Resolved, r.Outcome.Status));
            // A: one batch call per chapter = 2. B: one grouped call per chapter = 2 (not 1 flat, not 4).
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "A"));
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "B"));
        }

        [Fact]
        public async Task Queue_EscalationStep_RegroupsByStepBatchSize()
        {
            var settings = NewSettings();
            // A batch 4 (one call for the chapter's 4 items); B batch 2 (4 suspects → 2 calls).
            await RegisterChainAsync(settings, ("A", 4), ("B", 2));

            var ch = Guid.NewGuid();
            var queued = Enumerable.Range(0, 4).Select(_ => MakeChapterItem(ch)).ToList();
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", BatchJson((0, "unknown"), (1, "unknown"), (2, "unknown"), (3, "unknown")))
                .ForConfig("B", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, queued);

            Assert.Equal(4, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.Resolved, r.Outcome.Status));
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "A"));
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "B"));
        }

        [Fact]
        public async Task Queue_ChunkStarted_FiresPerBatchSizeChunk_BeforeItsOutcome()
        {
            var settings = NewSettings();
            // Single-entry chain, batch size 2 over 4 items in one chapter → two step-0 chunks.
            await RegisterChainAsync(settings, ("A", 2));

            var ch = Guid.NewGuid();
            var queued = Enumerable.Range(0, 4).Select(_ => MakeChapterItem(ch)).ToList();
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var chunks = new List<IReadOnlyList<QueuedParagraph>>();
            await foreach (var _ in Walk(svc, settings).AttributeQueueAsync(
                queued,
                new AttributionQueueCallbacks(ChunkStarted: chunk => chunks.Add([.. chunk])),
                CancellationToken.None))
            {
            }

            // Two chunks of two, covering every item exactly once, each within the config's batch size.
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal(2, c.Count));
            Assert.Equal(
                queued.Select(q => q.ParagraphId).OrderBy(g => g),
                chunks.SelectMany(c => c).Select(q => q.ParagraphId).OrderBy(g => g));
        }

        [Fact]
        public async Task ChainStep_RunAsync_StreamsOncePerItem_ChunkedByBatchSize_NoDeferral()
        {
            // Drive the coarse IChainStep seam directly (no chain lookup, no walk): one config over
            // 4 items in one chapter, batch size 2 → two chunks, one StepOutcome per item, ItemDeferred
            // never fired by the step.
            var settings = NewSettings();
            var config = await AddConfigAsync(settings, "A", batchSize: 2);

            var ch = Guid.NewGuid();
            var items = Enumerable.Range(0, 4).Select(_ => MakeChapterItem(ch)).ToList();
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", BatchJson((0, "Alice"), (1, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var chunks = new List<IReadOnlyList<QueuedParagraph>>();
            var deferred = new List<QueuedParagraph>();
            var callbacks = new AttributionQueueCallbacks(
                ChunkStarted: chunk => chunks.Add([.. chunk]),
                ItemDeferred: deferred.Add);

            var outcomes = new List<(QueuedParagraph Item, StepOutcome Step)>();
            await foreach (var pair in ((IChainStep)svc).RunAsync(
                items, new ChainStepOptions(config, IsFinal: false, SelfConsistency: false),
                callbacks, CancellationToken.None))
            {
                outcomes.Add(pair);
            }

            // One outcome per item, every item covered exactly once.
            Assert.Equal(
                items.Select(i => i.ParagraphId).OrderBy(g => g),
                outcomes.Select(o => o.Item.ParagraphId).OrderBy(g => g));
            // Two chunks of two; ChunkStarted per in-flight chunk, ItemDeferred never.
            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.Equal(2, c.Count));
            Assert.Empty(deferred);
        }

        [Fact]
        public async Task Queue_ChunkOutcomes_Retired_BeforeNextChunkStarts()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 1), ("B", 1));
            var llm = new SequenceCompletionRunner().ForConfig("A", Unknown).ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var ch = Guid.NewGuid();
            var queued = new List<QueuedParagraph> { MakeChapterItem(ch, "P0"), MakeChapterItem(ch, "P1") };

            // One chapter group, batch size 1 => two chunks per step. Record the callback interleaving.
            var log = new List<string>();
            await foreach (var _ in Walk(svc, settings).AttributeQueueAsync(
                queued,
                new AttributionQueueCallbacks(
                    ChunkStarted: chunk => log.Add($"start:{chunk[0].Preview}"),
                    ItemDeferred: i => log.Add($"defer:{i.Preview}")),
                CancellationToken.None))
            {
            }

            // Step 0 chunk 1 must retire P0 (defer it) before chunk 2 puts P1 in flight. Buffering the
            // whole group would emit both starts first, leaving P0 stuck showing Processing.
            var step0 = log.Take(4).ToList();
            Assert.Equal(["start:P0", "defer:P0", "start:P1", "defer:P1"], step0);
        }

        [Fact]
        public async Task Queue_ParseFailure_RetriesSameConfigOnce_ThenEscalates()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 1), ("B", 1));
            // A returns garbage twice (the re-ask is asked of A before B is woken); B resolves.
            var llm = new SequenceCompletionRunner().ForConfig("A", "not json").ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var outcome = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal("Alice", Speaker(outcome));
            Assert.Equal(["A", "A", "B"], llm.Configs.Select(c => c.Name));
        }

        [Fact]
        public async Task Queue_ParseFailure_RetryThatParses_DoesNotEscalate()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 1), ("B", 1));
            // A garbles the first answer and gets the re-ask right — B is never woken.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", "not json", Resolved("Alice"))
                .ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var outcome = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, outcome.Status);
            Assert.Equal("Alice", Speaker(outcome));
            Assert.Equal(["A", "A"], llm.Configs.Select(c => c.Name));
        }

        [Fact]
        public async Task Queue_ParseFailureRetry_SamplesOffGreedy()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 1), ("B", 1));
            var llm = new SequenceCompletionRunner().ForConfig("A", "not json").ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, [MakeItem()]);

            // A greedy re-ask of an identical prompt would return the identical garbage.
            Assert.True(llm.Configs[1].Temperature > 0);
        }

        [Fact]
        public async Task Queue_NonSuspectItems_NotReAsked()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            var (batch, ctx) = MakeBatch(2);
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", BatchJson((0, "Alice"), (1, "unknown")))
                .ForConfig("B", BatchJson((0, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), ctx, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, batch);

            Assert.All(results, r => Assert.Equal(AttributionStatus.Resolved, r.Outcome.Status));
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "B"));   // only the one suspect escalated
        }

        [Fact]
        public async Task Queue_SelfConsistency_PerIndex_OnlyDisagreeingIndexEscalates()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            var (batch, ctx) = MakeBatch(3);
            var llm = new SequenceCompletionRunner()
                .ForConfig("A",
                    BatchJson((0, "Alice"), (1, "Alice"), (2, "Alice")),
                    BatchJson((0, "Alice"), (1, "Alice"), (2, "Zorg")))
                .ForConfig("B", BatchJson((0, "Alice")));
            var svc = NewService(llm, new ChainReader(DefaultContext(), ctx, KnownAlice()), settings);

            var results = await DrainStreamAsync(svc, settings, batch);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.Equal(AttributionStatus.Resolved, r.Outcome.Status));
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "A"));   // self-sampled twice
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "B"));   // only index 2 escalated
        }

        // ─────────────────────────────────────────────────────────────────────
        // Batch orchestration
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task Batch_ParseFailureEscalatesMidChain_SingleFallbackAtFinal()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));

            var (batch, ctx) = MakeBatch(2);
            // A returns garbage (non-final ParseFailure → re-asked once, then the whole chunk
            // escalates), B (final) also returns garbage in batch → final falls back to single-item;
            // single also garbage → Failed.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", "not json")
                .ForConfig("B", "still not json");
            var reader = new ChainReader(DefaultContext(), ctx, KnownAlice());
            var svc = NewService(llm, reader, settings);

            var results = await DrainStreamAsync(svc, settings, batch);

            Assert.Equal(2, results.Count);
            Assert.All(results, o => Assert.Equal(AttributionStatus.Failed, o.Outcome.Status));
            // A: 1 batch call + 1 same-rung re-ask = 2. B is final, so it is exempt from the
            // re-ask and spends 1 batch call + 2 single fallbacks = 3. Total 5.
            Assert.Equal(5, llm.Calls.Count);
            Assert.Equal(["A", "A"], llm.Configs.Take(2).Select(c => c.Name));
            Assert.All(llm.Configs.Skip(2), c => Assert.Equal("B", c.Name));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Self-consistency (slice 004): double-sample non-final steps, escalate
        // on disagreement. Toggle off by default; never applied to the final step.
        // ─────────────────────────────────────────────────────────────────────

        [Fact]
        public async Task SelfConsistency_Agreement_TwoCalls_NoEscalation()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            // Step 0 (non-final) self-samples twice; both agree → accepted at A, B never called.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Resolved("Alice"), Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", Speaker(result));
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "A"));
            Assert.DoesNotContain(llm.Configs, c => c.Name == "B");
        }

        [Fact]
        public async Task SelfConsistency_Disagreement_Escalates_CarriesSample1()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            // A disagrees with itself → Inconsistent → escalates to B (final), whose answer stands.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Resolved("Alice"), Resolved("Zorg"))
                .ForConfig("B", Resolved("Mordecai"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Mordecai", Speaker(result));       // B's final answer, not sample 1
            Assert.Equal(2, llm.Configs.Count(c => c.Name == "A")); // two samples at A
            Assert.Contains(llm.Configs, c => c.Name == "B");       // escalated
        }

        [Theory]
        [InlineData(null, 0.7)]  // null config temp → greedy sample 1 avoided by 0.7 default
        [InlineData(0.0, 0.7)]   // 0 config temp → 0.7 default
        [InlineData(0.4, 0.4)]   // positive config temp → used as-is
        public async Task SelfConsistency_TemperatureOverride_BothSamples(double? configTemp, double expected)
        {
            var settings = NewSettings();
            var a = new LlmServerConfig
            {
                Name = "A", BaseUrl = "http://localhost/A", Model = "A",
                AttributionBatchSize = 8, Temperature = configTemp,
            };
            var b = new LlmServerConfig { Name = "B", BaseUrl = "http://localhost/B", Model = "B", AttributionBatchSize = 8 };
            var created = new List<LlmServerConfig> { await settings.CreateConfigAsync(a), await settings.CreateConfigAsync(b) };
            await settings.SetActiveConfigAsync(created[0].Id);
            await settings.SetAttributionChainEntriesAsync(
                [new AttributionChainEntry(created[0].Id, false), new AttributionChainEntry(created[1].Id, false)]);
            await settings.SetSelfConsistencyAsync(true);

            var llm = new SequenceCompletionRunner().ForConfig("A", Resolved("Alice"), Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, [MakeItem()]);

            var temps = llm.Configs.Where(c => c.Name == "A").Select(c => c.Temperature).ToList();
            Assert.Equal(2, temps.Count);
            Assert.All(temps, t => Assert.Equal(expected, t));
        }

        [Fact]
        public async Task SelfConsistency_SecondSampleParseFailure_KeepsSample1_NoEscalation()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            // Sample 1 = known character (None trigger, accept); sample 2 = garbage (swallowed) → no
            // Inconsistent, accepted at A with sample 1, B never reached.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Resolved("Alice"), "not json");
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", Speaker(result));
            Assert.DoesNotContain(llm.Configs, c => c.Name == "B");
        }

        [Fact]
        public async Task SelfConsistency_ToggleOff_OneCallPerStep()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            // toggle left off (default) — behaviour identical to slice 003.

            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Unknown)
                .ForConfig("B", Resolved("Alice"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            await DrainStreamAsync(svc, settings, [MakeItem()]);

            Assert.Equal(1, llm.Configs.Count(c => c.Name == "A"));
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "B"));
        }

        [Fact]
        public async Task SelfConsistency_FinalStep_OneCall_EvenWhenToggleOn()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            // A escalates (unknown, self-sampled twice). B is final — one call, no self-sampling even
            // if its two answers would differ.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Unknown, Unknown)
                .ForConfig("B", Resolved("Alice"), Resolved("Zorg"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, KnownAlice()), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Alice", Speaker(result));            // B's single (first) answer stands
            Assert.Equal(1, llm.Configs.Count(c => c.Name == "B"));
        }

        [Fact]
        public async Task SelfConsistency_AliasCanonicalization_TreatedAsAgreement()
        {
            var settings = NewSettings();
            await RegisterChainAsync(settings, ("A", 8), ("B", 8));
            await settings.SetSelfConsistencyAsync(true);

            var chars = new List<Character>
            {
                new() { Id = Guid.NewGuid(), Name = "Elizabeth",
                    Aliases = [new CharacterAlias { Id = Guid.NewGuid(), Name = "Liz" }] },
            };
            // Sample 1 = alias "Liz", sample 2 = owner "Elizabeth" → canonicalize to the same name →
            // agreement → accepted at A, no escalation.
            var llm = new SequenceCompletionRunner()
                .ForConfig("A", Resolved("Liz"), Resolved("Elizabeth"));
            var svc = NewService(llm, new ChainReader(DefaultContext(), null, chars), settings);

            var result = Assert.Single(await DrainStreamAsync(svc, settings, [MakeItem()])).Outcome;

            Assert.Equal(AttributionStatus.Resolved, result.Status);
            Assert.Equal("Liz", Speaker(result));          // sample 1 carried verbatim
            Assert.DoesNotContain(llm.Configs, c => c.Name == "B");
        }
    }
}
