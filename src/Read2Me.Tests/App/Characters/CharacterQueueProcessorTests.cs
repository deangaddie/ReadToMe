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
        private readonly CharacterQueueProcessor _sut;
        private readonly QueuedParagraph _item;

        public CharacterQueueProcessorTests()
        {
            _queue = new CharacterQueueService();
            _attribution = new FakeAttributionService();
            _resolver = new FakeResolver();
            _commands = new FakeCommandHandler();
            _sut = new CharacterQueueProcessor(
                _queue,
                _attribution,
                _resolver,
                _commands,
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
        public async Task Unknown_WithReason_FlowsReasonIntoOutcome()
        {
            _attribution.Outcome = new AttributionOutcome(
                AttributionStatus.Unknown, null, null,
                "Speaker unknown after escalating through 2 models (A → B)");

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var outcome = _queue.OutcomeOf(_item.Folder, _item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Unknown, outcome.Kind);
            Assert.Equal("Speaker unknown after escalating through 2 models (A → B)", outcome.Reason);
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
        public async Task DrainsWholeQueue_AppliesOutcomePerItem()
        {
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
        public async Task MultiChapterDrain_AppliesEachOutcome()
        {
            _resolver.ResolvedId = Guid.NewGuid();
            var chA = Guid.NewGuid();
            var chB = Guid.NewGuid();
            var a1 = MakeChapterItem(chA);
            var b1 = MakeChapterItem(chB);
            _queue.Enqueue([a1, b1]);
            var first = await _queue.Reader.ReadAsync();

            _attribution.StreamResults.Enqueue((a1, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));
            _attribution.StreamResults.Enqueue((b1, new AttributionOutcome(AttributionStatus.Unknown, null, null, null)));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.NotNull(_queue.ResolvedOf(a1.Folder, a1.ParagraphId));
            Assert.Equal(ParagraphOutcomeKind.Unknown, _queue.OutcomeOf(b1.Folder, b1.ParagraphId)!.Kind);
        }

        [Fact]
        public async Task MixedStreamOutcomes_AppliedIndividually()
        {
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            _attribution.StreamResults.Enqueue((items[0], new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));
            _attribution.StreamResults.Enqueue((items[1], new AttributionOutcome(AttributionStatus.Unknown, null, null, null)));
            _attribution.StreamResults.Enqueue((items[2], new AttributionOutcome(AttributionStatus.Failed, null, null, "boom")));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.NotNull(_queue.ResolvedOf(items[0].Folder, items[0].ParagraphId));
            Assert.Equal(ParagraphOutcomeKind.Unknown, _queue.OutcomeOf(items[1].Folder, items[1].ParagraphId)!.Kind);
            var failed = _queue.OutcomeOf(items[2].Folder, items[2].ParagraphId);
            Assert.NotNull(failed);
            Assert.Equal(ParagraphOutcomeKind.Failed, failed.Kind);
            Assert.Equal("boom", failed.Reason);
        }

        [Fact]
        public async Task Step0ResolvesApplyBeforeLaterStreamItems()
        {
            var chapterId = Guid.NewGuid();
            var early = MakeChapterItem(chapterId);
            var late = MakeChapterItem(chapterId);
            _queue.Enqueue([early, late]);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            // Stream yields the step-0 resolve first; by the time the second item streams, the first
            // must already be applied (chip flipped to done).
            _attribution.StreamResults.Enqueue((early, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));
            _attribution.StreamResults.Enqueue((late, new AttributionOutcome(AttributionStatus.Resolved, "Frodo", null, null)));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            // Commands applied in stream order.
            Assert.Equal(2, _commands.SentCommands.Count);
            var cmd0 = Assert.IsType<SetParagraphCharacterCommand>(_commands.SentCommands[0]);
            Assert.Equal(early.ParagraphId, cmd0.ParagraphId);
        }

        [Fact]
        public async Task DrainedItems_NotMarkedProcessing_UntilTheirChunkStarts()
        {
            var chapterId = Guid.NewGuid();
            var worked = MakeChapterItem(chapterId);
            var untouched = MakeChapterItem(chapterId);
            _queue.Enqueue([worked, untouched]);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            // Only 'worked' is signalled + yielded; 'untouched' is drained but never streamed. It must
            // stay Queued — proof the whole drained queue is not flipped to Processing up front.
            _attribution.StreamResults.Enqueue((worked, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.Equal(ParagraphQueueStatus.Queued, _queue.StatusOf(untouched.Folder, untouched.ParagraphId));
            // The item that was actually worked was chunk-signalled before its outcome applied.
            Assert.Contains(_attribution.ChunksStarted, c => c.Contains(worked));
        }

        [Fact]
        public async Task DeferredItem_ReturnsToQueued_WhileAwaitingEscalation()
        {
            var chapterId = Guid.NewGuid();
            var deferred = MakeChapterItem(chapterId);
            var decided = MakeChapterItem(chapterId);
            _queue.Enqueue([deferred, decided]);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            // 'deferred' goes in flight, is answered suspect, and is held back for a later chain step
            // without ever being yielded. It must not be left showing Processing.
            _attribution.DeferItems.Add(deferred);
            _attribution.StreamResults.Enqueue((decided, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.Equal(ParagraphQueueStatus.Queued, _queue.StatusOf(deferred.Folder, deferred.ParagraphId));
            Assert.Contains(_attribution.ChunksStarted, c => c.Contains(deferred));
        }

        [Fact]
        public async Task DeferredItem_DecidedByLaterStep_CompletesNormally()
        {
            var chapterId = Guid.NewGuid();
            var item = MakeChapterItem(chapterId);
            _queue.Enqueue([item]);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            // Held back by step 0, then resolved by a later escalation step: the deferral is transient
            // and must not block the terminal outcome.
            _attribution.DeferItems.Add(item);
            _attribution.StreamResults.Enqueue((item, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.Null(_queue.StatusOf(item.Folder, item.ParagraphId));
            Assert.NotNull(_queue.ResolvedOf(item.Folder, item.ParagraphId));
        }

        [Fact]
        public async Task ProcessorThrows_MarksAllDrainedItemsFailed()
        {
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

        [Fact]
        public async Task UnexpectedThrowMidStream_FailsOnlyUndecidedItems()
        {
            var chapterId = Guid.NewGuid();
            var decided = MakeChapterItem(chapterId);
            var undecided = MakeChapterItem(chapterId);
            _queue.Enqueue([decided, undecided]);
            var first = await _queue.Reader.ReadAsync();

            _resolver.ResolvedId = Guid.NewGuid();
            // First item decides, then the stream throws before the second — only the second fails.
            _attribution.StreamThenThrow(
                (decided, new AttributionOutcome(AttributionStatus.Resolved, "Bilbo", null, null)),
                new InvalidOperationException("boom"));

            await _sut.ProcessItemAsync(first, CancellationToken.None);

            Assert.NotNull(_queue.ResolvedOf(decided.Folder, decided.ParagraphId));
            var failed = _queue.OutcomeOf(undecided.Folder, undecided.ParagraphId);
            Assert.NotNull(failed);
            Assert.Equal(ParagraphOutcomeKind.Failed, failed.Kind);
        }

        private class FakeAttributionService() : CharacterAttributionService(null!, null!, null!, null!, NullLogger<CharacterAttributionService>.Instance, null!)
        {
            public AttributionOutcome? Outcome { get; set; }
            public Exception? ThrowException { get; set; }

            /// <summary>
            /// Scripted stream, in yield order; when empty, falls back to <see cref="Outcome"/> for
            /// every drained item in book order.
            /// </summary>
            public Queue<(QueuedParagraph Item, AttributionOutcome Outcome)> StreamResults { get; } = new();

            private (QueuedParagraph, AttributionOutcome)[]? _thenYield;
            private Exception? _midStreamThrow;

            /// <summary>Yields the given outcome, then throws mid-stream before any remaining items.</summary>
            public void StreamThenThrow((QueuedParagraph, AttributionOutcome) yielded, Exception ex)
            {
                _thenYield = [yielded];
                _midStreamThrow = ex;
            }

            /// <summary>Chunks the callback reported for each item, in order it was signalled.</summary>
            public List<IReadOnlyList<QueuedParagraph>> ChunksStarted { get; } = new();

            /// <summary>Items to signal as deferred (escalation-held) before the stream yields anything.</summary>
            public List<QueuedParagraph> DeferItems { get; } = new();

            public override async IAsyncEnumerable<(QueuedParagraph Item, AttributionOutcome Outcome)>
                AttributeQueueAsync(IReadOnlyList<QueuedParagraph> queued,
                    AttributionQueueCallbacks? callbacks,
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
            {
                if (ThrowException != null) throw ThrowException;

                // Chunk goes in flight, then the chain holds these back for a later step.
                foreach (var item in DeferItems)
                {
                    Signal(callbacks, item);
                    callbacks?.ItemDeferred?.Invoke(item);
                }

                if (_midStreamThrow != null)
                {
                    foreach (var pair in _thenYield!)
                    {
                        Signal(callbacks, pair.Item1);
                        yield return pair;
                    }
                    throw _midStreamThrow;
                }
                if (StreamResults.Count > 0)
                {
                    while (StreamResults.Count > 0)
                    {
                        var pair = StreamResults.Dequeue();
                        Signal(callbacks, pair.Item);
                        yield return pair;
                    }
                }
                else
                {
                    foreach (var item in queued)
                    {
                        Signal(callbacks, item);
                        yield return (item, Outcome!);
                    }
                }
                await Task.CompletedTask;
            }

            private void Signal(AttributionQueueCallbacks? callbacks, QueuedParagraph item)
            {
                IReadOnlyList<QueuedParagraph> chunk = [item];
                ChunksStarted.Add(chunk);
                callbacks?.ChunkStarted?.Invoke(chunk);
            }
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
