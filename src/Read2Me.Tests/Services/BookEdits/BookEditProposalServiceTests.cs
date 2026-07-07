using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.BookEdits
{
    public class BookEditProposalServiceTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private LlmSettingsService NewSettings() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private BookEditProposalService NewService(FakeLlmClient llm, LlmSettingsService settings) =>
            new(llm, settings, new EmptyReader(),
                NullLogger<BookEditProposalService>.Instance,
                new EventBroadcaster<LlmStreamEvent>(),
                new FakeAiServiceReporter());

        private sealed class EmptyReader : ProjectReaderFakeBase;

        private static async Task RegisterActiveConfigAsync(LlmSettingsService svc)
        {
            var config = new LlmServerConfig { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test" };
            config = await svc.CreateConfigAsync(config);
            await svc.SetActiveConfigAsync(config.Id);
        }

        private static EditProgram Program(EditTransform transform) =>
            new(true, null, EditTargetSelector.ChapterTitle, NodeFilter.All, ParagraphFilter.All, transform);

        private static EditTarget Target(int n, string value, string? path = null) =>
            new(BookEditTargetKind.ChapterTitle, Guid.NewGuid(), value, path ?? $"Chapter {n}", n, null, null);

        [Fact]
        public async Task Propose_SetTemplate_RendersPerOrdinal()
        {
            var program = Program(new EditTransform(TransformKind.SetTemplate, Template: "Chapter {n}: {old}"));
            var targets = new[] { Target(1, "Intro"), Target(2, "Storm") };

            var proposals = await NewService(new FakeLlmClient(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, CancellationToken.None);

            Assert.Equal("Chapter 1: Intro", proposals[0].NewValue);
            Assert.Equal("Chapter 2: Storm", proposals[1].NewValue);
            Assert.All(proposals, p => Assert.Equal(ProposalStatus.Proposed, p.Status));
        }

        [Fact]
        public async Task Propose_RegexReplace_NoChangeMarked()
        {
            var program = Program(new EditTransform(TransformKind.RegexReplace, Pattern: "^\\d+\\. ", Replacement: ""));
            var targets = new[] { Target(1, "1. Intro"), Target(2, "No prefix") };

            var proposals = await NewService(new FakeLlmClient(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, CancellationToken.None);

            Assert.Equal(ProposalStatus.Proposed, proposals[0].Status);
            Assert.Equal("Intro", proposals[0].NewValue);
            Assert.Equal(ProposalStatus.NoChange, proposals[1].Status);
        }

        [Fact]
        public async Task Propose_Llm_MapsBatchResultsByIndex_MissingIndexFails()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient("""
                [ { "index": 0, "reasoning": "r", "new_text": "It is a truth." },
                  { "index": 2, "reasoning": "r", "new_text": "Third fixed." } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "restore first letter"));
            var targets = new[] { Target(1, "t is a truth."), Target(2, "second"), Target(3, "hird") };

            var proposals = await NewService(llm, settings)
                .ProposeAsync(Folder, program, targets, null, CancellationToken.None);

            Assert.Equal("It is a truth.", proposals[0].NewValue);
            Assert.Equal(ProposalStatus.Failed, proposals[1].Status);
            Assert.Equal("Third fixed.", proposals[2].NewValue);
        }

        [Fact]
        public async Task Propose_Llm_BatchesOfEight_ReportsProgress()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            // 10 targets → 2 batches; response covers indexes 0-7 (batch1) and 0-1 (batch2)
            var batch1 = string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{ \"index\": {i}, \"reasoning\": \"r\", \"new_text\": \"v{i}\" }}"));
            var batch2 = string.Join(",", Enumerable.Range(0, 2).Select(i => $"{{ \"index\": {i}, \"reasoning\": \"r\", \"new_text\": \"w{i}\" }}"));
            var llm = new FakeLlmClient($"[{batch1}]", $"[{batch2}]");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();
            var reports = new List<(int Done, int Total)>();
            var progress = new SyncProgress(reports);

            var proposals = await NewService(llm, settings)
                .ProposeAsync(Folder, program, targets, progress, CancellationToken.None);

            Assert.Equal(2, llm.CallCount);
            Assert.Equal(10, proposals.Count);
            Assert.Equal("v0", proposals[0].NewValue);
            Assert.Equal("w1", proposals[9].NewValue);
            Assert.Contains((8, 10), reports);
            Assert.Contains((10, 10), reports);
        }

        [Fact]
        public async Task Propose_Llm_NoConfig_AllFailed()
        {
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var proposals = await NewService(new FakeLlmClient(), NewSettings())
                .ProposeAsync(Folder, program, [Target(1, "x")], null, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposals[0].Status);
            Assert.Contains("No active LLM", proposals[0].FailureReason);
        }

        [Fact]
        public async Task Propose_Llm_ServiceThrows_RemainingTargetsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient { Throws = new HttpRequestException("down") };
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();

            var proposals = await NewService(llm, settings)
                .ProposeAsync(Folder, program, targets, null, CancellationToken.None);

            Assert.Equal(10, proposals.Count);
            Assert.All(proposals, p => Assert.Equal(ProposalStatus.Failed, p.Status));
            Assert.Equal(1, llm.CallCount); // no retry per batch once the service is down
        }

        /// <summary>IProgress that records synchronously (Progress&lt;T&gt; posts to a sync context).</summary>
        private sealed class SyncProgress(List<(int, int)> reports) : IProgress<(int Done, int Total)>
        {
            public void Report((int Done, int Total) value) => reports.Add(value);
        }
    }
}
