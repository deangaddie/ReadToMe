using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>/api/preflight/{taskKind}</c> (Angular ticket 25) with the fake container controller: the
/// plan for a cold service, the run's per-service stages on the caller's hub connection, the GPU
/// conflict swept and stopped first, and a failed start reported as such.
/// </summary>
[Collection(E2eCollection.Name)]
public class PreflightApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    /// <summary>The seeded LLM config's base URL, which the fake resolves to a service of the same name.</summary>
    private const string Llm = "http://fake-llm";

    private string Url(string taskKind, string rest) => $"{app.BaseUrl}/api/preflight/{taskKind}/{rest}";

    [Fact]
    public async Task Plan_is_ready_when_everything_required_answers()
    {
        var plan = await PlanAsync("CharacterAttribution");

        Assert.True(plan.GetProperty("ready").GetBoolean());
        Assert.Empty(plan.GetProperty("toStart").EnumerateArray());
        Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task Plan_lists_a_stopped_required_service_and_run_starts_it_with_stages()
    {
        app.FakeControl.StatusByName[Llm] = AiServiceStatus.Stopped;
        try
        {
            var plan = await PlanAsync("characterattribution");
            Assert.False(plan.GetProperty("ready").GetBoolean());
            var toStart = Assert.Single(plan.GetProperty("toStart").EnumerateArray());
            Assert.Equal(Llm, toStart.GetProperty("name").GetString());
            Assert.Equal("Stopped", toStart.GetProperty("status").GetString());
            Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("preflight", messages.Enqueue);
            await hub.StartAsync();

            var response = await Http.PostAsJsonAsync(Url("CharacterAttribution", "run"), new { connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var run = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("run").GetString();
            Assert.False(string.IsNullOrEmpty(run));

            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));

            Assert.All(messages, m => Assert.Equal(run, m.GetProperty("run").GetString()));
            var stages = messages.Where(m => m.GetProperty("kind").GetString() == "stage")
                .Select(m => $"{m.GetProperty("name").GetString()}:{m.GetProperty("stage").GetString()}").ToList();
            Assert.Equal([$"{Llm}:waitingToStart", $"{Llm}:starting", $"{Llm}:ready"], stages);
            var done = messages.Last();
            Assert.True(done.GetProperty("ok").GetBoolean());
            Assert.Contains($"start:{Llm}", app.FakeControl.OpLog);
            Assert.Equal(AiServiceStatus.Ready, app.FakeControl.StatusByName[Llm]);
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Gpu_conflict_is_planned_and_stopped_before_the_required_service_starts()
    {
        // The LLM is a GPU service that is cold, while chatterbox (GPU, unrelated) still holds VRAM.
        app.FakeControl.Status = AiServiceStatus.Stopped;
        app.FakeControl.GpuUrls.Add(Llm);
        app.FakeControl.StatusByName[Llm] = AiServiceStatus.Stopped;
        app.FakeControl.StatusByName["chatterbox"] = AiServiceStatus.Ready;
        try
        {
            var plan = await PlanAsync("CharacterAttribution");
            var conflict = Assert.Single(plan.GetProperty("conflicts").EnumerateArray());
            Assert.Equal("chatterbox", conflict.GetProperty("name").GetString());
            Assert.Equal("GPU: one model at a time", conflict.GetProperty("reason").GetString());

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("preflight", messages.Enqueue);
            await hub.StartAsync();

            var response = await Http.PostAsJsonAsync(Url("CharacterAttribution", "run"), new { connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));

            var stages = messages.Where(m => m.GetProperty("kind").GetString() == "stage")
                .Select(m => $"{m.GetProperty("name").GetString()}:{m.GetProperty("stage").GetString()}").ToList();
            Assert.Equal(
            [
                "chatterbox:waitingToStop", $"{Llm}:waitingToStart",
                "chatterbox:stopping", "chatterbox:stopped",
                $"{Llm}:starting", $"{Llm}:ready",
            ], stages);
            Assert.Equal(["shutdown:chatterbox", $"start:{Llm}"], app.FakeControl.OpLog.ToArray());
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Audio_generation_on_a_gpu_tts_sweeps_a_running_llama_even_when_the_tts_is_ready()
    {
        // Acceptance: starting a TTS task while llama runs shows the GPU conflict and stops llama
        // first — even though the TTS server already answers, since llama still holds the VRAM.
        const string tts = "http://fake-tts";
        app.FakeControl.Status = AiServiceStatus.Stopped;
        app.FakeControl.GpuUrls.Add(tts);
        app.FakeControl.StatusByName[tts] = AiServiceStatus.Ready;
        app.FakeControl.StatusByName["http://fake-whisper"] = AiServiceStatus.Ready;
        app.FakeControl.StatusByName["http://fake-similarity"] = AiServiceStatus.Ready;
        app.FakeControl.StatusByName["llama"] = AiServiceStatus.Ready;
        try
        {
            var plan = await PlanAsync("AudioGeneration");
            Assert.False(plan.GetProperty("ready").GetBoolean());
            Assert.Empty(plan.GetProperty("toStart").EnumerateArray());
            var conflict = Assert.Single(plan.GetProperty("conflicts").EnumerateArray());
            Assert.Equal("llama", conflict.GetProperty("name").GetString());

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("preflight", messages.Enqueue);
            await hub.StartAsync();

            await Http.PostAsJsonAsync(Url("AudioGeneration", "run"), new { connectionId = hub.ConnectionId });
            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));

            Assert.True(messages.Last().GetProperty("ok").GetBoolean());
            Assert.Equal(["shutdown:llama"], app.FakeControl.OpLog.ToArray());
            Assert.Equal(AiServiceStatus.Stopped, app.FakeControl.StatusByName["llama"]);
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task A_failed_start_ends_the_run_with_the_reason()
    {
        app.FakeControl.StatusByName[Llm] = AiServiceStatus.Stopped;
        app.FakeControl.StartFailures[Llm] = "health check timed out";
        try
        {
            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("preflight", messages.Enqueue);
            await hub.StartAsync();

            await Http.PostAsJsonAsync(Url("BookEdit", "run"), new { connectionId = hub.ConnectionId });
            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));

            var failed = messages.Single(m => m.TryGetProperty("stage", out var s) && s.GetString() == "failed");
            Assert.Equal("health check timed out", failed.GetProperty("error").GetString());
            var done = messages.Last();
            Assert.False(done.GetProperty("ok").GetBoolean());
            Assert.Contains("health check timed out", done.GetProperty("reason").GetString());
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Unknown_task_kind_is_400()
    {
        var response = await Http.PostAsync(Url("MakeCoffee", "plan"), null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("CharacterAttribution", problem.GetProperty("detail").GetString());
    }

    private async Task<JsonElement> PlanAsync(string taskKind)
    {
        var response = await Http.PostAsync(Url(taskKind, "plan"), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
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
