using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.Data;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The item type an API client sees is computed from the speaker, not read from storage
/// (ADR-0006). The word itself is unchanged, so existing scripts — and
/// <see cref="FrozenItemBoundaryTests"/> — are untouched; what changes is that flipping an item's
/// speaker flips the word with it, in both directions.
/// </summary>
[Collection(E2eCollection.Name)]
public class DerivedItemTypeTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Item_type_follows_the_speaker_in_both_directions()
    {
        var folder = $"derived-type-{Guid.NewGuid():N}";
        var builder = await app.SeedMisSplitParagraphProjectAsync(
            folder, "Derived Type Book", "Author", characterName: "Alice");

        var chapterId = builder.ChapterId("ch1");
        var paragraphId = builder.ParagraphId("p2");
        var narrationItemId = builder.ItemId("lead");
        var dialogItemId = builder.ItemId("clean");
        var aliceId = builder.CharacterId("Alice");

        var items = await ReadItemsAsync(folder, chapterId, paragraphId);
        Assert.Equal("Narration", items[narrationItemId].ItemType);
        Assert.Equal("Character", items[dialogItemId].ItemType);
        Assert.Null(items[dialogItemId].CharacterId);   // unattributed still reports as dialog

        // Give the narration item to a character: it reports as dialog now.
        await SetSpeakerAsync(folder, narrationItemId, aliceId);
        items = await ReadItemsAsync(folder, chapterId, paragraphId);
        Assert.Equal("Character", items[narrationItemId].ItemType);
        Assert.Equal(aliceId, items[narrationItemId].CharacterId);

        // Give the dialog item to the narrator: it reports as narration.
        await SetSpeakerAsync(folder, dialogItemId, ProjectDbContext.NarratorId);
        items = await ReadItemsAsync(folder, chapterId, paragraphId);
        Assert.Equal("Narration", items[dialogItemId].ItemType);
        Assert.Equal(ProjectDbContext.NarratorId, items[dialogItemId].CharacterId);
    }

    private async Task SetSpeakerAsync(string folder, Guid itemId, Guid? characterId)
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetItemCharacter", itemId, characterId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Dictionary<Guid, ItemRow>> ReadItemsAsync(string folder, Guid chapterId, Guid paragraphId)
    {
        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{chapterId}/children"));
        var paragraph = children.RootElement.GetProperty("paragraphs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == paragraphId);
        return paragraph.GetProperty("items").EnumerateArray()
            .Select(i => new ItemRow(
                i.GetProperty("id").GetGuid(),
                i.GetProperty("itemType").GetString()!,
                i.GetProperty("characterId").ValueKind == JsonValueKind.Null
                    ? null
                    : i.GetProperty("characterId").GetGuid()))
            .ToDictionary(r => r.Id);
    }

    private sealed record ItemRow(Guid Id, string ItemType, Guid? CharacterId);
}
