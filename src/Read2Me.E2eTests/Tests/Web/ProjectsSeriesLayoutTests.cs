using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Projects page's Series view (#218, variant D of #207): clicking a series card opens its
/// panel full width directly after the last card in the clicked card's row, so the sort order of
/// the grid never breaks. Layout only: the merge, sort, panel index and every flow are spec-tested.
/// </summary>
public class ProjectsSeriesLayoutTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task An_open_series_panel_spans_the_grid_after_the_clicked_cards_row()
    {
        await App.SeedProjectAsync("scarlet", "A Study in Scarlet", "Arthur Conan Doyle");
        await App.SeedProjectAsync("four", "The Sign of the Four", "Arthur Conan Doyle");
        // Loose books after the series card by name, so its row holds more than the series card.
        foreach (var (folder, title) in new[]
                 {
                     ("dracula", "Dracula"), ("emma", "Emma"), ("frankenstein", "Frankenstein"),
                     ("persuasion", "Persuasion"), ("time-machine", "The Time Machine"),
                     ("treasure-island", "Treasure Island"), ("wuthering", "Wuthering Heights"),
                 })
            await App.SeedProjectAsync(folder, title, "Gutenberg");
        var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series", new { name = "Adventures of Holmes", folder = "scarlet" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>())!.Id;
        Assert.Equal(HttpStatusCode.OK,
            (await Http.PutAsJsonAsync($"{App.BaseUrl}/api/series/{id}/books/four", new { })).StatusCode);

        await Page.SetViewportSizeAsync(1100, 900);
        await GotoAppAsync("projects");
        await Page.Locator(".projects__view label", new() { HasText = "Series" }).ClickAsync();
        var grid = Page.Locator(".projects__grid--series");
        var seriesCard = grid.Locator(".projects__series-card");
        await Expect(seriesCard).ToBeVisibleAsync();

        await seriesCard.Locator(".projects__series-open").ClickAsync();
        var panel = grid.Locator(".projects__panel");
        await Expect(panel.Locator(".r2m-project-card")).ToHaveCountAsync(2);

        var gridBox = (await grid.BoundingBoxAsync())!;
        var cardBox = (await seriesCard.BoundingBoxAsync())!;
        var panelBox = (await panel.BoundingBoxAsync())!;
        // Full width of the grid, below the clicked card's row.
        Assert.True(Math.Abs(panelBox.X - gridBox.X) < 2 && Math.Abs(panelBox.Width - gridBox.Width) < 2,
            $"panel (x={panelBox.X}, w={panelBox.Width}) should span the grid (x={gridBox.X}, w={gridBox.Width})");
        Assert.True(panelBox.Y >= cardBox.Y + cardBox.Height,
            $"panel (y={panelBox.Y}) should sit below the clicked card (bottom={cardBox.Y + cardBox.Height})");

        // The card just before it ends the clicked card's row, and is not the clicked card itself;
        // the card just after it starts the next row, below the panel.
        var json = await panel.EvaluateAsync<JsonElement>(
            """
            panel => {
              const box = (el) => { const r = el.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width }; };
              const prev = panel.previousElementSibling, next = panel.nextElementSibling;
              return { previousIsClicked: prev.classList.contains('projects__series-card'), previous: box(prev), next: box(next) };
            }
            """);
        Box BoxOf(string name) => new(
            json.GetProperty(name).GetProperty("x").GetDouble(),
            json.GetProperty(name).GetProperty("y").GetDouble(),
            json.GetProperty(name).GetProperty("width").GetDouble());
        var siblings = new Siblings(json.GetProperty("previousIsClicked").GetBoolean(), BoxOf("previous"), BoxOf("next"));
        Assert.False(siblings.PreviousIsClicked, "the panel should follow the row's last card, not the clicked card");
        Assert.True(Math.Abs(siblings.Previous.Y - cardBox.Y) < 2,
            $"the card before the panel (y={siblings.Previous.Y}) should share the clicked card's row (y={cardBox.Y})");
        Assert.True(siblings.Previous.X + 2 * siblings.Previous.Width > gridBox.X + gridBox.Width,
            "the card before the panel should be the last in its row");
        Assert.True(siblings.Next.Y >= panelBox.Y + panelBox.Height,
            $"the card after the panel (y={siblings.Next.Y}) should start a row below it (bottom={panelBox.Y + panelBox.Height})");
        Assert.True(Math.Abs(siblings.Next.X - gridBox.X) < 2, "the card after the panel should start its row");
    }

    private sealed record IdResponse(Guid Id);

    private sealed record Box(double X, double Y, double Width);

    private sealed record Siblings(bool PreviousIsClicked, Box Previous, Box Next);
}
