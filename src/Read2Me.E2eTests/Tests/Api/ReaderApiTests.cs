using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads behind the Angular reader (ticket 10): the enriched chapter children, resolved voice
/// names per chapter and the sparse audio review map.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Chapter_children_carry_order_key_pause_flags_and_voice_instructions()
    {
        var folder = $"api-reader-children-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Reader Book", "Author");

        var paragraphs = (await GetJsonAsync(
            $"/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/children")).GetProperty("paragraphs");

        var first = paragraphs[0];
        Assert.False(first.GetProperty("isPauseParagraph").GetBoolean());
        var item = first.GetProperty("items")[0];
        Assert.Equal(book.ItemId("n1"), item.GetProperty("id").GetGuid());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("orderKey").GetString()));
        Assert.False(item.GetProperty("isPause").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("voiceInstructions").ValueKind);
        Assert.Equal("Narration", item.GetProperty("itemType").GetString());
    }

    [Fact]
    public async Task Chapter_voices_resolve_per_speech_item_and_credit_the_linked_narrator()
    {
        var folder = $"api-reader-voices-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Voices Book", "Author", characterName: "Watson");
        await app.SeedNarratorVoiceAsync(folder);
        var path = $"/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/voices";

        var voices = await GetJsonAsync(path);

        var narration = voices.GetProperty(book.ItemId("n1").ToString());
        Assert.Equal("Narrator Voice", narration.GetProperty("voiceName").GetString());
        Assert.Equal(JsonValueKind.Null, narration.GetProperty("narratedBy").ValueKind);
        // The unattributed dialog line has no speaker, so no voice resolves.
        var dialog = voices.GetProperty(book.ItemId("line1").ToString());
        Assert.Equal(JsonValueKind.Null, dialog.GetProperty("voiceName").ValueKind);
        Assert.Equal(3, voices.EnumerateObject().Count());

        var link = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetNarratorCharacter", characterId = book.CharacterId("Watson") });
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var linked = await GetJsonAsync(path);
        Assert.Equal("Watson", linked.GetProperty(book.ItemId("n1").ToString()).GetProperty("narratedBy").GetString());
        Assert.Equal(JsonValueKind.Null,
            linked.GetProperty(book.ItemId("line1").ToString()).GetProperty("narratedBy").ValueKind);
    }

    [Fact]
    public async Task Audio_reviews_are_sparse_and_keyed_by_item()
    {
        var folder = $"api-reader-reviews-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Reviews Book", "Author");
        var path = $"/api/projects/{folder}/audio/reviews";

        Assert.Empty((await GetJsonAsync(path)).EnumerateObject());

        var factory = app.Services.GetRequiredService<IProjectDbContextFactory>();
        await using (var db = await factory.CreateAsync(Path.Combine(app.WorkspaceDir, folder)))
        {
            db.AudioReviews.Add(new AudioReview
            {
                Id = Guid.NewGuid(),
                ParagraphItemId = book.ItemId("n1"),
                State = AudioReviewState.NeedsReview,
                NormalizeOk = true,
                VerifyOk = false,
                Wer = 0.42,
                VerifyReason = "WER above threshold",
                Transcript = "It was a dark night.",
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var reviews = await GetJsonAsync(path);

        var review = Assert.Single(reviews.EnumerateObject());
        Assert.Equal(book.ItemId("n1").ToString(), review.Name);
        Assert.Equal("NeedsReview", review.Value.GetProperty("state").GetString());
        Assert.False(review.Value.GetProperty("verifyOk").GetBoolean());
        Assert.Equal(0.42, review.Value.GetProperty("wer").GetDouble());
        Assert.Equal("WER above threshold", review.Value.GetProperty("verifyReason").GetString());
    }

    [Fact]
    public async Task Unknown_project_is_404()
    {
        var voices = await Http.GetAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz/nodes/chapter/{Guid.NewGuid()}/voices");
        Assert.Equal(HttpStatusCode.NotFound, voices.StatusCode);

        var reviews = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-project-xyz/audio/reviews");
        Assert.Equal(HttpStatusCode.NotFound, reviews.StatusCode);
    }
}
