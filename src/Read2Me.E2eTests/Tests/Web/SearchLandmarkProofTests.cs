using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #483: each filter bar is one named search landmark, laid out as before.</summary>
public class SearchLandmarkProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static string Engine => Environment.GetEnvironmentVariable("R2M_E2E_BROWSER") ?? "chromium";

    private async Task<ILocator> OneSearchAsync(string name)
    {
        await Expect(Page.GetByRole(AriaRole.Search)).ToHaveCountAsync(1);
        var landmark = Page.GetByRole(AriaRole.Search, new() { Name = name });
        await Expect(landmark).ToBeVisibleAsync();
        await Expect(landmark.Locator("input[type=search]")).ToBeVisibleAsync();
        return landmark;
    }

    private static async Task SameRowAsync(ILocator a, ILocator b)
    {
        var ba = (await a.BoundingBoxAsync())!;
        var bb = (await b.BoundingBoxAsync())!;
        Assert.True(Math.Abs(ba.Y + ba.Height - (bb.Y + bb.Height)) < 2, $"bottoms {ba.Y + ba.Height} vs {bb.Y + bb.Height}");
        Assert.True(ba.X + ba.Width <= bb.X + 1, $"{ba.X}+{ba.Width} overlaps {bb.X}");
    }

    [Fact]
    public async Task The_cast_filter_bar_is_a_search_landmark_holding_search_and_sort()
    {
        await App.SeedThreeDialogParagraphProjectAsync("search-cast", "Search Cast", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/search-cast/cast");
        var landmark = await OneSearchAsync("Filter characters");
        var sort = landmark.GetByRole(AriaRole.Combobox, new() { Name = "Sort" });
        await Expect(sort).ToBeVisibleAsync();
        await SameRowAsync(landmark.Locator("input[type=search]"), sort);
        Assert.Equal("flex", await landmark.EvaluateAsync<string>("e => getComputedStyle(e).display"));
        await landmark.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-483-cast-{Engine}.png") });
    }

    [Fact]
    public async Task The_pronunciations_search_is_a_search_landmark_beside_Add()
    {
        await App.SeedProjectAsync("search-pron", "Search Pron", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/search-pron/pronunciations");
        var landmark = await OneSearchAsync("Filter pronunciations");
        var add = Page.Locator(".pron__toolbar [data-action=add]");
        await Expect(landmark.Locator("[data-action=add]")).ToHaveCountAsync(0);
        await SameRowAsync(add, landmark);
        var box = (await landmark.BoundingBoxAsync())!;
        var input = (await landmark.Locator("input[type=search]").BoundingBoxAsync())!;
        Assert.True(Math.Abs(box.Width - input.Width) < 2, $"search {box.Width} vs input {input.Width}");
        await Page.Locator(".pron__toolbar").ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-483-pron-{Engine}.png") });
    }

    [Fact]
    public async Task The_LLM_config_list_search_is_a_search_landmark_beside_New()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("settings/llm");
        var landmark = await OneSearchAsync("Filter configurations");
        var head = Page.Locator(".r2m-config-list__head");
        var newButton = head.GetByRole(AriaRole.Button, new() { Name = "New" });
        await SameRowAsync(landmark, newButton);
        var headBox = (await head.BoundingBoxAsync())!;
        var newBox = (await newButton.BoundingBoxAsync())!;
        Assert.True(newBox.X + newBox.Width <= headBox.X + headBox.Width + 1, "New spills out of the list head");
        await head.ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"r2m-483-llm-{Engine}.png") });
    }
}
