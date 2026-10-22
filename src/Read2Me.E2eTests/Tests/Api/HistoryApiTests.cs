using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Api;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.History;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>GET /api/projects/{folder}/history</c>: the contract only. What History records is proved
/// over a real project in the History module's own tests.
/// </summary>
public class HistoryApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> HistoryAsync(string folder) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history")).RootElement;

    [Fact]
    public async Task History_lists_steps_newest_first_with_the_head_and_the_busy_answer()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");

        var started = await HistoryAsync(folder);
        var only = Assert.Single(started.GetProperty("steps").EnumerateArray());
        Assert.Equal("History started", only.GetProperty("label").GetString());

        var created = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent("""{ "type": "CreateCharacter", "name": "Ganondorf" }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var history = await HistoryAsync(folder);
        var steps = history.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["New character", "History started"], steps.Select(s => s.GetProperty("label").GetString()));
        Assert.Equal(["request", "started"], steps.Select(s => s.GetProperty("kind").GetString()));
        Assert.Equal(["character", null], steps.Select(s => s.GetProperty("action").GetString()));

        var tip = steps[0];
        Assert.Equal(tip.GetProperty("stepId").GetGuid(), history.GetProperty("headStepId").GetGuid());
        Assert.Equal(steps[1].GetProperty("stepId").GetGuid(), tip.GetProperty("parentStepId").GetGuid());
        Assert.Equal(2, tip.GetProperty("sequence").GetInt32());
        Assert.False(tip.GetProperty("isOpen").GetBoolean());
        Assert.False(string.IsNullOrEmpty(tip.GetProperty("author").GetString()));
        Assert.True(tip.TryGetProperty("startedAt", out _));
        Assert.True(tip.TryGetProperty("endedAt", out _));
        Assert.Equal(JsonValueKind.Null, history.GetProperty("busy").ValueKind);
        Assert.False(history.GetProperty("restoring").GetBoolean());
    }

    [Fact]
    public async Task One_request_is_one_step_labelled_in_user_terms_whatever_it_commits()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "History Book", "Author");
        await HistoryAsync(folder);

        // "New character…" plus assign: a create and an assign, committed as two mutations.
        var assigned = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent($$"""{ "type": "CreateCharacterAndAssign", "name": "Zelda", "paragraphId": "{{book.ParagraphId("p2")}}" }""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var steps = (await HistoryAsync(folder)).GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["New character", "History started"], steps.Select(s => s.GetProperty("label").GetString()));
        Assert.Equal("1 paragraph in ch1", steps[0].GetProperty("summary").GetString());
        Assert.False(steps[0].GetProperty("isOpen").GetBoolean());
    }

    [Fact]
    public async Task A_request_that_commits_nothing_leaves_no_step()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        await HistoryAsync(folder);

        var response = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/narrator-only-mode",
            new StringContent("""{ "enabled": false }""", Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode);

        Assert.Single((await HistoryAsync(folder)).GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public void Every_project_write_endpoint_declares_its_place_in_History()
    {
        var undeclared = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/projects/{folder}", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(m => m != "GET") == true)
            .Where(e => e.Metadata.GetMetadata<RecordsHistoryStep>() is null && e.Metadata.GetMetadata<RecordsNoHistoryStep>() is null)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}");

        Assert.Empty(undeclared);
    }

    [Fact]
    public void Every_project_read_endpoint_declares_whether_it_reads_at_a_step()
    {
        var undeclared = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/projects/{folder}", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .Where(e => e.Metadata.GetMetadata<ReadsAtHistoryStep>() is null && e.Metadata.GetMetadata<ReadsLiveOnly>() is null)
            .Select(e => e.RoutePattern.RawText);

        Assert.Empty(undeclared);
    }

    // ── viewing a step: ?at= ────────────────────────────────────────────────

    [Fact]
    public async Task A_read_with_at_reads_the_project_as_it_stood_after_that_step()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        await CreateCharacterAsync(folder, "Ganondorf");

        Assert.Contains("Ganondorf", await CharacterNamesAsync($"/api/projects/{folder}/characters"));
        Assert.DoesNotContain("Ganondorf", await CharacterNamesAsync($"/api/projects/{folder}/characters?at={started}"));
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/status?at={started}")).StatusCode);
    }

    [Fact]
    public async Task A_read_at_an_unknown_step_is_404_and_at_something_else_400()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        await HistoryAsync(folder);

        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/book?at={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/book?at=yesterday")).StatusCode);
    }

    [Fact]
    public async Task A_read_at_a_step_whose_old_schema_fails_to_migrate_is_422_and_History_says_it_cant_be_opened()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        await BreakBaseSnapshotAsync(folder);

        var read = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/book?at={started}");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, read.StatusCode);
        Assert.True(Assert.Single((await HistoryAsync(folder)).GetProperty("steps").EnumerateArray())
            .GetProperty("cantOpen").GetBoolean());
    }

    /// <summary>The base snapshot broken so that every step opened from it fails to migrate.</summary>
    private Task BreakBaseSnapshotAsync(string folder) =>
        PriorSchema.BreakAsync(Path.Combine(app.WorkspaceDir, folder, "history", "snapshots", "base.db"));

    [Fact]
    public async Task A_write_or_a_live_read_with_at_is_400_and_writes_nothing()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();

        var write = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands?at={started}",
            new StringContent("""{ "type": "CreateCharacter", "name": "Ganondorf" }""", Encoding.UTF8, "application/json"));
        var live = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/audio/reviews?at={started}");

        Assert.Equal(HttpStatusCode.BadRequest, write.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, live.StatusCode);
        Assert.DoesNotContain("Ganondorf", await CharacterNamesAsync($"/api/projects/{folder}/characters"));
        Assert.Single((await HistoryAsync(folder)).GetProperty("steps").EnumerateArray());
    }

    [Fact]
    public async Task A_step_is_returned_by_id_and_an_unknown_one_is_404()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();

        var step = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}")).RootElement;

        Assert.Equal(started, step.GetProperty("stepId").GetGuid());
        Assert.Equal("History started", step.GetProperty("label").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/history/{Guid.NewGuid()}")).StatusCode);
    }

    // ── Restore ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_step_says_what_restoring_it_costs_and_Restore_adds_a_step_from_it()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "History Book", "Author");
        var alice = await CharacterIdAsync(folder, "Alice");
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), alice);
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        var edited = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent($$"""{ "type": "UpdateParagraphItemText", "itemId": "{{book.ItemId("n1")}}", "text": "A bright morning." }""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), alice); // generated again for the new words

        var step = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}")).RootElement;
        var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}/restore", null);

        Assert.Equal(1, step.GetProperty("restorePreview").GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = JsonDocument.Parse(await restore.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, restored.GetProperty("audioDiscardCount").GetInt32());
        var restoreStep = restored.GetProperty("step");
        Assert.Equal(started, restoreStep.GetProperty("restoredFromStepId").GetGuid());
        Assert.StartsWith("Restore to 'History started' (", restoreStep.GetProperty("label").GetString());

        var history = await HistoryAsync(folder);
        Assert.Equal(restoreStep.GetProperty("stepId").GetGuid(), history.GetProperty("headStepId").GetGuid());
        Assert.Equal(3, history.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task Restoring_an_unknown_step_is_404_and_with_at_400()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{Guid.NewGuid()}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}/restore?at={started}", null)).StatusCode);
    }

    [Fact]
    public async Task While_a_Restore_runs_every_write_and_another_Restore_is_409_Project_is_restoring()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        var restoring = app.Services.GetRequiredService<RestoringProjects>();
        var folderId = new Read2Me.Core.Models.ProjectFolderId(folder);

        Assert.True(restoring.TryBegin(folderId));
        try
        {
            var write = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
                new StringContent("""{ "type": "CreateCharacter", "name": "Ganondorf" }""", Encoding.UTF8, "application/json"));
            var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}/restore", null);

            Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            Assert.Equal("Project is restoring",
                JsonDocument.Parse(await write.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
            Assert.True((await HistoryAsync(folder)).GetProperty("restoring").GetBoolean());
        }
        finally
        {
            restoring.End(folderId);
        }
        Assert.DoesNotContain("Ganondorf", await CharacterNamesAsync($"/api/projects/{folder}/characters"));
    }

    [Fact]
    public async Task While_the_project_is_busy_a_step_cannot_be_read_or_viewed_and_409_says_why()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        var alice = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"))
            .RootElement.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Alice").GetProperty("id").GetGuid();
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), alice);

        // An Assembly parked half-way through its encode keeps the project busy.
        var hold = new TaskCompletionSource();
        app.Encoder.HoldAtHalf = hold;
        try
        {
            var start = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/assembly",
                new StringContent("""{ "allowPartial": true }""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            await BusyAsync(folder, busy: true);

            var read = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/book?at={started}");
            var step = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}");
            var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{started}/restore", null);

            Assert.Equal(HttpStatusCode.Conflict, read.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, step.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            var detail = JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString();
            Assert.StartsWith("Assembly", detail);
        }
        finally
        {
            hold.TrySetResult();
            await BusyAsync(folder, busy: false);
            app.Encoder.HoldAtHalf = null;
        }
    }

    // ── state files at a step ───────────────────────────────────────────────

    [Fact]
    public async Task A_state_file_with_at_is_served_as_it_was_after_that_step_and_404_when_it_wasnt_there()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        await PutCoverAsync(folder, "first cover");
        var first = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();
        await PutCoverAsync(folder, "second cover");
        var cover = $"{app.BaseUrl}/workspace/{folder}/cover.png";

        var then = await Http.GetAsync($"{cover}?at={first}");

        Assert.Equal(HttpStatusCode.OK, then.StatusCode);
        Assert.Equal("image/png", then.Content.Headers.ContentType?.MediaType);
        Assert.Equal("first cover", await then.Content.ReadAsStringAsync());
        Assert.True(then.Headers.CacheControl?.Extensions.Any(e => e.Name == "immutable")); // a closed step never changes
        Assert.Equal("second cover", await Http.GetStringAsync(cover));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{cover}?at={started}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{cover}?at={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.GetAsync($"{cover}?at=yesterday")).StatusCode);
    }

    [Fact]
    public async Task A_preview_source_with_at_is_400_since_it_is_a_result()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");
        var started = (await HistoryAsync(folder)).GetProperty("headStepId").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await Http.GetAsync($"{app.BaseUrl}/preview-source/{folder}/{Guid.NewGuid()}?at={started}")).StatusCode);
    }

    private async Task PutCoverAsync(string folder, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", "cover.png");
        var saved = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/cover", form);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private async Task CreateCharacterAsync(string folder, string name)
    {
        var created = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent($$"""{ "type": "CreateCharacter", "name": "{{name}}" }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    private async Task<Guid> CharacterIdAsync(string folder, string name) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"))
            .RootElement.EnumerateArray().First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

    private async Task<List<string?>> CharacterNamesAsync(string path) =>
        JsonDocument.Parse(await Http.GetStringAsync(app.BaseUrl + path)).RootElement
            .EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();

    /// <summary>Waits until GET /history says the project is busy, or idle again.</summary>
    private async Task BusyAsync(string folder, bool busy)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await HistoryAsync(folder)).GetProperty("busy").ValueKind != JsonValueKind.Null != busy)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The project never became {(busy ? "busy" : "idle")}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task History_of_an_unknown_project_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-{Guid.NewGuid():N}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
