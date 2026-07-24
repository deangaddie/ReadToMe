using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Voice
{
    public class VoiceDesignPromptServiceTests
    {
        private sealed class FakeLlmPromptService : LlmPromptService
        {
            private int _callCount;
            private readonly string _template;

            public int CallCount => _callCount;

            public FakeLlmPromptService(string template, int initialCount = 0) : base(null!, null!)
            {
                _template = template;
                _callCount = initialCount;
            }

            public override Task<string> GetVoicePromptAsync()
            {
                _callCount++;
                return Task.FromResult(_template);
            }

            public override Task<string> GetVoicePlanPromptAsync()
                => Task.FromResult(_template);

            public void FireOnChanged() => NotifyChanged();
        }

        private sealed class FakeLlmSettings : LlmSettingsService
        {
            private readonly LlmServerConfig? _config;
            public FakeLlmSettings(LlmServerConfig? config) : base(null!, null!) => _config = config;
            public override Task<LlmServerConfig?> GetActiveConfigAsync() => Task.FromResult(_config);
        }

        private static LlmServerConfig Config() => new() { Name = "t", BaseUrl = "http://x", Model = "m" };

        private static VoiceDesignPromptService Create(
            FakeLlmCompletionRunner runner,
            LlmServerConfig? config = null,
            string template = "template")
            => new(
                runner,
                new FakeLlmSettings(config),
                new FakeLlmPromptService(template),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDesignPromptService>.Instance);

        // ---- Voice prompt path (free text) ----

        [Fact]
        public async Task GenerateWithPrompt_Success_ReturnsTrimmedText_AndSendsFreeTextRequest()
        {
            var runner = new FakeLlmCompletionRunner().Completes("  rich voice description \n");
            var sut = Create(runner, Config());

            var result = await sut.GenerateWithPromptAsync("rendered prompt");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.Success, result.Status);
            Assert.Equal("rich voice description", result.Prompt);

            var request = Assert.Single(runner.Requests);
            Assert.Equal("rendered prompt", request.Prompt);
            Assert.Equal("Voice prompt", request.Label);
            Assert.Equal(CompletionShape.None, request.Shape);
            Assert.Null(request.JsonSchema);
            Assert.Equal("http://x", request.Config.BaseUrl);
            Assert.True(request.DisableThinking);
        }

        [Fact]
        public async Task GenerateWithPrompt_NoLlmConfigured_DoesNotRun()
        {
            var runner = new FakeLlmCompletionRunner();
            var sut = Create(runner, config: null);

            var result = await sut.GenerateWithPromptAsync("rendered prompt");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.NoLlmConfigured, result.Status);
            Assert.Empty(runner.Requests);
        }

        [Fact]
        public async Task GenerateWithPrompt_RunnerFailure_MapsToFailedWithReason()
        {
            var runner = new FakeLlmCompletionRunner()
                .Fails(LlmRunOutcome.ServiceUnavailable, "llama is down");
            var sut = Create(runner, Config());

            var result = await sut.GenerateWithPromptAsync("rendered prompt");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.Failed, result.Status);
            Assert.Equal("llama is down", result.FailureReason);
        }

        // ---- Voice plan path (JSON object) ----

        private const string PlanJson =
            """[{"name":"Default","description":"calm","design_prompt":"A calm voice"}]""";

        [Fact]
        public async Task GeneratePlan_Success_ReturnsVoices_AndSendsSchemaConstrainedRequest()
        {
            var runner = new FakeLlmCompletionRunner().Completes(PlanJson);
            var sut = Create(runner, Config());

            var result = await sut.GeneratePlanAsync("Dune", "Herbert", "Paul");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.Success, result.Status);
            Assert.NotNull(result.Voices);
            Assert.Equal("Default", Assert.Single(result.Voices).Name);

            var request = Assert.Single(runner.Requests);
            Assert.Equal("Voice plan: Paul", request.Label);
            Assert.Equal(CompletionShape.Array, request.Shape);
            Assert.Equal(VoicePlanSchema.JsonSchema, request.JsonSchema);
            // Plans keep thinking: change points across a published book are recall-heavy.
            Assert.False(request.DisableThinking);
        }

        [Fact]
        public async Task GeneratePlan_UnparsableResponse_MapsToFailed()
        {
            var runner = new FakeLlmCompletionRunner().Completes("not json");
            var sut = Create(runner, Config());

            var result = await sut.GeneratePlanAsync("Dune", "Herbert", "Paul");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.Failed, result.Status);
            Assert.Null(result.Voices);
            Assert.NotNull(result.FailureReason);
        }

        [Fact]
        public async Task GeneratePlan_NoLlmConfigured_DoesNotRun()
        {
            var runner = new FakeLlmCompletionRunner();
            var sut = Create(runner, config: null);

            var result = await sut.GeneratePlanAsync("Dune", "Herbert", "Paul");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.NoLlmConfigured, result.Status);
            Assert.Empty(runner.Requests);
        }

        // ---- Prompt rendering ----

        [Fact]
        public async Task BuildRenderedPrompt_SubstitutesAllTokens()
        {
            var sut = Create(new FakeLlmCompletionRunner(),
                template: "{{book_title}} by {{book_author}} — {{character_name}}");

            var result = await sut.BuildRenderedPromptAsync("Dune", "Herbert", "Paul");

            Assert.Equal("Dune by Herbert — Paul", result);
        }

        [Fact]
        public async Task BuildRenderedPrompt_CachesTemplate_CallsGetOnce()
        {
            var fake = new FakeLlmPromptService("Hello {{CharacterName}}");
            var sut = new VoiceDesignPromptService(null!, null!, fake, null!);

            await sut.BuildRenderedPromptAsync("", "", "A");
            await sut.BuildRenderedPromptAsync("", "", "B");

            Assert.Equal(1, fake.CallCount);
        }

        [Fact]
        public async Task BuildRenderedPrompt_AfterOnChangedFires_ReloadsTemplate()
        {
            var fake = new FakeLlmPromptService("template");
            var sut = new VoiceDesignPromptService(null!, null!, fake, null!);

            await sut.BuildRenderedPromptAsync("", "", "");
            Assert.Equal(1, fake.CallCount);

            fake.FireOnChanged();

            await sut.BuildRenderedPromptAsync("", "", "");
            Assert.Equal(2, fake.CallCount);
        }
    }
}
