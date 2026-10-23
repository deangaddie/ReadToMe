using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Pronunciation list over HTTP: list and add, their refusals and History step, and an item's
/// Spoken text reaching TTS. The seed's items read "It was a dark and stormy night.",
/// "“Hello there,” she said." and "The rain kept falling."
/// </summary>
public class PronunciationApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private string Url(string folder) => $"{app.BaseUrl}/api/projects/{folder}/pronunciations";

    private async Task<string> SeedAsync()
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        return folder;
    }

    private Task<HttpResponseMessage> AddAsync(string folder, string? written, string? spoken) =>
        Http.PostAsJsonAsync(Url(folder), new { writtenForm = written, spokenForm = spoken });

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<List<JsonElement>> ListAsync(string folder, Guid? at = null) =>
        JsonDocument.Parse(await Http.GetStringAsync(Url(folder) + (at is { } step ? $"?at={step}" : "")))
            .RootElement.EnumerateArray().ToList();

    private async Task<List<JsonElement>> StepsAsync(string folder) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history"))
            .RootElement.GetProperty("steps").EnumerateArray().ToList();

    [Fact]
    public async Task A_new_project_has_an_empty_list()
    {
        var folder = await SeedAsync();

        Assert.Empty(await ListAsync(folder));
    }

    [Fact]
    public async Task Add_answers_201_with_the_normalised_entry_and_its_reach_and_the_list_sorts_by_written_form()
    {
        var folder = await SeedAsync();
        await app.SeedProjectAsync($"{folder}-x", "Other", "Author"); // a second project: lists are per book

        var stormy = await AddAsync(folder, "  stormy \n night ", " STORM-ee   nite ");
        var rain = await AddAsync(folder, "Rain", "RAYN");

        Assert.Equal(HttpStatusCode.Created, stormy.StatusCode);
        var entry = await JsonOf(stormy);
        Assert.Equal("stormy night", entry.GetProperty("writtenForm").GetString());
        Assert.Equal("STORM-ee nite", entry.GetProperty("spokenForm").GetString());
        Assert.Equal(1, entry.GetProperty("matchCount").GetInt32());
        Assert.Equal(1, entry.GetProperty("itemCount").GetInt32());
        Assert.Equal(0, entry.GetProperty("withAudioCount").GetInt32());
        Assert.Equal($"/api/projects/{folder}/pronunciations/{entry.GetProperty("id").GetGuid()}", stormy.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Created, rain.StatusCode);

        var list = await ListAsync(folder);
        Assert.Equal(["Rain", "stormy night"], list.Select(e => e.GetProperty("writtenForm").GetString()));
        Assert.Empty(await ListAsync($"{folder}-x"));
    }

    [Fact]
    public async Task The_list_counts_matches_items_and_the_items_with_audio()
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedNarrationBookAsync(folder, "Part One",
            ["Pug ran. Pug fell.", "Pug slept.", "Kulgan read."]);
        var narrator = Read2Me.Data.ProjectDbContext.NarratorId;
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), narrator);

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "Pug", "Pugg")).StatusCode);

        var pug = Assert.Single(await ListAsync(folder));
        Assert.Equal(3, pug.GetProperty("matchCount").GetInt32());
        Assert.Equal(2, pug.GetProperty("itemCount").GetInt32());
        Assert.Equal(1, pug.GetProperty("withAudioCount").GetInt32());
    }

    [Theory]
    [InlineData(null, "RAYN")]
    [InlineData("   ", "RAYN")]
    [InlineData("'Rain", "RAYN")]
    [InlineData("Rain!", "RAYN")]
    [InlineData("Rain", null)]
    [InlineData("Rain", "  ")]
    public async Task An_invalid_form_is_400_and_lands_no_step(string? written, string? spoken)
    {
        var folder = await SeedAsync();
        var before = (await StepsAsync(folder)).Count;

        var response = await AddAsync(folder, written, spoken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, (await StepsAsync(folder)).Count);
        Assert.Empty(await ListAsync(folder));
    }

    [Fact]
    public async Task Forms_past_their_length_are_400()
    {
        var folder = await SeedAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(folder, new string('a', 101), "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(folder, "Rain", new string('a', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, new string('a', 100), new string('b', 200))).StatusCode);
    }

    [Fact]
    public async Task A_written_form_already_listed_ignoring_case_is_409_and_lands_no_step()
    {
        var folder = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "Rain", "RAYN")).StatusCode);
        var before = (await StepsAsync(folder)).Count;

        var duplicate = await AddAsync(folder, "RAIN", "rane");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("RAIN", (await JsonOf(duplicate)).GetProperty("detail").GetString());
        Assert.Equal(before, (await StepsAsync(folder)).Count);
        Assert.Equal("RAYN", Assert.Single(await ListAsync(folder)).GetProperty("spokenForm").GetString());
    }

    [Fact]
    public async Task An_add_is_one_History_step_and_the_list_reads_at_a_step()
    {
        var folder = await SeedAsync();
        var started = (await StepsAsync(folder))[0].GetProperty("stepId").GetGuid();

        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "Rain", "RAYN")).StatusCode);

        var steps = await StepsAsync(folder);
        Assert.Equal(2, steps.Count);
        Assert.Equal("Pronunciation: add \"Rain\"", steps[0].GetProperty("label").GetString());
        Assert.Empty(await ListAsync(folder, at: started));
        Assert.Equal("Rain", Assert.Single(await ListAsync(folder, at: steps[0].GetProperty("stepId").GetGuid()))
            .GetProperty("writtenForm").GetString());
    }

    [Fact]
    public async Task Add_refuses_at_a_step_and_an_unknown_project_is_404()
    {
        var folder = await SeedAsync();
        var started = (await StepsAsync(folder))[0].GetProperty("stepId").GetGuid();

        var at = await Http.PostAsJsonAsync($"{Url(folder)}?at={started}", new { writtenForm = "Rain", spokenForm = "RAYN" });

        Assert.Equal(HttpStatusCode.BadRequest, at.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Url($"no-such-{Guid.NewGuid():N}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync($"no-such-{Guid.NewGuid():N}", "Rain", "RAYN")).StatusCode);
    }

    [Fact]
    public async Task The_next_generated_item_holding_the_written_form_is_spoken_with_the_respelling()
    {
        app.FakeAi.Reset();
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "stormy", "storm-ee")).StatusCode);

        var enqueue = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
            new { level = "chapter", nodeId = book.ChapterId("ch1"), needsAudioOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

        List<JsonObject> bodies;
        lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
        var inputs = bodies.Select(b => b["input"]!.GetValue<string>()).ToList();
        Assert.Contains("It was a dark and storm-ee night.", inputs);
        Assert.Contains("The rain kept falling.", inputs);
        Assert.DoesNotContain("It was a dark and stormy night.", inputs);
    }
}
