using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// THROWAWAY proof for #507, deleted in the PR's last commit. An attacker page on a second in-proc
/// server (another 127.0.0.1 port: the same site) opens the live hub, joins stream:llm and fires
/// bodyless POSTs. R2M_PROOF_BEFORE=1 (run with the middleware taken out of Startup) asserts the
/// attack works; otherwise it asserts the host refuses it. Then the app itself still works through
/// the host, and, with R2M_PROOF_DEV=1 (a host on :5000 and bun run dev on :4300), through the proxy.
/// </summary>
public class RequestIsolationProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly string _browser = pw.BrowserName;

    private static readonly bool Before = Environment.GetEnvironmentVariable("R2M_PROOF_BEFORE") == "1";

    private static void Log(string line) =>
        File.AppendAllText(Environment.GetEnvironmentVariable("R2M_PROOF_LOG") ?? Path.Combine(Path.GetTempPath(), "r2m-proof.log"), line + Environment.NewLine);

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private const string AttackerScript = """
        async (app) => {
          const ws = new WebSocket(app.replace(/^http/, 'ws') + '/hubs/live');
          const hub = await new Promise((resolve) => {
            ws.onopen = () => ws.send(JSON.stringify({ protocol: 'json', version: 1 }) + '\x1e');
            ws.onmessage = (e) => {
              for (const frame of String(e.data).split('\x1e').filter(Boolean)) {
                const m = JSON.parse(frame);
                if (m.type === undefined)
                  ws.send(JSON.stringify({ type: 1, invocationId: '1', target: 'JoinStream', arguments: ['llm'] }) + '\x1e');
                else if (m.type === 3 && m.invocationId === '1')
                  resolve(m.error ? 'JoinStream error: ' + m.error : 'joined stream:llm');
              }
            };
            ws.onclose = (e) => resolve('socket closed before joining (code ' + e.code + ')');
          });
          ws.close();
          const post = async (path) => {
            try { await fetch(app + path, { method: 'POST', mode: 'no-cors' }); return 'sent'; }
            catch (e) { return 'network error: ' + e; }
          };
          return {
            hub,
            negotiate: await post('/hubs/live/negotiate?negotiateVersion=1'),
            cancel: await post('/api/attribution/cancel'),
          };
        }
        """;

    [Fact]
    public async Task An_attacker_page_on_another_localhost_port_cannot_reach_the_hub_or_the_API()
    {
        await using var attacker = await StartAttackerAsync();
        var seen = new Dictionary<string, (int Status, string? Site)>();
        Page.Response += async (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (!path.StartsWith("/api") && !path.StartsWith("/hubs")) return;
            var headers = await response.Request.AllHeadersAsync();
            lock (seen) seen[path] = (response.Status, headers.GetValueOrDefault("sec-fetch-site"));
        };
        string? socketSite = null;
        Page.WebSocket += (_, socket) => socketSite ??= socket.Url;

        await Page.GotoAsync(attacker.Url);
        var result = await Page.EvaluateAsync<JsonElement>(AttackerScript, App.BaseUrl);
        await Page.WaitForTimeoutAsync(500); // let the response events land

        Log($"[{_browser}] {(Before ? "BEFORE" : "AFTER")} the fix: attacker {attacker.Url} -> app {App.BaseUrl}");
        Log($"  hub socket ({socketSite}): {result.GetProperty("hub").GetString()}");
        foreach (var (path, (status, site)) in seen.OrderBy(p => p.Key))
            Log($"  POST {path}: {status} (Sec-Fetch-Site: {site})");

        var hub = result.GetProperty("hub").GetString();
        if (Before)
        {
            Assert.Equal("joined stream:llm", hub);
            Assert.Equal(200, seen["/hubs/live/negotiate"].Status);
            Assert.Equal(204, seen["/api/attribution/cancel"].Status);
        }
        else
        {
            Assert.StartsWith("socket closed before joining", hub);
            Assert.Equal(403, seen["/hubs/live/negotiate"].Status);
            Assert.Equal(403, seen["/api/attribution/cancel"].Status);
        }
    }

    [Fact]
    public async Task The_app_served_by_the_host_connects_the_hub_and_uploads_a_cover()
    {
        Assert.SkipWhen(Before, "the app half of the proof runs after the fix");
        await App.SeedProjectAsync("isolation-proof", "Isolation Proof", "A. Author");

        await GotoAppAsync("projects/isolation-proof"); // waits for data-live-state=connected
        await UploadCoverAsync(Page);

        Log($"[{_browser}] host {App.BaseUrl}/app: hub connected, cover uploaded and shown");
    }

    [Fact]
    public async Task The_app_through_the_dev_proxy_connects_the_hub_and_uploads_a_cover()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("R2M_PROOF_DEV") == "1",
            "needs a host on :5000 and bun run dev on :4300 (R2M_PROOF_DEV=1)");
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
        var title = $"isolation-proof-{DateTime.UtcNow:HHmmss}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent(title), "bookTitle" },
            { new StringContent("Herman Melville"), "author" },
        };
        var book = new ByteArrayContent("I\n\nCall me Ishmael.\n"u8.ToArray());
        book.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(book, "file", "book.txt");
        var created = await http.PostAsync("/api/projects", form);
        created.EnsureSuccessStatusCode();
        var folder = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("folderName").GetString()!;
        try
        {
            (await http.PostAsync($"/api/projects/{folder}/import", JsonContent.Create(new { reread = false }))).EnsureSuccessStatusCode();

            var sockets = new List<string>();
            Page.WebSocket += (_, socket) => sockets.Add(socket.Url);
            await Page.GotoAsync($"http://localhost:4300/app/projects/{folder}");
            await Expect(Page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });
            await UploadCoverAsync(Page);

            Log($"[{_browser}] dev proxy http://localhost:4300/app: hub connected over {string.Join(", ", sockets)}, cover uploaded and shown");
        }
        finally
        {
            await http.DeleteAsync($"/api/projects/{folder}");
        }
    }

    private static async Task UploadCoverAsync(IPage page)
    {
        var upload = page.WaitForResponseAsync(r => r.Url.EndsWith("/cover") && r.Request.Method == "PUT");
        await page.Locator(".details__drop .r2m-file-drop__input").SetInputFilesAsync(new FilePayload
        {
            Name = "cover.png", MimeType = "image/png", Buffer = Png,
        });
        Assert.Equal(200, (await upload).Status);
        await Assertions.Expect(page.Locator("img.details__image")).ToBeVisibleAsync();
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
