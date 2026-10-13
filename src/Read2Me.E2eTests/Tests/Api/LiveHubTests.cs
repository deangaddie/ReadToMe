using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>/hubs/live</c> end to end over the real host: group membership, snapshots, receipt routing
/// per project and late-joiner stream replay (Angular ticket 06).
/// </summary>
public class LiveHubTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private HubConnection Connect() => new HubConnectionBuilder()
        .WithUrl($"{app.BaseUrl}/hubs/live")
        .Build();

    [Fact]
    public async Task JoinProject_returns_snapshot_and_receipts_reach_only_that_project_group()
    {
        var folderA = $"hub-a-{Guid.NewGuid():N}";
        var folderB = $"hub-b-{Guid.NewGuid():N}";
        var bookA = await app.SeedProjectAsync(folderA, "Hub A", "Author");
        var bookB = await app.SeedProjectAsync(folderB, "Hub B", "Author");

        await using var a = Connect();
        await using var b = Connect();
        var receiptsA = new ConcurrentQueue<JsonElement>();
        var receiptsB = new ConcurrentQueue<JsonElement>();
        a.On<JsonElement>("receipt", receiptsA.Enqueue);
        b.On<JsonElement>("receipt", receiptsB.Enqueue);
        await a.StartAsync();
        await b.StartAsync();

        var snapshot = await a.InvokeAsync<JsonElement>("JoinProject", folderA);
        await b.InvokeAsync<JsonElement>("JoinProject", folderB);
        Assert.Equal(folderA, snapshot.GetProperty("folder").GetString());
        Assert.True(snapshot.TryGetProperty("revision", out _));
        Assert.Equal(JsonValueKind.Object, snapshot.GetProperty("nodes").ValueKind);

        var response = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folderA}/commands",
            new StringContent("""{ "type": "CreateCharacter", "name": "Hub Character" }""", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        // Sentinels: one more write to each project. The relay sends receipts in commit order, so
        // once each connection has its sentinel, anything A's write sent it has already arrived.
        await RetitleAsync(folderB, bookB.ChapterId("ch1"), "Sentinel B");
        await RetitleAsync(folderA, bookA.ChapterId("ch1"), "Sentinel A");
        await WaitForAsync(() => receiptsA.Count >= 2 && !receiptsB.IsEmpty);

        var inA = receiptsA.ToList();
        Assert.Equal(["CreateCharacterMutation", "UpdateChapterTitleMutation"],
            inA.Select(r => r.GetProperty("mutationName").GetString())); // verbatim mutation names, once each
        var receipt = inA[0];
        Assert.Equal(folderA, receipt.GetProperty("folder").GetString());
        Assert.True(receipt.GetProperty("revision").GetInt64() >= 1);
        Assert.Contains("Characters", receipt.GetProperty("effects").GetProperty("facets").GetString());
        // B saw only its own sentinel, never A's receipt.
        Assert.Equal(folderB, Assert.Single(receiptsB).GetProperty("folder").GetString());

        var live = await a.InvokeAsync<JsonElement>("GetSnapshot");
        Assert.True(live.GetProperty("projects").TryGetProperty(folderA, out var project));
        Assert.True(project.GetProperty("revision").GetInt64() >= 1);
        Assert.True(live.GetProperty("queue").GetProperty("attribution").TryGetProperty("queuedCount", out _));
        Assert.True(live.TryGetProperty("throughput", out _));
    }

    /// <summary>
    /// The web client's own-write detection (Angular ticket 11): a command sent with
    /// <c>X-Origin-Id</c> comes back on the hub with that id as <c>originId</c>; one sent without
    /// comes back unattributed. A manual reread is stamped the same way.
    /// </summary>
    [Fact]
    public async Task Receipts_echo_the_callers_origin_id()
    {
        var folder = $"hub-origin-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Origin Book", "Author");
        await using var conn = Connect();
        var receipts = new ConcurrentQueue<JsonElement>();
        conn.On<JsonElement>("receipt", receipts.Enqueue);
        await conn.StartAsync();
        await conn.InvokeAsync<JsonElement>("JoinProject", folder);
        var origin = Guid.NewGuid();

        using (var mine = new HttpRequestMessage(HttpMethod.Post, $"{app.BaseUrl}/api/projects/{folder}/commands"))
        {
            mine.Headers.Add("X-Origin-Id", origin.ToString());
            mine.Content = new StringContent(
                $$"""{ "type": "UpdateChapterTitle", "chapterId": "{{book.ChapterId("ch1")}}", "title": "Mine" }""",
                Encoding.UTF8, "application/json");
            (await Http.SendAsync(mine)).EnsureSuccessStatusCode();
        }
        await WaitForAsync(() => receipts.Count >= 1);
        Assert.True(receipts.TryDequeue(out var own));
        Assert.Equal(origin, own.GetProperty("originId").GetGuid());

        var theirs = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent(
                $$"""{ "type": "UpdateChapterTitle", "chapterId": "{{book.ChapterId("ch1")}}", "title": "Theirs" }""",
                Encoding.UTF8, "application/json"));
        theirs.EnsureSuccessStatusCode();
        await WaitForAsync(() => receipts.Count >= 1);
        Assert.True(receipts.TryDequeue(out var anonymous));
        Assert.Equal(Guid.Empty, anonymous.GetProperty("originId").GetGuid());
    }

    [Fact]
    public async Task Invalid_folder_and_unknown_stream_are_rejected()
    {
        await using var conn = Connect();
        await conn.StartAsync();

        var badFolder = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinProject", "../etc"));
        Assert.Contains("Invalid project folder", badFolder.Message);
        var badStream = await Assert.ThrowsAsync<HubException>(() => conn.InvokeAsync("JoinStream", "video"));
        Assert.Contains("Unknown stream", badStream.Message);
    }

    [Fact]
    public async Task Late_stream_joiner_gets_the_in_progress_turn_first_and_non_members_get_nothing()
    {
        var llm = app.Services.GetRequiredService<EventBroadcaster<LlmStreamEvent>>();
        var marker = $"turn-{Guid.NewGuid():N}";
        llm.Publish(new RequestStarted(marker, "prompt", 1, "cfg"));
        llm.Publish(new ThinkingDelta("think "));
        llm.Publish(new ContentDelta("hello"));
        llm.Publish(new ContentDelta(" world"));

        await using var member = Connect();
        await using var bystander = Connect();
        var received = new ConcurrentQueue<JsonElement>();
        var strayed = new ConcurrentQueue<JsonElement>();
        member.On<JsonElement>("llm", received.Enqueue);
        bystander.On<JsonElement>("llm", strayed.Enqueue);
        await member.StartAsync();
        await bystander.StartAsync();

        await member.InvokeAsync("JoinStream", "llm");
        await WaitForAsync(() => received.Count >= 2);

        var messages = received.ToList();
        Assert.Equal("requestStarted", messages[0].GetProperty("kind").GetString());
        Assert.Equal(marker, messages[0].GetProperty("paragraphPreview").GetString());
        Assert.Equal("delta", messages[1].GetProperty("kind").GetString());
        Assert.Equal("think ", messages[1].GetProperty("thinking").GetString());
        Assert.Equal("hello world", messages[1].GetProperty("content").GetString());

        // Live traffic after joining arrives batched; the bystander never joined the stream group.
        llm.Publish(new ContentDelta("!"));
        llm.Publish(new StreamCompleted(1, 2, 3, 4));
        await WaitForAsync(() => received.Any(m => m.GetProperty("kind").GetString() == "streamCompleted"));
        Assert.Empty(strayed);

        await member.InvokeAsync("LeaveStream", "llm");
        var countAfterLeave = received.Count;

        // Sentinel: the bystander joins in the member's place and must see the next turn start.
        // Once it has, and a round trip on the member's own connection has come back after it,
        // anything the group sent the member would already be here.
        await bystander.InvokeAsync("JoinStream", "llm");
        var afterLeave = $"after-leave-{Guid.NewGuid():N}";
        llm.Publish(new RequestStarted(afterLeave, "p", 1, "cfg"));
        await WaitForAsync(() => strayed.Any(m =>
            m.TryGetProperty("paragraphPreview", out var p) && p.GetString() == afterLeave));
        await member.InvokeAsync<JsonElement>("GetSnapshot");
        Assert.Equal(countAfterLeave, received.Count);
    }

    private async Task RetitleAsync(string folder, Guid chapterId, string title)
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent(
                $$"""{ "type": "UpdateChapterTitle", "chapterId": "{{chapterId}}", "title": "{{title}}" }""",
                Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Condition not met within {timeoutMs} ms");
            await Task.Delay(20);
        }
    }
}
