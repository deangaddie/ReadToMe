using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The inline series Voice editors on the series cast board (#221): two rows' editors open at once,
/// each directly under its own row. Layout only: an open editor reading again on the hub's
/// <c>series</c> signal and everything else are spec-tested (series-voice-editor.spec.ts).
/// </summary>
public class SeriesVoiceEditorTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private sealed record Seeded(Guid Series, Guid Watson, Guid Holmes);

    /// <summary>Two Holmes books in a series, Watson and Holmes shared from Scarlet and linked in Four, each with a Voice.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (scarlet, four) = ($"scarlet-{tag}", $"four-{tag}");
        var scarletBook = await App.SeedMultiChapterProjectAsync(scarlet, "A Study in Scarlet", "Arthur Conan Doyle", chapters: 1, characterName: "Watson");
        var fourBook = await App.SeedMultiChapterProjectAsync(four, "The Sign of the Four", "Arthur Conan Doyle", chapters: 1, characterName: "Watson");
        var scarletWatson = scarletBook.CharacterId("Watson");
        var fourWatson = fourBook.CharacterId("Watson");
        var scarletHolmes = await RunAsync(scarlet, new { type = "CreateCharacter", name = "Sherlock Holmes" });
        var fourHolmes = await RunAsync(four, new { type = "CreateCharacter", name = "Sherlock Holmes" });
        await RunAsync(scarlet, new { type = "CreateVoice", characterId = scarletWatson, name = "Army doctor" });
        await RunAsync(scarlet, new { type = "CreateVoice", characterId = scarletHolmes, name = "Consulting detective" });

        var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series", new { name = $"Sherlock Holmes {tag}", folder = scarlet });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var series = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{App.BaseUrl}/api/series/{series}/books/{four}", new { })).StatusCode);

        async Task<Guid> ShareAndLinkAsync(Guid scarletCharacter, Guid fourCharacter)
        {
            var shared = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/series/{series}/characters", new { folder = scarlet, characterId = scarletCharacter });
            Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
            var id = (await shared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("seriesCharacterId").GetGuid();
            Assert.Equal(HttpStatusCode.NoContent, (await Http.PutAsJsonAsync(
                $"{App.BaseUrl}/api/series/{series}/characters/{id}/links/{four}", new { characterId = fourCharacter })).StatusCode);
            return id;
        }

        return new Seeded(series, await ShareAndLinkAsync(scarletWatson, fourWatson), await ShareAndLinkAsync(scarletHolmes, fourHolmes));
    }

    private async Task<Guid> RunAsync(string folder, object command)
    {
        var response = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{folder}/commands", command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("newEntityId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : Guid.Empty;
    }

    private ILocator Row(Guid seriesCharacter) => Page.Locator($"r2m-series-cast-board tr[data-series-character=\"{seriesCharacter}\"]");

    private ILocator EditorRow(Guid seriesCharacter) => Page.Locator($"r2m-series-cast-board tr[data-editor-for=\"{seriesCharacter}\"]");

    // Needs a real browser: where each editor row sits is real table layout.
    [Fact]
    public async Task Two_rows_Voice_editors_open_at_once_each_directly_under_its_row()
    {
        var seeded = await SeedAsync();
        await Page.SetViewportSizeAsync(1200, 1000);
        await GotoAppAsync($"projects?series={seeded.Series}");
        await Expect(Row(seeded.Holmes)).ToBeVisibleAsync();

        await Row(seeded.Holmes).Locator("[data-action=voices]").ClickAsync();
        // Opened first: the editor pushes Watson's row down, so a click before it lands could miss.
        await Expect(EditorRow(seeded.Holmes).Locator("tr[data-voice-id]")).ToHaveCountAsync(1);
        await Row(seeded.Watson).Locator("[data-action=voices]").ClickAsync();
        await Expect(EditorRow(seeded.Watson).Locator("tr[data-voice-id]")).ToHaveCountAsync(1);

        // "Sherlock Holmes" sorts before "Watson": row, its editor, row, its editor, top to bottom.
        var holmesRow = (await Row(seeded.Holmes).BoundingBoxAsync())!;
        var holmesEditor = (await EditorRow(seeded.Holmes).BoundingBoxAsync())!;
        var watsonRow = (await Row(seeded.Watson).BoundingBoxAsync())!;
        var watsonEditor = (await EditorRow(seeded.Watson).BoundingBoxAsync())!;
        Assert.True(Math.Abs(holmesEditor.Y - (holmesRow.Y + holmesRow.Height)) < 2,
            $"Holmes' editor (y={holmesEditor.Y}) should start where its row ends ({holmesRow.Y + holmesRow.Height})");
        Assert.True(Math.Abs(watsonRow.Y - (holmesEditor.Y + holmesEditor.Height)) < 2,
            $"Watson's row (y={watsonRow.Y}) should follow Holmes' editor (bottom={holmesEditor.Y + holmesEditor.Height})");
        Assert.True(Math.Abs(watsonEditor.Y - (watsonRow.Y + watsonRow.Height)) < 2,
            $"Watson's editor (y={watsonEditor.Y}) should start where its row ends ({watsonRow.Y + watsonRow.Height})");
        // Each editor spans the board's width, not one cell.
        Assert.True(Math.Abs(holmesEditor.Width - holmesRow.Width) < 2,
            $"Holmes' editor (w={holmesEditor.Width}) should be as wide as its row (w={holmesRow.Width})");
    }
}
