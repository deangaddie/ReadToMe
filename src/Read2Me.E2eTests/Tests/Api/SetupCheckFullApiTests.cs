using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Full check over the agent API on the in-proc host: start, join, Preflight and the direct AI endpoints refused for the run,
/// cancel. The fake encode holds the run in its first step, the ffmpeg encode, so the test can act mid-run. The runner's
/// rules (refusal, gates, watchdog, progress) are SetupCheckFullCheckTests.
/// </summary>
public class SetupCheckFullApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private static readonly string[] DirectAiRoutes =
    [
        "/api/projects/any/characters/discover",
        "/api/projects/any/book-edits/plan",
        "/api/projects/any/pronunciations/try",
        "/api/settings/voice-design/1/test",
        "/api/ai-services/llama/start",
    ];

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
            Assert.Equal("Local", run.GetProperty("lane").GetString());
            Assert.Equal("Encoding a 1 s tone to .m4b", run.GetProperty("label").GetString());

            var joined = await Http.PostAsync(Url("/full"), null);
            Assert.Equal(HttpStatusCode.Accepted, joined.StatusCode);
            // The seeded configs add a CPU step each for transcription and similarity, and GPU steps
            // for paragraph TTS, voice design and the one LLM model.
            Assert.Equal(6, (await ReadAsync(joined)).GetProperty("fullRun").GetProperty("total").GetInt32());

            // Cancelled once the run is in its step, which then finishes.
            await app.FfmpegEncode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var plan = await Http.PostAsync($"{app.BaseUrl}/api/preflight/CharacterAttribution/plan", null);
            Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode);
            Assert.Equal("Setup check running", (await ReadAsync(plan)).GetProperty("title").GetString());
            // The endpoints that drive an AI service without Preflight refuse too.
            foreach (var route in DirectAiRoutes)
            {
                var refused = await Http.PostAsync($"{app.BaseUrl}{route}", new StringContent("{}", Encoding.UTF8, "application/json"));
                Assert.True(refused.StatusCode == HttpStatusCode.Conflict, $"{route} answered {(int)refused.StatusCode}");
                Assert.Equal("Setup check running", (await ReadAsync(refused)).GetProperty("title").GetString());
            }

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
        Assert.Equal("Passed", ffmpeg.GetProperty("result").GetString());
        Assert.Equal("Full", ffmpeg.GetProperty("tier").GetString());
        Assert.Equal(JsonValueKind.String, final.GetProperty("fullCheckedAt").ValueKind);
        // Cancelled during the ffmpeg step: the CPU checks never ran, so no container was driven.
        var transcription = final.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "transcription");
        Assert.Equal("NotRun", transcription.GetProperty("result").GetString());
        Assert.Equal("Nothing to put back: the check drove no containers.", final.GetProperty("putBack").GetString());

        var afterwards = await Http.PostAsync($"{app.BaseUrl}/api/preflight/CharacterAttribution/plan", null);
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
        var nothingToCancel = await Http.PostAsync(Url("/full/cancel"), null);
        Assert.Equal(HttpStatusCode.Conflict, nothingToCancel.StatusCode);
    }

    [Fact]
    public async Task One_check_re_runs_alone_a_second_re_run_is_refused_and_a_Quick_tier_id_is_not_found()
    {
        await QuickCheckedAsync();
        var before = await ReadAsync(await Http.GetAsync(Url()));
        var ffmpegBefore = before.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "ffmpeg");
        Assert.False(ffmpegBefore.GetProperty("rerun").GetProperty("gpu").GetBoolean());
        app.FfmpegEncode.HoldNext();
        try
        {
            var started = await Http.PostAsync(Url("/checks/ffmpeg/rerun"), null);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var run = (await ReadAsync(started)).GetProperty("fullRun");
            Assert.Equal(1, run.GetProperty("total").GetInt32());
            Assert.Equal("Encoding a 1 s tone to .m4b", run.GetProperty("label").GetString());

            var again = await Http.PostAsync(Url("/checks/ffmpeg/rerun"), null);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Equal("A check is re-running: re-run this one once it ends.", (await ReadAsync(again)).GetProperty("detail").GetString());
            await app.FfmpegEncode.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            app.FfmpegEncode.Release();
        }

        var final = await FinishedAsync();
        var ffmpeg = final.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "ffmpeg");
        Assert.Equal("Passed", ffmpeg.GetProperty("result").GetString());
        // A re-run isn't a whole Full check: its time stays as it was.
        Assert.Equal(before.GetProperty("fullCheckedAt").ToString(), final.GetProperty("fullCheckedAt").ToString());
        Assert.Equal("Nothing to put back: the check drove no containers.", final.GetProperty("putBack").GetString());

        var quickTier = await Http.PostAsync(Url("/checks/docker/rerun"), null);
        Assert.Equal(HttpStatusCode.NotFound, quickTier.StatusCode);
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
