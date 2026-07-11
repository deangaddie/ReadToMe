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
    public class BookEditPlannerTests : AppDbTestBase
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private LlmSettingsService NewSettings() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private BookEditPlanner NewPlanner(FakeLlmClient llm, LlmSettingsService settings)
        {
            var reader = new EmptyReader();
            return new(llm, settings, reader, new ChapterOutlineBuilder(reader),
                NullLogger<BookEditPlanner>.Instance,
                new EventBroadcaster<LlmStreamEvent>(),
                new FakeAiServiceReporter());
        }

        private sealed class EmptyReader : ProjectReaderFakeBase;

        private static async Task RegisterActiveConfigAsync(LlmSettingsService svc)
        {
            var config = new LlmServerConfig { Name = "Test", BaseUrl = "http://localhost:8080", Model = "test" };
            config = await svc.CreateConfigAsync(config);
            await svc.SetActiveConfigAsync(config.Id);
        }

        private const string ValidPlanJson = """
            { "reasoning": "rename", "supported": true, "unsupported_reason": null,
              "target": "chapter_title",
              "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
              "paragraph_filter": { "where": [] },
              "transform": { "kind": "set_template", "pattern": null, "replacement": null, "template": "Chapter {n}", "instruction": null } }
            """;

        [Fact]
        public async Task Plan_NoConfig_ReturnsNoLlmConfigured()
        {
            var planner = NewPlanner(new FakeLlmClient(ValidPlanJson), NewSettings());
            var outcome = await planner.PlanAsync(Folder, "rename chapters", CancellationToken.None);
            Assert.Equal(EditPlanStatus.NoLlmConfigured, outcome.Status);
        }

        [Fact]
        public async Task Plan_ValidResponse_ReturnsOkWithProgram()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient(ValidPlanJson);

            var outcome = await NewPlanner(llm, settings).PlanAsync(Folder, "rename every chapter to 'Chapter {n}'", CancellationToken.None);

            Assert.Equal(EditPlanStatus.Ok, outcome.Status);
            Assert.Equal(EditTargetSelector.ChapterTitle, outcome.Program!.Target);
            Assert.Equal(TransformKind.SetTemplate, outcome.Program.Transform.Kind);
            Assert.Contains("rename every chapter", llm.Prompts[0]);
        }

        [Fact]
        public async Task Plan_UnsupportedResponse_ReturnsUnsupportedWithReason()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var raw = """
                { "reasoning": "structural", "supported": false, "unsupported_reason": "Cannot split chapters.",
                  "target": "chapter_title",
                  "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
                  "paragraph_filter": { "where": [] },
                  "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": null } }
                """;

            var outcome = await NewPlanner(new FakeLlmClient(raw), settings).PlanAsync(Folder, "split chapter 4", CancellationToken.None);

            Assert.Equal(EditPlanStatus.Unsupported, outcome.Status);
            Assert.Equal("Cannot split chapters.", outcome.Reason);
        }

        [Fact]
        public async Task Plan_GarbageResponse_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);

            var outcome = await NewPlanner(new FakeLlmClient("not json at all"), settings).PlanAsync(Folder, "x", CancellationToken.None);

            Assert.Equal(EditPlanStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.Reason);
        }

        [Fact]
        public async Task Plan_LlmThrows_ReturnsFailed()
        {
            var settings = NewSettings();
            await RegisterActiveConfigAsync(settings);
            var llm = new FakeLlmClient(ValidPlanJson) { Throws = new HttpRequestException("boom") };

            var outcome = await NewPlanner(llm, settings).PlanAsync(Folder, "x", CancellationToken.None);

            Assert.Equal(EditPlanStatus.Failed, outcome.Status);
            Assert.Equal("boom", outcome.Reason);
        }
    }
}
