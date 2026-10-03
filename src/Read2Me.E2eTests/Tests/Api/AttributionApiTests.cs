using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        await app.WaitForQueueDrainAsync("/api/attribution/queue");

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

    /// <summary>
    /// The seeded llama config is switchable and its model starts <c>unloaded</c> here, so the first
    /// attribution request has to go through the switch-and-wait gate: autoload trigger, poll
    /// <c>GET /v1/models</c> until it reads <c>loaded</c>, then the real request. A stamped speaker
    /// proves the whole path ran, because the real request only goes out once the model has loaded.
    /// </summary>
    [Fact]
    public async Task Attribution_on_an_unloaded_model_waits_for_the_switch_then_resolves()
    {
        var folder = $"api-attr-switch-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Switch Api Book", "Author", characterName: "Alice");
        app.FakeAi.LlmModels = FakeLlmModelStore.Switching(
            target: FakeAiRoutingHandler.DefaultModel, loadsAfterPolls: 2);
        app.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        try
        {
            var chapterId = builder.ChapterId("ch1");
            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue",
                new { level = "chapter", nodeId = chapterId, unprocessedOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            await app.WaitForQueueDrainAsync("/api/attribution/queue", timeoutSeconds: 60);

            var children = JsonDocument.Parse(await Http.GetStringAsync(
                $"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{chapterId}/children"));
            var line = children.RootElement.GetProperty("paragraphs").EnumerateArray()
                .SelectMany(p => p.GetProperty("items").EnumerateArray())
                .Single(i => i.GetProperty("id").GetGuid() == builder.ItemId("line1"));
            Assert.Equal(builder.CharacterId("Alice"), line.GetProperty("characterId").GetGuid());

            // The model the request named is the one now resident.
            Assert.Contains("\"loaded\"", app.FakeAi.LlmModels.RenderJson());
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    /// <summary>
    /// The chapter pass end to end (attribution-grammar 02): a Chapter-style active config sends one
    /// grammar-restricted, greedy, thinking-off request per dialog item, with the roster in a system
    /// message, and the <c>Name | delivery</c> answers stamp speaker and voice instructions.
    /// </summary>
    [Fact]
    public async Task Chapter_style_attributes_each_dialog_item_with_a_roster_grammar()
    {
        var folder = $"api-attr-chapter-{Guid.NewGuid():N}";
        var builder = await app.SeedThreeDialogParagraphProjectAsync(folder, "Chapter Api Book", "Author", characterName: "Alice");
        var asked = new List<string>();
        app.FakeAi.LlmReply = prompt =>
        {
            // The question names the item as ⟦k.ii⟧; answer it by that marker.
            var marker = System.Text.RegularExpressions.Regex.Match(prompt, @"Who speaks (⟦\d+\.\d+⟧)").Groups[1].Value;
            lock (asked) asked.Add(marker);
            return "Alice | calm";
        };

        using var scope = app.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<Read2Me.Services.LlmSettingsService>();
        var previousActive = await settings.GetActiveConfigIdAsync();
        var previousChain = await settings.GetAttributionChainEntriesAsync();
        var chapterConfig = await settings.CreateConfigAsync(new Read2Me.AppData.Entities.LlmServerConfig
        {
            Name = $"fake-chapter-{Guid.NewGuid():N}",
            BaseUrl = "http://fake-llm",
            Model = FakeAiRoutingHandler.DefaultModel,
            SupportsModelSwitch = true,
            MaxTokens = 8192,
            Temperature = 0.8,
            PromptStyle = Read2Me.AppData.Entities.AttributionPromptStyle.Chapter,
        });
        try
        {
            await settings.SetActiveConfigAsync(chapterConfig.Id);
            await settings.SetAttributionChainEntriesAsync([new Read2Me.Services.AttributionChainEntry(chapterConfig.Id, Thinking: false)]);

            var chapterId = builder.ChapterId("ch1");
            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue",
                new { level = "chapter", nodeId = chapterId, unprocessedOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
            await app.WaitForQueueDrainAsync("/api/attribution/queue");

            // One request per dialog item, in chapter order (p1 is narration, so k starts at 1).
            Assert.Equal(["⟦1.0⟧", "⟦2.0⟧", "⟦3.0⟧"], asked);

            List<System.Text.Json.Nodes.JsonObject> bodies;
            lock (app.FakeAi.LlmBodiesSeen) bodies = [.. app.FakeAi.LlmBodiesSeen.Where(b => b["grammar"] is not null)];
            Assert.Equal(3, bodies.Count);
            Assert.All(bodies, b =>
            {
                Assert.Contains("\"Alice\"", b["grammar"]!.GetValue<string>());
                Assert.Equal(0, b["temperature"]!.GetValue<double>());
                Assert.Equal(48, b["max_tokens"]!.GetValue<int>());
                Assert.False(b["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
                Assert.Null(b["response_format"]);
                var messages = b["messages"]!.AsArray();
                Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
                Assert.Contains("- Alice", messages[0]!["content"]!.GetValue<string>());
                Assert.Equal("user", messages[1]!["role"]!.GetValue<string>());
            });
            // Feed-forward: the last ask shows the earlier answers as labels.
            Assert.Contains("{Alice} ⟦1.0⟧", bodies[2]["messages"]![1]!["content"]!.GetValue<string>());

            var children = JsonDocument.Parse(await Http.GetStringAsync(
                $"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{chapterId}/children"));
            var items = children.RootElement.GetProperty("paragraphs").EnumerateArray()
                .SelectMany(p => p.GetProperty("items").EnumerateArray())
                .ToDictionary(i => i.GetProperty("id").GetGuid());
            foreach (var line in new[] { "line1", "line2", "line3" })
            {
                var item = items[builder.ItemId(line)];
                Assert.Equal(builder.CharacterId("Alice"), item.GetProperty("characterId").GetGuid());
                Assert.Equal("calm", item.GetProperty("voiceInstructions").GetString());
            }
        }
        finally
        {
            await settings.SetAttributionChainEntriesAsync(previousChain);
            if (previousActive is { } id) await settings.SetActiveConfigAsync(id);
            await settings.DeleteConfigAsync(chapterConfig.Id);
            app.FakeAi.Reset();
        }
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

    [Fact]
    public async Task Dismiss_returns_ok_and_is_idempotent()
    {
        var first = await Http.PostAsync($"{app.BaseUrl}/api/attribution/dismiss", null);
        var second = await Http.PostAsync($"{app.BaseUrl}/api/attribution/dismiss", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }
}
