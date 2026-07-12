using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Models;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The endpoint the consonant-soften A/B preview's "Original" player points at. It serves an item's
/// Preview Source — the audio before any Audio Post-Process Step — which lives in a dot-prefixed dir
/// the static-file provider will not serve, and so needs a route of its own.
/// </summary>
[Collection(E2eCollection.Name)]
public class PreviewSourceEndpointTests(E2eAppFixture app)
{
    private static readonly ProjectFolderId Folder = new("preview-source-book");

    private async Task SaveAsync(Guid itemId, byte[] wav)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPreviewSourceCache>()
            .SaveAsync(Folder, itemId, wav);
    }

    [Fact]
    public async Task Cached_preview_source_is_served_as_a_wav()
    {
        var itemId = Guid.NewGuid();
        byte[] wav = [1, 2, 3, 4];
        await SaveAsync(itemId, wav);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/{Folder.Value}/{itemId:D}");

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        // Without a length the response chunks, and the <audio> element reports an infinite
        // duration — no total time and no scrub bar on the Original player.
        Assert.Equal(wav.Length, response.Content.Headers.ContentLength);
        Assert.Equal(wav, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Regenerated_item_serves_the_newest_preview_source()
    {
        var itemId = Guid.NewGuid();
        await SaveAsync(itemId, [1, 1]);
        await SaveAsync(itemId, [2, 2]);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/{Folder.Value}/{itemId:D}");

        Assert.Equal<byte[]>([2, 2], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Uncached_item_is_404()
    {
        using var http = new HttpClient();

        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/{Folder.Value}/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_folder_is_404()
    {
        var itemId = Guid.NewGuid();
        await SaveAsync(itemId, [1, 2, 3, 4]);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/no-such-book/{itemId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Non_guid_item_is_404()
    {
        using var http = new HttpClient();

        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/{Folder.Value}/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Folder_that_is_not_a_bare_path_segment_is_404()
    {
        // The folder name reaches the app from a URL, so it must never be combined into a path.
        // An *un*-encoded "../.." never gets here — the client normalises it away and it lands on the
        // Blazor fallback page — so the encoded form is the one that actually reaches the route.
        var itemId = Guid.NewGuid();
        await SaveAsync(itemId, [1, 2, 3, 4]);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/preview-source/..%2F..%2Fsecrets/{itemId:D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
