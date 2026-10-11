using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>SetItemDeliveryCue</c> through the book-command endpoint: a producer's hand-written delivery
/// cue on one speech item. A real change discards the item's audio and its review, as a text edit
/// does; an unchanged cue keeps both.
/// </summary>
[Collection(E2eCollection.Name)]
public class DeliveryCueApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private Task<HttpResponseMessage> SetCueAsync(string folder, Guid itemId, string? cue) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetItemDeliveryCue", itemId, cue });

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> ItemAsync(string folder, Guid chapterId, Guid itemId)
    {
        var paragraphs = (await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapterId}/children"))
            .GetProperty("paragraphs");
        return paragraphs.EnumerateArray()
            .SelectMany(p => p.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("id").GetGuid() == itemId);
    }

    private async Task<string?> CueAsync(string folder, Guid chapterId, Guid itemId) =>
        (await ItemAsync(folder, chapterId, itemId)).GetProperty("voiceInstructions").GetString();

    private async Task<Guid> CharacterIdAsync(string folder, string name) =>
        (await GetJsonAsync($"/api/projects/{folder}/characters")).EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    /// <summary>Gives <paramref name="itemId"/> a WAV and a NeedsReview verdict on it.</summary>
    private async Task SeedAudioAndReviewAsync(string folder, Guid itemId, Guid characterId)
    {
        await app.SeedItemAudioAsync(folder, itemId, characterId);
        var review = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands", new
        {
            type = "SetAudioReview",
            paragraphItemId = itemId,
            normalizeOk = true,
            verifyOk = false,
            wer = 0.5,
            verifyReason = "mismatch",
            transcript = "something else",
            originalTextSnapshot = "“Hello there,” she said.",
        });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.True(await HasReviewAsync(folder, itemId));
    }

    private async Task<bool> HasReviewAsync(string folder, Guid itemId) =>
        (await GetJsonAsync($"/api/projects/{folder}/audio/reviews")).TryGetProperty(itemId.ToString(), out _);

    [Fact]
    public async Task Add_edit_and_remove_a_cue_with_null_or_empty_or_whitespace()
    {
        var folder = $"api-cue-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Cue Book", "Author");
        var chapter = book.ChapterId("ch1");
        var line = book.ItemId("line1");

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "  softly  ")).StatusCode);
        Assert.Equal("softly", await CueAsync(folder, chapter, line));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "loud, angry")).StatusCode);
        Assert.Equal("loud, angry", await CueAsync(folder, chapter, line));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, null)).StatusCode);
        Assert.Null(await CueAsync(folder, chapter, line));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "whispered")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "")).StatusCode);
        Assert.Null(await CueAsync(folder, chapter, line));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "whispered")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "   ")).StatusCode);
        Assert.Null(await CueAsync(folder, chapter, line));
    }

    [Fact]
    public async Task Narration_and_attributed_dialog_accept_a_cue()
    {
        var folder = $"api-cue-narr-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Cue Narration Book", "Author");
        var chapter = book.ChapterId("ch1");
        var alice = await CharacterIdAsync(folder, "Alice");
        var assign = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "SetItemCharacter", itemId = book.ItemId("line1"), characterId = alice });
        Assert.Equal(HttpStatusCode.OK, assign.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, book.ItemId("n1"), "slow and grave")).StatusCode);
        Assert.Equal("slow and grave", await CueAsync(folder, chapter, book.ItemId("n1")));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, book.ItemId("line1"), "cheerful")).StatusCode);
        Assert.Equal("cheerful", await CueAsync(folder, chapter, book.ItemId("line1")));
    }

    [Fact]
    public async Task Rejects_an_over_long_cue_a_pause_item_and_an_unknown_item()
    {
        var folder = $"api-cue-bad-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Cue Reject Book", "Author");
        var chapter = book.ChapterId("ch1");
        var line = book.ItemId("line1");

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, new string('a', 200))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCueAsync(folder, line, new string('b', 201))).StatusCode);
        Assert.Equal(new string('a', 200), await CueAsync(folder, chapter, line));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCueAsync(folder, Guid.NewGuid(), "softly")).StatusCode);

        var insert = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "InsertPauseParagraph", anchorItemId = line, position = "After", pauseKind = "Pause" });
        Assert.Equal(HttpStatusCode.OK, insert.StatusCode);
        var pauseItem = (await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapter}/children"))
            .GetProperty("paragraphs").EnumerateArray()
            .SelectMany(p => p.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("isPause").GetBoolean())
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SetCueAsync(folder, pauseItem, "softly")).StatusCode);
        Assert.Null(await CueAsync(folder, chapter, pauseItem));
    }

    [Fact]
    public async Task A_changed_cue_discards_audio_and_review_and_an_unchanged_one_keeps_them()
    {
        var folder = $"api-cue-audio-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Cue Audio Book", "Author");
        var chapter = book.ChapterId("ch1");
        var line = book.ItemId("line1");
        await app.SeedDeliveryCueAsync(folder, line, "calm");
        await SeedAudioAndReviewAsync(folder, line, await CharacterIdAsync(folder, "Alice"));

        // The same cue, normalised, writes nothing: the take and its verdict survive.
        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, " calm ")).StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await ItemAsync(folder, chapter, line)).GetProperty("audioFileName").ValueKind);
        Assert.True(await HasReviewAsync(folder, line));

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "furious")).StatusCode);
        var item = await ItemAsync(folder, chapter, line);
        Assert.Equal("furious", item.GetProperty("voiceInstructions").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("audioFileName").ValueKind);
        Assert.False(await HasReviewAsync(folder, line));
    }

    [Fact]
    public async Task The_change_reaches_the_live_hub_and_the_chapter_audio_count()
    {
        var folder = $"api-cue-hub-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Cue Hub Book", "Author");
        var chapter = book.ChapterId("ch1");
        var line = book.ItemId("line1");
        await app.SeedItemAudioAsync(folder, line, await CharacterIdAsync(folder, "Alice"));
        var statusPath = $"/api/projects/{folder}/nodes/chapter/{chapter}/status";
        var remainingBefore = (await GetJsonAsync(statusPath)).GetProperty("audioRemaining").GetInt32();

        await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
        var receipts = new ConcurrentQueue<JsonElement>();
        hub.On<JsonElement>("receipt", receipts.Enqueue);
        await hub.StartAsync();
        await hub.InvokeAsync<JsonElement>("JoinProject", folder);

        Assert.Equal(HttpStatusCode.OK, (await SetCueAsync(folder, line, "breathless")).StatusCode);

        var deadline = Stopwatch.StartNew();
        while (receipts.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        var receipt = Assert.Single(receipts);
        Assert.Equal("SetItemDeliveryCueMutation", receipt.GetProperty("mutationName").GetString());
        var effects = receipt.GetProperty("effects");
        Assert.Contains("Audio", effects.GetProperty("facets").GetString());
        Assert.DoesNotContain("Structure", effects.GetProperty("facets").GetString());
        Assert.Contains(line, effects.GetProperty("paragraphItemIds").EnumerateArray().Select(i => i.GetGuid()));

        Assert.Equal(remainingBefore + 1, (await GetJsonAsync(statusPath)).GetProperty("audioRemaining").GetInt32());
    }
}
