using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR.Client;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The AI book-edit flow over HTTP (Angular ticket 19): plan, propose with hub progress, retry one
/// row, apply through the ordinary command endpoint, cancel and drop.
/// </summary>
[Collection(E2eCollection.Name)]
public class BookEditApiTests(E2eAppFixture app)
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
            await hub.StartAsync();

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
            app.FakeAi.LlmDelay = TimeSpan.FromMilliseconds(400);

            await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
            var messages = new ConcurrentQueue<JsonElement>();
            hub.On<JsonElement>("bookEdit", messages.Enqueue);
            await hub.StartAsync();

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
            Assert.InRange(landed, 8, 16); // one or two of the three batches
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
    public async Task Change_case_upper_re_cases_whole_titles_at_once_and_refuses_retries()
    {
        var folder = $"api-edit-case-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Case Book", "Author", chapters: 3);
        app.FakeAi.LlmReply = _ => ChangeCasePlan("chapter_title", null, "upper");
        try
        {
            var plan = await PlanAsync(folder, "make the chapter titles uppercase");
            Assert.Equal("Ok", plan.GetProperty("status").GetString());
            Assert.Equal("ChangeCase", plan.GetProperty("transform").GetString());
            Assert.Equal(3, plan.GetProperty("targetCount").GetInt32());
            Assert.Equal(0, plan.GetProperty("requestCount").GetInt32());
            Assert.EndsWith("— change to upper case", plan.GetProperty("summary").GetString());
            Assert.Equal(0, plan.GetProperty("warnings").GetArrayLength());
            var program = plan.GetProperty("program").GetString()!;

            int promptsBefore;
            lock (app.FakeAi.LlmPromptsSeen) promptsBefore = app.FakeAi.LlmPromptsSeen.Count;
            var propose = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"), new { thinking = false });
            Assert.Equal(HttpStatusCode.Accepted, propose.StatusCode);
            var rows = (await WaitForRunAsync(folder, program, "Completed")).GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal(["CHAPTER 1", "CHAPTER 2", "CHAPTER 3"], rows.Select(r => r.GetProperty("newValue").GetString()));
            Assert.All(rows, r => Assert.Equal("Proposed", r.GetProperty("status").GetString()));
            lock (app.FakeAi.LlmPromptsSeen) Assert.Equal(promptsBefore, app.FakeAi.LlmPromptsSeen.Count);

            var retry = await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose-one"),
                new { targetId = rows[0].GetProperty("id").GetGuid(), thinking = false });
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            var row = JsonDocument.Parse(await retry.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("Failed", row.GetProperty("status").GetString());
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Change_case_with_a_pattern_counts_and_re_cases_only_the_matching_items()
    {
        var folder = $"api-edit-case-span-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Span Book", "Author", chapters: 2);
        app.FakeAi.LlmReply = _ => ChangeCasePlan("paragraph_text", @"\bopens\b", "upper");
        try
        {
            var plan = await PlanAsync(folder, "make 'opens' uppercase");
            Assert.Equal("ChangeCase", plan.GetProperty("transform").GetString());
            Assert.EndsWith("— change text matching \"\\bopens\\b\" to upper case", plan.GetProperty("summary").GetString());
            // The dialogue lines hold no match, so they are not in the plan at all.
            Assert.Equal(2, plan.GetProperty("targetCount").GetInt32());
            // No title holds "opens": no "titles also match" warning.
            Assert.Equal(0, plan.GetProperty("warnings").GetArrayLength());
            var program = plan.GetProperty("program").GetString()!;

            await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"), new { thinking = false });
            var rows = (await WaitForRunAsync(folder, program, "Completed")).GetProperty("rows").EnumerateArray().ToList();

            Assert.Equal(["Chapter 1 OPENS.", "Chapter 2 OPENS."], rows.Select(r => r.GetProperty("newValue").GetString()));
            Assert.All(rows, r => Assert.Equal("Proposed", r.GetProperty("status").GetString()));
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    private const string AllCapsPattern = @"\b\p{Lu}{2,}(?:['’]\p{Lu}+)?(?:[ -]+\p{Lu}+(?:['’]\p{Lu}+)?)*\b";

    [Fact]
    public async Task Change_case_sentence_keeps_the_book_names_numerals_and_acronyms()
    {
        var folder = $"api-edit-case-names-{Guid.NewGuid():N}";
        await app.SeedNarrationBookAsync(folder, "PART IV",
        [
            "HARI SELDON— . . . born in the Galactic Era.",
            "THE MAYORS",
            "THE FOUR KINGDOMS— The name given to the Province of Anacreon.",
            "The last strong Emperor was Cleon II, and UV light was rare.",
            "Gaal Dornick met Hari Seldon on Trantor, near the Four Kingdoms.",
            "The mayors argued; the four winds blew over the kingdoms.",
        ]);
        app.FakeAi.LlmReply = _ => ChangeCasePlan("paragraph_text", AllCapsPattern, "sentence");
        try
        {
            var plan = await PlanAsync(folder, "make the all-caps words sentence case");
            Assert.Equal("ChangeCase", plan.GetProperty("transform").GetString());
            Assert.EndsWith("— names, numerals and acronyms are kept", plan.GetProperty("summary").GetString());
            // Only the four items holding all-caps words count; the two plain prose lines are dropped.
            Assert.Equal(4, plan.GetProperty("targetCount").GetInt32());
            Assert.Equal(["1 title also matches — run again targeting part titles"],
                plan.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
            var program = plan.GetProperty("program").GetString()!;

            await Http.PostAsJsonAsync(Url(folder, $"/{program}/propose"), new { thinking = false });
            var rows = (await WaitForRunAsync(folder, program, "Completed")).GetProperty("rows").EnumerateArray().ToList();

            var changed = rows.Where(r => r.GetProperty("status").GetString() == "Proposed")
                .Select(r => r.GetProperty("newValue").GetString()).ToList();
            Assert.Equal(
            [
                "Hari Seldon— . . . born in the Galactic Era.",
                "The mayors",
                "The Four Kingdoms— The name given to the Province of Anacreon.",
            ], changed);
            // The numerals and acronyms line matches the pattern but comes back unchanged.
            Assert.Equal(["NoChange"], rows.Where(r => r.GetProperty("status").GetString() != "Proposed")
                .Select(r => r.GetProperty("status").GetString()));
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Change_case_pattern_matching_no_item_is_no_targets()
    {
        var folder = $"api-edit-case-none-{Guid.NewGuid():N}";
        await app.SeedNarrationBookAsync(folder, "PART IV", ["Plain prose only.", "Nothing shouts here."]);
        app.FakeAi.LlmReply = _ => ChangeCasePlan("paragraph_text", AllCapsPattern, "sentence");
        try
        {
            var plan = await PlanAsync(folder, "make the all-caps words sentence case");
            Assert.Equal("NoTargets", plan.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, plan.GetProperty("program").ValueKind);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    private static string RegexReplacePlan(string pattern) =>
        $$"""
        { "reasoning": "find and replace", "supported": true, "unsupported_reason": null,
          "target": "paragraph_text",
          "node_filter": { "ordinal_from": null, "ordinal_to": null, "title_regex": null },
          "paragraph_filter": { "where": [] },
          "transform": { "kind": "regex_replace", "pattern": {{JsonSerializer.Serialize(pattern)}},
            "replacement": "begins", "template": null, "instruction": null, "case_mode": null } }
        """;

    [Fact]
    public async Task Regex_replace_counts_only_the_items_its_pattern_matches()
    {
        var folder = $"api-edit-regex-{Guid.NewGuid():N}";
        await app.SeedMultiChapterProjectAsync(folder, "Regex Book", "Author", chapters: 3);
        try
        {
            app.FakeAi.LlmReply = _ => RegexReplacePlan("opens");
            var plan = await PlanAsync(folder, "replace opens with begins");
            Assert.Equal("Ok", plan.GetProperty("status").GetString());
            // The three narration items match; the three dialogue lines do not count.
            Assert.Equal(3, plan.GetProperty("targetCount").GetInt32());
            Assert.Equal(0, plan.GetProperty("warnings").GetArrayLength());

            app.FakeAi.LlmReply = _ => RegexReplacePlan("nowhere in the book");
            var none = await PlanAsync(folder, "replace nowhere with begins");
            Assert.Equal("NoTargets", none.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, none.GetProperty("program").ValueKind);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public async Task Plan_reports_unsupported_and_no_target_outcomes_without_a_session()
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

            var blank = await Http.PostAsJsonAsync(Url(folder, "/plan"), new { instruction = "  " });
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }
        finally
        {
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
