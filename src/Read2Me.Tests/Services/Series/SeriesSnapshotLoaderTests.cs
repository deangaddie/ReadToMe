using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Series;
using Read2Me.Services;
using Read2Me.Services.Series;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// The Series snapshot loader (#234, #244) over a real <c>series.db</c> and migrated books in a
    /// temp Workspace: it loads the registry layer that is on disk, and deletes nothing. "Sherlock
    /// Holmes" holds "A Study in Scarlet" and "The Sign of the Four", plus a member whose project was
    /// deleted outside the app; Dr. Watson is linked in both books, and the Sign also keeps a link to
    /// a Character deleted outside the app.
    /// </summary>
    public class SeriesSnapshotLoaderTests : ProjectDbTestBase
    {
        private const string Study = "a-study-in-scarlet";
        private const string Sign = "the-sign-of-the-four";

        private static readonly Guid SeriesId = Guid.NewGuid();
        private static readonly Guid Watson = Guid.NewGuid();
        private static readonly Guid Orphan = Guid.NewGuid();
        private static readonly Guid GoneProject = Guid.NewGuid();
        private static readonly Guid WatsonInStudy = Guid.NewGuid();
        private static readonly Guid WatsonInSign = Guid.NewGuid();
        private static readonly Guid DeletedInSign = Guid.NewGuid();
        private static readonly Guid Holmes = Guid.NewGuid();
        private static readonly Guid HolmesInStudy = Guid.NewGuid();
        private static readonly Guid HolmesInSign = Guid.NewGuid();
        private static readonly Guid ArmyDoctor = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private Guid _study;
        private Guid _sign;

        public SeriesSnapshotLoaderTests()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await base.DisposeAsync();
        }

        [Fact]
        public async Task A_series_loads_its_members_links_Not_the_same_and_Characters_from_disk()
        {
            await SeedAsync();

            var series = (await LoadAsync(l => l.LoadSeriesAsync(SeriesId, withCasts: true, Ct))).Value!;

            Assert.Equal("Sherlock Holmes", series.Name);
            Assert.Equal([(Study, "A Study in Scarlet", 1m), (Sign, "The Sign of the Four", 2m)], series.Books.Select(b => (b.Folder, b.Title, b.Position)));
            Assert.Equal([new SeriesLinkRules.Link(Watson, _study, WatsonInStudy), new SeriesLinkRules.Link(Watson, _sign, WatsonInSign)],
                series.Links.OrderBy(l => l.ProjectId == _sign));
            Assert.Contains(series.Casts[_sign].Characters, c => c.CharacterId == WatsonInSign && c.Name == "Dr. Watson");
            Assert.False(series.Casts[_sign].NarratorLinked);
            Assert.Equal([GoneProject], series.StaleMembers);
            Assert.Equal(2, series.StaleLinks.Count);
            Assert.Single(series.StaleNotTheSame);
        }

        [Fact]
        public async Task Loading_deletes_nothing_the_stale_rows_are_still_in_series_db()
        {
            await SeedAsync();

            await LoadAsync(l => l.LoadSeriesAsync(SeriesId, withCasts: true, Ct));
            await LoadAsync(l => l.LoadWorkspaceAsync(Ct));
            await LoadAsync(l => l.LoadSeriesOfAsync(new ProjectFolderId(Sign), withCasts: true, Ct));

            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            Assert.Equal(3, await db.Books.CountAsync(Ct));
            Assert.Equal(4, await db.Links.CountAsync(Ct));
            Assert.Equal(2, await db.Characters.CountAsync(Ct));
            Assert.Equal(1, await db.NotTheSame.CountAsync(Ct));
        }

        [Fact]
        public async Task The_workspace_and_a_projects_series_load_the_registry_layer_without_Characters()
        {
            await SeedAsync();

            var workspace = await LoadAsync(l => l.LoadWorkspaceAsync(Ct));
            var (series, projectId) = (await LoadAsync(l => l.LoadSeriesOfAsync(new ProjectFolderId(Sign), withCasts: false, Ct)))!.Value;
            var (forSign, signId) = (await LoadAsync(l => l.LoadForProjectAsync(new ProjectFolderId(Sign), Ct)))!.Value;

            Assert.Equal([Study, Sign], Assert.Single(workspace.List()).Books.Select(b => b.Folder));
            Assert.Equal(0, workspace.Unreadable);
            Assert.Equal(_sign, projectId);
            Assert.Empty(series.Casts);
            Assert.Empty(series.Voices);
            Assert.Equal([Study], series.Peers(_sign, WatsonInSign)!.Others.Select(p => p.Folder));
            Assert.Equal(new ProjectSeries(SeriesId, "Sherlock Holmes", 2m), forSign.MembershipOf(signId));
        }

        [Fact]
        public async Task The_Voices_layer_loads_only_for_the_Series_characters_asked_about_and_deletes_nothing()
        {
            await SeedAsync();
            await SeedVoicesAsync();

            var series = await LoadAsync(async l =>
                await l.WithVoicesAsync((await l.LoadSeriesAsync(SeriesId, withCasts: true, Ct)).Value!, [Watson], Ct));

            Assert.Equal([Watson], series.Voices.Keys);
            Assert.Equal([Study, Sign], series.Voices[Watson].Select(h => h.Book.Folder));
            var inSign = series.Voices[Watson][1];
            Assert.Equal((WatsonInSign, ArmyDoctor), (inSign.Book.CharacterId, Assert.Single(inSign.Voices).Id));
            Assert.Equal([ArmyDoctor], inSign.PositionalRuleVoices);
            Assert.Equal(ArmyDoctor, inSign.DefaultVoiceId);
            // The same Voice with the same reference audio, each under its own Character's folder: no book differs.
            Assert.Equal(series.Voices[Watson][0].Version, inSign.Version);
            Assert.False(series.Differs(Watson, Sign));
            Assert.Null(series.SeriesCopy(Holmes));

            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            Assert.Equal(3, await db.Books.CountAsync(Ct));
            Assert.Equal(6, await db.Links.CountAsync(Ct));
            Assert.Equal(1, await db.NotTheSame.CountAsync(Ct));
        }

        [Fact]
        public async Task A_book_whose_reference_audio_differs_differs_from_series()
        {
            await SeedAsync();
            await SeedVoicesAsync();
            await File.WriteAllBytesAsync(Path.Combine(TempDir, Sign, AudioOf(WatsonInSign)), [9, 9, 9, 9, 9], Ct);

            var series = await LoadAsync(async l =>
                await l.WithVoicesAsync((await l.LoadSeriesAsync(SeriesId, withCasts: true, Ct)).Value!, [Watson], Ct));

            // A tie goes to the lowest Series position, so the Study holds the Series copy.
            Assert.Equal(Study, series.SeriesCopy(Watson)!.SeriesCopyFolder);
            Assert.True(series.Differs(Watson, Sign));
        }

        [Fact]
        public async Task An_unknown_series_is_refused_and_a_book_in_no_series_has_none()
        {
            await SeedAsync();
            await new BookHierarchyBuilder(() => OpenBookDbAsync("the-valley-of-fear")).BuildAsync();

            Assert.Equal(SeriesRefusal.UnknownSeries, (await LoadAsync(l => l.LoadSeriesAsync(Guid.NewGuid(), withCasts: false, Ct))).Refusal);
            Assert.Null(await LoadAsync(l => l.LoadSeriesOfAsync(new ProjectFolderId("the-valley-of-fear"), withCasts: false, Ct)));
        }

        // ── arrange ─────────────────────────────────────────────────────────────

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private string SeriesFolder => Path.Combine(TempDir, SeriesRegistry.FolderName, SeriesId.ToString("D"));

        private async Task SeedAsync()
        {
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Study))
                .WithProject(title: "A Study in Scarlet", author: "Arthur Conan Doyle")
                .WithCharacter("watson", new Character { Id = WatsonInStudy, Name = "Dr. Watson" })
                .WithCharacter("holmes", new Character { Id = HolmesInStudy, Name = "Sherlock Holmes" })
                .BuildAsync();
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Sign))
                .WithProject(title: "The Sign of the Four", author: "Arthur Conan Doyle")
                .WithCharacter("watson", new Character { Id = WatsonInSign, Name = "Dr. Watson" })
                .WithCharacter("holmes", new Character { Id = HolmesInSign, Name = "Sherlock Holmes" })
                .BuildAsync();
            _study = await ProjectIdAsync(Study);
            _sign = await ProjectIdAsync(Sign);

            Directory.CreateDirectory(SeriesFolder);
            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            db.Series.Add(new SeriesRow { Id = SeriesId, Name = "Sherlock Holmes" });
            db.Books.AddRange(
                new SeriesBook { ProjectId = _study, Position = 1m },
                new SeriesBook { ProjectId = _sign, Position = 2m },
                new SeriesBook { ProjectId = GoneProject, Position = 3m });
            db.Characters.AddRange(new SeriesCharacterRow { Id = Watson }, new SeriesCharacterRow { Id = Orphan });
            db.Links.AddRange(
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = _study, CharacterId = WatsonInStudy },
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = _sign, CharacterId = WatsonInSign },
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = GoneProject, CharacterId = Guid.NewGuid() },
                new SeriesLink { SeriesCharacterId = Orphan, ProjectId = _sign, CharacterId = DeletedInSign });
            db.NotTheSame.Add(new NotTheSameRow { Id = Guid.NewGuid(), SeriesCharacterId = Watson, ProjectId = _sign, CharacterId = DeletedInSign });
            await db.SaveChangesAsync(Ct);
        }

        private static string AudioOf(Guid characterId) => $"voices/{characterId}/army-doctor.wav";

        /// <summary>
        /// Holmes becomes a second Series character, linked in both books. Dr. Watson holds "Army
        /// doctor" in both, with the same reference audio and the Sign's one positional rule on it.
        /// </summary>
        private async Task SeedVoicesAsync()
        {
            await using (var series = await SeriesDbContext.OpenAsync(SeriesFolder, Ct))
            {
                series.Characters.Add(new SeriesCharacterRow { Id = Holmes });
                series.Links.AddRange(
                    new SeriesLink { SeriesCharacterId = Holmes, ProjectId = _study, CharacterId = HolmesInStudy },
                    new SeriesLink { SeriesCharacterId = Holmes, ProjectId = _sign, CharacterId = HolmesInSign });
                await series.SaveChangesAsync(Ct);
            }

            foreach (var (folder, watson) in new[] { (Study, WatsonInStudy), (Sign, WatsonInSign) })
            {
                var audio = Path.Combine(TempDir, folder, AudioOf(watson));
                Directory.CreateDirectory(Path.GetDirectoryName(audio)!);
                await File.WriteAllBytesAsync(audio, [1, 2, 3], Ct);

                await using var db = await OpenBookDbAsync(folder);
                db.Voices.Add(new Read2Me.Data.Entities.Voice { Id = ArmyDoctor, CharacterId = watson, Name = "Army doctor", AudioFileName = AudioOf(watson) });
                db.VoiceRules.Add(new VoiceRule { Id = Guid.NewGuid(), CharacterId = watson, VoiceId = ArmyDoctor, Rank = "a", IsDefault = true });
                if (folder == Sign)
                    db.VoiceRules.Add(new VoiceRule { Id = Guid.NewGuid(), CharacterId = watson, VoiceId = ArmyDoctor, Rank = "b" });
                await db.SaveChangesAsync(Ct);
            }
        }

        private async Task<ProjectDbContext> OpenBookDbAsync(string folder)
        {
            Directory.CreateDirectory(Path.Combine(TempDir, folder));
            var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(TempDir, folder, "project.db")};Pooling=false")
                .Options);
            await db.Database.MigrateAsync();
            return db;
        }

        private async Task<Guid> ProjectIdAsync(string folder)
        {
            await using var db = await OpenBookDbAsync(folder);
            return (await db.Projects.AsNoTracking().SingleAsync(Ct)).Id;
        }

        private async Task<T> LoadAsync<T>(Func<SeriesSnapshotLoader, Task<T>> load)
        {
            await using var scope = _root.CreateAsyncScope();
            return await load(scope.ServiceProvider.GetRequiredService<SeriesSnapshotLoader>());
        }
    }
}
