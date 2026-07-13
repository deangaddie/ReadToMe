using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Attribution over HTTP: enqueue a chapter, poll the queue, read the per-paragraph
/// resolution — the same flow the UI drives, minus the UI.
/// </summary>
[Collection(E2eCollection.Name)]
public class AttributionApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Enqueue_poll_and_read_resolution()
    {
        var folder = $"api-attr-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Attr Api Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        var chapterId = builder.ChapterId("ch1");
        var paragraphId = builder.ParagraphId("p2");

        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue",
            new { level = "chapter", nodeId = chapterId, unprocessedOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var enqueued = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
        Assert.Equal(1, enqueued);

        // Poll until the queue drains (fake LLM answers instantly; generous ceiling).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = JsonDocument.Parse(
                await Http.GetStringAsync($"{app.BaseUrl}/api/attribution/queue"));
            if (snapshot.RootElement.GetProperty("queuedCount").GetInt32() == 0 &&
                snapshot.RootElement.GetProperty("processingCount").GetInt32() == 0)
                break;
            await Task.Delay(200);
        }

        // Queue state is done and carries no outcome; the attribution itself is on the items.
        var status = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/paragraphs/{paragraphId}"));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("outcome").ValueKind);

        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{chapterId}/children"));
        var paragraph = children.RootElement.GetProperty("paragraphs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == paragraphId);
        var characterItems = paragraph.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("itemType").GetString() == "Character")
            .ToList();
        Assert.NotEmpty(characterItems);
        Assert.All(characterItems, i =>
            Assert.NotEqual(JsonValueKind.Null, i.GetProperty("characterId").ValueKind));
    }

    [Fact]
    public async Task Enqueue_unknown_folder_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/nope-attr/attribution/enqueue",
            new { level = "chapter", nodeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_returns_ok()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/attribution/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
