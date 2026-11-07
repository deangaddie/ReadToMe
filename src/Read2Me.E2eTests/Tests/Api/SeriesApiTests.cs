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

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The series registry and its lifecycle (#217): <c>/api/series</c>, membership and the position
/// set, the <c>series</c> field on the project reads, and every refusal the Series Agent API
/// (#213) lists. The books are the sample series (#209): Holmes, in publication order.
/// </summary>
public class SeriesApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private sealed record Holmes(string Scarlet, string Four, string Adventures);

    /// <summary>Three fresh Holmes projects; folders and series names are unique per test (one host per class).</summary>
    private async Task<Holmes> SeedHolmesAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var books = new Holmes($"scarlet-{tag}", $"four-{tag}", $"adventures-{tag}");
        await app.SeedProjectAsync(books.Scarlet, "A Study in Scarlet", "Arthur Conan Doyle", "Watson");
        await app.SeedProjectAsync(books.Four, "The Sign of the Four", "Arthur Conan Doyle", "Watson");
        await app.SeedProjectAsync(books.Adventures, "The Adventures of Sherlock Holmes", "Arthur Conan Doyle", "Watson");
        return books;
    }

    private static string UniqueName(string name = "Sherlock Holmes") => $"{name} {Guid.NewGuid():N}"[..(name.Length + 9)];

    private async Task<Guid> CreateAsync(string name, string? folder = null)
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name, folder });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> JoinAsync(Guid series, string folder, bool? move = null) =>
        move is null
            ? await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/books/{folder}", new { }) // move left out: false
            : await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/books/{folder}", new { move });

    private async Task<decimal> JoinedAtAsync(Guid series, string folder, bool? move = null)
    {
        var response = await JoinAsync(series, folder, move);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("position").GetDecimal();
    }

    private Task<HttpResponseMessage> SetPositionsAsync(Guid series, object positions) =>
        Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/positions", new { positions });

    private async Task<JsonElement?> SeriesAsync(Guid id)
    {
        var all = await GetJsonAsync("/api/series");
        foreach (var series in all.EnumerateArray())
            if (series.GetProperty("id").GetGuid() == id)
                return series;
        return null;
    }

    private static IEnumerable<(string Folder, decimal Position)> Books(JsonElement series) =>
        series.GetProperty("books").EnumerateArray()
            .Select(b => (b.GetProperty("folder").GetString()!, b.GetProperty("position").GetDecimal()));

    private async Task<JsonElement> ProjectRowAsync(string folder) =>
        (await GetJsonAsync("/api/projects")).EnumerateArray()
            .Single(p => p.GetProperty("folderName").GetString() == folder);

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, params string[] named)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var detail = (await JsonAsync(response)).GetProperty("detail").GetString();
        foreach (var name in named)
            Assert.Contains(name, detail);
    }

    private string RegistryPath(Guid series) =>
        Path.Combine(app.WorkspaceDir, ".series", series.ToString("D"), SeriesDbContext.DbFileName);

    private async Task<int> StoredBookCountAsync(Guid series)
    {
        await using var db = await SeriesDbContext.OpenAsync(Path.GetDirectoryName(RegistryPath(series))!);
        return await db.Books.CountAsync();
    }

    // ---- create, list, project reads ------------------------------------------------------------

    [Fact]
    public async Task A_series_created_from_a_book_starts_with_it_as_Book_1_and_the_project_reads_say_so()
    {
        var holmes = await SeedHolmesAsync();
        var name = UniqueName();

        var id = await CreateAsync($"  {name}  ", holmes.Scarlet);

        Assert.True(File.Exists(RegistryPath(id)));
        var series = (await SeriesAsync(id))!.Value;
        Assert.Equal(name, series.GetProperty("name").GetString()); // trimmed
        Assert.Equal([(holmes.Scarlet, 1m)], Books(series));

        var row = await ProjectRowAsync(holmes.Scarlet);
        Assert.Equal(id, row.GetProperty("series").GetProperty("seriesId").GetGuid());
        Assert.Equal(name, row.GetProperty("series").GetProperty("name").GetString());
        Assert.Equal(1m, row.GetProperty("series").GetProperty("position").GetDecimal());
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(holmes.Four)).GetProperty("series").ValueKind);

        var detail = await GetJsonAsync($"/api/projects/{holmes.Scarlet}");
        Assert.Equal(id, detail.GetProperty("series").GetProperty("seriesId").GetGuid());
        Assert.Equal(JsonValueKind.Null, (await GetJsonAsync($"/api/projects/{holmes.Four}")).GetProperty("series").ValueKind);
    }

    [Fact]
    public async Task A_series_created_by_name_only_starts_empty()
    {
        var id = await CreateAsync(UniqueName("Empty"));

        Assert.Empty(Books((await SeriesAsync(id))!.Value));
    }

    [Fact]
    public async Task A_blank_name_is_400_on_create_and_rename()
    {
        await AssertProblemAsync(await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = "   " }),
            HttpStatusCode.BadRequest);
        var id = await CreateAsync(UniqueName());
        await AssertProblemAsync(await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{id}", new { name = "" }),
            HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case_and_a_rename_applies_at_once()
    {
        var name = UniqueName();
        var first = await CreateAsync(name);

        await AssertProblemAsync(await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = name.ToUpperInvariant() }),
            HttpStatusCode.Conflict, name);

        var second = await CreateAsync(UniqueName("Other"));
        await AssertProblemAsync(await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{second}", new { name = name.ToLowerInvariant() }),
            HttpStatusCode.Conflict, name);

        // A series may change the case of its own name.
        var renamed = await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{first}", new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(name.ToUpperInvariant(), (await JsonAsync(renamed)).GetProperty("name").GetString());
        Assert.Equal(name.ToUpperInvariant(), (await SeriesAsync(first))!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Creating_with_an_unknown_folder_is_404()
    {
        await AssertProblemAsync(
            await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = UniqueName(), folder = "no-such-book" }),
            HttpStatusCode.NotFound, "no-such-book");
    }

    // ---- join, move, leave ----------------------------------------------------------------------

    [Fact]
    public async Task A_joining_book_gets_the_next_whole_number_and_joining_again_changes_nothing()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);

        Assert.Equal(2m, await JoinedAtAsync(id, holmes.Four));
        Assert.Equal(HttpStatusCode.OK, (await SetPositionsAsync(id,
            new Dictionary<string, decimal> { [holmes.Scarlet] = 1m, [holmes.Four] = 2.5m })).StatusCode);
        Assert.Equal(3m, await JoinedAtAsync(id, holmes.Adventures, move: false));
        Assert.Equal(2.5m, await JoinedAtAsync(id, holmes.Four));

        Assert.Equal([(holmes.Scarlet, 1m), (holmes.Four, 2.5m), (holmes.Adventures, 3m)], Books((await SeriesAsync(id))!.Value));
    }

    [Fact]
    public async Task A_book_in_another_series_is_refused_naming_it_unless_moved()
    {
        var holmes = await SeedHolmesAsync();
        var fromName = UniqueName("Doyle");
        var from = await CreateAsync(fromName, holmes.Scarlet);
        await JoinedAtAsync(from, holmes.Four);
        var to = await CreateAsync(UniqueName());

        await AssertProblemAsync(await JoinAsync(to, holmes.Scarlet), HttpStatusCode.Conflict, fromName, "A Study in Scarlet");

        Assert.Equal(1m, await JoinedAtAsync(to, holmes.Scarlet, move: true));
        Assert.Equal([(holmes.Four, 2m)], Books((await SeriesAsync(from))!.Value)); // not renumbered
        Assert.Equal([(holmes.Scarlet, 1m)], Books((await SeriesAsync(to))!.Value));
        Assert.Equal(to, (await ProjectRowAsync(holmes.Scarlet)).GetProperty("series").GetProperty("seriesId").GetGuid());
    }

    [Fact]
    public async Task Creating_from_a_book_in_another_series_is_409()
    {
        var holmes = await SeedHolmesAsync();
        var name = UniqueName();
        await CreateAsync(name, holmes.Scarlet);

        await AssertProblemAsync(
            await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = UniqueName("Other"), folder = holmes.Scarlet }),
            HttpStatusCode.Conflict, name);
    }

    [Fact]
    public async Task Leaving_renumbers_nobody_and_the_last_book_leaving_keeps_the_series()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);
        await JoinedAtAsync(id, holmes.Adventures);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/books/{holmes.Four}")).StatusCode);
        Assert.Equal([(holmes.Scarlet, 1m), (holmes.Adventures, 3m)], Books((await SeriesAsync(id))!.Value));
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(holmes.Four)).GetProperty("series").ValueKind);

        await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/books/{holmes.Scarlet}");
        await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/books/{holmes.Adventures}");
        Assert.Empty(Books((await SeriesAsync(id))!.Value));
    }

    [Fact]
    public async Task Unknown_series_folders_and_non_members_are_404()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        var unknown = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{unknown}", new { name = UniqueName() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await JoinAsync(unknown, holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{unknown}/books/{holmes.Scarlet}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetPositionsAsync(unknown, new Dictionary<string, decimal>())).StatusCode);

        await AssertProblemAsync(await JoinAsync(id, "no-such-book"), HttpStatusCode.NotFound, "no-such-book");
        await AssertProblemAsync(await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/books/no-such-book"), HttpStatusCode.NotFound);
        await AssertProblemAsync(await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/books/{holmes.Four}"),
            HttpStatusCode.NotFound, "The Sign of the Four");
    }

    // ---- the position set -----------------------------------------------------------------------

    [Fact]
    public async Task Positions_save_as_one_set_so_a_swap_works()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);

        var swapped = await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 2m, [holmes.Four] = 1m });

        Assert.Equal(HttpStatusCode.OK, swapped.StatusCode);
        Assert.Equal([(holmes.Four, 1m), (holmes.Scarlet, 2m)], Books(await JsonAsync(swapped)));
        Assert.Equal([(holmes.Four, 1m), (holmes.Scarlet, 2m)], Books((await SeriesAsync(id))!.Value));
    }

    [Fact]
    public async Task A_bad_position_is_400_a_stale_set_409_and_a_tie_409_naming_both_books()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);

        await AssertProblemAsync(await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = -1m, [holmes.Four] = 2m }),
            HttpStatusCode.BadRequest);
        await AssertProblemAsync(await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 1m, [holmes.Four] = 1.505m }),
            HttpStatusCode.BadRequest);
        await AssertProblemAsync(await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 1m }),
            HttpStatusCode.Conflict);
        await AssertProblemAsync(await SetPositionsAsync(id,
                new Dictionary<string, decimal> { [holmes.Scarlet] = 1m, [holmes.Four] = 2m, [holmes.Adventures] = 3m }),
            HttpStatusCode.Conflict);
        await AssertProblemAsync(await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 2m, [holmes.Four] = 2m }),
            HttpStatusCode.Conflict, "A Study in Scarlet", "The Sign of the Four");

        Assert.Equal([(holmes.Scarlet, 1m), (holmes.Four, 2m)], Books((await SeriesAsync(id))!.Value)); // nothing saved
    }

    // ---- delete ---------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_series_removes_its_registry_and_keeps_every_book()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}")).StatusCode);

        Assert.False(Directory.Exists(Path.GetDirectoryName(RegistryPath(id))));
        Assert.Null(await SeriesAsync(id));
        Assert.Equal(JsonValueKind.Null, (await ProjectRowAsync(holmes.Scarlet)).GetProperty("series").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync($"{app.BaseUrl}/api/projects/{holmes.Four}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_member_project_removes_its_registry_entry_at_once()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/projects/{holmes.Scarlet}")).StatusCode);

        Assert.Equal(1, await StoredBookCountAsync(id)); // before any series read could heal it
        Assert.Equal([(holmes.Four, 2m)], Books((await SeriesAsync(id))!.Value));
    }

    [Fact]
    public async Task A_series_whose_book_folder_went_missing_reads_without_it_and_its_next_write_drops_the_entry()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Path.Combine(app.WorkspaceDir, holmes.Four), recursive: true);

        Assert.Equal([(holmes.Scarlet, 1m)], Books((await SeriesAsync(id))!.Value));
        Assert.Equal(2, await StoredBookCountAsync(id)); // reads change nothing (#246)

        Assert.Equal(HttpStatusCode.OK, (await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{id}", new { name = UniqueName() })).StatusCode);
        Assert.Equal(1, await StoredBookCountAsync(id)); // writes heal
    }

    [Fact]
    public async Task A_corrupt_registry_is_left_out_of_reads_and_blocks_writes_whose_rules_it_could_break()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        var corrupt = Path.Combine(app.WorkspaceDir, ".series", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(corrupt);
        await File.WriteAllTextAsync(Path.Combine(corrupt, SeriesDbContext.DbFileName), "not a database");
        try
        {
            Assert.NotNull(await SeriesAsync(id));
            await AssertProblemAsync(await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = UniqueName() }),
                HttpStatusCode.UnprocessableEntity);
            await AssertProblemAsync(await JoinAsync(id, holmes.Four), HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(corrupt, recursive: true);
        }
    }

    /// <summary>
    /// A member whose project.db is there but can't be read is neither healed away nor left out of
    /// a position save: the set can't name it, so the save is a 422, not a crash.
    /// </summary>
    [Fact]
    public async Task An_unreadable_member_blocks_a_position_save_and_is_not_dropped()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);
        var fourFolder = Path.Combine(app.WorkspaceDir, holmes.Four);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(fourFolder, "project.db"), "not a database");
        try
        {
            await AssertProblemAsync(await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 1m }),
                HttpStatusCode.UnprocessableEntity);
            Assert.Equal([(holmes.Scarlet, 1m)], Books((await SeriesAsync(id))!.Value));
            Assert.Equal(2, await StoredBookCountAsync(id));
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
        return await Http.PutAsync($"{app.BaseUrl}/api/series/{series}/poster", form);
    }

    private async Task PutCoverAsync(string folder, string content)
    {
        using var form = ImageForm(Encoding.UTF8.GetBytes(content), "cover.png");
        Assert.Equal(HttpStatusCode.OK, (await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/cover", form)).StatusCode);
    }

    private async Task<JsonElement> PosterAsync(Guid series) => (await SeriesAsync(series))!.Value.GetProperty("poster");

    /// <summary>The bytes the poster's URL serves now.</summary>
    private async Task<string> ServedAsync(JsonElement poster)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{poster.GetProperty("url").GetString()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task An_uploaded_poster_is_served_and_a_replacement_takes_its_place()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await PutCoverAsync(holmes.Scarlet, "scarlet cover");

        var uploaded = await PutPosterAsync(id, Encoding.UTF8.GetBytes("first poster"));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal("Uploaded", (await JsonAsync(uploaded)).GetProperty("poster").GetProperty("source").GetString());

        var poster = await PosterAsync(id);
        Assert.Equal("Uploaded", poster.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, poster.GetProperty("coverPosition").ValueKind);
        Assert.Equal("first poster", await ServedAsync(poster)); // ahead of the book's cover
        var served = await Http.GetAsync($"{app.BaseUrl}/api/series/{id}/poster");
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);

        await Task.Delay(20); // a later write time, so the URL's version moves on
        Assert.Equal(HttpStatusCode.OK, (await PutPosterAsync(id, Encoding.UTF8.GetBytes("second poster"), "Second.JPG")).StatusCode);

        var replaced = await PosterAsync(id);
        Assert.NotEqual(poster.GetProperty("url").GetString(), replaced.GetProperty("url").GetString());
        Assert.Equal("second poster", await ServedAsync(replaced));
        Assert.Equal("image/jpeg", (await Http.GetAsync($"{app.BaseUrl}/api/series/{id}/poster")).Content.Headers.ContentType?.MediaType);
        Assert.Equal(["poster.jpg"], // the earlier upload is gone
            Directory.GetFiles(Path.GetDirectoryName(RegistryPath(id))!, "poster*").Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_wrong_type_or_oversize_poster_is_400_and_an_unknown_series_404()
    {
        var id = await CreateAsync(UniqueName());

        await AssertProblemAsync(await PutPosterAsync(id, [1, 2, 3], "poster.gif"), HttpStatusCode.BadRequest, ".png");
        await AssertProblemAsync(await PutPosterAsync(id, new byte[10 * 1024 * 1024 + 1]), HttpStatusCode.BadRequest, "10 MB");
        Assert.Equal("None", (await PosterAsync(id)).GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await PutPosterAsync(Guid.NewGuid(), [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{Guid.NewGuid()}/poster")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{app.BaseUrl}/api/series/{Guid.NewGuid()}/poster")).StatusCode);
    }

    [Fact]
    public async Task Clearing_the_poster_is_204_also_with_none_and_the_cover_shows_again()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await PutCoverAsync(holmes.Scarlet, "scarlet cover");

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/poster")).StatusCode);

        await PutPosterAsync(id, Encoding.UTF8.GetBytes("a poster"));
        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}/poster")).StatusCode);

        var poster = await PosterAsync(id);
        Assert.Equal("Cover", poster.GetProperty("source").GetString());
        Assert.Equal("scarlet cover", await ServedAsync(poster));
    }

    [Fact]
    public async Task The_fallback_is_the_cover_of_the_lowest_positioned_book_and_follows_a_reorder()
    {
        var holmes = await SeedHolmesAsync();
        var id = await CreateAsync(UniqueName(), holmes.Scarlet);
        await JoinedAtAsync(id, holmes.Four);
        await PutCoverAsync(holmes.Scarlet, "scarlet cover");
        await PutCoverAsync(holmes.Four, "four cover");

        var poster = await PosterAsync(id);
        Assert.Equal("Cover", poster.GetProperty("source").GetString());
        Assert.Equal(1m, poster.GetProperty("coverPosition").GetDecimal());
        Assert.Equal("scarlet cover", await ServedAsync(poster));

        await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Scarlet] = 3m, [holmes.Four] = 2m });

        poster = await PosterAsync(id);
        Assert.Equal(2m, poster.GetProperty("coverPosition").GetDecimal());
        Assert.Equal("four cover", await ServedAsync(poster));

        // Only the lowest book's cover counts: without one, the card shows the placeholder.
        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/projects/{holmes.Four}/cover")).StatusCode);
        Assert.Equal("None", (await PosterAsync(id)).GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_empty_series_has_no_poster()
    {
        var id = await CreateAsync(UniqueName());

        var poster = await PosterAsync(id);
        Assert.Equal("None", poster.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, poster.GetProperty("url").ValueKind);
        Assert.Equal(JsonValueKind.Null, poster.GetProperty("coverPosition").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{app.BaseUrl}/api/series/{id}/poster")).StatusCode);
    }

    // ---- no History, no origin, the hub signal --------------------------------------------------

    [Fact]
    public async Task Registry_writes_make_no_History_step_ignore_the_origin_and_signal_the_hub()
    {
        var holmes = await SeedHolmesAsync();
        var stepsBefore = await StepCountAsync(holmes.Four);
        await using var hub = new HubConnectionBuilder().WithUrl($"{app.BaseUrl}/hubs/live").Build();
        var messages = new ConcurrentQueue<JsonElement>();
        hub.On<JsonElement>("series", messages.Enqueue);
        await hub.StartJoinedAsync();

        var id = await CreateAsync(UniqueName());
        using (var join = new HttpRequestMessage(HttpMethod.Put, $"{app.BaseUrl}/api/series/{id}/books/{holmes.Four}"))
        {
            join.Headers.Add("X-Origin-Id", Guid.NewGuid().ToString());
            join.Content = JsonContent.Create(new { move = false });
            Assert.Equal(HttpStatusCode.OK, (await Http.SendAsync(join)).StatusCode);
        }
        await SetPositionsAsync(id, new Dictionary<string, decimal> { [holmes.Four] = 4m });
        await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/series/{id}", new { name = UniqueName("Renamed") });

        Assert.Equal(stepsBefore, await StepCountAsync(holmes.Four));

        // The writes land inside one 250 ms window, so they arrive as few messages: a re-read
        // signal with no payload and no origin.
        await WaitForAsync(() => messages.Any(m => m.GetProperty("seriesId").GetGuid() == id));
        var changed = messages.First(m => m.GetProperty("seriesId").GetGuid() == id);
        Assert.Equal("Changed", changed.GetProperty("kind").GetString());
        Assert.False(changed.TryGetProperty("originId", out _));

        await Http.DeleteAsync($"{app.BaseUrl}/api/series/{id}");
        await WaitForAsync(() => messages.Any(m => m.GetProperty("seriesId").GetGuid() == id && m.GetProperty("kind").GetString() == "Deleted"));
    }

    private async Task<int> StepCountAsync(string folder) =>
        (await GetJsonAsync($"/api/projects/{folder}/history")).GetProperty("steps").GetArrayLength();

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
