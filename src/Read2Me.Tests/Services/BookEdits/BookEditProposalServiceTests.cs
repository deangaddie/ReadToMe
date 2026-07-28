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

        private BookEditProposalService NewService(FakeLlmCompletionRunner runner, LlmSettingsService settings) =>
            new(runner, settings, new EmptyReader(),
                NullLogger<BookEditProposalService>.Instance);

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

            var proposals = await NewService(new FakeLlmCompletionRunner(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal("Chapter 1: Intro", proposals[0].NewValue);
            Assert.Equal("Chapter 2: Storm", proposals[1].NewValue);
            Assert.All(proposals, p => Assert.Equal(ProposalStatus.Proposed, p.Status));
        }

        [Fact]
        public async Task Propose_RegexReplace_NoChangeMarked()
        {
            var program = Program(new EditTransform(TransformKind.RegexReplace, Pattern: "^\\d+\\. ", Replacement: ""));
            var targets = new[] { Target(1, "1. Intro"), Target(2, "No prefix") };

            var proposals = await NewService(new FakeLlmCompletionRunner(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Proposed, proposals[0].Status);
            Assert.Equal("Intro", proposals[0].NewValue);
            Assert.Equal(ProposalStatus.NoChange, proposals[1].Status);
        }

        [Fact]
        public async Task Propose_Llm_MapsBatchResultsByIndex_MissingIndexFails()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "It is a truth." },
                  { "index": 2, "reasoning": "r", "new_text": "Third fixed." } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "restore first letter"));
            var targets = new[] { Target(1, "t is a truth."), Target(2, "second"), Target(3, "hird") };

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

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
            var runner = new FakeLlmCompletionRunner().Completes($"[{batch1}]").Completes($"[{batch2}]");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();
            var reports = new List<(int Done, int Total)>();
            var progress = new SyncProgress(reports);

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, targets, progress, false, CancellationToken.None);

            Assert.Equal(2, runner.Requests.Count);
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
            var proposals = await NewService(new FakeLlmCompletionRunner(), NewSettings())
                .ProposeAsync(Folder, program, [Target(1, "x")], null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposals[0].Status);
            Assert.Contains("No active LLM", proposals[0].FailureReason);
        }

        [Fact]
        public async Task Propose_Llm_RunFails_RemainingTargetsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.ServiceUnavailable, "down");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal(10, proposals.Count);
            Assert.All(proposals, p => Assert.Equal(ProposalStatus.Failed, p.Status));
            Assert.Equal("down", proposals[9].FailureReason);
            Assert.Single(runner.Requests); // no retry per batch once the service is down
        }

        [Fact]
        public async Task Propose_Llm_UnparsableBatch_FailsBatchItemsButContinues()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var batch2 = string.Join(",", Enumerable.Range(0, 2).Select(i => $"{{ \"index\": {i}, \"reasoning\": \"r\", \"new_text\": \"w{i}\" }}"));
            var runner = new FakeLlmCompletionRunner().Completes("garbage").Completes($"[{batch2}]");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal(10, proposals.Count);
            Assert.All(proposals.Take(8), p => Assert.Equal(ProposalStatus.Failed, p.Status));
            Assert.Equal("w0", proposals[8].NewValue);
            Assert.Equal("w1", proposals[9].NewValue);
            Assert.Equal(2, runner.Requests.Count); // parse failure does not stop the loop
        }

        [Fact]
        public async Task Propose_Llm_CancelMidLoop_ReturnsPartialProposals()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var batch1 = string.Join(",", Enumerable.Range(0, 8).Select(i => $"{{ \"index\": {i}, \"reasoning\": \"r\", \"new_text\": \"v{i}\" }}"));
            using var cts = new CancellationTokenSource();
            var runner = new CancelAfterFirstRunner($"[{batch1}]", cts);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var targets = Enumerable.Range(1, 10).Select(n => Target(n, $"old{n}")).ToList();

            var proposals = await NewService2(runner, settings)
                .ProposeAsync(Folder, program, targets, null, false, cts.Token);

            Assert.Equal(8, proposals.Count); // first batch kept, second never completed
        }

        [Fact]
        public async Task ProposeOne_Llm_SendsSingleItemAndFoldsHintIntoInstruction()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "Storm's Coming" } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix the title"));
            var target = Target(2, "storm coming");

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, target, "use an apostrophe", false, CancellationToken.None);

            Assert.Equal("Storm's Coming", proposal.NewValue);
            Assert.Equal(ProposalStatus.Proposed, proposal.Status);
            var request = Assert.Single(runner.Requests);
            Assert.Contains("fix the title\n\nAdditional guidance from the user: use an apostrophe", request.Prompt);
            Assert.StartsWith("1 edit(s):", request.Label);
            // batch of one: the items block is exactly this target at index 0
            Assert.Contains(
                PromptTemplates.BuildEditItemsJson([(0, target.DisplayPath, target.CurrentValue)]),
                request.Prompt);
        }

        [Fact]
        public async Task ProposeOne_Llm_BlankHint_LeavesPromptIdentical()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "Storm" } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix the title"));
            var target = Target(2, "storm coming");
            var service = NewService(runner, settings);

            await service.ProposeOneAsync(Folder, program, target, null, false, CancellationToken.None);
            await service.ProposeOneAsync(Folder, program, target, "   ", false, CancellationToken.None);

            Assert.Equal(runner.Requests[0].Prompt, runner.Requests[1].Prompt);
            Assert.DoesNotContain("Additional guidance", runner.Requests[0].Prompt);

            // and identical to what the batch path renders for the same single target
            var batchRunner = new FakeLlmCompletionRunner().Completes("[]");
            await NewService(batchRunner, settings)
                .ProposeAsync(Folder, program, [target], null, false, CancellationToken.None);
            Assert.Equal(batchRunner.Requests[0].Prompt, runner.Requests[0].Prompt);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task Propose_Llm_PassesDisableThinkingThrough(bool disableThinking)
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("[]");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));
            var service = NewService(runner, settings);

            await service.ProposeAsync(
                Folder, program, [Target(1, "a")], null, disableThinking, CancellationToken.None);
            await service.ProposeOneAsync(
                Folder, program, Target(2, "b"), null, disableThinking, CancellationToken.None);

            Assert.All(runner.Requests, r => Assert.Equal(disableThinking, r.DisableThinking));
            Assert.Equal(2, runner.Requests.Count);
        }

        [Fact]
        public async Task ProposeOne_DeterministicProgram_FailsWithoutCallingLlm()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner();
            var program = Program(new EditTransform(TransformKind.SetTemplate, Template: "Chapter {n}"));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), "hint", false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task ProposeOne_Llm_ResponseMissingIndexZero_Fails()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 1, "reasoning": "r", "new_text": "wrong slot" } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Equal("The AI response did not include this item.", proposal.FailureReason);
        }

        [Fact]
        public async Task ProposeOne_Llm_UnchangedText_IsNoChange()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "Intro" } ]
                """);
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.NoChange, proposal.Status);
        }

        [Fact]
        public async Task ProposeOne_Llm_RunFails_FailsWithRunError()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.ServiceUnavailable, "down");
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Equal("down", proposal.FailureReason);
        }

        [Fact]
        public async Task ProposeOne_Llm_Cancelled_FailsInsteadOfThrowing()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var runner = new FakeLlmCompletionRunner().Throws(new OperationCanceledException(cts.Token));
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, cts.Token);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Contains("Cancelled", proposal.FailureReason);
        }

        [Fact]
        public async Task ProposeOne_Llm_NoConfig_FailsWithoutCallingLlm()
        {
            var runner = new FakeLlmCompletionRunner();
            var program = Program(new EditTransform(TransformKind.Llm, Instruction: "fix"));

            var proposal = await NewService(runner, NewSettings())
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Contains("No active LLM", proposal.FailureReason);
            Assert.Empty(runner.Requests);
        }

        private BookEditProposalService NewService2(ILlmCompletionRunner runner, LlmSettingsService settings) =>
            new(runner, settings, new EmptyReader(),
                NullLogger<BookEditProposalService>.Instance);

        /// <summary>First run completes with the given raw; second cancels the source and throws.</summary>
        private sealed class CancelAfterFirstRunner(string firstRaw, CancellationTokenSource cts) : ILlmCompletionRunner
        {
            private int _calls;

            public Task<LlmRunResult<T>> RunAsync<T>(LlmRunRequest request, TryParse<T> parser, CancellationToken ct)
            {
                if (_calls++ > 0)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(ct);
                }
                parser(firstRaw, out var value, out _);
                return Task.FromResult(new LlmRunResult<T>(LlmRunOutcome.Completed, value, firstRaw, null));
            }

            public Task<LlmRunResult<string>> RunAsync(LlmRunRequest request, CancellationToken ct)
                => throw new NotSupportedException();
        }

        /// <summary>IProgress that records synchronously (Progress&lt;T&gt; posts to a sync context).</summary>
        private sealed class SyncProgress(List<(int, int)> reports) : IProgress<(int Done, int Total)>
        {
            public void Report((int Done, int Total) value) => reports.Add(value);
        }
    }
}
