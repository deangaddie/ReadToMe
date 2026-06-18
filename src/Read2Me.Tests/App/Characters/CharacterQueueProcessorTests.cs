using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Characters;
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

        private class FakeAttributionService() : CharacterAttributionService(null!, null!, null!, null!, NullLogger<CharacterAttributionService>.Instance, null!)
        {
            public AttributionOutcome? Outcome { get; set; }
            public Exception? ThrowException { get; set; }

            public override Task<AttributionOutcome> AttributeAsync(QueuedParagraph item, CancellationToken ct)
            {
                if (ThrowException != null) throw ThrowException;
                return Task.FromResult(Outcome!);
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
