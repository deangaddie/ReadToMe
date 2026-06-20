using System.Threading.Tasks;
using Read2Me.Services;
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
