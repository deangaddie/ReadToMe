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
/// plan for a cold service or a GPU rival, and the run's per-service stages on the caller's hub
/// connection. The conflict sweep and a failed start are PreflightRunCoordinatorTests.
/// </summary>
public class PreflightApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    /// <summary>The seeded LLM config's base URL, which the fake resolves to a service of the same name.</summary>
    private const string Llm = "http://fake-llm";

    private string Url(string taskKind, string rest) => $"{app.BaseUrl}/api/preflight/{taskKind}/{rest}";

    [Fact]
    public async Task Plan_is_ready_when_everything_required_answers_and_names_a_running_gpu_rival()
    {
        var plan = await PlanAsync("CharacterAttribution");

        Assert.True(plan.GetProperty("ready").GetBoolean());
        Assert.Empty(plan.GetProperty("toStart").EnumerateArray());
        Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());

        // The LLM is a GPU service while audiocpp (GPU, unrelated) still holds VRAM. The run that
        // sweeps it is PreflightRunCoordinatorTests; the planner's rules are AiPreflightPlannerTests.
        app.FakeControl.Status = AiServiceStatus.Stopped;
        app.FakeControl.GpuUrls.Add(Llm);
        app.FakeControl.StatusByName[Llm] = AiServiceStatus.Ready;
        app.FakeControl.StatusByName["audiocpp"] = AiServiceStatus.Ready;
        try
        {
            var conflicted = await PlanAsync("CharacterAttribution");
            Assert.False(conflicted.GetProperty("ready").GetBoolean());
            var conflict = Assert.Single(conflicted.GetProperty("conflicts").EnumerateArray());
            Assert.Equal("audiocpp", conflict.GetProperty("name").GetString());
            Assert.Equal("GPU: one model at a time", conflict.GetProperty("reason").GetString());
        }
        finally
        {
            app.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Plan_lists_a_stopped_required_service_and_run_starts_it_with_stages()
    {
        app.FakeControl.StatusByName[Llm] = AiServiceStatus.Stopped;
        try
        {
            var plan = await PlanAsync("CharacterAttribution");
            Assert.False(plan.GetProperty("ready").GetBoolean());
            var toStart = Assert.Single(plan.GetProperty("toStart").EnumerateArray());
            Assert.Equal(Llm, toStart.GetProperty("name").GetString());
            Assert.Equal("Stopped", toStart.GetProperty("status").GetString());
            Assert.Empty(plan.GetProperty("conflicts").EnumerateArray());

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("preflight", messages.Enqueue);
            await hub.StartJoinedAsync();

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
