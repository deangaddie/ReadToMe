using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Read2Me.Services.Voice;
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

            public void FireOnChanged() => NotifyChanged();
        }

        private sealed class FakeLlmSettings : LlmSettingsService
        {
            private readonly LlmServerConfig? _config;
            public FakeLlmSettings(LlmServerConfig? config) : base(null!, null!) => _config = config;
            public override Task<LlmServerConfig?> GetActiveConfigAsync() => Task.FromResult(_config);
        }

        private sealed class FakeLlmClient : ILlmClient
        {
            private readonly IReadOnlyList<LlmChatChunk> _chunks;
            public FakeLlmClient(params LlmChatChunk[] chunks) => _chunks = chunks;

            public async IAsyncEnumerable<LlmChatChunk> StreamChatAsync(
                LlmServerConfig config, string prompt, string? jsonSchema = null,
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var c in _chunks)
                    yield return c;
                await Task.CompletedTask;
            }

            public Task<IReadOnlyList<string>> GetModelsAsync(LlmServerConfig config, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        [Fact]
        public async Task GenerateWithPrompt_PublishesStreamEvents()
        {
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += events.Add;

            var sut = new VoiceDesignPromptService(
                llm: new FakeLlmClient(
                    new LlmChatChunk("pondering", null, false),
                    new LlmChatChunk(null, "rich voice", false),
                    new LlmChatChunk(null, " description", false),
                    new LlmChatChunk(null, null, Done: true)),
                settings: new FakeLlmSettings(new LlmServerConfig { Name = "t", BaseUrl = "http://x", Model = "m" }),
                prompts: new FakeLlmPromptService("template"),
                logger: null!,
                broadcaster: broadcaster);

            var result = await sut.GenerateWithPromptAsync("rendered prompt");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.Success, result.Status);
            Assert.Equal("rich voice description", result.Prompt);

            Assert.Collection(events,
                e => Assert.Equal("rendered prompt", Assert.IsType<RequestStarted>(e).Prompt),
                e => Assert.Equal("pondering", Assert.IsType<ThinkingDelta>(e).Text),
                e => Assert.Equal("rich voice", Assert.IsType<ContentDelta>(e).Text),
                e => Assert.Equal(" description", Assert.IsType<ContentDelta>(e).Text),
                e => Assert.IsType<StreamCompleted>(e));
        }

        [Fact]
        public async Task GenerateWithPrompt_NoLlmConfigured_PublishesNothing()
        {
            var broadcaster = new EventBroadcaster<LlmStreamEvent>();
            var events = new List<LlmStreamEvent>();
            broadcaster.Event += events.Add;

            var sut = new VoiceDesignPromptService(
                llm: new FakeLlmClient(),
                settings: new FakeLlmSettings(null),
                prompts: new FakeLlmPromptService("template"),
                logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceDesignPromptService>.Instance,
                broadcaster: broadcaster);

            var result = await sut.GenerateWithPromptAsync("rendered prompt");

            Assert.Equal(VoiceDesignPromptService.GenerateStatus.NoLlmConfigured, result.Status);
            Assert.Empty(events);
        }

        [Fact]
        public async Task BuildRenderedPrompt_SubstitutesAllTokens()
        {
            var sut = new VoiceDesignPromptService(
                llm: null!,
                settings: null!,
                prompts: new FakeLlmPromptService("{{book_title}} by {{book_author}} — {{character_name}}"),
                logger: null!);

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
