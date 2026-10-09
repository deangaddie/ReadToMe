using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native cast page's narrator link (native-web 30, from the Angular class of the same name):
/// the banner's picker links a character, the seed row and the audio-mode voice preview read the
/// link back, and Unlink (confirmed) restores the unlinked strings. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class NarratorLinkTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Folder = "web-narrator-link";

    protected override WebApp WebApp => WebApp.Native;

    private ILocator Banner => Page.Locator("r2m-narrator-banner");
    private ILocator NarratorRow => Page.Locator(".cast__row", new() { HasText = "Narrator" });

    [Fact]
    public async Task Link_labels_the_narrator_row_and_the_voice_preview_until_it_is_unlinked()
    {
        var builder = await App.SeedProjectAsync(Folder, "Web Narrator Link", "A. Author", characterName: "Dr. Watson");
        await App.SeedEditableVoiceAsync(Folder, builder.CharacterId("Dr. Watson"), "Watson Voice");
        await App.SeedNarratorVoiceAsync(Folder);

        await GotoAppAsync($"projects/{Folder}/cast");
        await Expect(Page.Locator("r2m-cast-page")).ToBeVisibleAsync();
        await Expect(Banner).ToContainTextAsync("First-person book? Say who tells it");
        await Expect(NarratorRow).ToHaveCountAsync(1);
        await Expect(NarratorRow).Not.ToContainTextAsync("→");

        // Link from the banner's picker (a native select), once the roster has loaded and enabled it.
        var picker = Banner.GetByRole(AriaRole.Combobox);
        await Expect(picker).ToBeEnabledAsync();
        await picker.SelectOptionAsync(new SelectOptionValue { Label = "Dr. Watson" });
        await Expect(Banner).ToContainTextAsync("Narrated by Dr. Watson");
        await Expect(NarratorRow).ToContainTextAsync("Narrator → Dr. Watson");

        // The reader's audio mode reads the same link back on the narration line.
        await Page.GotoAsync(AppPath($"projects/{Folder}/book?mode=audio"));
        var preview = Page.Locator("[data-testid='voice-line']").First;
        await Expect(preview).ToContainTextAsync("Narrator → Dr. Watson");
        await Expect(preview).ToContainTextAsync("Watson Voice");

        // Unlink, warning confirmed; the Narrator's own voice is still there.
        await Page.GotoAsync(AppPath($"projects/{Folder}/cast"));
        await Banner.Locator(".narrator-banner__unlink").ClickAsync();
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("Unlink Dr. Watson as this book's narrator?");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();

        await Expect(Banner).ToContainTextAsync("First-person book? Say who tells it");
        await Expect(NarratorRow).ToHaveCountAsync(1);
        await Expect(NarratorRow).Not.ToContainTextAsync("→");

        var narrator = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/{Folder}/characters/summary");
        Assert.Contains("\"narratesBook\":false", await narrator.TextAsync());
    }
}
