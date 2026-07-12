using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The endpoint the consonant-soften A/B preview's "Filtered" player points at. It serves whatever
/// the circuit last rendered under its token, and nothing before that.
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioPreviewEndpointTests(E2eAppFixture app)
{
    private AudioPreviewStore Store => app.Services.GetRequiredService<AudioPreviewStore>();

    [Fact]
    public async Task Unrendered_token_is_404()
    {
        using var http = new HttpClient();

        var response = await http.GetAsync($"{app.BaseUrl}/audio-preview/{Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rendered_token_serves_the_preview_wav()
    {
        var token = Guid.NewGuid().ToString("N");
        byte[] wav = [1, 2, 3, 4];
        await Store.SaveAsync(token, wav);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/audio-preview/{token}");

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}");
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        // Without a length the response chunks, and the <audio> element reports an infinite
        // duration — no total time and no scrub bar on the Filtered player.
        Assert.Equal(wav.Length, response.Content.Headers.ContentLength);
        Assert.Equal(wav, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Re_rendered_token_serves_the_newest_wav()
    {
        var token = Guid.NewGuid().ToString("N");
        await Store.SaveAsync(token, [1, 1]);
        await Store.SaveAsync(token, [2, 2]);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{app.BaseUrl}/audio-preview/{token}");

        Assert.Equal<byte[]>([2, 2], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Non_guid_token_is_404()
    {
        using var http = new HttpClient();

        var response = await http.GetAsync($"{app.BaseUrl}/audio-preview/..%2F..%2Fsecrets");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
