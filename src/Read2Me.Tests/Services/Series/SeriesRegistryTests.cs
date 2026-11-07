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
    /// The series registry (#234, #246) over a real <c>series.db</c> and migrated books in a temp
    /// Workspace: reads change nothing and take no lock; writes heal. "Sherlock Holmes" holds "A
    /// Study in Scarlet" and "The Sign of the Four", plus a member whose project was deleted outside
    /// the app; Dr. Watson is linked in both books, and the Sign also keeps a link to a Character
    /// deleted outside the app and a <i>Not the same</i> naming it.
    /// </summary>
    public class SeriesRegistryTests : ProjectDbTestBase
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
        private static readonly Guid HolmesInStudy = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private Guid _study;
        private Guid _sign;

        public SeriesRegistryTests()
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
        public async Task Reads_filter_the_stale_rows_and_leave_them_in_series_db()
        {
            await SeedAsync();

            var view = (await RegistryAsync(r => r.GetAsync(SeriesId, Ct))).Value!;
            var cast = (await RegistryAsync(r => r.CastAsync(SeriesId, Ct))).Value!;
            await RegistryAsync(r => r.ListAsync(Ct));
            await RegistryAsync(r => r.MembershipsAsync(Ct));
            await RegistryAsync(r => r.LinksOfAsync(new ProjectFolderId(Sign), Ct));

            Assert.Equal([Study, Sign], view.Books.Select(b => b.Folder));
            Assert.Equal(Watson, Assert.Single(cast.Rows).SeriesCharacterId);
            Assert.Equal((3, 4, 2, 1), await StoredAsync());
        }

        [Fact]
        public async Task A_write_deletes_the_series_stale_rows_gone_members_gone_links_and_stale_Not_the_same()
        {
            await SeedAsync();

            var shared = await RegistryAsync(r => r.ShareAsync(SeriesId, Study, HolmesInStudy, Ct));

            Assert.Null(shared.Refusal);
            // Left: the two books, Watson's two links and Holmes's new one, and no Not the same.
            Assert.Equal((2, 3, 2, 0), await StoredAsync());
            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            Assert.DoesNotContain(await db.Characters.Select(c => c.Id).ToListAsync(Ct), id => id == Orphan);
        }

        [Fact]
        public async Task A_lifecycle_write_drops_a_member_whose_project_is_gone()
        {
            await SeedAsync();

            await RegistryAsync(r => r.RenameAsync(SeriesId, "The Adventures of Sherlock Holmes", Ct));

            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            Assert.Equal(new HashSet<Guid> { _study, _sign }, (await db.Books.Select(b => b.ProjectId).ToListAsync(Ct)).ToHashSet());
        }

        [Fact]
        public async Task Reads_do_not_wait_behind_a_write_holding_the_series_lock()
        {
            await SeedAsync();
            using var hold = await _root.GetRequiredService<SeriesRegistryLock>().EnterAsync(Ct);

            var budget = TimeSpan.FromSeconds(10);
            await RegistryAsync(r => r.ListAsync(Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.GetAsync(SeriesId, Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.MembershipsAsync(Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.MembershipOfAsync(new ProjectFolderId(Sign), Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.CastAsync(SeriesId, Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.LinksOfAsync(new ProjectFolderId(Sign), Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.CharacterAsync(SeriesId, Watson, Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.PreviewLinkAsync(SeriesId, Watson, Sign, Guid.NewGuid(), Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.PreviewResolveAsync(SeriesId, Watson, Sign, push: true, Ct)).WaitAsync(budget, Ct);
            await RegistryAsync(r => r.PreviewShareAndLinkAsync(SeriesId,
                [new(Study, HolmesInStudy), new(Sign, Guid.NewGuid())], Ct)).WaitAsync(budget, Ct);
        }

        // ── arrange ─────────────────────────────────────────────────────────────

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private string SeriesFolder => Path.Combine(TempDir, SeriesRegistry.FolderName, SeriesId.ToString("D"));

        /// <summary>The rows in series.db: books, links, Series characters, <i>Not the same</i>.</summary>
        private async Task<(int Books, int Links, int Characters, int NotTheSame)> StoredAsync()
        {
            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            return (await db.Books.CountAsync(Ct), await db.Links.CountAsync(Ct),
                await db.Characters.CountAsync(Ct), await db.NotTheSame.CountAsync(Ct));
        }

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

        private async Task<T> RegistryAsync<T>(Func<SeriesRegistry, Task<T>> call)
        {
            await using var scope = _root.CreateAsyncScope();
            return await call(scope.ServiceProvider.GetRequiredService<SeriesRegistry>());
        }
    }
}
