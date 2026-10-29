using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Full check over the agent API on the in-proc host: start, join, Preflight refused for the run,
/// cancel. The fake encode holds the run in its one step so the test can act mid-run. The runner's
/// rules (refusal, gates, watchdog, progress) are SetupCheckFullCheckTests.
/// </summary>
public class SetupCheckFullApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private string Url(string rest = "") => $"{app.BaseUrl}/api/setup-check{rest}";

    [Fact]
    public async Task A_run_starts_a_second_request_joins_it_Preflight_waits_and_cancel_lets_the_step_finish()
    {
        await QuickCheckedAsync();
        app.FfmpegEncode.HoldNext();
        try
        {
            var started = await Http.PostAsync(Url("/full"), null);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var run = (await ReadAsync(started)).GetProperty("fullRun");
            Assert.Equal(1, run.GetProperty("step").GetInt32());
            Assert.Equal("local", run.GetProperty("lane").GetString());
            Assert.Equal("Encoding a 1 s tone to .m4b", run.GetProperty("label").GetString());

            var joined = await Http.PostAsync(Url("/full"), null);
            Assert.Equal(HttpStatusCode.Accepted, joined.StatusCode);
            Assert.Equal(1, (await ReadAsync(joined)).GetProperty("fullRun").GetProperty("total").GetInt32());

            // Cancelled once the run is in its step, which then finishes.
            await app.FfmpegEncode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var plan = await Http.PostAsync($"{app.BaseUrl}/api/preflight/CharacterAttribution/plan", null);
            Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode);
            Assert.Equal("Setup check running", (await ReadAsync(plan)).GetProperty("title").GetString());

            var cancelled = await Http.PostAsync(Url("/full/cancel"), null);
            Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
            Assert.True((await ReadAsync(cancelled)).GetProperty("fullRun").GetProperty("cancelling").GetBoolean());
        }
        finally
        {
            app.FfmpegEncode.Release();
        }

        var final = await FinishedAsync();
        var ffmpeg = final.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "ffmpeg");
        Assert.Equal("passed", ffmpeg.GetProperty("result").GetString());
        Assert.Equal("full", ffmpeg.GetProperty("tier").GetString());
        Assert.Equal(JsonValueKind.String, final.GetProperty("fullCheckedAt").ValueKind);

        var afterwards = await Http.PostAsync($"{app.BaseUrl}/api/preflight/CharacterAttribution/plan", null);
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
        var nothingToCancel = await Http.PostAsync(Url("/full/cancel"), null);
        Assert.Equal(HttpStatusCode.Conflict, nothingToCancel.StatusCode);
    }

    private async Task QuickCheckedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await ReadAsync(await Http.GetAsync(Url()))).GetProperty("quickCheckedAt").ValueKind == JsonValueKind.Null)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The start-up Quick check had not finished after 30 s.");
            await Task.Delay(100);
        }
    }

    private async Task<JsonElement> FinishedAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var report = await ReadAsync(await Http.GetAsync(Url()));
            if (!report.TryGetProperty("fullRun", out var run) || run.ValueKind == JsonValueKind.Null)
                return report;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The Full check had not ended after 30 s.");
            await Task.Delay(100);
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
