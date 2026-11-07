using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads and writes behind the web reader's selection (Angular ticket 12): the paragraph refs
/// a tree node selects, an enqueue by explicit ids, the bulk-assign preview and clearing a
/// paragraph's remembered outcome. Which paragraphs a read keeps is ProjectReaderTests
/// (GetCharacterParagraphs…, CharacterParagraphRefs…); the previews are
/// BulkAssignPreviewReaderTests and NarrationReleasePreviewReaderTests; the release is
/// ReleaseNarrationHandlerTests; the outcome is CharacterQueueServiceTests. These pin the routes,
/// the query binding, the 400s and the wire fields.
/// </summary>
public class SelectionApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static List<Guid> Ids(JsonElement array) =>
        array.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Paragraph_ids_bind_the_level_and_unprocessed_only_and_carry_ancestry()
    {
        var folder = $"api-sel-ids-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Select Book", "Author");
        var chapter = book.ChapterId("ch1");

        var all = await GetJsonAsync($"/api/projects/{folder}/nodes/Chapter/{chapter}/paragraph-ids");
        Assert.Contains(book.ParagraphId("p2"), Ids(all));
        var first = all.EnumerateArray().First();
        Assert.Equal(chapter, first.GetProperty("chapterId").GetGuid());
        Assert.Equal(book.VolumeId("v1"), first.GetProperty("volumeId").GetGuid());
        Assert.NotEqual(Guid.Empty, first.GetProperty("partId").GetGuid());

        var byVolume = await GetJsonAsync($"/api/projects/{folder}/nodes/Volume/{book.VolumeId("v1")}/paragraph-ids");
        Assert.Contains(book.ParagraphId("p2"), Ids(byVolume));

        // Attribute one; "unprocessed" then leaves it out.
        var assign = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p2"), characterId = book.CharacterId("Alice") });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
        var unprocessed = await GetJsonAsync(
            $"/api/projects/{folder}/nodes/Chapter/{chapter}/paragraph-ids?unprocessedOnly=true");
        Assert.DoesNotContain(book.ParagraphId("p2"), Ids(unprocessed));

        var badLevel = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/book/{chapter}/paragraph-ids");
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
    }

    [Fact]
    public async Task Enqueue_paragraphs_answers_202_and_the_llm_attributes_them()
    {
        var folder = $"api-sel-enq-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Enqueue Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");

        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue-paragraphs",
            new { paragraphIds = new[] { book.ParagraphId("p2") } });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32());

        await app.WaitForQueueDrainAsync("/api/attribution/queue");

        var p2 = (await GetJsonAsync($"/api/projects/{folder}/nodes/Chapter/{book.ChapterId("ch1")}/children"))
            .GetProperty("paragraphs").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == book.ParagraphId("p2"));
        Assert.Equal(JsonValueKind.String, p2.GetProperty("items")[0].GetProperty("characterId").ValueKind);
    }

    [Fact]
    public async Task Bulk_assign_preview_answers_its_wire_fields()
    {
        var folder = $"api-sel-preview-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Preview Book", "Author");

        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/bulk-assign-preview",
            new { paragraphIds = new[] { book.ParagraphId("p2") } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var preview = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, preview.GetProperty("paragraphsWithCharacterItems").GetInt32());
        Assert.Equal(1, preview.GetProperty("characterItems").GetInt32());
    }

    [Fact]
    public async Task Release_narration_previews_and_releases_and_refuses_a_scope_that_is_not_one()
    {
        var folder = $"api-sel-release-{Guid.NewGuid():N}";
        var book = await app.SeedTranscriptProjectAsync(folder, "Release Book", "Author");
        var chapter = book.ChapterId("ch1");

        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narration/release-preview",
            new { scope = "Chapter", nodeId = chapter });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Number, preview.GetProperty("paragraphs").ValueKind);
        Assert.Equal(JsonValueKind.Number, preview.GetProperty("itemsWithAudio").ValueKind);
        var marker = preview.GetProperty("speechMarkers")[0];
        Assert.Equal(JsonValueKind.String, marker.GetProperty("marker").ValueKind);
        Assert.Equal(JsonValueKind.Number, marker.GetProperty("paragraphs").ValueKind);

        var release = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "ReleaseNarration", scope = "Chapter", nodeId = chapter });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);

        // A whole-book release is not a scope, and a scope is sent by name.
        var volume = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narration/release-preview",
            new { scope = "Volume", nodeId = book.VolumeId("v1") });
        Assert.Equal(HttpStatusCode.BadRequest, volume.StatusCode);
        var unnamed = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narration/release-preview",
            new { scope = 7, nodeId = chapter });
        Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
    }

    [Fact]
    public async Task Clear_outcome_is_204_and_the_paragraph_status_forgets_it()
    {
        var folder = $"api-sel-outcome-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Outcome Book", "Author", characterName: "Alice");
        // A reply the parser cannot read fails the paragraph, which records an outcome.
        app.FakeAi.LlmReply = _ => "this is not the JSON you are looking for";
        var paragraphId = book.ParagraphId("p2");

        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue-paragraphs",
            new { paragraphIds = new[] { paragraphId } });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        await app.WaitForQueueDrainAsync("/api/attribution/queue");

        var statusPath = $"/api/projects/{folder}/attribution/paragraphs/{paragraphId}";
        var before = await GetJsonAsync(statusPath);
        Assert.Equal(JsonValueKind.Object, before.GetProperty("outcome").ValueKind);

        var clear = await Http.DeleteAsync($"{app.BaseUrl}{statusPath}/outcome");
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);

        var after = await GetJsonAsync(statusPath);
        Assert.Equal(JsonValueKind.Null, after.GetProperty("outcome").ValueKind);
    }

    [Fact]
    public async Task Unknown_project_is_404()
    {
        var b = $"{app.BaseUrl}/api/projects/no-such-project-sel";
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{b}/nodes/Chapter/{Guid.NewGuid()}/paragraph-ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync($"{b}/attribution/enqueue-paragraphs", new { paragraphIds = Array.Empty<Guid>() })).StatusCode);
    }
}
