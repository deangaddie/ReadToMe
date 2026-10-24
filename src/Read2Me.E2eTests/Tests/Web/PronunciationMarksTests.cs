using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Book view's Pronunciation marks (pronunciation ticket 15) where only a browser can tell:
/// the hover card is a top-layer popover drawn over the reader beside its mark, and a list change
/// made elsewhere reaches an open Book view's marks over the real hub. The seed's items read
/// "It was a dark and stormy night.", "“Hello there,” she said." and "The rain kept falling."
/// </summary>
public class PronunciationMarksTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private async Task AddAsync(string folder, string written, string spoken)
    {
        var added = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{folder}/pronunciations",
            new { writtenForm = written, spokenForm = spoken });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
    }

    [Fact]
    public async Task Hovering_a_mark_shows_its_card_in_the_top_layer_over_the_reader()
    {
        await App.SeedProjectAsync("pron-marks-card", "Marks Book", "A. Author");
        await AddAsync("pron-marks-card", "stormy", "STORM-ee");

        await GotoAppAsync("projects/pron-marks-card/book?mode=speakers");
        var mark = Page.Locator(".r2m-pron-mark", new() { HasText = "stormy" });
        await Expect(mark).ToBeVisibleAsync();
        await Expect(mark.Locator("xpath=ancestor::*[contains(@class,'r2m-item__text')]"))
            .ToHaveTextAsync("It was a dark and stormy night.");

        await mark.HoverAsync();
        var card = Page.Locator(".pron-card__panel");
        await Expect(card).ToBeVisibleAsync();
        await Expect(card).ToContainTextAsync("STORM-ee");
        Assert.True(await card.EvaluateAsync<bool>("el => el.matches(':popover-open')"));

        // Anchored under the mark, and on top: what the pointer finds at the card's centre is the card.
        var markBox = (await mark.BoundingBoxAsync())!;
        var cardBox = (await card.BoundingBoxAsync())!;
        Assert.InRange(cardBox.Y, markBox.Y + markBox.Height - 1, markBox.Y + markBox.Height + 24);
        var onTop = await Page.EvaluateAsync<bool>(
            "p => !!document.elementFromPoint(p.x, p.y)?.closest('.pron-card__panel')",
            new { x = (double)(cardBox.X + cardBox.Width / 2), y = (double)(cardBox.Y + cardBox.Height / 2) });
        Assert.True(onTop);

        // The pointer can travel onto the card without it closing; leaving both closes it.
        await card.Locator("[data-action=edit-pronunciation]").HoverAsync();
        await Expect(card).ToBeVisibleAsync();
        await Page.Mouse.MoveAsync(0, 0);
        await Expect(card).ToBeHiddenAsync();
    }

    [Fact]
    public async Task A_pronunciation_saved_elsewhere_marks_an_open_Book_view()
    {
        await App.SeedProjectAsync("pron-marks-live", "Marks Book", "A. Author");

        await GotoAppAsync("projects/pron-marks-live/book");
        await Expect(Page.Locator(".r2m-paragraph__prose").First).ToContainTextAsync("stormy");
        await Expect(Page.Locator(".r2m-pron-mark")).ToHaveCountAsync(0);

        await AddAsync("pron-marks-live", "rain", "RAYN");

        await Expect(Page.Locator(".r2m-pron-mark")).ToHaveTextAsync(["rain"], new() { Timeout = 5_000 });
    }
}
