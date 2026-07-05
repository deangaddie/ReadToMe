using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.Characters
{
    public class CharacterQueueProcessorTests : ProjectDbTestBase
    {
        private readonly CharacterQueueService _queue;
        private readonly FakeAttributionService _attribution;
        private readonly FakeResolver _resolver;
        private readonly FakeCommandHandler _commands;
        private readonly FakeLlmSettings _settings;
        private readonly CharacterQueueProcessor _sut;
        private readonly QueuedParagraph _item;

        public CharacterQueueProcessorTests()
        {
            _queue = new CharacterQueueService();
            _attribution = new FakeAttributionService();
            _resolver = new FakeResolver();
            _commands = new FakeCommandHandler();
            _settings = new FakeLlmSettings();
            _sut = new CharacterQueueProcessor(
                _queue,
                _attribution,
                _resolver,
                _commands,
                _settings,
                NullLogger<CharacterQueueProcessor>.Instance);

            _item = new QueuedParagraph(
                new ProjectFolderId("test"),
                Guid.NewGuid(),
                "Preview text",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid());
        }

        [Fact]
        public async Task Resolved_AssignsCharacterAndMarksComplete()
        {
            var charId = Guid.NewGuid();
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", "Whisper", null);
            _resolver.ResolvedId = charId;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Equal(charId, _resolver.LastResolvedId);
            Assert.Equal("Bilbo", _resolver.LastName);
            
            var cmd = Assert.Single(_commands.SentCommands);
            var setCmd = Assert.IsType<SetParagraphCharacterCommand>(cmd);
            Assert.Equal(_item.ParagraphId, setCmd.ParagraphId);
            Assert.Equal(charId, setCmd.CharacterId);
            Assert.Equal("Whisper", setCmd.VoiceInstructions);

            var outcome = _queue.ResolvedOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(charId, outcome.CharacterId);
            Assert.Equal("Bilbo", outcome.Name);
            
            Assert.Null(_queue.StatusOf(_item.Folder, _item.ParagraphId));
        }

        [Fact]
        public async Task Unknown_MarksUnknown_NoAssignment()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.Unknown, null, null, null);

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Empty(_commands.SentCommands);
            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Unknown, outcome.Kind);
        }

        [Fact]
        public async Task NoLlmConfigured_MarksFailed_WithReason()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.NoLlmConfigured, null, null, "No config");

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("No config", outcome.Reason);
        }

        [Fact]
        public async Task Failed_MarksFailed_WithReason()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.Failed, null, null, "LLM Error");

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("LLM Error", outcome.Reason);
        }

        [Fact]
        public async Task ServiceUnavailable_FirstTime_RequeuesInsteadOfFailing()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.ServiceUnavailable, null, null, "stalled");

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            // Back on the queue, waiting for recovery — not a terminal failure.
            Assert.Equal(ParagraphQueueStatus.Queued, _queue.StatusOf(_item.Folder, _item.ParagraphId));
            Assert.Null(_queue.OutcomeOf(_item.Folder, _item.ParagraphId));
        }

        [Fact]
        public async Task ServiceUnavailable_SecondTimeForRequeuedItem_MarksFailed()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.ServiceUnavailable, null, null, "stalled");
            var requeued = _item with { Requeued = true };

            await _sut.ProcessItemAsync(requeued, CancellationToken.None);

            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("stalled", outcome.Reason);
        }

        [Fact]
        public async Task ItemLevelCancel_DoesNotMarkFailed()
        {
            _attribution.ThrowException = new OperationCanceledException();
            // Host token not cancelled, but item-level cancel happened inside Processor (simulated by ThrowException)

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            // Item was marked processing at start
            // After OCE, it should be removed or left as is depending on CancelAll behavior.
            // CharacterQueueService.CancelAll removes all entries.
            // In Processor, OCE is caught and logged, but MarkFailed is NOT called for OCE.
            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.Null(outcome);
        }

        // ── Batch processing ──────────────────────────────────────────────────

        private QueuedParagraph MakeChapterItem(Guid chapterId) =>
            new(_item.Folder, Guid.NewGuid(), "Preview", chapterId, Guid.NewGuid(), Guid.NewGuid());

        [Fact]
        public async Task BatchSize_DrainsQueueAndAppliesOutcomePerItem()
        {
            _settings.BatchSize = 3;
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null);
            _resolver.ResolvedId = Guid.NewGuid();

            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            var first = await _queue.Reader.ReadAsync();

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            foreach (var item in items)
            {
                Assert.NotNull(_queue.ResolvedOf(item.Folder, item.ParagraphId));
                Assert.Null(_queue.StatusOf(item.Folder, item.ParagraphId));
            }
            Assert.Equal(3, _commands.SentCommands.Count);
        }

        [Fact]
        public async Task DeferredItems_ProcessedAsFollowUpBatch()
        {
            _settings.BatchSize = 3;
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            var first = await _queue.Reader.ReadAsync();

            var resolved = new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null);
            _resolver.ResolvedId = Guid.NewGuid();
            _attribution.BatchResults.Enqueue(new BatchAttributionResult(
                [(items[0], resolved), (items[1], resolved)], [items[2]]));
            _attribution.BatchResults.Enqueue(new BatchAttributionResult(
                [(items[2], resolved)], []));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.Empty(_attribution.BatchResults);
            foreach (var item in items)
                Assert.NotNull(_queue.ResolvedOf(item.Folder, item.ParagraphId));
        }

        [Fact]
        public async Task MixedBatchOutcomes_AppliedIndividually()
        {
            _settings.BatchSize = 3;
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            _attribution.BatchResults.Enqueue(new BatchAttributionResult(
                [
                    (items[0], new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)),
                    (items[1], new AttributionOutcome(AttributionStatus.Unknown, null, null, null)),
                    (items[2], new AttributionOutcome(AttributionStatus.Failed, null, null, "boom")),
                ], []));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.NotNull(_queue.ResolvedOf(items[0].Folder, items[0].ParagraphId));
            Assert.Equal(ParagraphOutcomeKind.Unknown, _queue.OutcomeOf(items[1].Folder, items[1].ParagraphId)!.Kind);
            var failed = _queue.OutcomeOf(items[2].Folder, items[2].ParagraphId);
            Assert.NotNull(failed);
            Assert.Equal(ParagraphOutcomeKind.Failed, failed.Kind);
            Assert.Equal("boom", failed.Reason);
        }

        [Fact]
        public async Task ProcessorThrows_MarksAllDrainedItemsFailed()
        {
            _settings.BatchSize = 2;
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            var first = await _queue.Reader.ReadAsync();

            _attribution.ThrowException = new InvalidOperationException("boom");

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            foreach (var item in items)
            {
                var outcome = _queue.OutcomeOf(item.Folder, item.ParagraphId);
                Assert.NotNull(outcome);
                Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            }
        }

        private class FakeAttributionService() : CharacterAttributionService(null!, null!, null!, null!, NullLogger<CharacterAttributionService>.Instance, null!, null!)
        {
            public AttributionOutcome? Outcome { get; set; }
            public Exception? ThrowException { get; set; }

            /// <summary>Queued per-call results; when empty, falls back to Outcome for every batch item.</summary>
            public Queue<BatchAttributionResult> BatchResults { get; } = new();

            public override Task<AttributionOutcome> AttributeAsync(QueuedParagraph item, CancellationToken ct)
            {
                if (ThrowException != null) throw ThrowException;
                return Task.FromResult(Outcome!);
            }

            public override Task<BatchAttributionResult> AttributeBatchAsync(
                IReadOnlyList<QueuedParagraph> batch, CancellationToken ct)
            {
                if (ThrowException != null) throw ThrowException;
                if (BatchResults.Count > 0) return Task.FromResult(BatchResults.Dequeue());
                return Task.FromResult(new BatchAttributionResult(
                    [.. batch.Select(b => (b, Outcome!))], []));
            }
        }

        private class FakeLlmSettings() : LlmSettingsService(null!, NullLogger<LlmSettingsService>.Instance)
        {
            public int? BatchSize { get; set; }

            public override Task<LlmServerConfig?> GetActiveConfigAsync() =>
                Task.FromResult(BatchSize is { } size
                    ? new LlmServerConfig { AttributionBatchSize = size }
                    : null);
        }

        private class FakeResolver() : CharacterResolver(null!, null!)
        {
            public Guid ResolvedId { get; set; }
            public Guid? LastResolvedId { get; set; }
            public string? LastName { get; set; }

            public override Task<Guid> ResolveOrCreateAsync(ProjectFolderId folder, string name, CancellationToken ct)
            {
                LastResolvedId = ResolvedId;
                LastName = name;
                return Task.FromResult(ResolvedId);
            }
        }

        private class FakeCommandHandler : IBookCommandHandler
        {
            public System.Collections.Generic.List<BookCommand> SentCommands { get; } = [];
            public Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
            {
                SentCommands.Add(command);
                return Task.FromResult<Guid?>(null);
            }
        }
    }
}
