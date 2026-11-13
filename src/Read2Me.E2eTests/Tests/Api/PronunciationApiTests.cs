using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The Pronunciation list over HTTP: the contract only — routes, statuses, the wire fields, the
/// History steps and the 409s the endpoints own, and an item's Spoken text reaching TTS. What a change
/// discards, the reach and impact counts and a Restore's discard by Spoken text are proved over a real
/// project in PronunciationMutationTests, PronunciationImpactTests, PronunciationListTests,
/// PronunciationSpansTests and RestoreTests; the try-out's GPU holders in TryOutGpuBusyTests. The seed's
/// items read "It was a dark and stormy night.", "“Hello there,” she said." and "The rain kept falling."
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
    public async Task Add_answers_201_with_the_entry_and_its_reach_and_the_list_is_per_book()
    {
        var folder = await SeedAsync();
        await app.SeedProjectAsync($"{folder}-x", "Other", "Author");

        var stormy = await AddAsync(folder, "stormy", "STORM-ee");

        Assert.Equal(HttpStatusCode.Created, stormy.StatusCode);
        var entry = await JsonOf(stormy);
        Assert.Equal("stormy", entry.GetProperty("writtenForm").GetString());
        Assert.Equal("STORM-ee", entry.GetProperty("spokenForm").GetString());
        Assert.Equal(1, entry.GetProperty("matchCount").GetInt32());
        Assert.Equal(1, entry.GetProperty("itemCount").GetInt32());
        Assert.Equal(0, entry.GetProperty("withAudioCount").GetInt32());
        Assert.Equal($"/api/projects/{folder}/pronunciations/{entry.GetProperty("id").GetGuid()}", stormy.Headers.Location?.ToString());

        var listed = Assert.Single(await ListAsync(folder));
        Assert.Equal(entry.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());
        Assert.Equal(1, listed.GetProperty("matchCount").GetInt32());
        Assert.Empty(await ListAsync($"{folder}-x"));
    }

    [Theory]
    [InlineData("'Rain", "RAYN")]
    [InlineData("Rain", null)]
    public async Task An_invalid_form_is_422_and_lands_no_step(string? written, string? spoken)
    {
        var folder = await SeedAsync();
        var before = (await StepsAsync(folder)).Count;

        var response = await AddAsync(folder, written, spoken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, (await StepsAsync(folder)).Count);
    }

    [Fact]
    public async Task A_written_form_already_listed_is_409_and_lands_no_step()
    {
        var folder = await SeedAsync();
        Assert.Equal(HttpStatusCode.Created, (await AddAsync(folder, "Rain", "RAYN")).StatusCode);
        var before = (await StepsAsync(folder)).Count;

        var duplicate = await AddAsync(folder, "RAIN", "rane");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("RAIN", (await JsonOf(duplicate)).GetProperty("detail").GetString());
        Assert.Equal(before, (await StepsAsync(folder)).Count);
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
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
    }

    [Fact]
    public async Task Edit_and_delete_answer_what_they_cost_and_are_steps_labelled_with_the_written_form()
    {
        var folder = await SeedAsync();
        var rain = await AddedIdAsync(folder, "Rain", "RAYN");

        var edited = await EditAsync(folder, rain, "rain", "rane");
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var entry = await JsonOf(edited);
        Assert.Equal(rain, entry.GetProperty("id").GetGuid());
        Assert.Equal("rane", entry.GetProperty("spokenForm").GetString());
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("audioDiscardCount").ValueKind);
        Assert.Equal(JsonValueKind.Number, entry.GetProperty("needsAudioCount").ValueKind);

        // Saving it unchanged is a 200 that lands no step.
        var stepsBefore = (await StepsAsync(folder)).Count;
        Assert.Equal(HttpStatusCode.OK, (await EditAsync(folder, rain, " rain ", "rane")).StatusCode);
        Assert.Equal(stepsBefore, (await StepsAsync(folder)).Count);

        var deleted = await DeleteAsync(folder, rain);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var cost = await JsonOf(deleted);
        Assert.Equal(JsonValueKind.Number, cost.GetProperty("audioDiscardCount").ValueKind);
        Assert.Equal(JsonValueKind.Number, cost.GetProperty("needsAudioCount").ValueKind);

        var steps = await StepsAsync(folder);
        Assert.Equal(["Pronunciation: delete \"rain\"", "Pronunciation: edit \"rain\"", "Pronunciation: add \"Rain\""],
            steps.Take(3).Select(s => s.GetProperty("label").GetString()));
        Assert.Equal(["Delete", "Pronunciation", "Pronunciation"], steps.Take(3).Select(s => s.GetProperty("action").GetString()));
    }

    [Fact]
    public async Task A_Restore_to_before_an_add_discards_the_audio_of_its_matches()
    {
        var (folder, book) = await SeedWithAudioAsync(["Holmes ran.", "Holmes slept.", "Watson read."]);
        var before = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history"))
            .RootElement.GetProperty("headStepId").GetGuid();
        await AddedIdAsync(folder, "Holmes", "Holmz");
        await app.SeedItemAudioAsync(folder, book.ItemId("n1"), Read2Me.Data.ProjectDbContext.NarratorId);
        await app.SeedItemAudioAsync(folder, book.ItemId("n3"), Read2Me.Data.ProjectDbContext.NarratorId);

        var preview = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history/{before}"))
            .RootElement.GetProperty("restorePreview").GetProperty("audioDiscardCount").GetInt32();
        var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/history/{before}/restore", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal((1, 1), (preview, (await JsonOf(restore)).GetProperty("audioDiscardCount").GetInt32()));
        Assert.Equal(["n3"], await WithAudioAsync(folder, book, 3));
        Assert.Empty(await ListAsync(folder));
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
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await EditAsync(folder, night, "night!", "x")).StatusCode);
    }

    [Fact]
    public async Task A_write_is_409_while_audio_generation_is_running_for_the_project()
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

            var edit = await EditAsync(folder, rain, "Rain", "rane");

            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
            Assert.StartsWith("Audio generation is running", (await JsonOf(edit)).GetProperty("detail").GetString());
        }
        finally
        {
            app.FakeAi.HoldSpeech.TrySetResult();
            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
            app.FakeAi.Reset();
        }
        Assert.Equal(before, (await StepsAsync(folder)).Count);
    }

    // ── the impact of a draft ───────────────────────────────────────────────

    [Fact]
    public async Task The_impact_of_a_draft_answers_its_reach_overlaps_duplicate_and_matches_and_writes_nothing()
    {
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedNarrationBookAsync(folder, "Part One",
            ["The rain fell. Van Helsing stood.", "Helsing slept. Watson read.", "Nothing here."]);
        var vanHelsing = await AddedIdAsync(folder, "Van Helsing", "VAHN HEL-sing");
        await app.SeedItemAudioAsync(folder, book.ItemId("n2"), Read2Me.Data.ProjectDbContext.NarratorId);
        var steps = (await StepsAsync(folder)).Count;

        var response = await ImpactAsync(folder, new { writtenForm = "helsing", spokenForm = "HEL-sing" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var impact = await JsonOf(response);
        Assert.Equal(1, impact.GetProperty("matchCount").GetInt32());
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
        Assert.Equal(0, match.GetProperty("sentenceStart").GetInt32());
        Assert.Equal(0, match.GetProperty("matchStart").GetInt32());
        Assert.Equal(7, match.GetProperty("matchLength").GetInt32());
        Assert.Equal("HEL-sing slept.", match.GetProperty("spokenSentence").GetString());
        Assert.Equal(0, match.GetProperty("spokenMatchStart").GetInt32());
        Assert.Equal(8, match.GetProperty("spokenMatchLength").GetInt32());

        var duplicate = await JsonOf(await ImpactAsync(folder, new { writtenForm = "VAN HELSING", spokenForm = "x" }));
        Assert.Equal(vanHelsing, duplicate.GetProperty("duplicateOf").GetProperty("id").GetGuid());
        Assert.Equal(steps, (await StepsAsync(folder)).Count);
    }

    [Fact]
    public async Task The_impact_of_an_invalid_draft_is_422_and_of_an_unknown_entry_404()
    {
        var folder = await SeedAsync();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ImpactAsync(folder, new { writtenForm = "'Rain", spokenForm = "x" })).StatusCode);
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
    /// One holder, running in another project, proves the 409 and its body; which holders refuse
    /// (attribution and audio generation queued, a voice batch running) is TryOutGpuBusyTests'.
    /// </summary>
    [Fact]
    public async Task A_try_out_is_409_while_audio_generation_holds_the_GPU_in_another_project()
    {
        app.FakeAi.Reset();
        var busy = $"api-pronunciations-{Guid.NewGuid():N}";
        var busyBook = await app.SeedProjectAsync(busy, "Busy Book", "Author");
        await app.SeedNarratorVoiceAsync(busy);
        var folder = $"api-pronunciations-{Guid.NewGuid():N}";
        var book = await app.SeedProjectAsync(folder, "Pronunciation Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);

        app.FakeAi.HoldSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{busy}/audio/enqueue",
                new { level = "Chapter", nodeId = busyBook.ChapterId("ch1"), needsAudioOnly = true })).StatusCode);

            var refused = await TryAsync(folder, new { itemId = book.ItemId("n1"), sentenceStart = 0, asWritten = false, voice = "item" });

            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("Can't play while Audio generation is running: it has the GPU.",
                (await JsonOf(refused)).GetProperty("detail").GetString());
        }
        finally
        {
            app.FakeAi.HoldSpeech.TrySetResult();
            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
            app.FakeAi.Reset();
        }
    }

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
