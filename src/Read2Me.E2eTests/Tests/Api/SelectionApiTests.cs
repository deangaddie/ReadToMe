using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads and writes behind the web reader's selection (Angular ticket 12): the paragraph refs
/// a tree node selects, an enqueue by explicit ids, the bulk-assign preview and clearing a
/// paragraph's remembered outcome.
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

    [Fact]
    public async Task Paragraph_ids_list_the_character_paragraphs_under_a_node_with_ancestry()
    {
        var folder = $"api-sel-ids-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Select Book", "Author");
        var chapter = book.ChapterId("ch1");

        // Narration-only p1 is not a Character paragraph; the three dialog paragraphs are.
        var all = await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapter}/paragraph-ids");
        Assert.Equal(
            new HashSet<Guid> { book.ParagraphId("p2"), book.ParagraphId("p3"), book.ParagraphId("p4") },
            all.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet());
        var first = all.EnumerateArray().First();
        Assert.Equal(chapter, first.GetProperty("chapterId").GetGuid());
        Assert.Equal(book.VolumeId("v1"), first.GetProperty("volumeId").GetGuid());
        Assert.NotEqual(Guid.Empty, first.GetProperty("partId").GetGuid());

        // The same refs answer at the volume level, so a tree root selects without loading chapters.
        var byVolume = await GetJsonAsync($"/api/projects/{folder}/nodes/volume/{book.VolumeId("v1")}/paragraph-ids");
        Assert.Equal(3, byVolume.GetArrayLength());

        // Attribute one; "unprocessed" then leaves it out.
        var characterId = (await GetJsonAsync($"/api/projects/{folder}/characters"))
            .EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Alice").GetProperty("id").GetGuid();
        var assign = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p2"), characterId });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        var unprocessed = await GetJsonAsync(
            $"/api/projects/{folder}/nodes/chapter/{chapter}/paragraph-ids?unprocessedOnly=true");
        Assert.Equal(
            new HashSet<Guid> { book.ParagraphId("p3"), book.ParagraphId("p4") },
            unprocessed.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet());

        var badLevel = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/book/{chapter}/paragraph-ids");
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
    }

    [Fact]
    public async Task Enqueue_paragraphs_queues_the_dialog_ones_and_the_llm_attributes_them()
    {
        var folder = $"api-sel-enq-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Enqueue Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");

        // p1 is narration only and an unknown id names nothing: neither is queued.
        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue-paragraphs",
            new { paragraphIds = new[] { book.ParagraphId("p1"), book.ParagraphId("p2"), book.ParagraphId("p3"), Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var enqueued = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
        Assert.Equal(2, enqueued);

        await app.WaitForQueueDrainAsync("/api/attribution/queue");

        var paragraphs = (await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/children"))
            .GetProperty("paragraphs").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid());
        Guid? SpeakerOf(string name) =>
            paragraphs[book.ParagraphId(name)].GetProperty("items")[0].GetProperty("characterId") is { ValueKind: JsonValueKind.String } id
                ? id.GetGuid() : null;
        Assert.NotNull(SpeakerOf("p2"));
        Assert.NotNull(SpeakerOf("p3"));
        Assert.Null(SpeakerOf("p4"));
    }

    [Fact]
    public async Task Bulk_assign_preview_counts_dialog_lines_and_their_paragraphs()
    {
        var folder = $"api-sel-preview-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Preview Book", "Author");

        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/bulk-assign-preview",
            new { paragraphIds = new[] { book.ParagraphId("p1"), book.ParagraphId("p2"), book.ParagraphId("p3") } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var preview = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, preview.GetProperty("paragraphsWithCharacterItems").GetInt32());
        Assert.Equal(2, preview.GetProperty("characterItems").GetInt32());
    }

    [Fact]
    public async Task Release_narration_previews_then_makes_the_speech_marked_paragraphs_selectable()
    {
        var folder = $"api-sel-release-{Guid.NewGuid():N}";
        var book = await app.SeedTranscriptProjectAsync(folder, "Release Book", "Author");
        var chapter = book.ChapterId("ch1");

        // The chapter's ten Q:/A: paragraphs carry speech markers; its plain narration does not.
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narration/release-preview",
            new { scope = "Chapter", nodeId = chapter });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(10, preview.GetProperty("paragraphs").GetInt32());
        Assert.Equal(0, preview.GetProperty("itemsWithAudio").GetInt32());
        Assert.Equal(["A:=5", "Q:=5"], preview.GetProperty("speechMarkers").EnumerateArray()
            .Select(m => $"{m.GetProperty("marker").GetString()}={m.GetProperty("paragraphs").GetInt32()}"));

        var release = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "ReleaseNarration", scope = "Chapter", nodeId = chapter });
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);

        var ids = (await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapter}/paragraph-ids?unprocessedOnly=true"))
            .EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(book.ParagraphId("pq1"), ids);
        Assert.DoesNotContain(book.ParagraphId("pplain"), ids);

        // A Paragraph release is unfiltered: the plain narration can still be released on its own.
        var one = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narration/release-preview",
            new { scope = "Paragraph", nodeId = book.ParagraphId("pplain") });
        var onePreview = JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, onePreview.GetProperty("paragraphs").GetInt32());
        Assert.Equal(0, onePreview.GetProperty("speechMarkers").GetArrayLength());

        // A whole-book release is not a scope.
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

        // Clearing again, with nothing recorded, is still fine.
        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}{statusPath}/outcome")).StatusCode);
    }

    [Fact]
    public async Task Unknown_project_is_404_on_every_endpoint()
    {
        var b = $"{app.BaseUrl}/api/projects/no-such-project-sel";
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{b}/nodes/chapter/{Guid.NewGuid()}/paragraph-ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync($"{b}/attribution/enqueue-paragraphs", new { paragraphIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync($"{b}/characters/bulk-assign-preview", new { paragraphIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync($"{b}/narration/release-preview", new { scope = "Chapter", nodeId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.DeleteAsync($"{b}/attribution/paragraphs/{Guid.NewGuid()}/outcome")).StatusCode);
    }
}
