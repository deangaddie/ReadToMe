#pragma warning disable BL0005 // Component parameters set directly to exercise public UI callbacks.
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using NSubstitute;
using Read2Me.App.Shared;
using Read2Me.App.Shared.Characters;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Xunit;

namespace Read2Me.Tests.App.Characters;

public sealed class NarratorLinkUiTests
{
    private static readonly Guid WatsonId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Banner_Unlinked_InvitesAndOffersOnlyNonNarratorCharacters()
    {
        var html = await RenderAsync<NarratorLinkBanner>(new Dictionary<string, object?>
        {
            [nameof(NarratorLinkBanner.Narrator)] = NarratorIdentity.Unlinked,
            [nameof(NarratorLinkBanner.Characters)] = Characters(),
        });

        Assert.Contains("Narrated by its own Narrator voice", html);
        Assert.Contains("First-person book? Say who tells it", html);
        Assert.Contains("Narrated by", html);
        var choices = NarratorLinkBanner.EligibleCharacters(Characters()).ToList();
        Assert.Collection(choices, character => Assert.Equal("Dr. Watson", character.Name));
    }

    [Theory]
    [InlineData("watson.wav", "1 ready voice", "false", "mud-chip-color-info")]
    [InlineData(null, "0 ready voices", "true", "mud-chip-color-warning")]
    public async Task Banner_Linked_ShowsVoiceCountAndActions(
        string? audioFileName, string expectedCount, string warning, string expectedColorClass)
    {
        var characters = Characters(audioFileName);
        var html = await RenderAsync<NarratorLinkBanner>(new Dictionary<string, object?>
        {
            [nameof(NarratorLinkBanner.Narrator)] = new NarratorIdentity(WatsonId, "Dr. Watson", true),
            [nameof(NarratorLinkBanner.Characters)] = characters,
        });

        Assert.Contains("Narrated by", html);
        Assert.Contains("Dr. Watson", html);
        Assert.Contains(expectedCount, html);
        Assert.Contains($"data-warning=\"{warning}\"", html);
        Assert.Contains(expectedColorClass, html);
        Assert.Contains("Change", html);
        Assert.Contains("Unlink", html);
    }

    [Fact]
    public async Task CharacterRows_ExplainLinkedNarratorAndMarkLinkedCharacter()
    {
        var narrator = new NarratorIdentity(WatsonId, "Dr. Watson", true);
        var seedHtml = await RenderAsync<CharacterListRowContent>(new Dictionary<string, object?>
        {
            [nameof(CharacterListRowContent.Character)] = Characters()[0],
            [nameof(CharacterListRowContent.Narrator)] = narrator,
        });
        var watsonHtml = await RenderAsync<CharacterListRowContent>(new Dictionary<string, object?>
        {
            [nameof(CharacterListRowContent.Character)] = Characters()[1],
            [nameof(CharacterListRowContent.Narrator)] = narrator,
        });
        var plainHtml = await RenderAsync<CharacterListRowContent>(new Dictionary<string, object?>
        {
            [nameof(CharacterListRowContent.Character)] = Characters()[0],
            [nameof(CharacterListRowContent.Narrator)] = NarratorIdentity.Unlinked,
        });

        Assert.Contains("Narrator → Dr. Watson", System.Net.WebUtility.HtmlDecode(seedHtml));
        Assert.Contains("data-book-icon=\"true\"", seedHtml);
        Assert.Contains("data-book-icon=\"true\"", watsonHtml);
        Assert.DoesNotContain("→", plainHtml);
        Assert.Contains(">Narrator<", plainHtml);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task Signpost_ShowsJumpAndOnlyShowsUnusedVoicesWhenPresent(int voiceCount, bool expectExpander)
    {
        var voices = Enumerable.Range(1, voiceCount)
            .Select(i => new Voice { Id = Guid.NewGuid(), Name = $"Narrator voice {i}" })
            .ToList();
        var html = await RenderAsync<NarratorSignpost>(new Dictionary<string, object?>
        {
            [nameof(NarratorSignpost.Narrator)] = new NarratorIdentity(WatsonId, "Dr. Watson", true),
            [nameof(NarratorSignpost.UnusedVoices)] = voices,
        });

        Assert.Contains("Narrator 🔗 Dr. Watson", html);
        Assert.Contains("Narration in this book is spoken by Dr. Watson. Voices and voice rules are edited on Dr. Watson.", html);
        Assert.Contains("Go to Dr. Watson", html);
        Assert.Equal(expectExpander, html.Contains($"{voiceCount} unused narrator voice"));
        Assert.DoesNotContain("Add voice", html);
        Assert.DoesNotContain("Rename", html);
        Assert.DoesNotContain("Delete", html);
    }

    [Fact]
    public async Task Banner_ChangeSelectionAndConfirmedUnlinkRaiseTheSingleWriteCallback()
    {
        var writes = new List<Guid?>();
        var banner = new NarratorLinkBanner
        {
            NarratorChanged = EventCallback.Factory.Create<Guid?>(this, writes.Add),
        };

        banner.BeginChange();
        Assert.True(banner.IsChanging);

        await banner.SetAsync(WatsonId);
        Assert.False(banner.IsChanging);
        Assert.Equal(WatsonId, Assert.Single(writes));

        await banner.UnlinkCoreAsync(() => Task.FromResult(false));
        Assert.Single(writes);

        await banner.UnlinkCoreAsync(() => Task.FromResult(true));
        Assert.Equal(new Guid?[] { WatsonId, null }, writes);
    }

    [Fact]
    public void UnlinkDialog_WarnsThatRenderedNarrationWillNotMatch()
    {
        var warning = UnlinkNarratorDialog.Warning("Dr. Watson");

        Assert.Contains("Narration already rendered in Dr. Watson's voice will not match until it is regenerated.",
            warning);
        Assert.DoesNotContain("checkbox", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteConfirmation_AddsNarratorWarningOnlyForLinkedCharacter()
    {
        var narrator = new NarratorIdentity(WatsonId, "Dr. Watson", true);
        var linkedMessage = CharacterDetailPanel.LinkedNarratorDeleteMessage(Characters()[1], narrator);
        var otherMessage = CharacterDetailPanel.LinkedNarratorDeleteMessage(
            new Character { Id = Guid.NewGuid(), Name = "Sherlock Holmes" }, narrator);

        Assert.Equal("Dr. Watson narrates this book; deleting will return narration to the Narrator voice.", linkedMessage);
        Assert.Null(otherMessage);
    }

    private static List<Character> Characters(string? watsonAudio = null) =>
    [
        new() { Id = ProjectDbContext.NarratorId, Name = "Narrator", IsNarrator = true },
        new()
        {
            Id = WatsonId,
            Name = "Dr. Watson",
            Voices = [new Voice { Id = Guid.NewGuid(), Name = "Watson", AudioFileName = watsonAudio }],
        },
    ];

    private static async Task<string> RenderAsync<TComponent>(
        IDictionary<string, object?>? parameters = null)
        where TComponent : IComponent
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = ParameterView.FromDictionary(parameters ?? new Dictionary<string, object?>());
            var output = await renderer.RenderComponentAsync<TComponent>(view);
            return output.ToHtmlString();
        });
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/project/test-book");

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            Uri = ToAbsoluteUri(uri).ToString();
    }
}
