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
            new(runner, settings, new EmptyReader(), new EmptyReader(),
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
                PromptTemplates.BuildEditItemsJson([(0, target.DisplayPath, target.CurrentValue, null)]),
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
        public async Task Propose_ChangeCaseUpper_ReCasesAndMarksUnchanged()
        {
            var program = Program(new EditTransform(TransformKind.ChangeCase, CaseMode: CaseMode.Upper));
            var targets = new[] { Target(1, "Part one"), Target(2, "PART TWO") };

            var proposals = await NewService(new FakeLlmCompletionRunner(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal("PART ONE", proposals[0].NewValue);
            Assert.Equal(ProposalStatus.Proposed, proposals[0].Status);
            Assert.Equal(ProposalStatus.NoChange, proposals[1].Status);
        }

        [Fact]
        public async Task Propose_ChangeCaseSentence_ReCasesInCode()
        {
            var runner = new FakeLlmCompletionRunner();
            var program = Program(new EditTransform(TransformKind.ChangeCase, CaseMode: CaseMode.Sentence));

            var proposals = await NewService(runner, NewSettings())
                .ProposeAsync(Folder, program, [Target(1, "PART IV"), Target(2, "Part IV")],
                    null, false, CancellationToken.None);

            Assert.Equal("Part IV", proposals[0].NewValue);
            Assert.Equal(ProposalStatus.Proposed, proposals[0].Status);
            Assert.Equal(ProposalStatus.NoChange, proposals[1].Status);
            Assert.Empty(runner.Requests);
        }

        [Theory]
        [InlineData(CaseMode.Sentence, "Fitzwilliam Darcy rode to Pemberley")]
        [InlineData(CaseMode.Title, "Fitzwilliam Darcy Rode to Pemberley")]
        [InlineData(CaseMode.Upper, "FITZWILLIAM DARCY RODE TO PEMBERLEY")]
        public async Task Propose_ChangeCase_KeepsTheNamesTheBookUses(CaseMode mode, string expected)
        {
            var reader = new BookTextReader(["He met Fitzwilliam Darcy there.", "They left for Pemberley.", "he rode", "to"]);
            var program = Program(new EditTransform(TransformKind.ChangeCase, CaseMode: mode));

            var proposals = await new BookEditProposalService(new FakeLlmCompletionRunner(), NewSettings(),
                    reader, reader, NullLogger<BookEditProposalService>.Instance)
                .ProposeAsync(Folder, program, [Target(1, "FITZWILLIAM DARCY RODE TO PEMBERLEY")],
                    null, false, CancellationToken.None);

            Assert.Equal(expected, proposals[0].NewValue);
            Assert.Equal(mode == CaseMode.Upper ? 0 : 1, reader.BookTextReads);
        }

        private sealed class BookTextReader(IReadOnlyList<string> texts) : ProjectReaderFakeBase
        {
            public int BookTextReads { get; private set; }

            public override Task<IReadOnlyList<string>> GetBookTextAsync(ProjectFolderId folderId, CancellationToken ct = default)
            {
                BookTextReads++;
                return Task.FromResult(texts);
            }
        }

        [Fact]
        public async Task ProposeOne_ChangeCase_FailsWithoutCallingLlm()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner();
            var program = Program(new EditTransform(TransformKind.ChangeCase, CaseMode: CaseMode.Upper));

            var proposal = await NewService(runner, settings)
                .ProposeOneAsync(Folder, program, Target(1, "Intro"), null, false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task Propose_SetTemplateWithSources_MissingAndSelfRowsFail_InPlace_TheRestPropose()
        {
            var program = Program(new EditTransform(TransformKind.SetTemplate, Template: "Chapter {p1}, {p2}",
                Sources: [new EditSource(EditSourceKind.Paragraph, 1), new EditSource(EditSourceKind.Paragraph, 2)]));
            EditTarget With(int n, params EditSourceValue[] sources) => Target(n, "") with { Sources = sources };
            var targets = new[]
            {
                With(1, new("p1", "1", null), new("p2", "The Period", null)),
                With(2, new("p1", "2", null), new("p2", null, "p2 is missing: the chapter has only 1 paragraph")),
                With(3, new("p1", null, "p1 is the paragraph being edited"), new("p2", "x", null)),
                With(4, new("p1", "4", null), new("p2", "The Mail", null)),
            };

            var proposals = await NewService(new FakeLlmCompletionRunner(), NewSettings())
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal(targets.Select(t => t.Id), proposals.Select(p => p.Id));
            Assert.Equal(
                [(ProposalStatus.Proposed, "Chapter 1, The Period", null),
                 (ProposalStatus.Failed, null, "p2 is missing: the chapter has only 1 paragraph"),
                 (ProposalStatus.Failed, null, "p1 is the paragraph being edited"),
                 (ProposalStatus.Proposed, "Chapter 4, The Mail", null)],
                proposals.Select(p => (p.Status, p.NewValue, p.FailureReason)));
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

        private static EditProgram SourceProgram(string instruction, params EditSourceKind[] kinds) =>
            Program(new EditTransform(TransformKind.Llm, Instruction: instruction,
                Sources: kinds.Select((k, i) => new EditSource(k, k == EditSourceKind.Paragraph ? i + 1 : 0)).ToList()));

        private static EditTarget WithSources(int n, string value, params (string Name, string? Value, string? Missing)[] sources) =>
            Target(n, value) with { Sources = sources.Select(s => new EditSourceValue(s.Name, s.Value, s.Missing)).ToList() };

        [Fact]
        public async Task ProposeOne_CarriesTheSourcesPresent_AndOnlyASourceFailureBlocksAskingAgain()
        {
            var service = NewService(new FakeLlmCompletionRunner(), NewSettings());
            var program = SourceProgram("join", EditSourceKind.Paragraph, EditSourceKind.Paragraph);

            // p1 present, p2 missing: p1 is listed, p2 is left out, and asking again would fail the same way.
            var partial = await service.ProposeOneAsync(Folder, program,
                WithSources(1, "x", ("p1", "1", null), ("p2", null, "p2 is missing: the chapter has only 1 paragraph")),
                null, false, CancellationToken.None);
            Assert.Equal(["p1|Paragraph 1|1"], partial.Sources.Select(s => $"{s.Name}|{s.Label}|{s.Value}"));
            Assert.False(partial.CanAskAgain);

            // The paragraph being edited is a source failure too.
            var self = await service.ProposeOneAsync(Folder, program,
                WithSources(1, "x", ("p1", null, "p1 is the paragraph being edited")), null, false, CancellationToken.None);
            Assert.Empty(self.Sources);
            Assert.False(self.CanAskAgain);
        }

        [Fact]
        public async Task Propose_Llm_Sources_RideOnTheirEntriesWithGlossesForNamesPresent()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "Chapter 1, The Period" } ]
                """);
            var program = SourceProgram("Chapter <number in p1>, <p2>", EditSourceKind.Paragraph, EditSourceKind.Paragraph);
            var target = WithSources(1, "", ("p1", "CHAPTER I.", null), ("p2", "The Period.", null));

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, [target], null, false, CancellationToken.None);

            Assert.Equal("Chapter 1, The Period", proposals[0].NewValue);
            var prompt = Assert.Single(runner.Requests).Prompt;
            Assert.Contains("\"sources\": {", prompt);
            Assert.Contains("\"p1\": \"CHAPTER I.\"", prompt);
            Assert.Contains("\"p2\": \"The Period.\"", prompt);
            Assert.Contains("Entries may also carry \"sources\"", prompt);
            Assert.Contains("p1: Paragraph 1", prompt);
            Assert.Contains("p2: Paragraph 2", prompt);
            Assert.DoesNotContain("p3:", prompt);
        }

        [Fact]
        public async Task Propose_Llm_WithoutSources_LeavesSourcesOutOfThePrompt()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""[ { "index": 0, "reasoning": "r", "new_text": "x" } ]""");

            await NewService(runner, settings).ProposeAsync(Folder,
                Program(new EditTransform(TransformKind.Llm, Instruction: "fix")),
                [Target(1, "a")], null, false, CancellationToken.None);

            var prompt = Assert.Single(runner.Requests).Prompt;
            Assert.DoesNotContain("sources", prompt);
        }

        [Fact]
        public async Task Propose_Llm_MissingSourceRows_FailBeforeAnyRequest_InPlace_AndIndicesStayContiguous()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "A" }, { "index": 1, "reasoning": "r", "new_text": "C" } ]
                """);
            var program = SourceProgram("use p1", EditSourceKind.Paragraph);
            var targets = new[]
            {
                WithSources(1, "a", ("p1", "x", null)),
                WithSources(2, "b", ("p1", null, "p1 is missing: the chapter has no paragraphs")),
                WithSources(3, "c", ("p1", "z", null)),
            };

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, program, targets, null, false, CancellationToken.None);

            Assert.Equal(targets.Select(t => t.Id), proposals.Select(p => p.Id));
            Assert.Equal(
                [(ProposalStatus.Proposed, "A", null),
                 (ProposalStatus.Failed, null, "p1 is missing: the chapter has no paragraphs"),
                 (ProposalStatus.Proposed, "C", null)],
                proposals.Select(p => (p.Status, p.NewValue, p.FailureReason)));
            var prompt = Assert.Single(runner.Requests).Prompt;
            Assert.DoesNotContain("\"index\": 2", prompt);
            Assert.DoesNotContain("\"text\": \"b\"", prompt);
        }

        [Fact]
        public async Task Propose_Llm_PacksByCharacterBudget()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner()
                .Completes("""[ { "index": 0, "reasoning": "r", "new_text": "1" }, { "index": 1, "reasoning": "r", "new_text": "2" } ]""")
                .Completes("""[ { "index": 0, "reasoning": "r", "new_text": "3" } ]""");
            var big = new string('x', 7000);
            var targets = new[] { Target(1, big), Target(2, "small"), Target(3, big) };

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, Program(new EditTransform(TransformKind.Llm, Instruction: "fix")),
                    targets, null, false, CancellationToken.None);

            Assert.Equal(2, runner.Requests.Count);
            Assert.Equal(["1", "2", "3"], proposals.Select(p => p.NewValue));
            Assert.Equal(BatchPacker.RequestCount(targets), runner.Requests.Count);
        }

        [Fact]
        public async Task Propose_Llm_AnItemOverTheCeiling_FailsAlone_WithoutTrippingTheRemainingRows()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""
                [ { "index": 0, "reasoning": "r", "new_text": "A" }, { "index": 1, "reasoning": "r", "new_text": "C" } ]
                """);
            var targets = new[] { Target(1, "a"), Target(2, new string('x', 60_001)), Target(3, "c") };

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, Program(new EditTransform(TransformKind.Llm, Instruction: "fix")),
                    targets, null, false, CancellationToken.None);

            Assert.Equal(
                [(ProposalStatus.Proposed, "A", null),
                 (ProposalStatus.Failed, null, "Too long to send to the AI (text and sources)"),
                 (ProposalStatus.Proposed, "C", null)],
                proposals.Select(p => (p.Status, p.NewValue, p.FailureReason)));
            Assert.Single(runner.Requests);
        }

        [Fact]
        public async Task Propose_Llm_ServiceDown_FailsOnlyTheRemainingSendableRows_KeepingEarlierFailures()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Fails(LlmRunOutcome.ServiceUnavailable, "down");
            var targets = new[]
            {
                WithSources(1, "a", ("p1", null, "p1 is missing: the chapter has no paragraphs")),
                WithSources(2, "b", ("p1", "x", null)),
            };

            var proposals = await NewService(runner, settings)
                .ProposeAsync(Folder, SourceProgram("use p1", EditSourceKind.Paragraph), targets, null, false, CancellationToken.None);

            Assert.Equal(["p1 is missing: the chapter has no paragraphs", "down"], proposals.Select(p => p.FailureReason));
        }

        [Fact]
        public async Task ProposeOne_Llm_MissingSourceRow_FailsWithTheSameReasonWithoutARequest()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner();
            var target = WithSources(1, "a", ("p1", null, "p1 is missing: the chapter has no paragraphs"));

            var proposal = await NewService(runner, settings).ProposeOneAsync(
                Folder, SourceProgram("use p1", EditSourceKind.Paragraph), target, "hint", false, CancellationToken.None);

            Assert.Equal(ProposalStatus.Failed, proposal.Status);
            Assert.Equal("p1 is missing: the chapter has no paragraphs", proposal.FailureReason);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task ProposeOne_Llm_SendsTheStoredSourcesUnchanged_AndTheHintAddsNone()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner().Completes("""[ { "index": 0, "reasoning": "r", "new_text": "ok" } ]""");
            var target = WithSources(1, "a", ("p1", "CHAPTER IV.", null));

            await NewService(runner, settings).ProposeOneAsync(
                Folder, SourceProgram("use p1", EditSourceKind.Paragraph), target, "as a digit", false, CancellationToken.None);

            var prompt = Assert.Single(runner.Requests).Prompt;
            Assert.Contains("\"p1\": \"CHAPTER IV.\"", prompt);
            Assert.Contains("Additional guidance from the user: as a digit", prompt);
        }

        [Fact]
        public async Task ProposeOne_Llm_AnItemOverTheCeiling_Fails_WithoutARequest()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var runner = new FakeLlmCompletionRunner();

            var proposal = await NewService(runner, settings).ProposeOneAsync(
                Folder, Program(new EditTransform(TransformKind.Llm, Instruction: "fix")),
                Target(1, new string('x', 60_001)), null, false, CancellationToken.None);

            Assert.Equal("Too long to send to the AI (text and sources)", proposal.FailureReason);
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
            new(runner, settings, new EmptyReader(), new EmptyReader(),
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
