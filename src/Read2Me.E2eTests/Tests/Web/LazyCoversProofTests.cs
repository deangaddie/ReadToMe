using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #505: project covers and series posters are fetched and decoded only near
/// the viewport, and their 3:4 box is the same before and after the image loads.
/// </summary>
public class LazyCoversProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    // Title order is shelf order, so "Adventures…" series cards lead and "Waverley…" ones trail.
    private static readonly string[] Titles =
    [
        "Agnes Grey", "Anna Karenina", "Barchester Towers", "Bleak House", "Candide", "Cranford",
        "Daniel Deronda", "David Copperfield", "Dracula", "Emma", "Erewhon", "Evelina", "Frankenstein",
        "Great Expectations", "Hard Times", "Ivanhoe", "Jane Eyre", "Kidnapped", "Kim", "Lady Susan",
        "Little Dorrit", "Little Women", "Lorna Doone", "Mansfield Park", "Middlemarch", "Moby Dick",
        "Nostromo", "Northanger Abbey", "Oliver Twist", "Persuasion", "Pride and Prejudice",
        "Romola", "Ruth", "Sense and Sensibility", "Shirley", "Silas Marner", "The Moonstone",
        "Treasure Island", "Villette", "Vanity Fair",
    ];

    // Needs a real browser: when a lazy image is fetched and decoded is the engine's own decision.
    [Fact]
    public async Task Covers_and_posters_load_only_near_the_viewport()
    {
        // A real 1.08 MP image, the size of a usual EPUB cover.
        await Page.SetViewportSizeAsync(900, 1200);
        await Page.SetContentAsync("<body style='margin:0;height:1200px;background:linear-gradient(135deg,#357,#a53)'></body>");
        var cover = await Page.ScreenshotAsync();

        var folders = new List<string>();
        foreach (var title in Titles)
        {
            var folder = title.ToLowerInvariant().Replace(' ', '-');
            folders.Add(folder);
            await App.SeedProjectAsync(folder, title, "Gutenberg");
            await PutImageAsync($"/api/projects/{folder}/cover", cover);
        }

        var topPoster = await SeriesWithPosterAsync("Adventures of Holmes", folders[0], cover);
        var bottomPoster = await SeriesWithPosterAsync("Waverley Novels", folders[1], cover);

        var requests = new ConcurrentQueue<string>();
        Page.Request += (_, request) => requests.Enqueue(request.Url);
        bool Requested(string part) => requests.Any(url => url.Contains(part, StringComparison.Ordinal));
        int CoversRequested() => folders.Count(f => Requested($"/workspace/{f}/"));

        await Page.SetViewportSizeAsync(700, 700);
        await GotoAppAsync("projects");

        // Books view.
        // Only cards with a cover: the host's own wwwroot, inside its temp Workspace, lists as one more.
        var cards = Page.Locator(".projects__grid .r2m-project-card:has(.r2m-project-card__image)");
        await Expect(cards).ToHaveCountAsync(Titles.Length);
        await FirstRowLoadsAsync(".projects__grid > .r2m-project-card", ".r2m-project-card__image");
        await Page.WaitForTimeoutAsync(2000); // the negative below needs the engine to have settled

        var last = cards.Last;
        var lastFolder = (await last.GetAttributeAsync("data-folder"))!;
        var before = CoversRequested();
        await ProveLazyAsync("project cover", last, last.Locator(".r2m-project-card__image"),
            last.Locator(".r2m-project-card__cover"), () => Requested($"/workspace/{lastFolder}/"));
        TestContext.Current.TestOutputHelper!.WriteLine($"[{Page.Context.Browser!.BrowserType.Name}] covers requested at rest: {before} of {Titles.Length}");

        // Series view: one poster in the first row, one far below the fold.
        await GotoAppAsync("projects");
        requests.Clear();
        await Page.Locator(".projects__view label", new() { HasText = "Series" }).ClickAsync();
        var grid = Page.Locator(".projects__grid--series");
        await Expect(grid.Locator(".projects__series-card")).ToHaveCountAsync(2);
        await FirstRowLoadsAsync(".projects__grid--series > *", "img");
        await Page.WaitForTimeoutAsync(2000);

        Assert.True(Requested($"/api/series/{topPoster}/poster"), "the first row's poster should be fetched");
        var bottom = grid.Locator($".projects__series-card[data-series-id='{bottomPoster}']");
        await ProveLazyAsync("series poster", bottom, bottom.Locator(".projects__poster-image"),
            bottom.Locator(".projects__poster"), () => Requested($"/api/series/{bottomPoster}/poster"));
    }

    /// <summary>Every card sharing the first card's row has a decoded image, with no scroll.</summary>
    private async Task FirstRowLoadsAsync(string cardSelector, string imageSelector)
    {
        await Page.WaitForFunctionAsync(
            """
            ([cardSelector, imageSelector]) => {
              const cards = Array.from(document.querySelectorAll(cardSelector));
              if (!cards.length) return false;
              const row = cards.filter((c) => c.offsetTop === cards[0].offsetTop);
              return row.length > 1 && row.every((c) => c.querySelector(imageSelector)?.naturalWidth > 0);
            }
            """,
            new[] { cardSelector, imageSelector });
    }

    private async Task ProveLazyAsync(
        string what, ILocator card, ILocator image, ILocator box, Func<bool> requested)
    {
        const string NaturalWidth = "img => img.naturalWidth";
        const string Geometry = "el => [el.offsetTop, el.offsetHeight]";

        Assert.False(requested(), $"{what}: requested while far below the fold");
        Assert.Equal(0, await image.EvaluateAsync<int>(NaturalWidth));
        var cardBefore = await card.EvaluateAsync<int[]>(Geometry);
        var boxBefore = (await box.BoundingBoxAsync())!;
        var viewport = Page.ViewportSize!.Height;
        Assert.True(boxBefore.Y > viewport + 2500, $"{what}: only {boxBefore.Y}px down, not far below the fold");
        Assert.InRange(boxBefore.Height / boxBefore.Width, 1.32f, 1.35f);

        await card.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("img => img.complete && img.naturalWidth > 0", await image.ElementHandleAsync());

        Assert.True(requested(), $"{what}: loaded with no request seen");
        var cardAfter = await card.EvaluateAsync<int[]>(Geometry);
        var boxAfter = (await box.BoundingBoxAsync())!;
        Assert.Equal(cardBefore, cardAfter);
        // Firefox's viewport-relative height carries float noise from the scroll offset.
        Assert.Equal(boxBefore.Height, boxAfter.Height, tolerance: 0.01f);
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"[{Page.Context.Browser!.BrowserType.Name}] {what}: {boxBefore.Y:0}px down, not requested, naturalWidth 0; " +
            $"after scroll naturalWidth {await image.EvaluateAsync<int>(NaturalWidth)}; " +
            $"card top/height {cardBefore[0]}/{cardBefore[1]} -> {cardAfter[0]}/{cardAfter[1]}; " +
            $"box {boxBefore.Width:0.#}x{boxBefore.Height:0.#} -> {boxAfter.Width:0.#}x{boxAfter.Height:0.#}");
    }

    private async Task<Guid> SeriesWithPosterAsync(string name, string folder, byte[] poster)
    {
        var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series", new { name, folder });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        await PutImageAsync($"/api/series/{id}/poster", poster);
        return id;
    }

    private async Task PutImageAsync(string path, byte[] bytes)
    {
        var image = new ByteArrayContent(bytes);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { image, "file", "cover.png" } };
        var response = await Http.PutAsync($"{App.BaseUrl}{path}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record IdResponse(Guid Id);
}
