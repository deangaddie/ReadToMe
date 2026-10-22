using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.BookEdits;
using Read2Me.Services.History;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The AI book-edit flow over HTTP (Angular ticket 19): plan, propose with hub progress, retry one
/// row, apply through the ordinary command endpoint, cancel and drop.
/// </summary>
public class BookEditApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private const string LlmPlan =
        """
        { "reasoning": "capitalise every chapter title", "supported": true, "unsupported_reason": null,
          "target": "chapter_title",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": "capitalise the title" } }
        """;

    private const string TemplatePlan =
        """
        { "reasoning": "number the chapters", "supported": true, "unsupported_reason": null,
          "target": "chapter_title",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "set_template", "pattern": null, "replacement": null, "template": "{n}. {old}", "instruction": null } }
        """;

    private static bool IsPlanPrompt(string prompt) => prompt.Contains("structured edit plan");

    /// <summary>Answers a batch prompt with every listed text upper-cased, so rows are checkable.</summary>
    private static string UpperCaseBatchReply(string prompt)
    {
        // The items JSON is indented; keys stay in index/path/text order.
        var entries = Regex.Matches(prompt, @"""index"":\s*(\d+),\s*""path"":\s*""[^""]*"",\s*""text"":\s*""([^""]*)""")
            .Select(m => $$"""{ "index": {{m.Groups[1].Value}}, "reasoning": "fake", "new_text": "{{m.Groups[2].Value.ToUpperInvariant()}}" }""");
        return "[" + string.Join(",", entries) + "]";
    }

    private string Url(string folder, string rest = "") => $"{app.BaseUrl}/api/projects/{folder}/book-edits{rest}";

    private async Task<JsonElement> PlanAsync(string folder, string instruction, bool thinking = false)
    {
        var response = await Http.PostAsJsonAsync(Url(folder, "/plan"), new { instruction, thinking });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private async Task<JsonElement> GetRunAsync(string folder, string program) =>
        JsonDocument.Parse(await Http.GetStringAsync(Url(folder, $"/{program}"))).RootElement;

    private async Task<List<string>> ChapterTitlesAsync(string folder)
    {
        var overview = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/book")).RootElement;
        var volumeId = overview.GetProperty("volumes")[0].GetProperty("id").GetGuid();
        var volumeChildren = await ChildrenAsync(folder, "volume", volumeId);
        var partId = volumeChildren.GetProperty("parts")[0].GetProperty("id").GetGuid();
        var chapters = (await ChildrenAsync(folder, "part", partId)).GetProperty("chapters");
        return chapters.EnumerateArray().Select(c => c.GetProperty("title").GetString()!).ToList();
    }

    private async Task<JsonElement> ChildrenAsync(string folder, string level, Guid id)
    {
        return JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/{level}/{id}/children")).RootElement;
    }

    [Fact]
    public async Task Plan_propose_retry_one_and_apply_round_trip_over_the_hub()
    {
        var folder = $"api-edit-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Edit Book", "Author", chapters: 10);
        app.FakeAi.LlmReply = p => IsPlanPrompt(p) ? LlmPlan : UpperCaseBatchReply(p);
        try
        {
            var plan = await PlanAsync(folder, "capitalise chapter titles");
            Assert.Equal("Ok", plan.GetProperty("status").GetString());
            Assert.Equal("Llm", plan.GetProperty("transform").GetString());
            Assert.Equal(10, plan.GetProperty("targetCount").GetInt32());
            Assert.Equal(2, plan.GetProperty("requestCount").GetInt32()); // ceil(10 / 8)
            Assert.Equal(0, plan.GetProperty("warnings").GetArrayLength());
            Assert.False(string.IsNullOrEmpty(plan.GetProperty("summary").GetString()));
            var program = plan.GetProperty("program").GetString()!;

            Assert.Equal("Idle", (await GetRunAsync(folder, program)).GetProperty("status").GetString());

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("bookEdit", messages.Enqueue);
            await hub.StartJoinedAsync();

            var propose = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"),
                new { thinking = false, connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, propose.StatusCode);

            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));
            var list = messages.ToList();
            Assert.All(list, m => Assert.Equal(program, m.GetProperty("program").GetString()));
            Assert.Contains(list, m => m.GetProperty("kind").GetString() == "progress");
            var done = list.Single(m => m.GetProperty("kind").GetString() == "done");
            Assert.Equal(10, done.GetProperty("done").GetInt32());
            Assert.Equal(10, done.GetProperty("total").GetInt32());
            Assert.False(done.GetProperty("cancelled").GetBoolean());
            var rows = done.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(10, rows.Count);
            Assert.All(rows, r => Assert.Equal("Proposed", r.GetProperty("status").GetString()));
            Assert.Equal("CHAPTER 1", rows[0].GetProperty("newValue").GetString());
            Assert.Equal("Chapter 1", rows[0].GetProperty("oldValue").GetString());
            Assert.Equal("ChapterTitle", rows[0].GetProperty("kind").GetString());

            // The same rows are readable without the hub.
            var run = await GetRunAsync(folder, program);
            Assert.Equal("Completed", run.GetProperty("status").GetString());
            Assert.Equal(10, run.GetProperty("rows").GetArrayLength());

            // Retry one row with a hint: the hint reaches the prompt and a fresh row comes back.
            var targetId = rows[2].GetProperty("id").GetGuid();
            app.FakeAi.LlmReply = _ => """[ { "index": 0, "reasoning": "fake", "new_text": "Chapter Three" } ]""";
            var retry = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose-one"),
                new { targetId, hint = "spell the number out", thinking = false });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var row = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(targetId, row.GetProperty("id").GetGuid());
            Assert.Equal("Chapter Three", row.GetProperty("newValue").GetString());
            Assert.Equal("Proposed", row.GetProperty("status").GetString());
            string lastPrompt;
            lock (app.FakeAi.LlmPromptsSeen) lastPrompt = app.FakeAi.LlmPromptsSeen[^1];
            Assert.Contains("spell the number out", lastPrompt);

            var unknownTarget = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose-one"),
                new { targetId = Guid.NewGuid(), thinking = false });
            Assert.Equal(HttpStatusCode.NotFound, unknownTarget.StatusCode);

            // Apply two of them through the ordinary command endpoint.
            var apply = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands", new
            {
                type = "ApplyBookEdits",
                edits = new[]
                {
                    new { kind = "ChapterTitle", id = rows[0].GetProperty("id").GetGuid(), newValue = "CHAPTER 1" },
                    new { kind = "ChapterTitle", id = targetId, newValue = "Chapter Three" },
                },
            });
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
            var titles = await ChapterTitlesAsync(folder);
            Assert.Equal("CHAPTER 1", titles[0]);
            Assert.Equal("Chapter 2", titles[1]);
            Assert.Equal("Chapter Three", titles[2]);

            var drop = await Http.DeleteAsync(Url(folder, $"/{program}"));
            Assert.Equal(HttpStatusCode.NoContent, drop.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(Url(folder, $"/{program}"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Url(folder, $"/{program}"))).StatusCode);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Cancel_mid_proposal_keeps_the_rows_computed_so_far()
    {
        var folder = $"api-edit-cancel-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Cancel Book", "Author", chapters: 24);
        app.FakeAi.LlmReply = p => IsPlanPrompt(p) ? LlmPlan : UpperCaseBatchReply(p);
        try
        {
            var program = (await PlanAsync(folder, "capitalise chapter titles")).GetProperty("program").GetString()!;
            app.FakeAi.HoldLlmAfter(1); // the first batch of 8 lands, the second is held until cancelled

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("bookEdit", messages.Enqueue);
            await hub.StartJoinedAsync();

            var propose = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"),
                new { thinking = false, connectionId = hub.ConnectionId });
            Assert.Equal(HttpStatusCode.Accepted, propose.StatusCode);

            // A second start while one is in flight is refused rather than doubled.
            var again = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"), new { thinking = false });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "progress"));
            var cancel = await Http.PostAsync(Url(folder, $"/{program}/cancel"), null);
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

            await WaitForAsync(() => messages.Any(m => m.GetProperty("kind").GetString() == "done"));
            var done = messages.Single(m => m.GetProperty("kind").GetString() == "done");
            Assert.True(done.GetProperty("cancelled").GetBoolean());
            var landed = done.GetProperty("rows").GetArrayLength();
            Assert.Equal(8, landed);
            Assert.Equal(landed, done.GetProperty("done").GetInt32());

            var run = await GetRunAsync(folder, program);
            Assert.Equal("Cancelled", run.GetProperty("status").GetString());
            Assert.Equal(landed, run.GetProperty("rows").GetArrayLength());

            // Cancelling with nothing running is harmless.
            Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync(Url(folder, $"/{program}/cancel"), null)).StatusCode);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Deterministic_plans_propose_at_once_and_refuse_per_row_retries()
    {
        var folder = $"api-edit-det-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Template Book", "Author", chapters: 3);
        app.FakeAi.LlmReply = _ => TemplatePlan;
        try
        {
            var plan = await PlanAsync(folder, "number the chapters");
            Assert.Equal("SetTemplate", plan.GetProperty("transform").GetString());
            Assert.Equal(3, plan.GetProperty("targetCount").GetInt32());
            Assert.Equal(0, plan.GetProperty("requestCount").GetInt32());
            var program = plan.GetProperty("program").GetString()!;

            // No hub connection: the rows are still there to read back.
            var propose = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"), new { thinking = false });
            Assert.Equal(HttpStatusCode.Accepted, propose.StatusCode);
            var run = await WaitForRunAsync(folder, program, "Completed");
            var rows = run.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(["1. Chapter 1", "2. Chapter 2", "3. Chapter 3"], rows.Select(r => r.GetProperty("newValue").GetString()));

            var retry = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose-one"),
                new { targetId = rows[0].GetProperty("id").GetGuid(), thinking = false });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var row = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Failed", row.GetProperty("status").GetString());
            Assert.Contains("AI-written plans", row.GetProperty("failureReason").GetString());
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    private static string ChangeCasePlan(string target, string? pattern, string caseMode) =>
        $$"""
        { "reasoning": "change letter case", "supported": true, "unsupported_reason": null,
          "target": "{{target}}",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "change_case", "pattern": {{(pattern == null ? "null" : JsonSerializer.Serialize(pattern))}},
            "replacement": null, "template": null, "instruction": null, "case_mode": "{{caseMode}}" } }
        """;

    [Fact]
    public async Task Plan_reports_unsupported_no_target_and_title_warning_outcomes()
    {
        var folder = $"api-edit-plan-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Plan Book", "Author", chapters: 2);
        try
        {
            app.FakeAi.LlmReply = _ =>
                """{ "reasoning": "no", "supported": false, "unsupported_reason": "Cover art is not text.", "target": "chapter_title", "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null }, "paragraph_filter": { "where": [] }, "transform": { "kind": "llm", "pattern": null, "replacement": null, "template": null, "instruction": null } }""";
            var unsupported = await PlanAsync(folder, "change the cover");
            Assert.Equal("Unsupported", unsupported.GetProperty("status").GetString());
            Assert.Contains("Cover art", unsupported.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, unsupported.GetProperty("program").ValueKind);

            app.FakeAi.LlmReply = _ => LlmPlan.Replace("\"title_regex\": null", "\"title_regex\": \"^Nothing matches\"");
            var empty = await PlanAsync(folder, "capitalise the chapters called Nothing");
            Assert.Equal("NoTargets", empty.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("program").ValueKind);

            // A paragraph change_case whose pattern also matches titles plans with a warning. What
            // it counts and says is ScopeResolverTests and EditProgramDescriberTests; the re-casing
            // itself is BookEditProposalServiceTests and DeterministicTransformerTests.
            app.FakeAi.LlmReply = _ => ChangeCasePlan("paragraph_text", "Chapter", "upper");
            var warned = await PlanAsync(folder, "make 'Chapter' uppercase");
            Assert.Equal("Ok", warned.GetProperty("status").GetString());
            Assert.Equal("ChangeCase", warned.GetProperty("transform").GetString());
            Assert.Equal(0, warned.GetProperty("requestCount").GetInt32());
            Assert.Contains("titles also match", Assert.Single(warned.GetProperty("warnings").EnumerateArray()).GetString());

            var blank = await Http.PostAsJsonAsync(Url(folder, "/plan"), new { instruction = "  " });
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task A_plan_still_thinking_when_a_Restore_runs_answers_409_and_keeps_no_session()
    {
        var folder = $"api-edit-restore-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Restore Book", "Author", chapters: 2);
        var folderId = new Read2Me.Core.Models.ProjectFolderId(folder);
        var restoring = app.Services.GetRequiredService<RestoringProjects>();
        app.FakeAi.LlmReply = _ => TemplatePlan;
        app.FakeAi.HoldLlmAfter(0);
        try
        {
            // Past the endpoint's restoring check and waiting on the LLM when the Restore begins.
            var plan = Http.PostAsJsonAsync(Url(folder, "/plan"), new { instruction = "number the chapters" });
            await WaitForAsync(() => { lock (app.FakeAi.LlmPromptsSeen) return app.FakeAi.LlmPromptsSeen.Count > 0; });
            Assert.True(restoring.TryBegin(folderId));
            app.FakeAi.ReleaseLlm();

            var response = await plan;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(RestoringProjects.Reason,
                JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
            Assert.Empty(app.Services.GetRequiredService<IBookEditSessionStore>().RemoveAll(folderId));
        }
        finally
        {
            restoring.End(folderId);
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task A_plan_overtaken_by_a_whole_Restore_while_thinking_answers_409_and_keeps_no_session()
    {
        var folder = $"api-edit-restored-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Restored Book", "Author", chapters: 2);
        var folderId = new Read2Me.Core.Models.ProjectFolderId(folder);
        var restoring = app.Services.GetRequiredService<RestoringProjects>();
        app.FakeAi.LlmReply = _ => TemplatePlan;
        app.FakeAi.HoldLlmAfter(0);
        try
        {
            // The Restore begins and ends while the LLM thinks: the flag is down again by the check,
            // but the plan's targets would come from the book as it was before.
            var plan = Http.PostAsJsonAsync(Url(folder, "/plan"), new { instruction = "number the chapters" });
            await WaitForAsync(() => { lock (app.FakeAi.LlmPromptsSeen) return app.FakeAi.LlmPromptsSeen.Count > 0; });
            Assert.True(restoring.TryBegin(folderId));
            restoring.End(folderId);
            app.FakeAi.ReleaseLlm();

            var response = await plan;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(RestoringProjects.Reason,
                JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
            Assert.Empty(app.Services.GetRequiredService<IBookEditSessionStore>().RemoveAll(folderId));
        }
        finally
        {
            restoring.End(folderId);
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Unknown_folder_or_program_is_404()
    {
        var folder = $"api-edit-404-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Missing", "Author");

        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync(Url("nope-edit", "/plan"), new { instruction = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync(Url(folder, "/nope/propose"), new { thinking = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(Url(folder, "/nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsJsonAsync(Url(folder, "/nope/propose-one"), new { targetId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsync(Url(folder, "/nope/cancel"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(Url(folder, "/nope"))).StatusCode);
    }

    /// <summary>Polls the session until its run reaches <paramref name="status"/>, and answers it.</summary>
    private async Task<JsonElement> WaitForRunAsync(string folder, string program, string status, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var run = await GetRunAsync(folder, program);
            var current = run.GetProperty("status").GetString();
            if (current == status)
                return run;
            Assert.NotEqual("Failed", current); // surfaces the reason rather than timing out
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Run {program} was {current}, not {status}, within {timeoutMs} ms");
            await Task.Delay(20);
        }
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
