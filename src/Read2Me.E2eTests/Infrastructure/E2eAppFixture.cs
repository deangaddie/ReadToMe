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
/// One host per test class (<c>IClassFixture</c>): its workspace, app.db, web root, fakes and
/// queues belong to that class alone, so classes run in parallel while a class's tests run in order.
/// </summary>
public sealed class E2eAppFixture : IAsyncLifetime
{
    public FakeAiRoutingHandler FakeAi { get; } = new();
    public FakeAiServiceControl FakeControl { get; } = new();
    public FakeAudiobookEncoder Encoder { get; } = new();
    public string WorkspaceDir { get; private set; } = "";
    public string WebRootDir { get; private set; } = "";
    public string BaseUrl { get; private set; } = "";
    public IServiceProvider Services => _host!.Services;

    private IHost? _host;
    private string? _appDbConnectionString;

    /// <summary>The preview store's clock; advance it to expire voice-editor previews.</summary>
    public TestUtils.ManualTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public async ValueTask InitializeAsync()
    {
        WorkspaceDir = Path.Combine(Path.GetTempPath(), "r2me-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(WorkspaceDir);
        WebRootDir = Path.Combine(WorkspaceDir, "wwwroot");
        Directory.CreateDirectory(WebRootDir);

        // Mirrors Program.CreateHostBuilder minus Serilog, plus test overrides.
        // IHostBuilder.ConfigureServices delegates run after Startup.ConfigureServices,
        // so these registrations win.
        _host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development)
            .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Workspace:FolderPath"] = WorkspaceDir }))
            .ConfigureWebHostDefaults(web => web
                .UseStartup<Startup>()
                // Isolated physical web root so tests can stage (or withhold) the web bundle
                // without depending on whether a developer has run bun run build.
                .UseWebRoot(WebRootDir)
                .UseUrls("http://127.0.0.1:0"))
            .ConfigureServices(s =>
            {
                s.AddSingleton(FakeAi);
                s.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(FakeAi));
                s.AddSingleton<Read2Me.Services.Health.IAiServiceControl>(FakeControl);
                s.AddSingleton<IAudioNormalizer, PassThroughAudioNormalizer>();
                s.AddSingleton<IFfmpegProber, FakeFfmpegProber>();
                s.AddSingleton<Read2Me.Services.Audio.Assembly.IAudiobookEncoder>(Encoder);
                // Voice-editor previews expire on this clock, so a test can age them without waiting.
                s.AddSingleton<IPreviewStore>(new PreviewStore(Clock));
            })
            .Build();

        // Same migration step Program.Main performs before Run().
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Read2MeDbContext>();
            await db.Database.MigrateAsync();
            _appDbConnectionString = db.Database.GetConnectionString();
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

    public Task<TestUtils.BookHierarchyBuilder> SeedTranscriptProjectAsync(
        string folderName, string title, string author, string characterName = "Alice") =>
        WorkspaceSeeder.SeedTranscriptProjectAsync(
            Services, WorkspaceDir, folderName, title, author, characterName);

    public Task<TestUtils.BookHierarchyBuilder> SeedMultiChapterProjectAsync(
        string folderName, string title, string author, int chapters = 5, string characterName = "Alice") =>
        WorkspaceSeeder.SeedMultiChapterProjectAsync(
            Services, WorkspaceDir, folderName, title, author, chapters, characterName);

    public Task<TestUtils.BookHierarchyBuilder> SeedNarrationBookAsync(
        string folderName, string partTitle, IReadOnlyList<string> narration) =>
        WorkspaceSeeder.SeedNarrationBookAsync(Services, WorkspaceDir, folderName, partTitle, narration);

    public Task<TestUtils.BookHierarchyBuilder> SeedLongBookAsync(
        string folderName, string title, string author, int chapters = 12, int paragraphs = 30) =>
        WorkspaceSeeder.SeedLongBookAsync(Services, WorkspaceDir, folderName, title, author, chapters, paragraphs);

    public Task<TestUtils.BookHierarchyBuilder> SeedNestedBookAsync(string folderName, string title, string author) =>
        WorkspaceSeeder.SeedNestedBookAsync(Services, WorkspaceDir, folderName, title, author);

    public Task SeedItemAudioAsync(string folderName, Guid itemId, Guid characterId) =>
        WorkspaceSeeder.SeedItemAudioAsync(Services, WorkspaceDir, folderName, itemId, characterId);

    public Task SeedDeliveryCueAsync(string folderName, Guid itemId, string cue) =>
        WorkspaceSeeder.SeedDeliveryCueAsync(Services, WorkspaceDir, folderName, itemId, cue);

    public Task<Guid> SeedEditableVoiceAsync(string folderName, Guid characterId, string voiceName = "Alice Voice") =>
        WorkspaceSeeder.SeedEditableVoiceAsync(Services, WorkspaceDir, folderName, characterId, voiceName);

    public Task SeedNarratorVoiceAsync(string folderName, string? transcript = null) =>
        WorkspaceSeeder.SeedNarratorVoiceAsync(Services, WorkspaceDir, folderName, transcript);

    /// <summary>
    /// Polls a queue-status endpoint (e.g. <c>/api/attribution/queue</c>) until nothing is queued or
    /// processing. The host is shared by the tests of one class, so a test that enqueues work must
    /// leave with it drained — an in-flight paragraph would otherwise run against the class's next
    /// test's fake AI. Throws when the queue has not drained by the timeout, naming what is still pending.
    /// </summary>
    public async Task WaitForQueueDrainAsync(string queuePath, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            using var snapshot = JsonDocument.Parse(await _http.GetStringAsync($"{BaseUrl}{queuePath}"));
            var queued = snapshot.RootElement.GetProperty("queuedCount").GetInt32();
            var processing = snapshot.RootElement.GetProperty("processingCount").GetInt32();
            if (queued == 0 && processing == 0)
                return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"{queuePath} did not drain within {timeoutSeconds} s: {queued} queued, {processing} processing.");
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

        // Only this host's app.db pool: other classes' hosts are still running.
        if (_appDbConnectionString != null)
        {
            using var connection = new SqliteConnection(_appDbConnectionString);
            SqliteConnection.ClearPool(connection);
        }
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
