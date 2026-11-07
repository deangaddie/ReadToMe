using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Series;
using Read2Me.E2eTests.Infrastructure;
using static Read2Me.E2eTests.Tests.Api.SeriesSeed;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The series registry's routes (#217): <c>/api/series</c>, membership, the position set, the poster
/// (#219), the <c>series</c> field on the project reads and the delete-project cascade, with the
/// status each refusal maps to. Positions, ties and poster choice are the Series unit tests' to pin
/// (<c>SeriesPositionsTests</c>, <c>SeriesSnapshotTests</c>); these prove only what a host proves.
/// </summary>
public class SeriesApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private readonly SeriesSeed seed = new(app);

    private Task<HttpResponseMessage> TryJoinAsync(Guid series, string folder, bool? move = null) =>
        move is null
            ? Http.PutAsJsonAsync(seed.Url($"/api/series/{series}/books/{folder}"), new { }) // move left out: false
            : Http.PutAsJsonAsync(seed.Url($"/api/series/{series}/books/{folder}"), new { move });

    private Task<HttpResponseMessage> SetPositionsAsync(Guid series, object positions) =>
        Http.PutAsJsonAsync(seed.Url($"/api/series/{series}/positions"), new { positions });

    private async Task<JsonElement?> SeriesReadAsync(Guid id)
    {
        foreach (var series in (await seed.GetJsonAsync("/api/series")).EnumerateArray())
            if (series.GetProperty("id").GetGuid() == id)
                return series;
        return null;
    }

    private static IEnumerable<(string Folder, decimal Position)> Books(JsonElement series) =>
        series.GetProperty("books").EnumerateArray()
            .Select(b => (b.GetProperty("folder").GetString()!, b.GetProperty("position").GetDecimal()));

    private async Task<JsonElement> ProjectRowAsync(string folder) =>
        (await seed.GetJsonAsync("/api/projects")).EnumerateArray()
            .Single(p => p.GetProperty("folderName").GetString() == folder);

    private string RegistryPath(Guid series) => Path.Combine(seed.RegistryFolder(series), SeriesDbContext.DbFileName);

    private async Task<int> StoredBookCountAsync(Guid series)
    {
        await using var db = await seed.OpenRegistryAsync(series);
        return await db.Books.CountAsync();
    }

    // ---- create, list, rename, project reads ------------------------------------------------------

    [Fact]
    public async Task A_series_created_from_a_book_is_on_disk_and_the_project_reads_say_so()
    {
        var books = await seed.BooksAsync(2);
        var (scarlet, four) = (books[0], books[1]);
        var name = UniqueName();

        var id = await seed.CreateSeriesAsync($"  {name}  ", scarlet.Folder);

        Assert.True(File.Exists(RegistryPath(id)));
        var series = (await SeriesReadAsync(id))!.Value;
        Assert.Equal(name, series.GetProperty("name").GetString()); // trimmed
        Assert.Equal([(scarlet.Folder, 1m)], Books(series));

        var row = await ProjectRowAsync(scarlet.Folder);
        Assert.Equal(id, row.GetProperty("series").GetProperty("seriesId").GetGuid());
        Assert.Equal(name, row.GetProperty("series").GetProperty("name").GetString());
        Assert.Equal(1m, row.GetProperty("series").GetProperty("position").GetDecimal());
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(four.Folder)).GetProperty("series").ValueKind);

        Assert.Equal(id, (await seed.GetJsonAsync($"/api/projects/{scarlet.Folder}")).GetProperty("series").GetProperty("seriesId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await seed.GetJsonAsync($"/api/projects/{four.Folder}")).GetProperty("series").ValueKind);
    }

    [Fact]
    public async Task A_series_created_by_name_only_starts_empty()
    {
        var id = await seed.CreateSeriesAsync(UniqueName("Empty"));

        Assert.Empty(Books((await SeriesReadAsync(id))!.Value));
    }

    [Fact]
    public async Task A_blank_name_is_400_on_create_and_rename()
    {
        await AssertProblemAsync(await Http.PostAsJsonAsync(seed.Url("/api/series"), new { name = "   " }), HttpStatusCode.BadRequest);
        var id = await seed.CreateSeriesAsync(UniqueName());
        await AssertProblemAsync(await Http.PatchAsJsonAsync(seed.Url($"/api/series/{id}"), new { name = "" }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_a_rename_applies_at_once()
    {
        var name = UniqueName();
        var first = await seed.CreateSeriesAsync(name);

        await AssertProblemAsync(await Http.PostAsJsonAsync(seed.Url("/api/series"), new { name = name.ToUpperInvariant() }),
            HttpStatusCode.Conflict, name);

        var second = await seed.CreateSeriesAsync(UniqueName("Other"));
        await AssertProblemAsync(await Http.PatchAsJsonAsync(seed.Url($"/api/series/{second}"), new { name = name.ToLowerInvariant() }),
            HttpStatusCode.Conflict, name);

        // A series may change the case of its own name.
        var renamed = await Http.PatchAsJsonAsync(seed.Url($"/api/series/{first}"), new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(name.ToUpperInvariant(), (await JsonAsync(renamed)).GetProperty("name").GetString());
        Assert.Equal(name.ToUpperInvariant(), (await SeriesReadAsync(first))!.Value.GetProperty("name").GetString());
    }

    // ---- join, move, leave ----------------------------------------------------------------------

    /// <summary>Which position a book joins at is <c>SeriesPositionsTests</c>'; here, the routes and that joining twice saves nothing.</summary>
    [Fact]
    public async Task A_book_joins_once_and_leaves_and_the_project_reads_follow()
    {
        var holmes = await seed.SeriesAsync(books: 2, members: 1);

        Assert.Equal(2m, await seed.JoinAsync(holmes.Series, holmes.Four.Folder));
        Assert.Equal(2m, await seed.JoinAsync(holmes.Series, holmes.Four.Folder));
        Assert.Equal(2, await StoredBookCountAsync(holmes.Series));

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/books/{holmes.Four.Folder}"))).StatusCode);
        Assert.Equal([(holmes.Scarlet.Folder, 1m)], Books((await SeriesReadAsync(holmes.Series))!.Value));
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(holmes.Four.Folder)).GetProperty("series").ValueKind);

        // The last book leaving keeps the series.
        await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/books/{holmes.Scarlet.Folder}"));
        Assert.Empty(Books((await SeriesReadAsync(holmes.Series))!.Value));
    }

    [Fact]
    public async Task A_book_in_another_series_is_409_naming_it_on_join_and_create_unless_moved()
    {
        var holmes = await seed.SeriesAsync(books: 2);
        var fromName = (await SeriesReadAsync(holmes.Series))!.Value.GetProperty("name").GetString()!;
        var to = await seed.CreateSeriesAsync(UniqueName());

        await AssertProblemAsync(await TryJoinAsync(to, holmes.Scarlet.Folder), HttpStatusCode.Conflict, fromName, "A Study in Scarlet");
        await AssertProblemAsync(
            await Http.PostAsJsonAsync(seed.Url("/api/series"), new { name = UniqueName("Other"), folder = holmes.Scarlet.Folder }),
            HttpStatusCode.Conflict, fromName);

        var moved = await TryJoinAsync(to, holmes.Scarlet.Folder, move: true);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal([holmes.Four.Folder], Books((await SeriesReadAsync(holmes.Series))!.Value).Select(b => b.Folder));
        Assert.Equal(to, (await ProjectRowAsync(holmes.Scarlet.Folder)).GetProperty("series").GetProperty("seriesId").GetGuid());
    }

    [Fact]
    public async Task Unknown_series_folders_and_non_members_are_404()
    {
        var holmes = await seed.SeriesAsync(books: 2, members: 1);
        var unknown = Guid.NewGuid();

        await AssertProblemAsync(await Http.PostAsJsonAsync(seed.Url("/api/series"), new { name = UniqueName(), folder = "no-such-book" }),
            HttpStatusCode.NotFound, "no-such-book");
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PatchAsJsonAsync(seed.Url($"/api/series/{unknown}"), new { name = UniqueName() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(seed.Url($"/api/series/{unknown}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TryJoinAsync(unknown, holmes.Four.Folder)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(seed.Url($"/api/series/{unknown}/books/{holmes.Scarlet.Folder}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetPositionsAsync(unknown, new Dictionary<string, decimal>())).StatusCode);

        await AssertProblemAsync(await TryJoinAsync(holmes.Series, "no-such-book"), HttpStatusCode.NotFound, "no-such-book");
        await AssertProblemAsync(await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/books/no-such-book")), HttpStatusCode.NotFound);
        await AssertProblemAsync(await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/books/{holmes.Four.Folder}")),
            HttpStatusCode.NotFound, "The Sign of the Four");
    }

    // ---- the position set -----------------------------------------------------------------------

    /// <summary>Which sets are malformed, stale or tied is <c>SeriesPositionsTests</c>'; here, the status of each and that a refusal saves nothing.</summary>
    [Fact]
    public async Task A_position_set_saves_as_one_and_a_malformed_set_is_400_and_a_stale_one_409_saving_nothing()
    {
        var holmes = await seed.SeriesAsync(books: 2);

        await AssertProblemAsync(await SetPositionsAsync(holmes.Series,
            new Dictionary<string, decimal> { [holmes.Scarlet.Folder] = -1m, [holmes.Four.Folder] = 2m }), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await SetPositionsAsync(holmes.Series,
            new Dictionary<string, decimal> { [holmes.Scarlet.Folder] = 1m }), HttpStatusCode.Conflict);
        Assert.Equal([(holmes.Scarlet.Folder, 1m), (holmes.Four.Folder, 2m)], Books((await SeriesReadAsync(holmes.Series))!.Value));

        var swapped = await SetPositionsAsync(holmes.Series,
            new Dictionary<string, decimal> { [holmes.Scarlet.Folder] = 2m, [holmes.Four.Folder] = 1m });

        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal([(holmes.Four.Folder, 1m), (holmes.Scarlet.Folder, 2m)], Books(await JsonAsync(swapped)));
        Assert.Equal([(holmes.Four.Folder, 1m), (holmes.Scarlet.Folder, 2m)], Books((await SeriesReadAsync(holmes.Series))!.Value));
    }

    // ---- delete ---------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_series_removes_its_registry_and_keeps_every_book()
    {
        var holmes = await seed.SeriesAsync(books: 2);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}"))).StatusCode);

        Assert.False(Directory.Exists(seed.RegistryFolder(holmes.Series)));
        Assert.Null(await SeriesReadAsync(holmes.Series));
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(holmes.Scarlet.Folder)).GetProperty("series").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync(seed.Url($"/api/projects/{holmes.Four.Folder}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}"))).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_member_project_removes_its_registry_entry_at_once()
    {
        var holmes = await seed.SeriesAsync(books: 2);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/projects/{holmes.Scarlet.Folder}"))).StatusCode);

        Assert.Equal(1, await StoredBookCountAsync(holmes.Series)); // before any series read could heal it
        Assert.Equal([(holmes.Four.Folder, 2m)], Books((await SeriesReadAsync(holmes.Series))!.Value));
    }

    // ---- what can't be read is a 422 ------------------------------------------------------------

    [Fact]
    public async Task A_corrupt_registry_is_left_out_of_reads_and_blocks_writes_whose_rules_it_could_break()
    {
        var holmes = await seed.SeriesAsync(books: 2, members: 1);
        var corrupt = seed.RegistryFolder(Guid.NewGuid());
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, SeriesDbContext.DbFileName), "not a database");
        try
        {
            Assert.NotNull(await SeriesReadAsync(holmes.Series));
            await AssertProblemAsync(await Http.PostAsJsonAsync(seed.Url("/api/series"), new { name = UniqueName() }),
                HttpStatusCode.UnprocessableEntity);
            await AssertProblemAsync(await TryJoinAsync(holmes.Series, holmes.Four.Folder), HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(corrupt, recursive: true);
        }
    }

    /// <summary>
    /// A member whose project.db is there but can't be read is neither healed away nor left out of
    /// a position save: the set can't name it, so the save is a 422, not a crash. No unit test pins
    /// this refusal, so it stays here.
    /// </summary>
    [Fact]
    public async Task An_unreadable_member_blocks_a_position_save_and_is_not_dropped()
    {
        var holmes = await seed.SeriesAsync(books: 2);
        var fourFolder = Path.Combine(app.WorkspaceDir, holmes.Four.Folder);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(fourFolder, "project.db"), "not a database");
        try
        {
            await AssertProblemAsync(await SetPositionsAsync(holmes.Series, new Dictionary<string, decimal> { [holmes.Scarlet.Folder] = 1m }),
                HttpStatusCode.UnprocessableEntity);
            Assert.Equal(2, await StoredBookCountAsync(holmes.Series));
        }
        finally
        {
            // Other tests in this class heal only while every project reads.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(fourFolder, recursive: true);
        }
    }

    // ---- the poster (#219) ----------------------------------------------------------------------

    private static MultipartFormDataContent ImageForm(byte[] bytes, string fileName)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", fileName);
        return form;
    }

    private async Task<HttpResponseMessage> PutPosterAsync(Guid series, byte[] bytes, string fileName = "poster.png")
    {
        using var form = ImageForm(bytes, fileName);
        return await Http.PutAsync(seed.Url($"/api/series/{series}/poster"), form);
    }

    private async Task<JsonElement> PosterAsync(Guid series) => (await SeriesReadAsync(series))!.Value.GetProperty("poster");

    /// <summary>The bytes the poster's URL serves now.</summary>
    private async Task<string> ServedAsync(JsonElement poster)
    {
        var response = await Http.GetAsync(seed.Url(poster.GetProperty("url").GetString()!));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task An_uploaded_poster_is_served_and_a_replacement_takes_its_place()
    {
        var id = await seed.CreateSeriesAsync(UniqueName());

        var uploaded = await PutPosterAsync(id, Encoding.UTF8.GetBytes("first poster"));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal("Uploaded", (await JsonAsync(uploaded)).GetProperty("poster").GetProperty("source").GetString());

        var poster = await PosterAsync(id);
        Assert.Equal("Uploaded", poster.GetProperty("source").GetString());
        Assert.Equal("first poster", await ServedAsync(poster));
        Assert.Equal("image/png", (await Http.GetAsync(seed.Url($"/api/series/{id}/poster"))).Content.Headers.ContentType?.MediaType);

        await Task.Delay(20); // a later write time, so the URL's version moves on
        Assert.Equal(HttpStatusCode.OK, (await PutPosterAsync(id, Encoding.UTF8.GetBytes("second poster"), "Second.JPG")).StatusCode);

        var replaced = await PosterAsync(id);
        Assert.NotEqual(poster.GetProperty("url").GetString(), replaced.GetProperty("url").GetString());
        Assert.Equal("second poster", await ServedAsync(replaced));
        Assert.Equal("image/jpeg", (await Http.GetAsync(seed.Url($"/api/series/{id}/poster"))).Content.Headers.ContentType?.MediaType);
        Assert.Equal(["poster.jpg"], // the earlier upload is gone
            Directory.GetFiles(seed.RegistryFolder(id), "poster*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_wrong_type_or_oversize_poster_is_400_and_an_unknown_series_or_no_poster_404()
    {
        var id = await seed.CreateSeriesAsync(UniqueName());

        await AssertProblemAsync(await PutPosterAsync(id, [1, 2, 3], "poster.gif"), HttpStatusCode.BadRequest, ".png");
        await AssertProblemAsync(await PutPosterAsync(id, new byte[10 * 1024 * 1024 + 1]), HttpStatusCode.BadRequest, "10 MB");

        var poster = await PosterAsync(id);
        Assert.Equal("None", poster.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, poster.GetProperty("url").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(seed.Url($"/api/series/{id}/poster"))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await PutPosterAsync(Guid.NewGuid(), [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(seed.Url($"/api/series/{Guid.NewGuid()}/poster"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(seed.Url($"/api/series/{Guid.NewGuid()}/poster"))).StatusCode);
    }

    /// <summary>Which book's cover stands in is <c>SeriesSnapshotTests</c>'; here, that clearing is a 204 and the fallback is served.</summary>
    [Fact]
    public async Task Clearing_the_poster_is_204_also_with_none_and_the_cover_is_served_in_its_place()
    {
        var holmes = await seed.SeriesAsync(books: 1);
        using (var cover = ImageForm(Encoding.UTF8.GetBytes("scarlet cover"), "cover.png"))
            Assert.Equal(HttpStatusCode.OK, (await Http.PutAsync(seed.Url($"/api/projects/{holmes.Scarlet.Folder}/cover"), cover)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/poster"))).StatusCode);

        await PutPosterAsync(holmes.Series, Encoding.UTF8.GetBytes("a poster"));
        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/poster"))).StatusCode);

        var poster = await PosterAsync(holmes.Series);
        Assert.Equal("Cover", poster.GetProperty("source").GetString());
        Assert.Equal(1m, poster.GetProperty("coverPosition").GetDecimal());
        Assert.Equal("scarlet cover", await ServedAsync(poster));
    }

    // ---- no History, no origin, the hub signal --------------------------------------------------

    [Fact]
    public async Task Registry_writes_make_no_History_step_ignore_the_origin_and_signal_the_hub()
    {
        var four = (await seed.BooksAsync(1))[0];
        var stepsBefore = (await seed.HistoryLabelsAsync(four)).Count;
        await using var hub = new HubConnectionBuilder().WithUrl(seed.Url("/hubs/live")).Build();
        var messages = new ConcurrentQueue<JsonElement>();
        hub.On<JsonElement>("series", messages.Enqueue);
        await hub.StartJoinedAsync();

        var id = await seed.CreateSeriesAsync(UniqueName());
        using (var join = new HttpRequestMessage(HttpMethod.Put, seed.Url($"/api/series/{id}/books/{four.Folder}")))
        {
            join.Headers.Add("X-Origin-Id", Guid.NewGuid().ToString());
            join.Content = JsonContent.Create(new { move = false });
            Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(join)).StatusCode);
        }
        await SetPositionsAsync(id, new Dictionary<string, decimal> { [four.Folder] = 4m });
        await Http.PatchAsJsonAsync(seed.Url($"/api/series/{id}"), new { name = UniqueName("Renamed") });

        Assert.Equal(stepsBefore, (await seed.HistoryLabelsAsync(four)).Count);

        // The writes land inside one 250 ms window, so they arrive as few messages: a re-read
        // signal with no payload and no origin.
        await WaitForAsync(() => messages.Any(m => m.GetProperty("seriesId").GetGuid() == id));
        var changed = messages.First(m => m.GetProperty("seriesId").GetGuid() == id);
        Assert.Equal("Changed", changed.GetProperty("kind").GetString());
        Assert.False(changed.TryGetProperty("originId", out _));

        await Http.DeleteAsync(seed.Url($"/api/series/{id}"));
        await WaitForAsync(() => messages.Any(m => m.GetProperty("seriesId").GetGuid() == id && m.GetProperty("kind").GetString() == "Deleted"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Condition not met within {timeoutMs} ms");
            await Task.Delay(20);
        }
    }
}
