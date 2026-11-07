using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.Data;
using Read2Me.Data.Series;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The one seed helper of the Series API tests: Holmes books (the sample series, #209) seeded into
/// the host's workspace, a series made of them over HTTP, and the reads the classes share. Each test
/// seeds its own books; folders and series names are unique per call, since a class shares one host.
/// </summary>
internal sealed class SeriesSeed(E2eAppFixture app)
{
    public static readonly HttpClient Http = new();

    private static readonly (string Slug, string Title)[] Sample =
    [
        ("scarlet", "A Study in Scarlet"),
        ("four", "The Sign of the Four"),
        ("adventures", "The Adventures of Sherlock Holmes"),
    ];

    /// <summary>A seeded book: its Watson, and its Chapter 2 when seeded with chapters.</summary>
    public sealed record Book(string Folder, string Title, Guid Watson, Guid Chapter2)
    {
        public Guid Narrator => ProjectDbContext.NarratorId;
    }

    /// <summary>The books in publication order; <see cref="SeriesWatson"/> is empty unless Watson was shared.</summary>
    public sealed record Holmes(Guid Series, Guid SeriesWatson, IReadOnlyList<Book> Books)
    {
        public Book Scarlet => Books[0];
        public Book Four => Books[1];
        public Book Adventures => Books[2];
    }

    public string Url(string path) => $"{app.BaseUrl}{path}";

    public static string UniqueName(string name = "Sherlock Holmes") => $"{name} {Guid.NewGuid():N}"[..(name.Length + 9)];

    /// <summary>
    /// The first <paramref name="count"/> Holmes books, each with one Character, Watson. With
    /// <paramref name="chapters"/>, three chapters each, so a positional rule can start at Chapter 2.
    /// </summary>
    public async Task<Book[]> BooksAsync(int count = 3, bool chapters = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var books = new Book[count];
        for (var i = 0; i < count; i++)
        {
            var (slug, title) = Sample[i];
            var folder = $"{slug}-{tag}";
            var built = chapters
                ? await app.SeedMultiChapterProjectAsync(folder, title, "Arthur Conan Doyle", chapters: 3, characterName: "Watson")
                : await app.SeedProjectAsync(folder, title, "Arthur Conan Doyle", "Watson");
            books[i] = new Book(folder, title, built.CharacterId("Watson"), chapters ? built.ChapterId("Chapter 2") : Guid.Empty);
        }
        return books;
    }

    /// <summary>
    /// <paramref name="books"/> Holmes books, the first <paramref name="members"/> of them in a fresh
    /// series in that order; with <paramref name="linkWatson"/>, Watson shared from the first and
    /// linked in the other members.
    /// </summary>
    public async Task<Holmes> SeriesAsync(
        int books = 3, int? members = null, bool chapters = false, bool linkWatson = false)
    {
        var seeded = await BooksAsync(books, chapters);
        var joined = seeded.Take(members ?? books).ToList();
        var series = await CreateSeriesAsync(UniqueName(), joined[0].Folder);
        foreach (var book in joined.Skip(1))
            await JoinAsync(series, book.Folder);
        if (!linkWatson) return new Holmes(series, Guid.Empty, seeded);

        var watson = await ShareAsync(series, joined[0]);
        foreach (var book in joined.Skip(1))
            await LinkAsync(series, watson, book);
        return new Holmes(series, watson, seeded);
    }

    // ---- series writes that must succeed ---------------------------------------------------------

    public async Task<Guid> CreateSeriesAsync(string name, string? folder = null)
    {
        var response = await Http.PostAsJsonAsync(Url("/api/series"), new { name, folder });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    public async Task<decimal> JoinAsync(Guid series, string folder)
    {
        var response = await Http.PutAsJsonAsync(Url($"/api/series/{series}/books/{folder}"), new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("position").GetDecimal();
    }

    /// <summary>Shares the book's Watson as a new Series character, whose Id it returns.</summary>
    public async Task<Guid> ShareAsync(Guid series, Book book, Guid? characterId = null)
    {
        var response = await Http.PostAsJsonAsync(Url($"/api/series/{series}/characters"),
            new { folder = book.Folder, characterId = characterId ?? book.Watson });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("seriesCharacterId").GetGuid();
    }

    public Task<HttpResponseMessage> TryLinkAsync(Guid series, Guid seriesCharacter, string folder, Guid characterId) =>
        Http.PutAsJsonAsync(Url($"/api/series/{series}/characters/{seriesCharacter}/links/{folder}"), new { characterId });

    public async Task LinkAsync(Guid series, Guid seriesCharacter, Book book) =>
        Assert.Equal(HttpStatusCode.NoContent, (await TryLinkAsync(series, seriesCharacter, book.Folder, book.Watson)).StatusCode);

    // ---- book commands and reads -----------------------------------------------------------------

    public Task<HttpResponseMessage> CommandAsync(string folder, object command) =>
        Http.PostAsJsonAsync(Url($"/api/projects/{folder}/commands"), command);

    /// <summary>Runs a command that must succeed; the entity it made, or empty.</summary>
    public async Task<Guid> RunAsync(string folder, object command)
    {
        var response = await CommandAsync(folder, command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).TryGetProperty("newEntityId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetGuid()
            : Guid.Empty;
    }

    public Task<Guid> CreateVoiceAsync(Book book, string name, bool generated = false) =>
        RunAsync(book.Folder, new { type = "CreateVoice", characterId = book.Watson, name, isGenerated = generated });

    /// <summary>Watson's Voice in this book, or null when the book doesn't hold it.</summary>
    public async Task<JsonElement?> VoiceAsync(Book book, Guid voiceId) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/characters/{book.Watson}/voices")).GetProperty("voices").EnumerateArray()
            .Select(v => (JsonElement?)v).FirstOrDefault(v => v!.Value.GetProperty("id").GetGuid() == voiceId);

    public async Task<string?> VoiceNameAsync(Book book, Guid voiceId) =>
        (await VoiceAsync(book, voiceId))?.GetProperty("name").GetString();

    /// <summary>Watson's row of the book's <c>characters/summary</c>.</summary>
    public async Task<JsonElement> SummaryRowAsync(Book book) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/characters/summary")).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == book.Watson);

    public async Task<IReadOnlyList<string>> HistoryLabelsAsync(Book book) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/history")).GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("label").GetString()!).ToList();

    public async Task<Guid> HeadAsync(Book book) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/history")).GetProperty("headStepId").GetGuid();

    public async Task RestoreAsync(Book book, Guid step) =>
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync(Url($"/api/projects/{book.Folder}/history/{step}/restore"), null)).StatusCode);

    public Task<JsonElement> CastAsync(Guid series) => GetJsonAsync($"/api/series/{series}/cast");

    // ---- the registry on disk ----------------------------------------------------------------------

    public string RegistryFolder(Guid series) => Path.Combine(app.WorkspaceDir, ".series", series.ToString("D"));

    public Task<SeriesDbContext> OpenRegistryAsync(Guid series) => SeriesDbContext.OpenAsync(RegistryFolder(series));

    // ---- JSON ------------------------------------------------------------------------------------

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync(Url(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>A problem+json refusal with <paramref name="status"/> whose detail names each of <paramref name="named"/>; its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, params string[] named)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        var detail = body.GetProperty("detail").GetString();
        foreach (var name in named)
            Assert.Contains(name, detail);
        return body;
    }
}
