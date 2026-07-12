using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Audio generation over HTTP: enqueue narration items for a chapter, poll the
/// queue, confirm the wav landed and the item endpoint reflects completion.
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Enqueue_poll_and_audio_lands_on_disk()
    {
        var folder = $"api-audio-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Audio Api Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var chapterId = builder.ChapterId("ch1");
        var itemId = builder.ItemId("n1");

        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
            new { level = "chapter", nodeId = chapterId, needsAudioOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var enqueued = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
        Assert.Equal(2, enqueued); // n1 + n2 narration; unattributed character line excluded

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = JsonDocument.Parse(
                await Http.GetStringAsync($"{app.BaseUrl}/api/audio/queue"));
            if (snapshot.RootElement.GetProperty("queuedCount").GetInt32() == 0 &&
                snapshot.RootElement.GetProperty("processingCount").GetInt32() == 0)
                break;
            await Task.Delay(200);
        }

        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));

        var status = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/items/{itemId}"));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("outcome").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, status.RootElement.GetProperty("audioVersion").ValueKind);
    }

    [Fact]
    public async Task Enqueue_unknown_folder_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/nope-audio/audio/enqueue",
            new { level = "chapter", nodeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_returns_ok()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/audio/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
