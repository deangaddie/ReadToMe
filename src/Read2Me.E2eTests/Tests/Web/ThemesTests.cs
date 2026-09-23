using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The themes page (Angular ticket 04): applying a built-in theme restyles the document at once
/// (the <c>data-theme</c> scheme on the root), the choice survives a reload because the host stores
/// it, and it is the same selection Blazor reads.
/// </summary>
[Collection(E2eCollection.Name)]
public class ThemesTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    [Fact]
    public async Task Apply_restyles_immediately_persists_and_is_shared_with_the_host()
    {
        try
        {
            await GotoAppAsync("/app/settings/themes");
            var html = Page.Locator("html");
            await Expect(html).ToHaveAttributeAsync("data-theme", "light");

            var ocean = Page.Locator(".themes__card", new() { HasText = "Ocean" });
            await Expect(ocean.Locator(".themes__lock")).ToBeVisibleAsync();
            await ocean.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync();

            await Expect(html).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(ocean.Locator("r2m-status-chip", new() { HasText = "Active" })).ToBeVisibleAsync();
            await Expect(ocean.GetByRole(AriaRole.Button, new() { Name = "Applied" })).ToBeDisabledAsync();

            await Page.ReloadAsync();
            await Expect(html).ToHaveAttributeAsync("data-theme", "dark");

            var selection = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/settings/themes/selection");
            var oceanId = await ocean.GetAttributeAsync("data-theme-id");
            Assert.Contains($"\"selectedThemeId\":{oceanId}", await selection.TextAsync());
        }
        finally
        {
            // The selection is host state shared by every test in the collection: put Light back.
            var themes = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/settings/themes");
            using var doc = JsonDocument.Parse(await themes.TextAsync());
            var light = doc.RootElement.EnumerateArray().First(t => t.GetProperty("name").GetString() == "Light").GetProperty("id").GetInt32();
            await Page.APIRequest.PutAsync($"{App.BaseUrl}/api/settings/themes/selection", new()
            {
                DataObject = new { selectedThemeId = light, followSystemPreference = false },
            });
        }
    }
}
