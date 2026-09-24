using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Models;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The audio-processing settings surface behind Angular ticket 24: the full snapshot (scalars,
/// pauses, step configs), the per-card writes, the ffmpeg probe, the recent-sample picker and the
/// one-step A/B preview render.
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioProcessingApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();
    private string Base => $"{app.BaseUrl}/api/settings/audio-processing";

    private async Task<JsonElement> FullAsync() =>
        JsonDocument.Parse(await Http.GetStringAsync($"{Base}/full")).RootElement;

    [Fact]
    public async Task Full_snapshot_carries_scalars_pauses_and_both_paragraph_steps()
    {
        var full = await FullAsync();

        Assert.True(full.TryGetProperty("werThreshold", out _));
        Assert.True(full.TryGetProperty("chunkPauseMs", out _));
        Assert.True(full.TryGetProperty("audioMaxAttempts", out _));
        var pauses = full.GetProperty("pauses");
        foreach (var key in new[] { "volumeMs", "partMs", "chapterMs", "paragraphMs", "pauseMs" })
            Assert.True(pauses.TryGetProperty(key, out _), key);

        var steps = full.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(
            [AudioPostProcessStepIds.SilenceTrim, AudioPostProcessStepIds.ConsonantSoften],
            steps.Select(s => s.GetProperty("stepId").GetString()));
        Assert.True(steps[0].GetProperty("settings").TryGetProperty("thresholdDb", out _));
        Assert.True(steps[1].GetProperty("settings").TryGetProperty("preset", out _));
    }

    [Fact]
    public async Task Pauses_put_roundtrips()
    {
        var before = (await FullAsync()).GetProperty("pauses");
        var original = new
        {
            volumeMs = before.GetProperty("volumeMs").GetInt32(),
            partMs = before.GetProperty("partMs").GetInt32(),
            chapterMs = before.GetProperty("chapterMs").GetInt32(),
            paragraphMs = before.GetProperty("paragraphMs").GetInt32(),
            pauseMs = before.GetProperty("pauseMs").GetInt32(),
        };
        try
        {
            var put = await Http.PutAsJsonAsync($"{Base}/pauses",
                new { volumeMs = 4100, partMs = 3100, chapterMs = 2600, paragraphMs = 810, pauseMs = 510 });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var after = (await FullAsync()).GetProperty("pauses");
            Assert.Equal(4100, after.GetProperty("volumeMs").GetInt32());
            Assert.Equal(510, after.GetProperty("pauseMs").GetInt32());

            // The service Blazor reads sees the same row.
            using var scope = app.Services.CreateScope();
            var settings = await scope.ServiceProvider.GetRequiredService<AudioProcessingSettingsService>().GetAsync();
            Assert.Equal(2600, settings.ChapterPauseMs);
        }
        finally
        {
            await Http.PutAsJsonAsync($"{Base}/pauses", original);
        }
    }

    [Fact]
    public async Task Negative_pause_is_400()
    {
        var put = await Http.PutAsJsonAsync($"{Base}/pauses",
            new { volumeMs = -1, partMs = 0, chapterMs = 0, paragraphMs = 0, pauseMs = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Step_put_upserts_one_step_and_keeps_the_other()
    {
        var before = (await FullAsync()).GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("stepId").GetString() == AudioPostProcessStepIds.ConsonantSoften)
            .GetRawText();
        try
        {
            var put = await Http.PutAsJsonAsync($"{Base}/steps/{AudioPostProcessStepIds.ConsonantSoften}", new
            {
                stepId = AudioPostProcessStepIds.ConsonantSoften,
                enabled = true,
                settings = new { engine = "deesser", preset = "light" },
            });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var steps = (await FullAsync()).GetProperty("steps").EnumerateArray().ToList();
            var soften = steps.Single(s => s.GetProperty("stepId").GetString() == AudioPostProcessStepIds.ConsonantSoften);
            Assert.True(soften.GetProperty("enabled").GetBoolean());
            Assert.Equal("deesser", soften.GetProperty("settings").GetProperty("engine").GetString());
            Assert.Equal("light", soften.GetProperty("settings").GetProperty("preset").GetString());

            var trim = steps.Single(s => s.GetProperty("stepId").GetString() == AudioPostProcessStepIds.SilenceTrim);
            Assert.True(trim.GetProperty("settings").TryGetProperty("thresholdDb", out _));
        }
        finally
        {
            await Http.PutAsync($"{Base}/steps/{AudioPostProcessStepIds.ConsonantSoften}",
                new StringContent(before, Encoding.UTF8, "application/json"));
        }
    }

    [Fact]
    public async Task Step_put_refuses_an_unknown_step_and_a_mismatched_body()
    {
        var unknown = await Http.PutAsJsonAsync($"{Base}/steps/de-plosive",
            new { stepId = "de-plosive", enabled = true, settings = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var mismatch = await Http.PutAsJsonAsync($"{Base}/steps/{AudioPostProcessStepIds.SilenceTrim}",
            new { stepId = AudioPostProcessStepIds.ConsonantSoften, enabled = true, settings = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task Ffmpeg_test_persists_the_path_then_probes()
    {
        var original = (await FullAsync()).GetProperty("ffmpegPath").GetString();
        try
        {
            var test = await Http.PostAsJsonAsync($"{Base}/ffmpeg/test", new { ffmpegPath = @"C:\tools\ffmpeg.exe" });
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            var result = JsonDocument.Parse(await test.Content.ReadAsStringAsync()).RootElement;
            Assert.True(result.GetProperty("success").GetBoolean());
            Assert.Equal("fake ffmpeg", result.GetProperty("message").GetString());

            Assert.Equal(@"C:\tools\ffmpeg.exe", (await FullAsync()).GetProperty("ffmpegPath").GetString());
        }
        finally
        {
            await Http.PutAsJsonAsync(Base, new { ffmpegPath = original ?? "" });
        }
    }

    [Fact]
    public async Task Recent_samples_list_items_with_a_cached_preview_source()
    {
        var folder = $"api-samples-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Samples Book", "Author");
        var itemId = builder.ItemId("n1");
        // A sample is a generated item: it needs its stored WAV as well as a cached Preview Source.
        await app.SeedItemAudioAsync(folder, itemId, builder.CharacterId("Alice"));
        await SavePreviewSourceAsync(folder, itemId);

        var samples = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/audio/samples/recent?limit=20"))
            .RootElement.EnumerateArray().ToList();

        var sample = samples.Single(s => s.GetProperty("itemId").GetGuid() == itemId);
        Assert.Equal(folder, sample.GetProperty("folder").GetString());
        Assert.Equal("Samples Book", sample.GetProperty("projectTitle").GetString());
        Assert.False(string.IsNullOrEmpty(sample.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Preview_renders_the_draft_over_the_sample_and_answers_both_urls()
    {
        var folder = $"api-preview-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Preview Book", "Author");
        var itemId = builder.ItemId("n1");
        await SavePreviewSourceAsync(folder, itemId);

        var response = await Http.PostAsJsonAsync($"{Base}/steps/{AudioPostProcessStepIds.SilenceTrim}/preview", new
        {
            sample = new { folder, itemId },
            settings = new { thresholdDb = -40, padMs = 0 },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.False(string.IsNullOrEmpty(body.GetProperty("previewId").GetString()));
        Assert.Equal($"/preview-source/{folder}/{itemId:D}", body.GetProperty("originalUrl").GetString());
        var processed = body.GetProperty("processedUrl").GetString()!;
        Assert.StartsWith("/audio-preview/", processed);

        // No ffmpeg on the test host: the step falls back, and both players still have a WAV.
        Assert.False(body.GetProperty("appliedOk").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("removedMs").ValueKind);

        using var original = await Http.GetAsync($"{app.BaseUrl}{body.GetProperty("originalUrl").GetString()}");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        using var wav = await Http.GetAsync($"{app.BaseUrl}{processed}");
        Assert.Equal(HttpStatusCode.OK, wav.StatusCode);
        Assert.Equal("audio/wav", wav.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Preview_of_an_evicted_sample_is_422_unknown_step_400_unknown_folder_404()
    {
        var folder = $"api-preview-gone-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Gone Book", "Author");
        var itemId = builder.ItemId("n1");

        var gone = await Http.PostAsJsonAsync($"{Base}/steps/{AudioPostProcessStepIds.SilenceTrim}/preview",
            new { sample = new { folder, itemId }, settings = new { } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, gone.StatusCode);

        var unknownStep = await Http.PostAsJsonAsync($"{Base}/steps/de-plosive/preview",
            new { sample = new { folder, itemId }, settings = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownStep.StatusCode);

        var unknownFolder = await Http.PostAsJsonAsync($"{Base}/steps/{AudioPostProcessStepIds.SilenceTrim}/preview",
            new { sample = new { folder = "no-such-book", itemId }, settings = new { } });
        Assert.Equal(HttpStatusCode.NotFound, unknownFolder.StatusCode);
    }

    private async Task SavePreviewSourceAsync(string folder, Guid itemId)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPreviewSourceCache>()
            .SaveAsync(new ProjectFolderId(folder), itemId, FakeAiResponses.SilentWav());
    }
}
