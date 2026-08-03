using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.App.Characters
{
    /// <summary>
    /// Orchestration only: stream order, chunk/defer signals, the command's shape, and the failure
    /// fan-out. The queue is a <b>recorder</b>, so each test names the <see cref="Disposition"/> the
    /// processor decided rather than reading state back through the real queue — which cannot tell
    /// <c>RetryOnce</c> from <c>RetryAfter</c> at all.
    /// <para>
    /// Retry and settle <i>policy</i> is not here. It lives as a table in <c>QueueDispositionTests</c>
    /// (phase 1) and <c>CharacterDispositionTests</c> (translation + phase 2), with no fakes.
    /// </para>
    /// </summary>
    public class CharacterQueueProcessorTests
    {
        private readonly RecordingQueue _queue;
        private readonly FakeEscalationChain _attribution;
        private readonly FakeResolver _resolver;
        private readonly FakeCharacterReader _reader;
        private readonly FakeNarratorCatalog _catalog;
        private readonly FakeCommandHandler _commands;
        private readonly CharacterQueueProcessor _sut;
        private readonly QueuedParagraph _item;

        public CharacterQueueProcessorTests()
        {
            _queue = new RecordingQueue();
            _attribution = new FakeEscalationChain();
            _resolver = new FakeResolver();
            _reader = new FakeCharacterReader();
            _catalog = new FakeNarratorCatalog();
            _commands = new FakeCommandHandler();
            _sut = new CharacterQueueProcessor(
                _queue,
                _attribution,
                _resolver,
                _reader,
                _catalog,
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

        // ── Outcome builders ──────────────────────────────────────────────────

        private static AttributionSegment Dialog(string speaker, string text = "\"Hello.\"", string voice = "") =>
            new(text, AttributionSegmentType.Dialog, speaker, voice);

        private static AttributionSegment Narration(string text = "she said.") =>
            new(text, AttributionSegmentType.Narration, "narrator", string.Empty);

        /// <summary>A fully attributed answer: one dialog segment for the named speaker.</summary>
        private static AttributionOutcome Resolved(string speaker = "Bilbo", string voice = "") =>
            new(AttributionStatus.Resolved, [Dialog(speaker, voice: voice)], null);

        private static AttributionOutcome Segments(
            AttributionStatus status, string? reason, params AttributionSegment[] segments) =>
            new(status, segments, reason);

        /// <summary>The one disposition applied to <paramref name="item"/>.</summary>
        private T DispositionFor<T>(QueuedParagraph item) where T : Disposition =>
            Assert.IsType<T>(Assert.Single(_queue.Applied, a => a.Item.Equals(item)).D);

        // ── Apply ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task Resolved_ResolvesSpeakers_AppliesSegmentation_MarksComplete()
        {
            var charId = Guid.NewGuid();
            _attribution.Outcome = Segments(AttributionStatus.Resolved, null,
                Dialog("Bilbo", "\"Hello.\"", "Whisper"), Narration());
            _resolver.ResolvedId = charId;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Equal("Bilbo", _resolver.Names.Single());

            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Equal(_item.ParagraphId, cmd.ParagraphId);
            Assert.Collection(cmd.Segments,
                s =>
                {
                    Assert.Equal("\"Hello.\"", s.Text);
                    Assert.Equal(SegmentItemType.Character, s.Type);
                    Assert.Equal(charId, s.CharacterId);
                    Assert.Equal("Whisper", s.VoiceInstructions);
                },
                s =>
                {
                    Assert.Equal(SegmentItemType.Narration, s.Type);
                    // Narration is stamped with the narrator by the handler, not resolved by name.
                    Assert.Null(s.CharacterId);
                    Assert.Null(s.VoiceInstructions);
                });

            DispositionFor<Disposition.Complete>(_item);
        }

        [Fact]
        public async Task UnknownSpeaker_AppliesWithNullStamp_AndMarksUnfinished_WhenItemsStayUnattributed()
        {
            _attribution.Outcome = Segments(AttributionStatus.Unknown, "still unknown",
                Dialog("unknown"), Narration());
            _reader.Unattributed = 1;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            // The answer still applies — an unknown speaker resolves to no character, never a new one.
            Assert.Empty(_resolver.Names);
            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Null(cmd.Segments[0].CharacterId);

            Assert.Equal("still unknown", DispositionFor<Disposition.Unfinished>(_item).Reason);
        }

        [Fact]
        public async Task PartialAnswer_StampsKnownSegments_AndStaysUnfinished()
        {
            var charId = Guid.NewGuid();
            _resolver.ResolvedId = charId;
            _attribution.Outcome = Segments(AttributionStatus.Unknown, null,
                Dialog("Bilbo", "\"Hello.\""), Dialog("unknown", "\"Who's there?\""));
            _reader.Unattributed = 1;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Equal(charId, cmd.Segments[0].CharacterId);
            Assert.Null(cmd.Segments[1].CharacterId);

            DispositionFor<Disposition.Unfinished>(_item);
        }

        /// <summary>
        /// The wiring half of the post-apply check: the processor feeds the reader's count into
        /// <see cref="CharacterDisposition.DecideApplied"/>. What that count <em>means</em> is the
        /// phase-2 table in <c>CharacterDispositionTests</c>.
        /// </summary>
        [Fact]
        public async Task UnknownAnswer_ButEveryItemStamped_CompletesWithoutOutcome()
        {
            _attribution.Outcome = Segments(AttributionStatus.Unknown, null, Dialog("unknown"));
            _reader.Unattributed = 0;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            DispositionFor<Disposition.Complete>(_item);
        }

        /// <summary>
        /// The orchestration half: a segment-less answer never reaches the apply, and the processor
        /// stamps its own elapsed figure on the way past — phase 1 cannot know it, because one
        /// stopwatch spans a whole drained batch here rather than the store measuring per item.
        /// That it settles unfinished at all is the policy table's row in <c>QueueDispositionTests</c>.
        /// </summary>
        [Fact]
        public async Task EmptyParagraph_NoSegments_MarksUnfinished_WithoutApplying()
        {
            _attribution.Outcome = new AttributionOutcome(AttributionStatus.Unknown, null, null);

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Empty(_commands.SentCommands);
            Assert.NotNull(DispositionFor<Disposition.Unfinished>(_item).Elapsed);
        }

        [Fact]
        public async Task ItemLevelCancel_DoesNotMarkFailed()
        {
            _attribution.ThrowException = new OperationCanceledException();
            // Host token not cancelled, but item-level cancel happened inside Processor (simulated by
            // ThrowException). OperationCanceledException never becomes a WorkOutcome, so it never
            // reaches the decision at all.

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Empty(_queue.Applied);
        }

        // ── The narrator token on a dialog segment ────────────────────────────

        /// <summary>
        /// Linked, "narrator" is a wire alias of the linked character: it stamps that character, and
        /// never reaches the name resolver (which would create a Character called "narrator").
        /// </summary>
        [Fact]
        public async Task NarratorOnDialog_Linked_StampsTheLinkedCharacter()
        {
            var watson = Guid.NewGuid();
            _catalog.Narrator = new NarratorIdentity(watson, "Dr. Watson", true);
            _attribution.Outcome = Segments(AttributionStatus.Resolved, null, Dialog("narrator"));

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Equal(watson, cmd.Segments[0].CharacterId);
            Assert.Empty(_resolver.Names);
        }

        /// <summary>
        /// Unlinked it stamps nobody. Stamping the seed Narrator row would credit a spoken line to
        /// someone by definition not in the scene, and leave the item looking attributed — invisible
        /// to the unattributed re-queue filter forever.
        /// </summary>
        [Fact]
        public async Task NarratorOnDialog_Unlinked_StampsNobody()
        {
            _attribution.Outcome = Segments(AttributionStatus.Resolved, null, Dialog("narrator"));
            _reader.Unattributed = 1;

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Null(cmd.Segments[0].CharacterId);
            Assert.Empty(_resolver.Names);
            DispositionFor<Disposition.Unfinished>(_item);
        }

        /// <summary>Ordinary names are untouched by the link — they still resolve or are created.</summary>
        [Fact]
        public async Task OrdinaryName_ResolvesAsBefore_WhenLinked()
        {
            var charId = Guid.NewGuid();
            _catalog.Narrator = new NarratorIdentity(Guid.NewGuid(), "Dr. Watson", true);
            _resolver.ResolvedId = charId;
            _attribution.Outcome = Resolved("Bilbo");

            await _sut.ProcessItemAsync(_item, CancellationToken.None);

            Assert.Equal("Bilbo", _resolver.Names.Single());
            var cmd = Assert.IsType<ApplySegmentationCommand>(Assert.Single(_commands.SentCommands));
            Assert.Equal(charId, cmd.Segments[0].CharacterId);
        }

        /// <summary>One read per folder per drained batch — never per segment, never per paragraph.</summary>
        [Fact]
        public async Task NarratorLink_IsReadOncePerFolderPerDrainedBatch()
        {
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);
            _resolver.ResolvedId = Guid.NewGuid();
            _attribution.Outcome = Segments(AttributionStatus.Resolved, null,
                Dialog("Bilbo"), Narration(), Dialog("Frodo"));

            await _sut.ProcessItemAsync(items[0], CancellationToken.None);

            Assert.Equal(3, _commands.SentCommands.Count);
            Assert.Equal(1, _catalog.Reads);
        }

        // ── Batch processing ──────────────────────────────────────────────────

        private QueuedParagraph MakeChapterItem(Guid chapterId) =>
            new(_item.Folder, Guid.NewGuid(), "Preview", chapterId, Guid.NewGuid(), Guid.NewGuid());

        [Fact]
        public async Task DrainsWholeQueue_AppliesOutcomePerItem()
        {
            _attribution.Outcome = Resolved();
            _resolver.ResolvedId = Guid.NewGuid();

            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);

            await _sut.ProcessItemAsync(items[0], CancellationToken.None);

            foreach (var item in items)
                DispositionFor<Disposition.Complete>(item);
            Assert.Equal(3, _commands.SentCommands.Count);
        }

        [Fact]
        public async Task MultiChapterDrain_AppliesEachOutcome()
        {
            _resolver.ResolvedId = Guid.NewGuid();
            var a1 = MakeChapterItem(Guid.NewGuid());
            var b1 = MakeChapterItem(Guid.NewGuid());
            _queue.Enqueue([a1, b1]);

            _attribution.StreamResults.Enqueue((a1, Resolved()));
            _attribution.StreamResults.Enqueue((b1, new AttributionOutcome(AttributionStatus.Unknown, null, null)));

            await _sut.ProcessItemAsync(a1, CancellationToken.None);

            DispositionFor<Disposition.Complete>(a1);
            DispositionFor<Disposition.Unfinished>(b1);
        }

        [Fact]
        public async Task MixedStreamOutcomes_AppliedIndividually()
        {
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);

            _resolver.ResolvedId = Guid.NewGuid();
            _attribution.StreamResults.Enqueue((items[0], Resolved()));
            _attribution.StreamResults.Enqueue((items[1], new AttributionOutcome(AttributionStatus.Unknown, null, null)));
            _attribution.StreamResults.Enqueue((items[2], new AttributionOutcome(AttributionStatus.Failed, null, "boom")));

            await _sut.ProcessItemAsync(items[0], CancellationToken.None);

            DispositionFor<Disposition.Complete>(items[0]);
            DispositionFor<Disposition.Unfinished>(items[1]);
            Assert.Equal("boom", DispositionFor<Disposition.Failed>(items[2]).Reason);
        }

        [Fact]
        public async Task Step0ResolvesApplyBeforeLaterStreamItems()
        {
            var chapterId = Guid.NewGuid();
            var early = MakeChapterItem(chapterId);
            var late = MakeChapterItem(chapterId);
            _queue.Enqueue([early, late]);

            _resolver.ResolvedId = Guid.NewGuid();
            // Stream yields the step-0 resolve first; by the time the second item streams, the first
            // must already be applied (chip flipped to done).
            _attribution.StreamResults.Enqueue((early, Resolved("Bilbo")));
            _attribution.StreamResults.Enqueue((late, Resolved("Frodo")));

            await _sut.ProcessItemAsync(early, CancellationToken.None);

            // Commands applied in stream order.
            Assert.Equal(2, _commands.SentCommands.Count);
            var cmd0 = Assert.IsType<ApplySegmentationCommand>(_commands.SentCommands[0]);
            Assert.Equal(early.ParagraphId, cmd0.ParagraphId);
        }

        [Fact]
        public async Task DrainedItems_NotMarkedProcessing_UntilTheirChunkStarts()
        {
            var chapterId = Guid.NewGuid();
            var worked = MakeChapterItem(chapterId);
            var untouched = MakeChapterItem(chapterId);
            _queue.Enqueue([worked, untouched]);

            _resolver.ResolvedId = Guid.NewGuid();
            // Only 'worked' is signalled + yielded; 'untouched' is drained but never streamed. It must
            // never be marked Processing — proof the whole drained queue is not flipped up front.
            _attribution.StreamResults.Enqueue((worked, Resolved()));

            await _sut.ProcessItemAsync(worked, CancellationToken.None);

            Assert.DoesNotContain(untouched, _queue.Processing);
            Assert.DoesNotContain(untouched, _queue.Applied.Select(a => a.Item));
            // The item that was actually worked was chunk-signalled before its outcome applied.
            Assert.Contains(worked, _queue.Processing);
        }

        [Fact]
        public async Task DeferredItem_ReturnsToQueued_WhileAwaitingEscalation()
        {
            var chapterId = Guid.NewGuid();
            var deferred = MakeChapterItem(chapterId);
            var decided = MakeChapterItem(chapterId);
            _queue.Enqueue([deferred, decided]);

            _resolver.ResolvedId = Guid.NewGuid();
            // 'deferred' goes in flight, is answered suspect, and is held back for a later chain step
            // without ever being yielded. It must not be left showing Processing.
            _attribution.DeferItems.Add(deferred);
            _attribution.StreamResults.Enqueue((decided, Resolved()));

            await _sut.ProcessItemAsync(deferred, CancellationToken.None);

            Assert.Contains(deferred, _queue.Deferred);
            Assert.Contains(deferred, _queue.Processing);
        }

        [Fact]
        public async Task DeferredItem_DecidedByLaterStep_CompletesNormally()
        {
            var item = MakeChapterItem(Guid.NewGuid());
            _queue.Enqueue([item]);

            _resolver.ResolvedId = Guid.NewGuid();
            // Held back by step 0, then resolved by a later escalation step: the deferral is transient
            // and must not block the terminal outcome.
            _attribution.DeferItems.Add(item);
            _attribution.StreamResults.Enqueue((item, Resolved()));

            await _sut.ProcessItemAsync(item, CancellationToken.None);

            DispositionFor<Disposition.Complete>(item);
        }

        [Fact]
        public async Task ProcessorThrows_MarksAllDrainedItemsFailed()
        {
            var chapterId = Guid.NewGuid();
            var items = new[] { MakeChapterItem(chapterId), MakeChapterItem(chapterId) };
            _queue.Enqueue(items);

            _attribution.ThrowException = new InvalidOperationException("boom");

            await _sut.ProcessItemAsync(items[0], CancellationToken.None);

            foreach (var item in items)
                Assert.Equal("boom", DispositionFor<Disposition.Failed>(item).Reason);
        }

        [Fact]
        public async Task UnexpectedThrowMidStream_FailsOnlyUndecidedItems()
        {
            var chapterId = Guid.NewGuid();
            var decided = MakeChapterItem(chapterId);
            var undecided = MakeChapterItem(chapterId);
            _queue.Enqueue([decided, undecided]);

            _resolver.ResolvedId = Guid.NewGuid();
            // First item decides, then the stream throws before the second — only the second fails.
            _attribution.StreamThenThrow((decided, Resolved()), new InvalidOperationException("boom"));

            await _sut.ProcessItemAsync(decided, CancellationToken.None);

            DispositionFor<Disposition.Complete>(decided);
            DispositionFor<Disposition.Failed>(undecided);
        }

        /// <summary>
        /// Records what the processor decided; reimplements nothing, so there is nothing to drift.
        /// Enqueued items are what <see cref="DrainAll"/> hands back, standing in for the channel.
        /// </summary>
        private sealed class RecordingQueue : ICharacterQueue
        {
            private readonly List<QueuedParagraph> _queued = [];

            public List<(QueuedParagraph Item, Disposition D)> Applied { get; } = [];
            public List<QueuedParagraph> Processing { get; } = [];
            public List<QueuedParagraph> Deferred { get; } = [];

            public CancellationToken ItemCancellationToken => CancellationToken.None;

            public void Enqueue(IEnumerable<QueuedParagraph> paragraphs) => _queued.AddRange(paragraphs);

            public IReadOnlyList<QueuedParagraph> DrainAll(QueuedParagraph first)
            {
                var all = new List<QueuedParagraph> { first };
                all.AddRange(_queued.Where(q => !q.Equals(first)));
                _queued.Clear();
                return all;
            }

            public void MarkProcessing(QueuedParagraph item) => Processing.Add(item);

            public void MarkDeferred(QueuedParagraph item) => Deferred.Add(item);

            public void Apply(QueuedParagraph item, Disposition disposition) => Applied.Add((item, disposition));
        }

        private class FakeEscalationChain() : AttributionEscalationChain(null!, null!, null!, NullLogger<AttributionEscalationChain>.Instance)
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
            public List<string> Names { get; } = [];

            public override Task<Guid> ResolveOrCreateAsync(ProjectFolderId folder, string name, CancellationToken ct)
            {
                Names.Add(name);
                return Task.FromResult(ResolvedId);
            }
        }

        /// <summary>Serves the narrator link, and counts how often the processor asks for it.</summary>
        private sealed class FakeNarratorCatalog : IProjectCatalogReader
        {
            public NarratorIdentity Narrator { get; set; } = NarratorIdentity.Unlinked;
            public int Reads { get; private set; }

            public Task<NarratorIdentity> GetNarratorAsync(ProjectFolderId folderId, CancellationToken ct = default)
            {
                Reads++;
                return Task.FromResult(Narrator);
            }

            public IReadOnlyList<string> GetProjects() => [];
            public Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync() =>
                Task.FromResult<IReadOnlyList<ProjectSummary>>([]);
            public Task<Read2Me.Data.Entities.Project?> GetProjectAsync(ProjectFolderId folderId) =>
                Task.FromResult<Read2Me.Data.Entities.Project?>(null);
        }

        /// <summary>Stands in for the post-apply "is this paragraph fully stamped?" read.</summary>
        private sealed class FakeCharacterReader : IUnattributedItemCounter
        {
            public int Unattributed { get; set; }

            public Task<int> CountUnattributedCharacterItemsAsync(ProjectFolderId folderId, Guid paragraphId)
                => Task.FromResult(Unattributed);
        }

        private class FakeCommandHandler : IBookCommandHandler
        {
            public List<BookCommand> SentCommands { get; } = [];
            public Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
            {
                SentCommands.Add(command);
                return Task.FromResult<Guid?>(null);
            }
        }
    }
}
