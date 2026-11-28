using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.Data;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// THROWAWAY proof for #508, deleted in the PR's last commit. An attacker page on a second in-proc
/// server (another 127.0.0.1 port) frames /app and embeds a seeded cover and voice sample.
/// R2M_PROOF_BEFORE=1 (run with the middleware taken out of Startup) asserts all three load;
/// otherwise it asserts all three are blocked. Then the app itself shows the cover and plays the sample.
/// R2M_PROOF_STAGE labels the run in the log.
/// </summary>
public class SecurityHeadersProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly string _browser = pw.BrowserName;
    private const string Folder = "headers-proof";

    private static readonly bool Before = Environment.GetEnvironmentVariable("R2M_PROOF_BEFORE") == "1";
    private static readonly string Stage = Environment.GetEnvironmentVariable("R2M_PROOF_STAGE") ?? (Before ? "BEFORE" : "AFTER");

    private static void Log(string line) =>
        File.AppendAllText(Environment.GetEnvironmentVariable("R2M_PROOF_LOG") ?? Path.Combine(Path.GetTempPath(), "r2m-proof.log"), line + Environment.NewLine);

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private const string AttackerScript = """
        async ({ app, cover, sample }) => {
          const once = (el, ok, bad) => new Promise((resolve) => {
            el.addEventListener(ok, () => resolve(ok), { once: true });
            el.addEventListener(bad, () => resolve(bad), { once: true });
            setTimeout(() => resolve('timeout'), 5000);
          });
          const img = new Image();
          const imgDone = once(img, 'load', 'error');
          img.src = app + cover;
          const audio = new Audio();
          audio.preload = 'auto';
          const audioDone = once(audio, 'loadedmetadata', 'error');
          audio.src = app + sample;
          const frame = document.createElement('iframe');
          frame.name = 'victim';
          frame.width = 800; frame.height = 600;
          document.body.append(frame);
          const frameDone = new Promise((resolve) => frame.addEventListener('load', () => resolve('load'), { once: true }));
          frame.src = app + '/app/projects/headers-proof';
          return { img: await imgDone, audio: await audioDone, frame: await frameDone };
        }
        """;

    [Fact]
    public async Task An_attacker_page_on_another_port_can_neither_frame_the_app_nor_embed_its_files()
    {
        var narratorVoiceId = await SeedAsync();
        // Stages the bundle, so the framed /app is the real app.
        await GotoAppAsync($"projects/{Folder}");
        var coverPath = $"/workspace/{Folder}/cover.png";
        var samplePath = $"/workspace/{Folder}/{SamplePath(narratorVoiceId)}";

        await using var attacker = await StartAttackerAsync();
        await Page.GotoAsync(attacker.Url);
        var result = await Page.EvaluateAsync<JsonElement>(AttackerScript, new { app = App.BaseUrl, cover = coverPath, sample = samplePath });
        var framed = await FramedAppRendersAsync();

        var img = result.GetProperty("img").GetString();
        var audio = result.GetProperty("audio").GetString();
        Log($"[{_browser}] {Stage}: attacker {attacker.Url} -> app {App.BaseUrl}");
        Log($"  <iframe src=/app/projects/{Folder}>: {(framed ? "app rendered inside the frame" : "blocked (no app in the frame)")}");
        Log($"  <img src={coverPath}>: {img}");
        Log($"  <audio src={samplePath}>: {audio}");

        if (Environment.GetEnvironmentVariable("R2M_PROOF_OBSERVE") == "1")
        {
            // Only logged: main, where request isolation alone already refuses the embeds.
        }
        else if (Before)
        {
            Assert.True(framed);
            Assert.Equal("load", img);
            Assert.Equal("loadedmetadata", audio);
        }
        else
        {
            Assert.False(framed);
            Assert.Equal("error", img);
            Assert.Equal("error", audio);
        }

        // The app itself still shows the cover and plays the sample.
        await GotoAppAsync($"projects/{Folder}");
        var image = Page.Locator("img.details__image");
        await Expect(image).ToBeVisibleAsync();
        await Page.WaitForFunctionAsync("() => document.querySelector('img.details__image')?.naturalWidth > 0");

        // The help panel's guide link still opens in a new tab (GitHub answered locally, so offline works).
        await Page.Context.RouteAsync("https://github.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<title>guide</title>" }));
        await Page.Locator(".r2m-pipeline__help-toggle").First.ClickAsync();
        var popup = Page.Context.WaitForPageAsync();
        await Page.Locator("a.overview__help-guide:visible").First.ClickAsync();
        var tab = await popup;
        await tab.WaitForLoadStateAsync();
        var hasOpener = await tab.EvaluateAsync<bool>("() => window.opener !== null");
        Log($"[{_browser}] {Stage}: the help link opened a new tab at {tab.Url} (window.opener {(hasOpener ? "set" : "null")})");
        Assert.StartsWith("https://github.com/", tab.Url);
        await tab.CloseAsync();

        await GotoAppAsync($"projects/{Folder}/cast/{ProjectDbContext.NarratorId}");
        await Page.Locator("r2m-voice-card .voice-card__toggle").First.ClickAsync();
        var player = Page.Locator("r2m-voice-card [data-mode='reference'] r2m-audio-player:visible").First;
        await player.ScrollIntoViewIfNeededAsync();
        await player.Locator(".r2m-audio-player__toggle").ClickAsync();
        await player.Locator("audio").EvaluateAsync(
            "a => new Promise((resolve, reject) => { const t = setInterval(() => { if (a.error) reject(a.error.message); if (a.currentTime > 0) { clearInterval(t); resolve(); } }, 50); })");
        Log($"[{_browser}] {Stage}: the app at /app shows the cover and plays the voice sample");
    }

    private async Task<Guid> SeedAsync()
    {
        await App.SeedProjectAsync(Folder, "Moby Dick", "Herman Melville");
        var voiceId = await App.SeedEditableVoiceAsync(Folder, ProjectDbContext.NarratorId, "Narrator Voice");
        using var http = new HttpClient();
        using var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(Png);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(png, "file", "cover.png");
        (await http.PutAsync($"{App.BaseUrl}/api/projects/{Folder}/cover", form)).EnsureSuccessStatusCode();
        return voiceId;
    }

    private string SamplePath(Guid voiceId)
    {
        var voices = Directory.GetFiles(Path.Combine(App.WorkspaceDir, Folder, "voices"), $"{voiceId}-*.wav", SearchOption.AllDirectories);
        var full = Assert.Single(voices);
        return Path.GetRelativePath(Path.Combine(App.WorkspaceDir, Folder), full).Replace('\\', '/');
    }

    private async Task<bool> FramedAppRendersAsync()
    {
        var frame = Page.Frames.FirstOrDefault(f => f.Name == "victim");
        if (frame is null) return false;
        try
        {
            await frame.WaitForSelectorAsync("app-root", new() { Timeout = 5_000, State = WaitForSelectorState.Attached });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Attacker(WebApplication web, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;
        public ValueTask DisposeAsync() => web.DisposeAsync();
    }

    private static async Task<Attacker> StartAttackerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var web = builder.Build();
        web.MapGet("/", () => Results.Content("<!doctype html><title>Another page</title><p>Nothing to see.</p>", "text/html"));
        await web.StartAsync();
        var url = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Attacker(web, url + "/");
    }
}
