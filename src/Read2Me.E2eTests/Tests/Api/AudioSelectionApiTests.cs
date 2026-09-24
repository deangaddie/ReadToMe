using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads and writes behind the web reader's audio selection (Angular ticket 13): the item refs
/// a tree node selects, and an enqueue by explicit item ids (a selection, or one item to retry).
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioSelectionApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HashSet<Guid> Ids(JsonElement array) =>
        array.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToHashSet();

    [Fact]
    public async Task Item_ids_list_the_voiced_items_under_a_node_with_ancestry()
    {
        var folder = $"api-audio-ids-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Audio Select Book", "Author", characterName: "Alice");
        var chapter = book.ChapterId("ch1");

        // Only the narration item has a speaker; the three unattributed lines do not.
        var all = await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapter}/item-ids");
        Assert.Equal(new HashSet<Guid> { book.ItemId("n1") }, Ids(all));
        var first = all.EnumerateArray().First();
        Assert.Equal(book.ParagraphId("p1"), first.GetProperty("paragraphId").GetGuid());
        Assert.Equal(chapter, first.GetProperty("chapterId").GetGuid());
        Assert.Equal(book.VolumeId("v1"), first.GetProperty("volumeId").GetGuid());
        Assert.NotEqual(Guid.Empty, first.GetProperty("partId").GetGuid());

        // Narrator-only mode reads the unattributed lines too.
        var narratorOnly = await GetJsonAsync($"/api/projects/{folder}/nodes/volume/{book.VolumeId("v1")}/item-ids?narratorOnlyMode=true");
        Assert.Equal(4, narratorOnly.GetArrayLength());

        // Attribute one line: it joins the voiced set, and "needs audio" still lists it (no WAV yet).
        var characterId = (await GetJsonAsync($"/api/projects/{folder}/characters"))
            .EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Alice").GetProperty("id").GetGuid();
        var assign = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetItemCharacter", itemId = book.ItemId("line1"), characterId });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        var needsAudio = await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapter}/item-ids?needsAudioOnly=true");
        Assert.Equal(new HashSet<Guid> { book.ItemId("n1"), book.ItemId("line1") }, Ids(needsAudio));

        var badLevel = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/book/{chapter}/item-ids");
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
    }

    [Fact]
    public async Task Enqueue_items_queues_the_known_ids_and_the_audio_lands()
    {
        var folder = $"api-audio-enq-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Enqueue Items Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var itemId = book.ItemId("n2");

        // An unknown id names nothing and is not counted.
        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue-items",
            new { itemIds = new[] { itemId, Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var enqueued = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
        Assert.Equal(1, enqueued);

        await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));
        var status = await GetJsonAsync($"/api/projects/{folder}/audio/items/{itemId}");
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("audioVersion").ValueKind);

        // Nothing to queue is still accepted.
        var empty = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue-items", new { itemIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Accepted, empty.StatusCode);
    }

    [Fact]
    public async Task Unknown_project_is_404_on_both_endpoints()
    {
        var b = $"{app.BaseUrl}/api/projects/no-such-project-audio-sel";
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{b}/nodes/chapter/{Guid.NewGuid()}/item-ids")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync($"{b}/audio/enqueue-items", new { itemIds = Array.Empty<Guid>() })).StatusCode);
    }
}
