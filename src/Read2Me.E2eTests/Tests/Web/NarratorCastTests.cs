using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// What the cast page says about a linked narrator, beyond the link itself (which
/// <see cref="NarratorLinkTests"/> drives): the banner offers only real characters and counts the
/// linked one's ready voices, the Narrator row turns into a signpost that keeps the seed Narrator's
/// voices listed as unused and jumps to the linked character, and deleting that character warns
/// that narration goes back to the Narrator voice — a warning no other character's delete carries.
/// </summary>
[Collection(E2eCollection.Name)]
public class NarratorCastTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Folder = "web-narrator-cast";

    private ILocator Banner => Page.Locator("app-narrator-banner");
    private ILocator NarratorRow => Page.Locator(".cast__row", new() { HasText = "Narrator" });
    private ILocator Confirm => Page.Locator("r2m-confirm-dialog");

    [Fact]
    public async Task Linked_narrator_signpost_jump_and_delete_warning()
    {
        var book = await App.SeedProjectAsync(Folder, "Web Narrator Cast", "A. Author", characterName: "Dr. Watson");
        var watsonId = book.CharacterId("Dr. Watson");
        await App.SeedEditableVoiceAsync(Folder, watsonId, "Watson Voice");
        await App.SeedNarratorVoiceAsync(Folder);
        var lestrade = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/{Folder}/commands",
            new() { DataObject = new { type = "CreateCharacter", name = "Lestrade" } });
        Assert.True(lestrade.Ok);

        await GotoAppAsync($"/app/projects/{Folder}/cast");

        // The picker offers the characters, never the Narrator itself.
        await Banner.Locator("mat-select").ClickAsync(new() { Force = true });
        var options = Page.Locator("mat-option");
        await Expect(options).ToHaveTextAsync(["Dr. Watson", "Lestrade"]);
        await options.Filter(new() { HasText = "Dr. Watson" }).ClickAsync();

        // Linked: the banner counts the linked character's ready voices.
        await Expect(Banner).ToContainTextAsync("Narrated by Dr. Watson");
        await Expect(Banner).ToContainTextAsync("1 ready voice");

        // The Narrator row is a signpost now: where narration is edited, and its own voice kept unused.
        await NarratorRow.ClickAsync();
        var signpost = Page.Locator("[data-testid='narrator-signpost']");
        await Expect(signpost).ToContainTextAsync("Narrator → Dr. Watson");
        await Expect(signpost).ToContainTextAsync("1 unused narrator voice");
        await Expect(signpost).ToContainTextAsync("Narrator Voice");

        await signpost.GetByRole(AriaRole.Button, new() { Name = "Go to Dr. Watson" }).ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/cast/{watsonId}$"));
        var detail = Page.Locator("app-character-detail");
        await Expect(detail).ToContainTextAsync("Narrates this book");

        // Deleting the narrating character says what happens to narration; declined, nothing changes.
        await detail.Locator("[data-action='delete']").ClickAsync();
        await Expect(Confirm).ToContainTextAsync(
            "Dr. Watson narrates this book; deleting will return narration to the Narrator voice.");
        await Confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
        await Expect(Confirm).ToHaveCountAsync(0);
        await Expect(Banner).ToContainTextAsync("Narrated by Dr. Watson");

        // Any other character's delete carries no such note.
        await Page.Locator(".cast__row", new() { HasText = "Lestrade" }).ClickAsync();
        await Expect(Page.Locator(".cast__row--selected")).ToContainTextAsync("Lestrade");
        await detail.Locator("[data-action='delete']").ClickAsync();
        await Expect(Confirm).ToContainTextAsync("Delete Lestrade?");
        await Expect(Confirm).Not.ToContainTextAsync("narrates this book");
        await Confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
    }
}
