using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App;
using Read2Me.AppData;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Boots the real app (Startup, Kestrel, random port) against a throwaway temp
/// workspace, with all external AI HTTP traffic routed into <see cref="FakeAi"/>.
/// Shared per test collection.
/// </summary>
public sealed class E2eAppFixture : IAsyncLifetime
{
    public FakeAiRoutingHandler FakeAi { get; } = new();
    public FakeAiServiceControl FakeControl { get; } = new();
    public string WorkspaceDir { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public IServiceProvider Services => _host!.Services;

    private IHost? _host;

    public async ValueTask InitializeAsync()
    {
        WorkspaceDir = Path.Combine(Path.GetTempPath(), "r2me-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceDir);

        // Mirrors Program.CreateHostBuilder minus Serilog, plus test overrides.
        // IHostBuilder.ConfigureServices delegates run after Startup.ConfigureServices,
        // so these registrations win.
        _host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development)
            .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Workspace:FolderPath"] = WorkspaceDir }))
            .ConfigureWebHostDefaults(web => web
                .UseStartup<Startup>()
                .UseUrls("http://127.0.0.1:0"))
            .ConfigureServices(s =>
            {
                s.AddSingleton(FakeAi);
                s.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(FakeAi));
                s.AddSingleton<Read2Me.Services.Health.IAiServiceControl>(FakeControl);
                s.AddSingleton<IAudioNormalizer, PassThroughAudioNormalizer>();
                s.AddSingleton<IFfmpegProber, FakeFfmpegProber>();
            })
            .Build();

        // Same migration step Program.Main performs before Run().
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Read2MeDbContext>();
            await db.Database.MigrateAsync();
        }

        await _host.StartAsync();

        BaseUrl = _host.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        await WorkspaceSeeder.SeedServiceConfigsAsync(_host.Services);
    }

    public Task<TestUtils.BookHierarchyBuilder> SeedProjectAsync(
        string folderName, string title, string author, string characterName = "Alice") =>
        WorkspaceSeeder.SeedProjectAsync(Services, WorkspaceDir, folderName, title, author, characterName);

    public Task<TestUtils.BookHierarchyBuilder> SeedThreeDialogParagraphProjectAsync(
        string folderName, string title, string author, string characterName = "Alice") =>
        WorkspaceSeeder.SeedThreeDialogParagraphProjectAsync(
            Services, WorkspaceDir, folderName, title, author, characterName);

    public Task<TestUtils.BookHierarchyBuilder> SeedMisSplitParagraphProjectAsync(
        string folderName, string title, string author, string characterName = "Alice") =>
        WorkspaceSeeder.SeedMisSplitParagraphProjectAsync(
            Services, WorkspaceDir, folderName, title, author, characterName);

    public Task SeedItemAudioAsync(string folderName, Guid itemId, Guid characterId) =>
        WorkspaceSeeder.SeedItemAudioAsync(Services, WorkspaceDir, folderName, itemId, characterId);

    public Task<Guid> SeedEditableVoiceAsync(string folderName, Guid characterId, string voiceName = "Alice Voice") =>
        WorkspaceSeeder.SeedEditableVoiceAsync(Services, WorkspaceDir, folderName, characterId, voiceName);

    public Task SeedNarratorVoiceAsync(string folderName) =>
        WorkspaceSeeder.SeedNarratorVoiceAsync(Services, WorkspaceDir, folderName);

    /// <summary>
    /// Polls a queue-status endpoint (e.g. <c>/api/attribution/queue</c>) until nothing is queued or
    /// processing. The app is shared across the collection, so a test that enqueues work must leave
    /// with it drained — an in-flight paragraph would otherwise run against the next test's fake AI.
    /// </summary>
    public async Task WaitForQueueDrainAsync(string queuePath, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = JsonDocument.Parse(await _http.GetStringAsync($"{BaseUrl}{queuePath}"));
            if (snapshot.RootElement.GetProperty("queuedCount").GetInt32() == 0 &&
                snapshot.RootElement.GetProperty("processingCount").GetInt32() == 0)
                return;
            await Task.Delay(200);
        }
    }

    private static readonly HttpClient _http = new();

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }

        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(WorkspaceDir))
                    Directory.Delete(WorkspaceDir, recursive: true);
                break;
            }
            catch (IOException) { await Task.Delay(200); }
            catch (UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }
}
