using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Direct unit tests for <see cref="AttributionRequestBuilder"/> — the one new internal seam.
    /// Exercised through the <c>InternalsVisibleTo</c> door with a scripted batch reader, so the
    /// indirect runner-fake pins these used to need in the service tests move here: template/shape by
    /// included count, the three-bin classification, the missing-first retry loop, the
    /// entries→single-context adapter, the token budget, and the roster projection.
    /// </summary>
    public class AttributionRequestBuilderTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");
        private static readonly Guid Chapter = Guid.NewGuid();

        private LlmPromptService NewPrompts() => new(Factory, NullLogger<LlmPromptService>.Instance);

        private static LlmServerConfig Config(int? maxTokens = null, double? temperature = null) =>
            new()
            {
                Name = "Test",
                BaseUrl = "http://localhost:8080",
                Model = "test",
                AttributionBatchSize = 8,
                MaxTokens = maxTokens,
                Temperature = temperature,
            };

        private static ChainStepOptions Opts(LlmServerConfig config, bool thinking = false) =>
            new(config, IsFinal: true, SelfConsistency: false, thinking);

        private static QueuedParagraph Para(string preview) =>
            new(Folder, Guid.NewGuid(), preview, Chapter, Guid.NewGuid(), Guid.NewGuid());

        private static Character Character(string name, params string[] aliases) =>
            new() { Id = Guid.NewGuid(), Name = name, Aliases = [.. aliases.Select(a => new CharacterAlias { Name = a })] };

        private static Project Project() =>
            new() { Id = Guid.NewGuid(), Title = "Book", BookTitle = "The Book", Author = "Author", Filename = "b.epub" };

        // ---------------------------------------------------------------
        // Scripted batch reader: serves pre-built contexts in order (the retry loop pulls the next
        // one each call), records every requested id list, and hands back a fixed roster/project.
        // ---------------------------------------------------------------

        private sealed class BatchReaderFake : ProjectReaderFakeBase
        {
            private readonly Queue<ParagraphBatchContext?> _contexts;
            private readonly List<Character> _characters;
            private readonly Project? _project;

            public List<IReadOnlyList<Guid>> ReceivedIds { get; } = [];
            public int ReceivedBefore { get; private set; }
            public int ReceivedAfter { get; private set; }
            public NarratorIdentity Narrator { get; init; } = NarratorIdentity.Unlinked;

            public BatchReaderFake(
                IEnumerable<ParagraphBatchContext?> contexts,
                List<Character>? characters = null,
                Project? project = null)
            {
                _contexts = new Queue<ParagraphBatchContext?>(contexts);
                _characters = characters ?? [];
                _project = project;
            }

            public override Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
                ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after)
            {
                ReceivedIds.Add([.. paragraphIds]);
                ReceivedBefore = before;
                ReceivedAfter = after;
                return Task.FromResult(_contexts.Count > 0 ? _contexts.Dequeue() : null);
            }

            public override Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) =>
                Task.FromResult(_characters);

            public override Task<Project?> GetProjectAsync(ProjectFolderId folderId) =>
                Task.FromResult(_project);

            public override Task<NarratorIdentity> GetNarratorAsync(
                ProjectFolderId folderId, CancellationToken ct = default) =>
                Task.FromResult(Narrator);
        }

        /// <summary>
        /// A context whose leading run is exactly <paramref name="targets"/>, no context neighbours.
        /// Each target is one unattributed dialog item holding the whole paragraph text — the
        /// simplest shape the frozen-split prompt can ask about.
        /// </summary>
        private static ParagraphBatchContext Ctx(params (QueuedParagraph Item, string Text)[] targets)
        {
            var entries = targets
                .Select((t, i) => new BatchContextEntry(t.Text, [Dialog(t.Text)], i))
                .ToList();
            return new ParagraphBatchContext(entries, [.. targets.Select(t => t.Item.ParagraphId)], []);
        }

        private static ContextItem Dialog(string text, Guid? id = null) =>
            new(id ?? Guid.NewGuid(), text, AttributionWire.Dialog, AttributionWire.Unknown);

        private static ContextItem Narration(string text, Guid? id = null) =>
            new(id ?? Guid.NewGuid(), text, AttributionWire.Narration, AttributionWire.Narrator);

        private AttributionRequestBuilder NewBuilder(IProjectReader reader, LlmPromptService? prompts = null) =>
            new(prompts ?? NewPrompts(), reader);

        // ---------------------------------------------------------------
        // Context window: the builder is the only reader of it
        // ---------------------------------------------------------------

        [Fact]
        public async Task ContextWindowDefaults_PassedToReader()
        {
            var p = Para("Preview");
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], project: Project());

            await NewBuilder(reader).Build([p], Opts(Config()));

            Assert.Equal(PromptTemplates.DefaultContextParagraphsBefore, reader.ReceivedBefore);
            Assert.Equal(PromptTemplates.DefaultContextParagraphsAfter, reader.ReceivedAfter);
        }

        [Fact]
        public async Task CustomContextWindow_PassedToReader()
        {
            var prompts = NewPrompts();
            await prompts.SetContextWindowAsync(7, 3);

            var p = Para("Preview");
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], project: Project());

            await NewBuilder(reader, prompts).Build([p], Opts(Config()));

            Assert.Equal(7, reader.ReceivedBefore);
            Assert.Equal(3, reader.ReceivedAfter);
        }

        // ---------------------------------------------------------------
        // Template + shape by included count
        // ---------------------------------------------------------------

        [Fact]
        public async Task ChunkOfOne_RendersSingleObjectRequest()
        {
            var p = Para("Preview");
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], project: Project());
            var builder = NewBuilder(reader);

            var result = await builder.Build([p], Opts(Config()));

            Assert.NotNull(result.Request);
            Assert.Equal("Preview", result.Request!.Label);
            Assert.Equal(CompletionShape.Object, result.Request.Shape);
            Assert.Equal(ItemAttributionSchema.JsonSchema, result.Request.JsonSchema);
            Assert.Equal(p, Assert.Single(result.Included));
            Assert.Equal("Hello world", Assert.Single(Assert.Single(result.QueryItems)).Text);
            Assert.Empty(result.Unaskable);
            Assert.Empty(result.Deferred);

            // The parser wraps the single answer into the batch-shaped index→result map.
            Assert.True(result.Parser!(
                """{ "reasoning": "r", "items": [ { "index": 0, "speaker": "Alice", "voice_instructions": "" } ] }""",
                out var parsed, out _));
            Assert.Equal("Alice", Assert.Single(parsed![0].Items).Speaker);
        }

        [Fact]
        public async Task ChunkOfMany_RendersBatchArrayRequest()
        {
            var ps = new[] { Para("P0"), Para("P1"), Para("P2") };
            var reader = new BatchReaderFake(
                [Ctx((ps[0], "Text 0"), (ps[1], "Text 1"), (ps[2], "Text 2"))], project: Project());
            var builder = NewBuilder(reader);

            var result = await builder.Build(ps, Opts(Config()));

            Assert.NotNull(result.Request);
            Assert.Equal("3 paragraphs: P0", result.Request!.Label);
            Assert.Equal(CompletionShape.Array, result.Request.Shape);
            Assert.Equal(ItemBatchAttributionSchema.JsonSchema, result.Request.JsonSchema);
            Assert.Equal(3, result.Included.Count);
            Assert.Equal(["Text 0", "Text 1", "Text 2"],
                result.QueryItems.Select(items => Assert.Single(items).Text));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public async Task LinkedNarrator_IdentitySentenceUsesPrimaryNameAtMeasuredPosition(int count)
        {
            var ps = Enumerable.Range(0, count).Select(i => Para($"P{i}")).ToList();
            var reader = new BatchReaderFake(
                [Ctx([.. ps.Select((p, i) => (p, $"Text {i}"))])],
                project: Project())
            {
                Narrator = new NarratorIdentity(Guid.NewGuid(), "Dr. Watson", true),
            };

            var result = await NewBuilder(reader).Build(ps, Opts(Config()));

            const string identity =
                "This book is narrated by Dr. Watson, who is also a character in the story and speaks in scene.";
            var prompt = result.Request!.Prompt;
            var responseFormat = prompt.IndexOf("- JSON format:", StringComparison.Ordinal);
            var identityPosition = prompt.IndexOf(identity, StringComparison.Ordinal);
            var knownCharacters = prompt.IndexOf("Known characters (", StringComparison.Ordinal);

            Assert.True(responseFormat < identityPosition, "Identity must follow the JSON format line.");
            Assert.True(identityPosition < knownCharacters, "Identity must precede the character roster.");
            Assert.Contains($"\n\n{identity}\n\n", prompt);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        public async Task Thinking_TogglesDisableThinking_AtAnyChunkSize(int count)
        {
            var ps = Enumerable.Range(0, count).Select(i => Para($"P{i}")).ToList();
            var ctx = () => Ctx([.. ps.Select((p, i) => (p, $"Text {i}"))]);

            var on = await NewBuilder(new BatchReaderFake([ctx()], project: Project()))
                .Build(ps, Opts(Config(), thinking: true));
            var off = await NewBuilder(new BatchReaderFake([ctx()], project: Project()))
                .Build(ps, Opts(Config(), thinking: false));

            Assert.False(on.Request!.DisableThinking);
            Assert.True(off.Request!.DisableThinking);
        }

        // ---------------------------------------------------------------
        // Three-bin classification
        // ---------------------------------------------------------------

        [Fact]
        public async Task BlankTargetText_BinnedUnaskable_AndDropsToSingleShape()
        {
            var kept = Para("kept");
            var blank = Para("blank");
            // Two targets, one blank: it goes Unaskable and the surviving one renders as a single.
            var reader = new BatchReaderFake([Ctx((kept, "Hello world"), (blank, "   "))], project: Project());

            var result = await NewBuilder(reader).Build([kept, blank], Opts(Config()));

            Assert.Equal(kept, Assert.Single(result.Included));
            Assert.Equal(blank, Assert.Single(result.Unaskable));
            Assert.Equal(CompletionShape.Object, result.Request!.Shape);
        }

        [Fact]
        public async Task DeferredIds_BinnedDeferred()
        {
            var p0 = Para("P0");
            var p1 = Para("P1");
            var entries = new List<BatchContextEntry> { new("Text 0", [], 0) };
            var ctx = new ParagraphBatchContext(entries, [p0.ParagraphId], [p1.ParagraphId]);
            var reader = new BatchReaderFake([ctx], project: Project());

            var result = await NewBuilder(reader).Build([p0, p1], Opts(Config()));

            Assert.Equal(p0, Assert.Single(result.Included));
            Assert.Equal(p1, Assert.Single(result.Deferred));
            Assert.Empty(result.Unaskable);
        }

        [Fact]
        public async Task AllUnaskable_NullRequest()
        {
            var p = Para("P0");
            // The reader never finds the first id → null every time → single Unaskable, no request.
            var reader = new BatchReaderFake([null]);

            var result = await NewBuilder(reader).Build([p], Opts(Config()));

            Assert.Null(result.Request);
            Assert.Null(result.Parser);
            Assert.Empty(result.Included);
            Assert.Equal(p, Assert.Single(result.Unaskable));
            Assert.Empty(result.Characters);
        }

        // ---------------------------------------------------------------
        // Missing-first retry loop
        // ---------------------------------------------------------------

        [Fact]
        public async Task MissingFirstId_MarksItUnaskable_AndRetriesForTheRest()
        {
            var missing = Para("missing");
            var found = Para("found");
            // First call (both ids) → null; second call (just the survivor) → a context.
            var reader = new BatchReaderFake([null, Ctx((found, "Hello world"))], project: Project());

            var result = await NewBuilder(reader).Build([missing, found], Opts(Config()));

            Assert.Equal(missing, Assert.Single(result.Unaskable));
            Assert.Equal(found, Assert.Single(result.Included));

            // The loop re-requested context for the remainder only, after dropping the missing first.
            Assert.Equal(2, reader.ReceivedIds.Count);
            Assert.Equal([missing.ParagraphId, found.ParagraphId], reader.ReceivedIds[0]);
            Assert.Equal([found.ParagraphId], reader.ReceivedIds[1]);
        }

        // ---------------------------------------------------------------
        // Entries → single-context adapter
        // ---------------------------------------------------------------

        [Fact]
        public async Task SingleContextAdapter_QueryIsIndexedItems_NeighboursAreSegments()
        {
            var target = Para("target");
            // A flat span: one preceding context paragraph, then the lone target.
            var entries = new List<BatchContextEntry>
            {
                new("She spoke.", [Narration("She spoke.")], null),
                new("Hello world", [Dialog("Hello world")], 0),
            };
            var ctx = new ParagraphBatchContext(entries, [target.ParagraphId], []);
            var reader = new BatchReaderFake([ctx], project: Project());

            var result = await NewBuilder(reader).Build([target], Opts(Config()));

            // The single prompt shows the target as its numbered items and the neighbour as segments.
            var prompt = result.Request!.Prompt;
            var items = ContextJson(prompt).GetProperty("query").GetProperty("items");
            Assert.Equal(0, items[0].GetProperty("index").GetInt32());
            Assert.Equal("Hello world", items[0].GetProperty("text").GetString());
            Assert.Contains("She spoke.", prompt);
            Assert.Contains("narrator", prompt);
            // The target's items travel back, index-aligned with Included.
            Assert.Equal("Hello world", Assert.Single(Assert.Single(result.QueryItems)).Text);
        }

        // ---------------------------------------------------------------
        // Query items: the index→item map the apply stamps by (spec §1)
        // ---------------------------------------------------------------

        [Fact]
        public async Task QueryItems_RecordEveryItemInPromptOrder_NarrationIncluded()
        {
            var target = Para("target");
            var dialogId = Guid.NewGuid();
            var narrationId = Guid.NewGuid();
            var entries = new List<BatchContextEntry>
            {
                new("\"Go.\" she said.",
                    [Dialog("\"Go.\"", dialogId), Narration("she said.", narrationId)], 0),
            };
            var reader = new BatchReaderFake(
                [new ParagraphBatchContext(entries, [target.ParagraphId], [])], project: Project());

            var result = await NewBuilder(reader).Build([target], Opts(Config()));

            // Position i of the recorded list is the item the prompt numbered i.
            Assert.Equal([dialogId, narrationId],
                Assert.Single(result.QueryItems).Select(i => i.ItemId));

            var items = ContextJson(result.Request!.Prompt).GetProperty("query").GetProperty("items");
            Assert.Equal([0, 1], items.EnumerateArray().Select(i => i.GetProperty("index").GetInt32()));
            Assert.Equal("\"Go.\"", items[0].GetProperty("text").GetString());
            Assert.Equal("she said.", items[1].GetProperty("text").GetString());
        }

        [Fact]
        public async Task QueryItems_AlignWithIncluded_AcrossABatch_SkippingUnaskable()
        {
            var kept0 = Para("kept0");
            var blank = Para("blank");
            var kept1 = Para("kept1");
            var id0 = Guid.NewGuid();
            var id1 = Guid.NewGuid();
            var entries = new List<BatchContextEntry>
            {
                new("Text 0", [Dialog("Text 0", id0)], 0),
                new("   ", [Dialog("   ")], 1),
                new("Text 1", [Dialog("Text 1", id1)], 2),
            };
            var reader = new BatchReaderFake(
                [new ParagraphBatchContext(entries, [kept0.ParagraphId, blank.ParagraphId, kept1.ParagraphId], [])],
                project: Project());

            var result = await NewBuilder(reader).Build([kept0, blank, kept1], Opts(Config()));

            // The blank target is binned Unaskable, and the map renumbers with Included — it never
            // carries a slot for a paragraph that was not asked about.
            Assert.Equal([kept0, kept1], result.Included);
            Assert.Equal(blank, Assert.Single(result.Unaskable));
            Assert.Equal([[id0], [id1]],
                result.QueryItems.Select(items => items.Select(i => i.ItemId)));
        }

        /// <summary>
        /// The context JSON object of a rendered single prompt, found by the brace opening the
        /// object that holds "preceding". Read as a single JSON value rather than a substring, so
        /// prose after the token (a template is free to add some) does not break these tests.
        /// </summary>
        private static JsonElement ContextJson(string prompt)
        {
            var start = prompt.LastIndexOf('{', prompt.LastIndexOf("\"preceding\"", StringComparison.Ordinal));
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(prompt[start..]));
            return JsonDocument.ParseValue(ref reader).RootElement;
        }

        // ---------------------------------------------------------------
        // Token budget (D2) — applies on a chunk of one
        // ---------------------------------------------------------------

        [Fact]
        public async Task Budget_AppliesOnChunkOfOne_AsAFloor()
        {
            var p = Para("Preview");
            var longText = new string('x', 4000);
            var reader = new BatchReaderFake([Ctx((p, longText))], project: Project());

            var result = await NewBuilder(reader).Build([p], Opts(Config(maxTokens: 100)));

            var expected = AttributionTokenBudget.ForPassage(100, [longText]);
            Assert.Equal(expected, result.Request!.Overrides!.MaxTokens);
            // The passage needs far more than the configured 100 — the config is a floor, not a cap.
            Assert.True(result.Request.Overrides.MaxTokens > 100);
        }

        [Fact]
        public async Task Budget_UnsetConfig_StaysNull()
        {
            var p = Para("Preview");
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], project: Project());

            var result = await NewBuilder(reader).Build([p], Opts(Config(maxTokens: null)));

            Assert.Null(result.Request!.Overrides!.MaxTokens);
        }

        [Fact]
        public async Task TemperatureOverride_RidesTheRequest()
        {
            var p = Para("Preview");
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], project: Project());
            var opts = Opts(Config(temperature: 0.4)).Resampled();

            var result = await NewBuilder(reader).Build([p], opts);

            Assert.Equal(0.4, result.Request!.Overrides!.Temperature);
        }

        // ---------------------------------------------------------------
        // Roster projection
        // ---------------------------------------------------------------

        [Fact]
        public async Task RosterProjection_NameAndAliases_InPrompt_AndOnResult()
        {
            var p = Para("Preview");
            var characters = new List<Character> { Character("Alice", "Ali", "Al") };
            var reader = new BatchReaderFake([Ctx((p, "Hello world"))], characters, Project());

            var result = await NewBuilder(reader).Build([p], Opts(Config()));

            var expected = JsonSerializer.Serialize(new[] { new { name = "Alice", aliases = new[] { "Ali", "Al" } } });
            Assert.Contains(expected, result.Request!.Prompt);
            // The roster travels back so no later stage refetches it.
            Assert.Equal("Alice", Assert.Single(result.Characters).Name);
        }
    }
}
