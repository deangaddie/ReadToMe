using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The LLM settings page's own endpoints (Angular ticket 21): model list for an unsaved config,
/// the test console run, the attribution chain, and base URL → managed service resolution.
/// </summary>
[Collection(E2eCollection.Name)]
public class LlmSettingsApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private string Url(string rest = "") => $"{app.BaseUrl}/api/settings/llm{rest}";

    private async Task<JsonElement> GetJsonAsync(string url) =>
        JsonDocument.Parse(await Http.GetStringAsync(url)).RootElement;

    private async Task<int> FakeConfigIdAsync() =>
        (await GetJsonAsync(Url())).EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "fake").GetProperty("id").GetInt32();

    private async Task<int> CreateConfigAsync(string name)
    {
        var create = await Http.PostAsJsonAsync(Url(), new { name, baseUrl = "http://fake-llm" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    // ── models ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Models_lists_the_servers_models_for_an_unsaved_config()
    {
        var response = await Http.PostAsJsonAsync(Url("/models"), new { name = "unsaved", baseUrl = "http://fake-llm" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var models = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("models");
        Assert.Contains(FakeAiRoutingHandler.DefaultModel, models.EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Models_is_422_with_the_reason_when_the_server_cannot_be_reached()
    {
        var response = await Http.PostAsJsonAsync(Url("/models"), new { name = "bad", baseUrl = "http://no-such-llm" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    // ── test console ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Test_run_streams_over_the_llm_stream_and_reports_done_to_the_caller()
    {
        var id = await FakeConfigIdAsync();
        app.FakeAi.LlmReply = _ => "hello from the fake";
        try
        {
            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var llm = new ConcurrentQueue<JsonElement>();
            var test = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("llm", llm.Enqueue);
            hub.On<JsonElement>("llmTest", test.Enqueue);
            await hub.StartAsync();
            await hub.InvokeAsync("JoinStream", "llm");

            var send = await Http.PostAsJsonAsync(Url($"/{id}/test"),
                new { prompt = "say hello", connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);

            await WaitForAsync(() => !test.IsEmpty);
            var done = test.Single();
            Assert.Equal("done", done.GetProperty("kind").GetString());
            Assert.Equal(id, done.GetProperty("configId").GetInt32());

            await WaitForAsync(() => llm.Any(m => m.GetProperty("kind").GetString() == "runEnded"));
            var kinds = llm.Select(m => m.GetProperty("kind").GetString()).ToList();
            Assert.Contains("runStarted", kinds);
            Assert.Contains("requestStarted", kinds);
            lock (app.FakeAi.LlmPromptsSeen) Assert.Contains("say hello", app.FakeAi.LlmPromptsSeen);

            Assert.False((await GetJsonAsync(Url("/test"))).GetProperty("running").GetBoolean());
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Test_run_can_be_cancelled_and_a_second_send_while_running_is_409()
    {
        var id = await FakeConfigIdAsync();
        app.FakeAi.LlmDelay = TimeSpan.FromSeconds(30);
        try
        {
            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var test = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("llmTest", test.Enqueue);
            await hub.StartAsync();

            var send = await Http.PostAsJsonAsync(Url($"/{id}/test"),
                new { prompt = "slow", connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, send.StatusCode);

            var status = await GetJsonAsync(Url("/test"));
            Assert.True(status.GetProperty("running").GetBoolean());
            Assert.Equal(id, status.GetProperty("configId").GetInt32());

            var second = await Http.PostAsJsonAsync(Url($"/{id}/test"), new { prompt = "again" });
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

            var cancel = await Http.PostAsync(Url($"/{id}/test/cancel"), null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

            await WaitForAsync(() => !test.IsEmpty);
            Assert.Equal("cancelled", test.Single().GetProperty("kind").GetString());
            Assert.False((await GetJsonAsync(Url("/test"))).GetProperty("running").GetBoolean());

            // Cancelling with nothing running is harmless.
            Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync(Url($"/{id}/test/cancel"), null)).StatusCode);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Test_run_is_404_for_an_unknown_config_and_400_for_a_blank_prompt()
    {
        var id = await FakeConfigIdAsync();

        var unknown = await Http.PostAsJsonAsync(Url("/999999/test"), new { prompt = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var blank = await Http.PostAsJsonAsync(Url($"/{id}/test"), new { prompt = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    // ── attribution chain ────────────────────────────────────────────────────

    [Fact]
    public async Task Attribution_chain_put_then_get_round_trips_order_flags_and_self_consistency()
    {
        var a = await CreateConfigAsync($"chain-a-{Guid.NewGuid():N}");
        var b = await CreateConfigAsync($"chain-b-{Guid.NewGuid():N}");
        try
        {
            var put = await Http.PutAsJsonAsync(Url("/attribution-chain"), new
            {
                steps = new object[]
                {
                    new { configId = b, thinking = true, promptStyle = 1 },
                    new { configId = a, thinking = false, promptStyle = 0 },
                    new { configId = b, thinking = true, promptStyle = 1 }, // exact duplicate collapses
                },
                selfConsistency = true,
            });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var chain = await GetJsonAsync(Url("/attribution-chain"));
            Assert.True(chain.GetProperty("selfConsistency").GetBoolean());
            var steps = chain.GetProperty("steps").EnumerateArray().ToList();
            Assert.Equal(2, steps.Count);
            Assert.Equal(b, steps[0].GetProperty("configId").GetInt32());
            Assert.True(steps[0].GetProperty("thinking").GetBoolean());
            Assert.Equal(1, steps[0].GetProperty("promptStyle").GetInt32());
            Assert.Equal(a, steps[1].GetProperty("configId").GetInt32());

            var resolved = chain.GetProperty("resolved").EnumerateArray().ToList();
            Assert.Equal(2, resolved.Count);
            Assert.Equal(b, resolved[0].GetProperty("config").GetProperty("id").GetInt32());
            Assert.Equal(1, resolved[0].GetProperty("promptStyle").GetInt32());

            var available = chain.GetProperty("available").EnumerateArray().Select(c => c.GetProperty("id").GetInt32());
            Assert.Contains(a, available);

            // Deleting a config prunes its rungs.
            await Http.DeleteAsync(Url($"/{b}"));
            var pruned = (await GetJsonAsync(Url("/attribution-chain"))).GetProperty("steps");
            Assert.Equal(a, Assert.Single(pruned.EnumerateArray()).GetProperty("configId").GetInt32());
        }
        finally
        {
            await Http.PutAsJsonAsync(Url("/attribution-chain"), new { steps = Array.Empty<object>(), selfConsistency = false });
            await Http.DeleteAsync(Url($"/{a}"));
            await Http.DeleteAsync(Url($"/{b}"));
        }
    }

    [Fact]
    public async Task Empty_chain_resolves_to_the_default_config()
    {
        var fake = await FakeConfigIdAsync();

        var chain = await GetJsonAsync(Url("/attribution-chain"));

        Assert.Equal(0, chain.GetProperty("steps").GetArrayLength());
        var fallback = Assert.Single(chain.GetProperty("resolved").EnumerateArray());
        Assert.Equal(fake, fallback.GetProperty("config").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Attribution_chain_put_is_422_for_a_step_naming_no_config()
    {
        var put = await Http.PutAsJsonAsync(Url("/attribution-chain"), new
        {
            steps = new[] { new { configId = 999999, thinking = false, promptStyle = 0 } },
            selfConsistency = false,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, put.StatusCode);
    }

    // ── ai-services/resolve ──────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_maps_a_base_url_to_its_managed_service_or_404()
    {
        var hit = await Http.GetAsync($"{app.BaseUrl}/api/ai-services/resolve?baseUrl={Uri.EscapeDataString("http://127.0.0.1:8080/")}");
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        var service = JsonDocument.Parse(await hit.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("llama", service.GetProperty("name").GetString());

        var miss = await Http.GetAsync($"{app.BaseUrl}/api/ai-services/resolve?baseUrl={Uri.EscapeDataString("https://api.example.com")}");
        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);

        var none = await Http.GetAsync($"{app.BaseUrl}/api/ai-services/resolve");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Condition not met within {timeoutMs} ms");
            await Task.Delay(20);
        }
    }
}
