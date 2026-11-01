using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App.Configuration;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The host starts without a usable Workspace: it serves, the Setup check reports the Workspace
/// failure naming the source that set it, every other API call answers one "no Workspace" error, and
/// no hosted service that touches app.db runs. Each case starts its own host on its own content
/// root, with appsettings.Local.json and the command line as the app reads them.
/// </summary>
public class NoWorkspaceApiTests : IDisposable
{
    private static readonly HttpClient Http = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "r2me-e2e-no-ws", Guid.NewGuid().ToString("N"));
    private string ContentRoot => Path.Combine(_root, "app");
    private string LocalSettingsPath => Path.Combine(ContentRoot, "appsettings.Local.json");

    public NoWorkspaceApiTests() => Directory.CreateDirectory(ContentRoot);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Unset_names_the_local_settings_file_to_copy()
    {
        await using var app = await StartAsync();

        var workspace = await WorkspaceRowAsync(app, "Workspace:FolderPath isn't set");

        var fix = workspace.GetProperty("fix");
        Assert.Contains(LocalSettingsPath, fix.GetProperty("text").GetString());
        Assert.Contains("copy it from appsettings.Local.example.json", fix.GetProperty("text").GetString());
        Assert.Equal(LocalSettingsPath, fix.GetProperty("copy").GetString());
        await AssertNoWorkspaceModeAsync(app);
    }

    [Fact]
    public async Task Empty_in_the_local_settings_file_names_that_file()
    {
        await WriteLocalSettingsAsync("");
        await using var app = await StartAsync();

        var workspace = await WorkspaceRowAsync(app, "Workspace:FolderPath isn't set");

        Assert.Equal(
            $"Set Workspace:FolderPath to an absolute, writable folder in {LocalSettingsPath}, then restart the app.",
            workspace.GetProperty("fix").GetProperty("text").GetString());
        await AssertNoWorkspaceModeAsync(app);
    }

    [Fact]
    public async Task Relative_on_the_command_line_names_the_command_line()
    {
        await WriteLocalSettingsAsync(Path.Combine(_root, "ws"));
        await using var app = await StartAsync("--Workspace:FolderPath=ws");

        var workspace = await WorkspaceRowAsync(app, "ws is a relative path");

        Assert.Contains("comes from the command line", workspace.GetProperty("fix").GetProperty("text").GetString());
        await AssertNoWorkspaceModeAsync(app);
    }

    [Fact]
    public async Task A_file_names_the_local_settings_file()
    {
        var file = Path.Combine(_root, "ws.txt");
        await File.WriteAllTextAsync(file, "not a folder");
        await WriteLocalSettingsAsync(file);
        await using var app = await StartAsync();

        var workspace = await WorkspaceRowAsync(app, $"{file} is a file");

        Assert.Contains($"in {LocalSettingsPath}", workspace.GetProperty("fix").GetProperty("text").GetString());
        await AssertNoWorkspaceModeAsync(app);
    }

    [Fact]
    public async Task Unwritable_fails_naming_the_folder()
    {
        using var folder = new UnwritableFolder(Path.Combine(_root, "locked"));
        await using var app = await StartAsync($"--Workspace:FolderPath={folder.Path}");

        var workspace = await WorkspaceRowAsync(app, null);

        Assert.StartsWith($"{folder.Path} isn't writable:", workspace.GetProperty("found").GetString());
        Assert.Contains("comes from the command line", workspace.GetProperty("fix").GetProperty("text").GetString());
        await AssertNoWorkspaceModeAsync(app);
    }

    private async Task<E2eAppFixture> StartAsync(params string[] args)
    {
        var app = new E2eAppFixture { ContentRootDir = ContentRoot, Args = args };
        await app.InitializeAsync();
        return app;
    }

    private Task WriteLocalSettingsAsync(string folderPath) =>
        File.WriteAllTextAsync(LocalSettingsPath,
            JsonSerializer.Serialize(new { Workspace = new { FolderPath = folderPath } }));

    /// <summary>
    /// Waits for the start-up Quick check, then reads the Workspace row: Failed, with
    /// <paramref name="found"/> when given; every other row is Not run, needing the Workspace.
    /// </summary>
    private static async Task<JsonElement> WorkspaceRowAsync(E2eAppFixture app, string? found)
    {
        var report = await ReportAfterStartAsync(app);
        Assert.True(report.GetProperty("noWorkspace").GetBoolean());
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        var workspace = rows[0];
        Assert.Equal("workspace", workspace.GetProperty("id").GetString());
        Assert.Equal("Failed", workspace.GetProperty("result").GetString());
        if (found is not null)
            Assert.Equal(found, workspace.GetProperty("found").GetString());
        Assert.Contains("restart the app", workspace.GetProperty("fix").GetProperty("text").GetString());
        Assert.All(rows.Skip(1), r =>
        {
            Assert.Equal("NotRun", r.GetProperty("result").GetString());
            Assert.Equal("needs the Workspace", r.GetProperty("found").GetString());
        });
        return workspace;
    }

    /// <summary>The host serves the web app, refuses other API calls, and runs nothing that opens app.db.</summary>
    private async Task AssertNoWorkspaceModeAsync(E2eAppFixture app)
    {
        foreach (var path in new[] { "/api/projects", "/api/settings/theme", "/workspace/app.db" })
        {
            var refused = await Http.GetAsync($"{app.BaseUrl}{path}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("No Workspace", problem.RootElement.GetProperty("title").GetString());
        }

        var page = await Http.GetAsync($"{app.BaseUrl}/app/projects");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var gated = app.Services.GetServices<IHostedService>().OfType<WorkspaceHostedService>().ToList();
        Assert.Equal(3, gated.Count); // both queue workers and the History background-run tracker
        Assert.All(gated, s => Assert.Null(s.Running));
        Assert.Empty(Directory.GetFiles(_root, "app.db", SearchOption.AllDirectories));
    }

    private static async Task<JsonElement> ReportAfterStartAsync(E2eAppFixture app)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var response = await Http.GetAsync($"{app.BaseUrl}/api/setup-check");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("quickCheckedAt").ValueKind != JsonValueKind.Null)
                return doc.RootElement.Clone();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The start-up Quick check had not finished after 30 s.");
            await Task.Delay(100);
        }
    }
}
