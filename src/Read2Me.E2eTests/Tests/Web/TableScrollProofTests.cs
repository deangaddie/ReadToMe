using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #498: scrolling tables are named, keyboard-scrollable regions. Deleted in
/// the PR's final commit; the source and both engines' pass output go in the PR body.
/// </summary>
public class TableScrollProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task The_series_cast_board_is_a_named_region_the_arrow_keys_scroll()
    {
        // Eight books, one column each, overflow a 700 px page.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var folders = Enumerable.Range(1, 8).Select(i => $"holmes{i}-{tag}").ToArray();
        foreach (var (folder, i) in folders.Select((f, i) => (f, i)))
            await App.SeedMultiChapterProjectAsync(folder, $"The Adventures of Sherlock Holmes part {i + 1}", "Arthur Conan Doyle", chapters: 1, characterName: "Watson");
        var name = $"Sherlock Holmes {tag}";
        var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series", new { name, folder = folders[0] });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var series = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        foreach (var folder in folders.Skip(1))
            Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{App.BaseUrl}/api/series/{series}/books/{folder}", new { })).StatusCode);

        await Page.SetViewportSizeAsync(700, 900);
        await GotoAppAsync($"projects?series={series}");
        var region = Page.GetByRole(AriaRole.Region, new() { Name = $"{name}: series cast" });
        await Expect(region).ToBeVisibleAsync();
        await Expect(region.GetByRole(AriaRole.Table, new() { Name = $"{name}: series cast" })).ToBeVisibleAsync();
        Assert.True(await region.EvaluateAsync<bool>("r => r.scrollWidth > r.clientWidth"), "the board should overflow its region");

        // Tab from the Characters tab until the region takes focus: it comes before any cell control.
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Characters" }).FocusAsync();
        for (var i = 0; i < 10 && !await region.EvaluateAsync<bool>("r => document.activeElement === r"); i++)
            await Page.Keyboard.PressAsync("Tab");
        await Expect(region).ToBeFocusedAsync();
        for (var i = 0; i < 4; i++)
            await Page.Keyboard.PressAsync("ArrowRight");
        await Page.WaitForFunctionAsync("r => r.scrollLeft > 0", await region.ElementHandleAsync());
        Assert.True(await region.EvaluateAsync<double>("r => r.scrollLeft") > 0);
    }

    [Fact]
    public async Task The_Setup_check_table_is_a_named_region_and_scrolls_in_it_not_the_page()
    {
        (await Http.PostAsync($"{App.BaseUrl}/api/setup-check/quick", null)).EnsureSuccessStatusCode();
        await Page.SetViewportSizeAsync(700, 900);
        await GotoAppAsync("settings/setup-check");

        var region = Page.GetByRole(AriaRole.Region, new() { Name = "Setup checks" });
        var table = region.GetByRole(AriaRole.Table, new() { Name = "Setup checks" });
        await Expect(table).ToBeVisibleAsync();
        Assert.Equal("table", await table.EvaluateAsync<string>("t => getComputedStyle(t).display"));
        Assert.Equal("auto", await region.EvaluateAsync<string>("r => getComputedStyle(r).overflowX"));
        await Page.SetViewportSizeAsync(360, 900);
        await Expect(table).ToBeVisibleAsync();
        Assert.True(await region.EvaluateAsync<bool>("r => r.scrollWidth > r.clientWidth"), "the table should overflow its region at 360 px: " + await region.EvaluateAsync<string>("r => r.scrollWidth + '/' + r.clientWidth"));
        var regionBox = (await region.BoundingBoxAsync())!;
        Assert.True(regionBox.X + regionBox.Width <= 360, $"the region (right={regionBox.X + regionBox.Width}) should fit the page");
        Assert.False(await Page.EvaluateAsync<bool>("() => document.scrollingElement.scrollWidth > innerWidth"), "the page should not scroll sideways");
    }

    [Fact]
    public async Task The_Pronunciations_and_series_Voices_tables_are_named()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (scarlet, four) = ($"scarlet-{tag}", $"four-{tag}");
        var scarletBook = await App.SeedMultiChapterProjectAsync(scarlet, "A Study in Scarlet", "Arthur Conan Doyle", chapters: 1, characterName: "Watson");
        await App.SeedMultiChapterProjectAsync(four, "The Sign of the Four", "Arthur Conan Doyle", chapters: 1, characterName: "Watson");
        var watson = scarletBook.CharacterId("Watson");
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{scarlet}/commands",
            new { type = "CreateVoice", characterId = watson, name = "Army doctor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{scarlet}/pronunciations",
            new { writtenForm = "Watson", spokenForm = "WAT-sun" })).StatusCode);
        var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series", new { name = $"Holmes {tag}", folder = scarlet });
        var series = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{App.BaseUrl}/api/series/{series}/books/{four}", new { })).StatusCode);
        var shared = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series/{series}/characters", new { folder = scarlet, characterId = watson });
        var seriesWatson = (await shared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seriesCharacterId").GetGuid();

        await GotoAppAsync($"projects/{scarlet}/pronunciations");
        await Expect(Page.GetByRole(AriaRole.Table, new() { Name = "Pronunciations" })).ToBeVisibleAsync();

        await Page.SetViewportSizeAsync(1200, 1000);
        await GotoAppAsync($"projects?series={series}");
        await Page.Locator($"r2m-series-cast-board tr[data-series-character=\"{seriesWatson}\"] [data-action=voices]").ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Table, new() { Name = "Watson: series Voices" })).ToBeVisibleAsync();
    }
}
