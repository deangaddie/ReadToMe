using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using Read2Me.App.Pages;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App;

public class LlmPromptsPageTests : AppDbTestBase
{
    [Theory]
    [InlineData("legacy custom prompt", true)]
    [InlineData("custom {{narrator_identity}} prompt", false)]
    public async Task StoredAttributionOverride_RendersCompatibilityBadgeOnlyWhenTokenMissing(
        string storedPrompt, bool expectBadge)
    {
        var promptService = new LlmPromptService(Factory, NullLogger<LlmPromptService>.Instance);
        await promptService.SetCharacterPromptAsync(storedPrompt);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton(promptService);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<LlmPrompts>();
            return output.ToHtmlString();
        });

        const string badge = "Stored override missing {{narrator_identity}}";
        if (expectBadge)
            Assert.Contains(badge, html);
        else
            Assert.DoesNotContain(badge, html);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() =>
            Initialize("http://localhost/", "http://localhost/llm-prompts");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
    }
}
