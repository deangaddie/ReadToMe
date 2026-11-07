using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The services dashboard's endpoints (Angular ticket 25): status of every managed container in one
/// call, and Start / Restart / Shutdown as 202s whose outcome arrives as <c>serviceStatus</c> on
/// <c>/hubs/live</c> — with the fake container controller, so no docker.
/// </summary>
public class AiServicesApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private string Url(string rest = "") => $"{app.BaseUrl}/api/ai-services{rest}";

    [Fact]
    public async Task Status_lists_every_managed_service_with_the_probe_result_and_a_single_probe_also_pushes()
    {
        app.FakeControl.StatusByName["whisper"] = AiServiceStatus.Stopped;
        app.FakeControl.StatusByName["minilm-l6"] = AiServiceStatus.Starting;
        try
        {
            var all = JsonDocument.Parse(await Http.GetStringAsync(Url("/status"))).RootElement.EnumerateArray()
                .ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("status").GetString());

            var catalog = JsonDocument.Parse(await Http.GetStringAsync(Url())).RootElement.EnumerateArray()
                .Select(s => s.GetProperty("name").GetString()!).ToList();
            Assert.Equal(catalog.OrderBy(n => n), all.Keys.OrderBy(n => n));
            Assert.DoesNotContain("whisper-cpu", catalog);
            Assert.Equal("Stopped", all["whisper"]);
            Assert.Equal("Ready", all["llama"]);

            // Probing one service also pushes its status, with no op, to every client.
            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var pushed = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("serviceStatus", pushed.Enqueue);
            await hub.StartJoinedAsync();

            var probe = JsonDocument.Parse(await Http.GetStringAsync(Url("/minilm-l6/status"))).RootElement;
            Assert.Equal("Starting", probe.GetProperty("status").GetString());

            await WaitForAsync(() => pushed.Any(m => m.GetProperty("name").GetString() == "minilm-l6"));
            var message = pushed.First(m => m.GetProperty("name").GetString() == "minilm-l6");
            Assert.Equal("Starting", message.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, message.GetProperty("op").ValueKind);
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Shutdown_start_and_restart_answer_202_and_push_the_outcome_to_every_client()
    {
        app.FakeControl.StatusByName["llama"] = AiServiceStatus.Ready;
        app.FakeControl.StatusByName["whisper"] = AiServiceStatus.Ready;
        try
        {
            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var pushed = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("serviceStatus", pushed.Enqueue);
            await hub.StartJoinedAsync();

            var shutdown = await Http.PostAsync(Url("/llama/shutdown"), null);
            Assert.Equal(HttpStatusCode.Accepted, shutdown.StatusCode);
            await WaitForAsync(() => pushed.Any(m => m.GetProperty("op").GetString() == "Shutdown"));

            var stopped = pushed.First(m => m.GetProperty("op").GetString() == "Shutdown");
            Assert.Equal("llama", stopped.GetProperty("name").GetString());
            Assert.Equal("Stopped", stopped.GetProperty("status").GetString());
            Assert.True(stopped.GetProperty("ok").GetBoolean());
            Assert.Contains("shutdown:llama", app.FakeControl.OpLog);

            var start = await Http.PostAsync(Url("/llama/start"), null);
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            await WaitForAsync(() => pushed.Any(m => m.GetProperty("op").GetString() == "Start"));
            var ready = pushed.First(m => m.GetProperty("op").GetString() == "Start");
            Assert.Equal("Ready", ready.GetProperty("status").GetString());

            var restart = await Http.PostAsync(Url("/whisper/restart"), null);
            Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
            await WaitForAsync(() => pushed.Any(m => m.GetProperty("op").GetString() == "Restart"));
            var restarted = pushed.First(m => m.GetProperty("op").GetString() == "Restart");
            Assert.Equal("whisper", restarted.GetProperty("name").GetString());
            Assert.Equal("Ready", restarted.GetProperty("status").GetString());
            Assert.Contains("restart:whisper", app.FakeControl.OpLog);

            // The snapshot remembers what was last observed, for a client that connects later.
            var snapshot = await hub.InvokeAsync<JsonElement>("GetSnapshot");
            Assert.Equal("Ready", snapshot.GetProperty("serviceStatus").GetProperty("llama").GetString());
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Ops_and_probes_on_an_unknown_service_are_404()
    {
        var response = await Http.PostAsync(Url("/no-such-service/restart"), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var probe = await Http.GetAsync(Url("/no-such-service/status"));
        Assert.Equal(HttpStatusCode.NotFound, probe.StatusCode);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "timed out waiting for hub message");
            await Task.Delay(50);
        }
    }
}
