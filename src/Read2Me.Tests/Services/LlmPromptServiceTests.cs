using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class LlmPromptServiceTests : AppDbTestBase
    {
        private LlmPromptService NewService() =>
            new(Factory, NullLogger<LlmPromptService>.Instance);

        [Fact]
        public async Task GetCharacterPrompt_WhenUnset_ReturnsBuiltInDefault()
        {
            var svc = NewService();
            var result = await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full);
            Assert.Equal(PromptTemplates.DefaultCharacterPrompt, result);
        }

        [Fact]
        public async Task GetVoicePrompt_WhenUnset_ReturnsBuiltInDefault()
        {
            var svc = NewService();
            var result = await svc.GetVoicePromptAsync();
            Assert.Equal(PromptTemplates.DefaultVoicePrompt, result);
        }

        [Fact]
        public async Task SetCharacterPrompt_ThenGet_ReturnsStoredValue()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("custom character prompt");
            Assert.Equal("custom character prompt", await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task SetVoicePrompt_ThenGet_ReturnsStoredValue()
        {
            var svc = NewService();
            await svc.SetVoicePromptAsync("custom voice prompt");
            Assert.Equal("custom voice prompt", await svc.GetVoicePromptAsync());
        }

        [Fact]
        public async Task ResetCharacterPrompt_AfterSet_ReturnsDefaultAgain()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("overridden");
            await svc.ResetCharacterPromptAsync();
            Assert.Equal(PromptTemplates.DefaultCharacterPrompt, await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task ResetVoicePrompt_AfterSet_ReturnsDefaultAgain()
        {
            var svc = NewService();
            await svc.SetVoicePromptAsync("overridden");
            await svc.ResetVoicePromptAsync();
            Assert.Equal(PromptTemplates.DefaultVoicePrompt, await svc.GetVoicePromptAsync());
        }

        [Fact]
        public async Task GetBatchCharacterPrompt_WhenUnset_ReturnsBuiltInDefault()
        {
            var svc = NewService();
            var result = await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full);
            Assert.Equal(PromptTemplates.DefaultBatchCharacterPrompt, result);
        }

        [Fact]
        public async Task SetBatchCharacterPrompt_ThenGet_ReturnsStoredValue()
        {
            var svc = NewService();
            await svc.SetBatchCharacterPromptAsync("custom batch prompt");
            Assert.Equal("custom batch prompt", await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task ResetBatchCharacterPrompt_AfterSet_ReturnsDefaultAgain()
        {
            var svc = NewService();
            await svc.SetBatchCharacterPromptAsync("overridden");
            await svc.ResetBatchCharacterPromptAsync();
            Assert.Equal(PromptTemplates.DefaultBatchCharacterPrompt, await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task GetSimpleCharacterPrompt_WhenUnset_ReturnsSimpleDefault()
        {
            var svc = NewService();
            var result = await svc.GetCharacterPromptAsync(AttributionPromptStyle.Simple);
            Assert.Equal(PromptTemplates.DefaultSimpleCharacterPrompt, result);
        }

        [Fact]
        public async Task GetSimpleBatchCharacterPrompt_WhenUnset_ReturnsSimpleDefault()
        {
            var svc = NewService();
            var result = await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple);
            Assert.Equal(PromptTemplates.DefaultSimpleBatchCharacterPrompt, result);
        }

        [Fact]
        public async Task SetSimpleCharacterPrompt_ThenGet_ReturnsStoredValue()
        {
            var svc = NewService();
            await svc.SetSimpleCharacterPromptAsync("custom simple prompt");
            Assert.Equal("custom simple prompt", await svc.GetCharacterPromptAsync(AttributionPromptStyle.Simple));
        }

        [Fact]
        public async Task SetSimpleBatchCharacterPrompt_ThenGet_ReturnsStoredValue()
        {
            var svc = NewService();
            await svc.SetSimpleBatchCharacterPromptAsync("custom simple batch prompt");
            Assert.Equal("custom simple batch prompt", await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple));
        }

        [Fact]
        public async Task ResetSimpleCharacterPrompt_AfterSet_ReturnsDefaultAgain()
        {
            var svc = NewService();
            await svc.SetSimpleCharacterPromptAsync("overridden");
            await svc.ResetSimpleCharacterPromptAsync();
            Assert.Equal(PromptTemplates.DefaultSimpleCharacterPrompt, await svc.GetCharacterPromptAsync(AttributionPromptStyle.Simple));
        }

        [Fact]
        public async Task ResetSimpleBatchCharacterPrompt_AfterSet_ReturnsDefaultAgain()
        {
            var svc = NewService();
            await svc.SetSimpleBatchCharacterPromptAsync("overridden");
            await svc.ResetSimpleBatchCharacterPromptAsync();
            Assert.Equal(PromptTemplates.DefaultSimpleBatchCharacterPrompt, await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple));
        }

        [Fact]
        public async Task SimpleOverride_DoesNotAffectFullPrompts()
        {
            var svc = NewService();
            await svc.SetSimpleCharacterPromptAsync("simple single");
            await svc.SetSimpleBatchCharacterPromptAsync("simple batch");

            Assert.Equal(PromptTemplates.DefaultCharacterPrompt, await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
            Assert.Equal(PromptTemplates.DefaultBatchCharacterPrompt, await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task FullOverride_DoesNotAffectSimplePrompts()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("full single");
            await svc.SetBatchCharacterPromptAsync("full batch");

            Assert.Equal(PromptTemplates.DefaultSimpleCharacterPrompt, await svc.GetCharacterPromptAsync(AttributionPromptStyle.Simple));
            Assert.Equal(PromptTemplates.DefaultSimpleBatchCharacterPrompt, await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Simple));
        }

        [Fact]
        public async Task BatchAndSinglePrompts_StoredIndependently()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("single");
            await svc.SetBatchCharacterPromptAsync("batch");
            Assert.Equal("single", await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
            Assert.Equal("batch", await svc.GetBatchCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task SetThenSet_OverwritesSameRow_NoSecondRow()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("first");
            await svc.SetCharacterPromptAsync("second");

            await using var db = await Factory.CreateDbContextAsync();
            var count = await db.PromptSettings.CountAsync();
            Assert.Equal(1, count);
            Assert.Equal("second", await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task BlankWhitespace_FallsBackToDefault()
        {
            var svc = NewService();
            await svc.SetCharacterPromptAsync("   ");
            Assert.Equal(PromptTemplates.DefaultCharacterPrompt, await svc.GetCharacterPromptAsync(AttributionPromptStyle.Full));
        }

        [Fact]
        public async Task OnChanged_FiresForMutations()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetCharacterPromptAsync("a");     // +1
            await svc.SetVoicePromptAsync("b");         // +1
            await svc.ResetCharacterPromptAsync();      // +1
            await svc.ResetVoicePromptAsync();          // +1

            Assert.Equal(4, count);
        }

        [Fact]
        public async Task GetContextWindow_WhenUnset_ReturnsDefaults()
        {
            var svc = NewService();
            var (before, after) = await svc.GetContextWindowAsync();
            Assert.Equal(PromptTemplates.DefaultContextParagraphsBefore, before);
            Assert.Equal(PromptTemplates.DefaultContextParagraphsAfter, after);
        }

        [Fact]
        public async Task SetContextWindow_ThenGet_ReturnsStoredValues()
        {
            var svc = NewService();
            await svc.SetContextWindowAsync(2, 1);
            var (before, after) = await svc.GetContextWindowAsync();
            Assert.Equal(2, before);
            Assert.Equal(1, after);
        }

        [Fact]
        public async Task SetContextWindowTwice_StillSingleRow()
        {
            var svc = NewService();
            await svc.SetContextWindowAsync(3, 0);
            await svc.SetContextWindowAsync(5, 2);

            await using var db = await Factory.CreateDbContextAsync();
            Assert.Equal(1, await db.PromptSettings.CountAsync());

            var (before, after) = await svc.GetContextWindowAsync();
            Assert.Equal(5, before);
            Assert.Equal(2, after);
        }

        [Fact]
        public async Task SetContextWindow_FiresOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;
            await svc.SetContextWindowAsync(4, 0);
            Assert.Equal(1, count);
        }
    }
}
