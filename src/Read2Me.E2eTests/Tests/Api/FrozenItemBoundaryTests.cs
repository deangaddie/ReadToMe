using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.Data;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The frozen-boundary guarantee end to end (ADR 0005): attribution runs over a deliberately
/// mis-split paragraph and may only stamp speakers on the items that already exist — it cannot
/// re-slice, add, drop or reword one. The answer here is the awkward one on purpose: `unknown`
/// on the two-speaker item, a real name on the clean one, and a name on an index no item has.
/// </summary>
[Collection(E2eCollection.Name)]
public class FrozenItemBoundaryTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private const string LeadText = "The door swung open.";
    private const string MixedText = "“Hello there,” she said. “And who might you be?” he answered.";
    private const string CleanText = "“Only me,” came the reply.";

    // Items are numbered 0..n-1 in Order sequence, so: 0 = the narration lead, 1 = the two-speaker
    // item, 2 = the clean line. Index 9 exists in no paragraph — the apply must drop it.
    private const string PerItemAnswer = """
        {
          "reasoning": "fake",
          "items": [
            { "index": 1, "speaker": "unknown", "voice_instructions": "" },
            { "index": 2, "speaker": "Alice", "voice_instructions": "calm" },
            { "index": 9, "speaker": "Alice", "voice_instructions": "calm" }
          ]
        }
        """;

    [Fact]
    public async Task Attribution_stamps_speakers_without_touching_item_boundaries()
    {
        var folder = $"frozen-items-{Guid.NewGuid():N}";
        var builder = await app.SeedMisSplitParagraphProjectAsync(
            folder, "Frozen Items Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = _ => PerItemAnswer;

        var chapterId = builder.ChapterId("ch1");
        var paragraphId = builder.ParagraphId("p2");

        Assert.Equal(1, await EnqueueChapterAsync(folder, chapterId));
        await app.WaitForQueueDrainAsync("/api/attribution/queue");

        var items = await ReadItemsAsync(folder, chapterId, paragraphId);

        // The whole point: the split the importer produced is the split that survives.
        Assert.Equal(3, items.Count);
        Assert.Equal([LeadText, MixedText, CleanText], items.Select(i => i.Text));
        Assert.Equal(["Narration", "Character", "Character"], items.Select(i => i.ItemType));
        Assert.Equal([builder.ItemId("lead"), builder.ItemId("mixed"), builder.ItemId("clean")],
            items.Select(i => i.Id));

        // A named index stamps its own item; `unknown` leaves the two-speaker item for the user;
        // the index no item has changes nothing, here or on the narration item it might have hit.
        Assert.Equal(ProjectDbContext.NarratorId, items[0].CharacterId);
        Assert.Null(items[1].CharacterId);
        Assert.Equal(builder.CharacterId("Alice"), items[2].CharacterId);

        // Partly attributed: still unprocessed, so the user can re-queue it after fixing the split.
        // The re-queued run is drained again — the app is shared, and an in-flight paragraph would
        // otherwise run against the next test's fake AI.
        Assert.Equal(1, await EnqueueChapterAsync(folder, chapterId));
        await app.WaitForQueueDrainAsync("/api/attribution/queue");
    }

    private async Task<int> EnqueueChapterAsync(string folder, Guid chapterId)
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue",
            new { level = "chapter", nodeId = chapterId, unprocessedOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
    }

    private async Task<List<ItemRow>> ReadItemsAsync(string folder, Guid chapterId, Guid paragraphId)
    {
        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{chapterId}/children"));
        var paragraph = children.RootElement.GetProperty("paragraphs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == paragraphId);
        return [.. paragraph.GetProperty("items").EnumerateArray().Select(i => new ItemRow(
            i.GetProperty("id").GetGuid(),
            i.GetProperty("itemType").GetString()!,
            i.GetProperty("text").GetString()!,
            i.GetProperty("characterId").ValueKind == JsonValueKind.Null
                ? null
                : i.GetProperty("characterId").GetGuid()))];
    }

    private sealed record ItemRow(Guid Id, string ItemType, string Text, Guid? CharacterId);
}
