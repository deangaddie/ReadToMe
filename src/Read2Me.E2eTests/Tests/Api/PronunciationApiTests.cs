using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

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
            ["Holmes ran. Holmes fell.", "Holmes slept.", "Watson read."]);
        var narrator = Read2Me.Data.ProjectDbContext.NarratorId;
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "Holmes", "Holmz")).StatusCode);
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), narrator);

        var holmes = Assert.Single(await ListAsync(folder));
        Assert.Equal(3, holmes.GetProperty("matchCount").GetInt32());
        Assert.Equal(2, holmes.GetProperty("itemCount").GetInt32());
        Assert.Equal(1, holmes.GetProperty("withAudioCount").GetInt32());
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
            new { level = "Chapter", nodeId = book.ChapterId("ch1"), needsAudioOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

        List<JsonObject> bodies;
        lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
        var inputs = bodies.Select(b => b["input"]!.GetValue<string>()).ToList();
        Assert.Contains("It was a dark and storm-ee night.", inputs);
        Assert.Contains("The rain kept falling.", inputs);
        Assert.DoesNotContain("It was a dark and stormy night.", inputs);
    }

    // ── a change discards the audio whose Spoken text it alters ─────────────

    private Task<HttpResponseMessage> EditAsync(string folder, Guid id, string written, string spoken) =>
        Http.PutAsJsonAsync($"{Url(folder)}/{id}", new { writtenForm = written, spokenForm = spoken });

    private Task<HttpResponseMessage> DeleteAsync(string folder, Guid id) =>
        Http.DeleteAsync($"{Url(folder)}/{id}");

    private Task<HttpResponseMessage> ImpactAsync(string folder, object draft) =>
        Http.PostAsJsonAsync($"{Url(folder)}/impact", draft);

    private async Task<Guid> AddedIdAsync(string folder, string written, string spoken)
    {
        var added = await AddAsync(folder, written, spoken);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        return (await JsonOf(added)).GetProperty("id").GetGuid();
    }

    /// <summary>A narration book, one item per text (n1, n2, …), the named ones given audio.</summary>
    private async Task<(string Folder, TestUtils.BookHierarchyBuilder Book)> SeedWithAudioAsync(
        IReadOnlyList<string> narration, params string[] withAudio)
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedNarrationBookAsync(folder, "Part One", narration);
        foreach (var item in withAudio)
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);
        return (folder, book);
    }

    /// <summary>The seeded items (n1, n2, …) that have audio now, read as the reader reads them.</summary>
    private async Task<List<string>> WithAudioAsync(string folder, TestUtils.BookHierarchyBuilder book, int items)
    {
        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/Chapter/{book.ChapterId("ch1")}/children")).RootElement;
        var withAudio = children.GetProperty("paragraphs").EnumerateArray()
            .SelectMany(p => p.GetProperty("items").EnumerateArray())
            .Where(i => !string.IsNullOrEmpty(i.GetProperty("audioFileName").GetString()))
            .Select(i => i.GetProperty("id").GetGuid())
            .ToHashSet();
        return [.. Enumerable.Range(1, items).Select(n => $"n{n}").Where(n => withAudio.Contains(book.ItemId(n)))];
    }

    [Fact]
    public async Task An_add_discards_the_audio_of_the_items_it_newly_matches_and_counts_what_needs_generating()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."], "n1", "n3");

        var added = await AddAsync(folder, "Holmes", "Holmz");

        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var answer = await JsonOf(added);
        Assert.Equal(1, answer.GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("needsAudioCount").GetInt32()); // n1 discarded, n2 never had any
        Assert.Equal("Holmes", answer.GetProperty("writtenForm").GetString());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task A_spoken_form_edit_discards_the_audio_of_the_entrys_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), Read2Me.Data.ProjectDbContext.NarratorId);
        await app.SeedItemAudioAsync(folder, book.ItemId("n3"), Read2Me.Data.ProjectDbContext.NarratorId);

        var edited = await EditAsync(folder, holmes, "Holmes", "Hohmz");

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var answer = await JsonOf(edited);
        Assert.Equal(1, answer.GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal("Hohmz", answer.GetProperty("spokenForm").GetString());
        Assert.Equal(holmes, answer.GetProperty("id").GetGuid());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task A_written_form_edit_discards_the_audio_of_its_old_and_new_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Lestrade slept.", "Watson read."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        foreach (var item in new[] { "n1", "n2", "n3" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);

        var edited = await EditAsync(folder, holmes, "Watson", "Holmz");

        Assert.Equal(2, (await JsonOf(edited)).GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(["n2"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task An_overlapping_add_discards_only_where_the_winner_changes_what_is_said()
    {
        var (folder, book) = await SeedWithAudioAsync(["Van Helsing fell.", "Helsing stood.", "Van wept."]);
        await AddedIdAsync(folder, "Helsing", "HEL-sing");
        foreach (var item in new[] { "n1", "n2", "n3" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);

        // The longer entry wins in n1, but says what "Helsing" alone already made of it.
        var same = await AddAsync(folder, "Van Helsing", "Van HEL-sing");
        Assert.Equal(0, (await JsonOf(same)).GetProperty("audioDiscardCount").GetInt32());

        var different = await EditAsync(folder, (await JsonOf(same)).GetProperty("id").GetGuid(), "Van Helsing", "VAHN HEL-sing");
        Assert.Equal(1, (await JsonOf(different)).GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(["n2", "n3"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task A_delete_discards_the_audio_of_its_old_matches_and_is_a_Delete_step()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), Read2Me.Data.ProjectDbContext.NarratorId);
        await app.SeedItemAudioAsync(folder, book.ItemId("n3"), Read2Me.Data.ProjectDbContext.NarratorId);
        var steps = (await StepsAsync(folder)).Count;

        var deleted = await DeleteAsync(folder, holmes);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var answer = await JsonOf(deleted);
        Assert.Equal(1, answer.GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(2, answer.GetProperty("needsAudioCount").GetInt32());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
        Assert.Empty(await ListAsync(folder));
        var after = await StepsAsync(folder);
        Assert.Equal(steps + 1, after.Count);
        var step = after[0];
        Assert.Equal("Pronunciation: delete \"Holmes\"", step.GetProperty("label").GetString());
        Assert.Equal("Delete", step.GetProperty("action").GetString());
    }

    [Fact]
    public async Task Add_and_edit_are_Pronunciation_steps_labelled_with_the_written_form()
    {
        var folder = await SeedAsync();
        var rain = await AddedIdAsync(folder, "Rain", "RAYN");

        Assert.Equal(HttpStatusCode.OK, (await EditAsync(folder, rain, "rain", "rane")).StatusCode);

        var steps = await StepsAsync(folder);
        Assert.Equal(["Pronunciation: edit \"rain\"", "Pronunciation: add \"Rain\""],
            steps.Take(2).Select(s => s.GetProperty("label").GetString()));
        Assert.Equal(["Pronunciation", "Pronunciation"], steps.Take(2).Select(s => s.GetProperty("action").GetString()));
        Assert.Equal("rain", Assert.Single(await ListAsync(folder)).GetProperty("writtenForm").GetString());
    }

    [Fact]
    public async Task Saving_an_unchanged_entry_writes_nothing_lands_no_step_and_keeps_the_audio()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), Read2Me.Data.ProjectDbContext.NarratorId);
        var before = (await StepsAsync(folder)).Count;

        var saved = await EditAsync(folder, holmes, " Holmes ", "Holmz");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(0, (await JsonOf(saved)).GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(before, (await StepsAsync(folder)).Count);
        Assert.Equal(["n1"], await WithAudioAsync(folder, book, 1));
    }

    // ── a Restore across a list change discards as the list edits would ─────

    private async Task<Guid> HeadAsync(string folder) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history"))
            .RootElement.GetProperty("headStepId").GetGuid();

    /// <summary>The step's Restore preview, then the Restore's answer.</summary>
    private async Task<(int Preview, JsonElement Restored)> RestoreAsync(string folder, Guid step)
    {
        var preview = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history/{step}"))
            .RootElement.GetProperty("restorePreview").GetProperty("audioDiscardCount").GetInt32();
        var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{step}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        return (preview, await JsonOf(restore));
    }

    [Fact]
    public async Task A_Restore_to_before_an_add_discards_the_audio_of_its_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var before = await HeadAsync(folder);
        await AddedIdAsync(folder, "Holmes", "Holmz");
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), Read2Me.Data.ProjectDbContext.NarratorId);
        await app.SeedItemAudioAsync(folder, book.ItemId("n3"), Read2Me.Data.ProjectDbContext.NarratorId);

        var (preview, restored) = await RestoreAsync(folder, before);

        Assert.Equal((1, 1), (preview, restored.GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(2, restored.GetProperty("needsAudioCount").GetInt32()); // n1 discarded, n2 never had any
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
        Assert.Empty(await ListAsync(folder));
    }

    [Fact]
    public async Task A_Restore_to_before_a_spoken_form_edit_discards_the_audio_of_the_entrys_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        var before = await HeadAsync(folder);
        await EditAsync(folder, holmes, "Holmes", "Hohmz");
        foreach (var item in new[] { "n1", "n2", "n3" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);

        var (preview, restored) = await RestoreAsync(folder, before);

        Assert.Equal((2, 2), (preview, restored.GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(2, restored.GetProperty("needsAudioCount").GetInt32());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
        Assert.Equal("Holmz", Assert.Single(await ListAsync(folder)).GetProperty("spokenForm").GetString());
    }

    [Fact]
    public async Task A_Restore_to_before_a_delete_discards_the_audio_of_the_entrys_old_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        var before = await HeadAsync(folder);
        await DeleteAsync(folder, holmes);
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), Read2Me.Data.ProjectDbContext.NarratorId);
        await app.SeedItemAudioAsync(folder, book.ItemId("n3"), Read2Me.Data.ProjectDbContext.NarratorId);

        var (preview, restored) = await RestoreAsync(folder, before);

        Assert.Equal((1, 1), (preview, restored.GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(2, restored.GetProperty("needsAudioCount").GetInt32());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
        Assert.Equal(holmes, Assert.Single(await ListAsync(folder)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_item_whose_text_and_spoken_text_both_change_in_a_Restore_is_counted_once()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var before = await HeadAsync(folder);
        var edited = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent($$"""{ "type": "UpdateParagraphItemText", "itemId": "{{book.ItemId("n1")}}", "text": "Holmes walked." }""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await AddedIdAsync(folder, "Holmes", "Holmz");
        foreach (var item in new[] { "n1", "n2", "n3" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);

        var (preview, restored) = await RestoreAsync(folder, before);

        Assert.Equal((2, 2), (preview, restored.GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(2, restored.GetProperty("needsAudioCount").GetInt32());
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task A_Restore_that_leaves_the_list_as_it_is_keeps_the_audio_of_its_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        await AddedIdAsync(folder, "Holmes", "Holmz");
        var before = await HeadAsync(folder);
        var edited = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent($$"""{ "type": "UpdateParagraphItemText", "itemId": "{{book.ItemId("n3")}}", "text": "Watson wrote." }""",
                Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        foreach (var item in new[] { "n1", "n2", "n3" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);

        var (preview, restored) = await RestoreAsync(folder, before);

        Assert.Equal((1, 1), (preview, restored.GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(["n1", "n2"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task Edit_and_delete_of_an_unknown_entry_are_404_and_an_edit_onto_another_written_form_is_409()
    {
        var folder = await SeedAsync();
        await AddedIdAsync(folder, "Rain", "RAYN");
        var night = await AddedIdAsync(folder, "night", "nite");

        Assert.Equal(HttpStatusCode.NotFound, (await EditAsync(folder, Guid.NewGuid(), "Rain", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DeleteAsync(folder, Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await EditAsync(folder, night, "RAIN", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EditAsync(folder, night, "night!", "x")).StatusCode);
    }

    [Fact]
    public async Task Writes_are_409_while_audio_generation_is_running_for_the_project()
    {
        app.FakeAi.Reset();
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var rain = await AddedIdAsync(folder, "Rain", "RAYN");
        var before = (await StepsAsync(folder)).Count;

        app.FakeAi.HoldSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var enqueue = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "Chapter", nodeId = book.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            var add = await AddAsync(folder, "night", "nite");
            var edit = await EditAsync(folder, rain, "Rain", "rane");
            var delete = await DeleteAsync(folder, rain);

            Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
            Assert.StartsWith("Audio generation is running", (await JsonOf(edit)).GetProperty("detail").GetString());
        }
        finally
        {
            app.FakeAi.HoldSpeech.TrySetResult();
            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
            app.FakeAi.Reset();
        }
        Assert.Equal(before, (await StepsAsync(folder)).Count);
        Assert.Equal("RAYN", Assert.Single(await ListAsync(folder)).GetProperty("spokenForm").GetString());
    }

    // ── the impact of a draft ───────────────────────────────────────────────

    [Theory]
    [InlineData("add")]
    [InlineData("spoken")]
    [InlineData("written")]
    [InlineData("overlap")]
    [InlineData("delete")]
    public async Task The_impact_of_a_draft_counts_what_saving_it_then_discards(string change)
    {
        var (folder, book) = await SeedWithAudioAsync(
            ["Holmes ran. Holmes fell.", "Holmes slept.", "Watson read.", "Lestrade sang."]);
        var holmes = await AddedIdAsync(folder, "Holmes", "Holmz");
        foreach (var item in new[] { "n1", "n3", "n4" })
            await app.SeedItemAudioAsync(folder, book.ItemId(item), Read2Me.Data.ProjectDbContext.NarratorId);
        object draft = change switch
        {
            "add" => new { writtenForm = "Watson", spokenForm = "WAT-sun" },
            "spoken" => new { id = holmes, writtenForm = "Holmes", spokenForm = "Hohmz" },
            "written" => new { id = holmes, writtenForm = "Lestrade", spokenForm = "Holmz" },
            "overlap" => new { writtenForm = "Holmes ran", spokenForm = "Holmz runs" }, // wins over "Holmes" in n1 only
            _ => new { id = holmes, delete = true },
        };
        var stepsBefore = (await StepsAsync(folder)).Count;

        var impact = await ImpactAsync(folder, draft);

        Assert.Equal(HttpStatusCode.OK, impact.StatusCode);
        var counted = (await JsonOf(impact)).GetProperty("audioDiscardCount").GetInt32();
        Assert.Equal(stepsBefore, (await StepsAsync(folder)).Count);
        var saved = change switch
        {
            "add" => await AddAsync(folder, "Watson", "WAT-sun"),
            "spoken" => await EditAsync(folder, holmes, "Holmes", "Hohmz"),
            "written" => await EditAsync(folder, holmes, "Lestrade", "Holmz"),
            "overlap" => await AddAsync(folder, "Holmes ran", "Holmz runs"),
            _ => await DeleteAsync(folder, holmes),
        };
        Assert.Equal(counted, (await JsonOf(saved)).GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(change == "written" ? 2 : 1, counted);
    }

    [Fact]
    public async Task The_impact_of_a_draft_lists_its_reach_overlaps_duplicate_and_every_match()
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedNarrationBookAsync(folder, "Part One",
            ["The rain fell. Van Helsing stood.", "Helsing slept. Watson read.", "Nothing here."]);
        var vanHelsing = await AddedIdAsync(folder, "Van Helsing", "VAHN HEL-sing");
        await AddedIdAsync(folder, "Watson", "WAT-sun");
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), Read2Me.Data.ProjectDbContext.NarratorId);

        var impact = await JsonOf(await ImpactAsync(folder, new { writtenForm = "helsing", spokenForm = "HEL-sing" }));

        Assert.Equal(1, impact.GetProperty("matchCount").GetInt32()); // "Van Helsing" wins in n1
        Assert.Equal(1, impact.GetProperty("itemCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("withAudioCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("audioDiscardCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, impact.GetProperty("duplicateOf").ValueKind);
        var overlap = Assert.Single(impact.GetProperty("overlaps").EnumerateArray());
        Assert.Equal(vanHelsing, overlap.GetProperty("id").GetGuid());
        Assert.False(overlap.GetProperty("draftWins").GetBoolean());

        var match = Assert.Single(impact.GetProperty("matches").EnumerateArray());
        Assert.Equal(book.ItemId("n2"), match.GetProperty("itemId").GetGuid());
        Assert.Equal(book.ChapterId("ch1"), match.GetProperty("chapterId").GetGuid());
        Assert.Equal("ch1", match.GetProperty("chapterTitle").GetString());
        Assert.Equal("Narrator", match.GetProperty("speaker").GetString());
        Assert.True(match.GetProperty("hasAudio").GetBoolean());
        Assert.Equal("Helsing slept.", match.GetProperty("sentence").GetString());
        Assert.Equal(0, match.GetProperty("matchStart").GetInt32());
        Assert.Equal(7, match.GetProperty("matchLength").GetInt32());
        Assert.Equal("HEL-sing slept.", match.GetProperty("spokenSentence").GetString());
        Assert.Equal(0, match.GetProperty("spokenMatchStart").GetInt32());
        Assert.Equal(8, match.GetProperty("spokenMatchLength").GetInt32());

        var duplicate = await JsonOf(await ImpactAsync(folder, new { writtenForm = "WATSON", spokenForm = "x" }));
        Assert.Equal("Watson", duplicate.GetProperty("duplicateOf").GetProperty("writtenForm").GetString());
        var second = await JsonOf(await ImpactAsync(folder, new { writtenForm = "read", spokenForm = "red" }));
        var readMatch = Assert.Single(second.GetProperty("matches").EnumerateArray());
        Assert.Equal("Watson read.", readMatch.GetProperty("sentence").GetString());
        Assert.Equal(7, readMatch.GetProperty("matchStart").GetInt32());
        Assert.Equal("WAT-sun red.", readMatch.GetProperty("spokenSentence").GetString());
        Assert.Equal(8, readMatch.GetProperty("spokenMatchStart").GetInt32());
        Assert.Equal(3, readMatch.GetProperty("spokenMatchLength").GetInt32());
        // The match's case carries over: a sentence-initial "Watson" respelt "wot-son" reads "Wot-son".
        var initial = Assert.Single((await JsonOf(await ImpactAsync(folder, new { id = (Guid?)null, writtenForm = "Watson", spokenForm = "wot-son" })))
            .GetProperty("matches").EnumerateArray());
        var spokenSentence = initial.GetProperty("spokenSentence").GetString()!;
        Assert.Equal("Wot-son", spokenSentence.Substring(initial.GetProperty("spokenMatchStart").GetInt32(), initial.GetProperty("spokenMatchLength").GetInt32()));
    }

    [Fact]
    public async Task The_impact_of_an_invalid_draft_is_400_and_of_an_unknown_entry_404()
    {
        var folder = await SeedAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ImpactAsync(folder, new { writtenForm = "'Rain", spokenForm = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ImpactAsync(folder, new { id = Guid.NewGuid(), delete = true })).StatusCode);
    }

    // ── try-out samples ─────────────────────────────────────────────────────

    private Task<HttpResponseMessage> TryAsync(string folder, object request) =>
        Http.PostAsJsonAsync($"{Url(folder)}/try", request);

    /// <summary>The <c>input</c> of each audio.cpp speech request since the fake was last reset.</summary>
    private List<string> SpeechInputs()
    {
        lock (app.FakeAi.AudioCppSpeechBodies)
            return [.. app.FakeAi.AudioCppSpeechBodies.Select(b => b["input"]!.GetValue<string>())];
    }

    private string? LastVoiceRef()
    {
        lock (app.FakeAi.AudioCppSpeechBodies)
            return app.FakeAi.AudioCppSpeechBodies[^1]["voice_ref"]?["data"]?.GetValue<string>();
    }

    [Fact]
    public async Task A_try_out_answers_the_sentence_as_WAV_with_the_draft_in_its_entrys_place_or_as_written()
    {
        app.FakeAi.Reset();
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedNarrationBookAsync(folder, "Part One", ["Holmes ran. Watson read the stormy book."]);
        await app.SeedNarratorVoiceAsync(folder);
        var watson = await AddedIdAsync(folder, "Watson", "WAT-sun");
        await AddedIdAsync(folder, "stormy", "storm-ee");
        var steps = (await StepsAsync(folder)).Count;

        var match = Assert.Single((await JsonOf(await ImpactAsync(folder,
            new { id = watson, writtenForm = "Watson", spokenForm = "WOT-son" }))).GetProperty("matches").EnumerateArray());
        Assert.Equal(12, match.GetProperty("sentenceStart").GetInt32());

        var draft = new { id = watson, writtenForm = "Watson", spokenForm = "WOT-son" };
        var drafted = await TryAsync(folder, new { itemId = book.ItemId("n1"), sentenceStart = 12, draft, asWritten = false, voice = "item" });
        var asWritten = await TryAsync(folder, new { itemId = book.ItemId("n1"), sentenceStart = 12, draft, asWritten = true, voice = "item" });
        var saved = await TryAsync(folder, new { itemId = book.ItemId("n1"), sentenceStart = 12, asWritten = false, voice = "item" });

        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        Assert.Equal("audio/wav", drafted.Content.Headers.ContentType?.MediaType);
        Assert.Equal("RIFF", Encoding.ASCII.GetString((await drafted.Content.ReadAsByteArrayAsync())[..4]));
        Assert.Equal(["WOT-son read the storm-ee book.", "Watson read the stormy book.", "WAT-sun read the storm-ee book."],
            SpeechInputs());
        Assert.Equal(HttpStatusCode.OK, asWritten.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // Nothing is stored: no step, the list as it was, the item still without audio.
        Assert.Equal(steps, (await StepsAsync(folder)).Count);
        Assert.Equal("WAT-sun", (await ListAsync(folder)).Single(e => e.GetProperty("id").GetGuid() == watson)
            .GetProperty("spokenForm").GetString());
        Assert.Empty(await WithAudioAsync(folder, book, 1));
    }

    [Fact]
    public async Task A_try_out_speaks_in_the_chosen_voice_or_the_items_own()
    {
        app.FakeAi.Reset();
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var alice = await app.SeedEditableVoiceAsync(folder, book.CharacterId("Alice"));
        var folderPath = Path.Combine(app.WorkspaceDir, folder);
        var narratorRef = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(folderPath, "voices", "narrator.wav")));
        var aliceRef = Convert.ToBase64String(await File.ReadAllBytesAsync(
            Directory.GetFiles(Path.Combine(folderPath, "voices", book.CharacterId("Alice").ToString()), "*.wav").Single()));
        Assert.NotEqual(narratorRef, aliceRef);

        Assert.Equal(HttpStatusCode.OK, (await TryAsync(folder,
            new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = new { voiceId = alice } })).StatusCode);
        Assert.Equal(aliceRef, LastVoiceRef());
        Assert.Equal(HttpStatusCode.OK, (await TryAsync(folder,
            new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "item" })).StatusCode);
        Assert.Equal(narratorRef, LastVoiceRef());

        Assert.Equal(HttpStatusCode.NotFound, (await TryAsync(folder,
            new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = new { voiceId = Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryAsync(folder,
            new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "narrator" })).StatusCode);
    }

    [Fact]
    public async Task A_try_out_of_an_unknown_item_is_404_of_no_sentence_400_and_without_a_voice_422()
    {
        var folder = await SeedAsync();
        var book = await app.SeedNarrationBookAsync($"{folder}-b", "Part One", ["Holmes ran."]);

        Assert.Equal(HttpStatusCode.NotFound, (await TryAsync(folder,
            new { itemId = Guid.NewGuid(), sentenceStart = 0, asWritten = false, voice = "item" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryAsync($"{folder}-b",
            new { itemId = book.ItemId("n1"), sentenceStart = 3, asWritten = false, voice = "item" })).StatusCode);
        var voiceless = await TryAsync($"{folder}-b", new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "item" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, voiceless.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TryAsync($"{folder}-b", new
        {
            itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "item",
            draft = new { writtenForm = "'Holmes", spokenForm = "x" },
        })).StatusCode);
    }

    /// <summary>
    /// A try-out in one project while <paramref name="start"/> keeps a GPU holder busy in another: it
    /// is refused 409, naming the holder. <paramref name="drain"/> releases and drains the holder
    /// whatever happens, so the class's next test meets an idle host.
    /// </summary>
    private async Task AssertTryOutRefusedWhileAsync(
        string holder, Func<string, TestUtils.BookHierarchyBuilder, Task<HttpResponseMessage>> start, Func<Task> drain)
    {
        app.FakeAi.Reset();
        var busy = $"api-pronunciations-{Guid.NewGuid():N}";
        var busyBook = await app.SeedProjectAsync(busy, "Busy Book", "Author", characterName: "Alice");
        await app.SeedNarratorVoiceAsync(busy);
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);

        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await start(busy, busyBook)).StatusCode);

            var refused = await TryAsync(folder, new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "item" });

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal($"Can't play while {holder} is running: it has the GPU.",
                (await JsonOf(refused)).GetProperty("detail").GetString());
        }
        finally
        {
            await drain();
            app.FakeAi.Reset();
        }
    }

    [Fact]
    public Task A_try_out_is_409_while_audio_generation_holds_the_GPU_in_any_project() =>
        AssertTryOutRefusedWhileAsync("Audio generation",
            (busy, busyBook) =>
            {
                app.FakeAi.HoldSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{busy}/audio/enqueue",
                    new { level = "Chapter", nodeId = busyBook.ChapterId("ch1"), needsAudioOnly = true });
            },
            async () =>
            {
                app.FakeAi.HoldSpeech?.TrySetResult();
                await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
            });

    [Fact]
    public Task A_try_out_is_409_while_attribution_holds_the_GPU_in_any_project() =>
        AssertTryOutRefusedWhileAsync("Attribution",
            (busy, busyBook) =>
            {
                app.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
                app.FakeAi.HoldLlmAfter(0);
                return Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{busy}/attribution/enqueue",
                    new { level = "Chapter", nodeId = busyBook.ChapterId("ch1"), unprocessedOnly = true });
            },
            async () =>
            {
                app.FakeAi.ReleaseLlm();
                await app.WaitForQueueDrainAsync("/api/attribution/queue");
            });

    [Fact]
    public Task A_try_out_is_409_while_a_voice_batch_holds_the_GPU_in_any_project() =>
        AssertTryOutRefusedWhileAsync("a voice batch",
            (busy, _) =>
            {
                app.FakeAi.LlmReply = _ => """[ { "name": "Main Voice", "description": "the only voice", "design_prompt": "A clear adult voice." } ]""";
                app.FakeAi.HoldLlmAfter(0);
                // The runner is running once it answers 202: Start sets IsRunning before it returns.
                return Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{busy}/voice-batch/prompts", new { regenerateAll = false });
            },
            async () =>
            {
                app.FakeAi.ReleaseLlm();
                await app.WaitForVoiceBatchIdleAsync();
            });

    // ── the Book view's marks ───────────────────────────────────────────────

    private async Task<JsonElement> MatchesAsync(string folder, IEnumerable<Guid> chapterIds, Guid? at = null)
    {
        var query = string.Join("&", chapterIds.Select(id => $"chapterIds={id}").Append(at is { } step ? $"at={step}" : "")
            .Where(p => p != ""));
        return JsonDocument.Parse(await Http.GetStringAsync($"{Url(folder)}/matches?{query}")).RootElement;
    }

    [Fact]
    public async Task Matches_answer_the_spans_in_the_named_chapters_with_the_entries_they_name()
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedMultiChapterProjectAsync(folder, "Marks Book", "Author", chapters: 3);
        var line = await AddedIdAsync(folder, "Line", "LYNE");
        var opens = await AddedIdAsync(folder, "opens", "OH-penz");
        await AddedIdAsync(folder, "Nowhere", "NO-wair");

        var matches = await MatchesAsync(folder, [book.ChapterId("Chapter 2"), book.ChapterId("Chapter 3")]);

        var spans = matches.GetProperty("spans").EnumerateArray()
            .Select(s => (s.GetProperty("itemId").GetGuid(), s.GetProperty("start").GetInt32(), s.GetProperty("length").GetInt32(),
                s.GetProperty("pronunciationId").GetGuid(), s.GetProperty("sentenceStart").GetInt32()))
            .ToList();
        Assert.Equal(
        [
            (book.ItemId("n2"), 10, 5, opens, 0), // "Chapter 2 opens."
            (book.ItemId("line2"), 1, 4, line, 0), // "“Line 2,” she said."
            (book.ItemId("n3"), 10, 5, opens, 0),
            (book.ItemId("line3"), 1, 4, line, 0),
        ], spans);
        var entries = matches.GetProperty("pronunciations").EnumerateArray()
            .Select(e => (e.GetProperty("id").GetGuid(), e.GetProperty("writtenForm").GetString(), e.GetProperty("spokenForm").GetString()))
            .ToList();
        Assert.Equal([(line, "Line", "LYNE"), (opens, "opens", "OH-penz")], entries);
    }

    [Fact]
    public async Task A_span_names_the_sentence_it_is_in_and_the_longest_written_form_wins()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran. Van Helsing fell."]);
        await AddedIdAsync(folder, "Holmes", "HOHMZ");
        var vanHelsing = await AddedIdAsync(folder, "Van Helsing", "VAHN HEL-sing");
        await AddedIdAsync(folder, "Helsing", "HEL-sing");

        var spans = (await MatchesAsync(folder, [book.ChapterId("ch1")])).GetProperty("spans").EnumerateArray().ToList();

        Assert.Equal(2, spans.Count);
        Assert.Equal(12, spans[1].GetProperty("start").GetInt32());
        Assert.Equal(11, spans[1].GetProperty("length").GetInt32());
        Assert.Equal(vanHelsing, spans[1].GetProperty("pronunciationId").GetGuid());
        Assert.Equal(12, spans[1].GetProperty("sentenceStart").GetInt32());
    }

    [Fact]
    public async Task Matches_read_at_a_step_and_none_or_unknown_chapters_answer_nothing()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran."]);
        var started = (await StepsAsync(folder))[0].GetProperty("stepId").GetGuid();
        await AddedIdAsync(folder, "Holmes", "HOHMZ");

        Assert.Single((await MatchesAsync(folder, [book.ChapterId("ch1")])).GetProperty("spans").EnumerateArray());
        var then = await MatchesAsync(folder, [book.ChapterId("ch1")], at: started);
        Assert.Empty(then.GetProperty("spans").EnumerateArray());
        Assert.Empty(then.GetProperty("pronunciations").EnumerateArray());
        Assert.Empty((await MatchesAsync(folder, [])).GetProperty("spans").EnumerateArray());
        Assert.Empty((await MatchesAsync(folder, [Guid.NewGuid()])).GetProperty("spans").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{Url($"no-such-{Guid.NewGuid():N}")}/matches?chapterIds={Guid.NewGuid()}")).StatusCode);
    }
}
