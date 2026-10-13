using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>SetItemDeliveryCue</c> through the book-command endpoint, end to end: the change reaches the
/// live hub as a receipt and the chapter's audio count. The cue's validation and its audio discard
/// are unit-tested on the handler (<c>SetItemDeliveryCueHandlerTests</c>).
/// </summary>
public class DeliveryCueApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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

    private async Task<Guid> CharacterIdAsync(string folder, string name) =>
        (await GetJsonAsync($"/api/projects/{folder}/characters")).EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

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
