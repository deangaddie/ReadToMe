using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Data.Series;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Series;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Services.Series
{
    /// <summary>
    /// The series' commit hook (ADR 0018, #237) over a real <c>series.db</c> and migrated books in a
    /// temp Workspace: a Book commit in, what it refused, copied, unlinked or signalled out. "Sherlock
    /// Holmes" holds "A Study in Scarlet" (1), "The Sign of the Four" (2) and "The Hound of the
    /// Baskervilles" (3); Dr. Watson is linked in all three. The Study and the Sign hold the same
    /// "Army doctor" Voice, so the Study holds the Series copy; the Hound holds none, so it differs.
    /// </summary>
    public class SeriesVoiceCopierTests : ProjectDbTestBase
    {
        private const string Study = "a-study-in-scarlet";
        private const string Sign = "the-sign-of-the-four";
        private const string Hound = "the-hound-of-the-baskervilles";

        private static readonly Guid SeriesId = Guid.NewGuid();
        private static readonly Guid Watson = Guid.NewGuid();
        private static readonly Guid WatsonInStudy = Guid.NewGuid();
        private static readonly Guid WatsonInSign = Guid.NewGuid();
        private static readonly Guid WatsonInHound = Guid.NewGuid();
        private static readonly Guid HolmesInStudy = Guid.NewGuid();
        private static readonly Guid ArmyDoctor = Guid.NewGuid();

        private readonly ServiceProvider _root;
        private readonly List<SeriesChanged> _signals = [];

        public SeriesVoiceCopierTests()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _root.GetRequiredService<EventBroadcaster<SeriesChanged>>().Event += _signals.Add;
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await base.DisposeAsync();
        }

        // ---- copy after commit ---------------------------------------------------------------------

        [Fact]
        public async Task A_Voice_edit_in_the_series_copy_is_copied_and_brings_a_differing_book_back_in_line()
        {
            await SeedAsync();

            var outcome = await CommitAsync(new UpdateVoiceMutation(new ProjectFolderId(Study), ArmyDoctor, "Old campaigner", null));

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            Assert.Equal("Old campaigner", Assert.Single(await VoicesOfAsync(Sign, WatsonInSign)).Name);
            // The Hound held none of Watson's Voices: it takes the whole set, and so holds the Voice too.
            Assert.Equal("Old campaigner", Assert.Single(await VoicesOfAsync(Hound, WatsonInHound)).Name);
            Assert.Contains(_signals, s => s.SeriesId == SeriesId);
        }

        [Fact]
        public async Task A_Voice_edit_in_a_differing_book_is_copied_but_brings_no_other_book_in_line()
        {
            await SeedAsync();
            var hounds = Guid.NewGuid();
            await AddVoiceAsync(Hound, WatsonInHound, hounds, "Hound's doctor");

            await CommitAsync(new UpdateVoiceMutation(new ProjectFolderId(Hound), hounds, "Moor walker", null));

            // A copy of the one Voice, added beside the Army doctor; no book takes the Hound's whole set.
            Assert.Equal(["Army doctor", "Moor walker"], (await VoicesOfAsync(Sign, WatsonInSign)).Select(v => v.Name).Order());
            Assert.Equal(["Army doctor", "Moor walker"], (await VoicesOfAsync(Study, WatsonInStudy)).Select(v => v.Name).Order());
        }

        // ---- refusals --------------------------------------------------------------------------------

        [Fact]
        public async Task Deleting_a_shared_Voice_that_is_another_books_last_is_refused_and_nothing_is_deleted()
        {
            await SeedAsync();

            var outcome = await CommitAsync(new DeleteVoiceMutation(new ProjectFolderId(Study), ArmyDoctor));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
            Assert.Equal(BookMutationRejection.Blocked, rejected.Reason);
            Assert.Equal(nameof(SeriesVoiceDeleteReason.LastVoice), rejected.Extensions!["reason"]);
            Assert.Single(await VoicesOfAsync(Study, WatsonInStudy));
            Assert.Single(await VoicesOfAsync(Sign, WatsonInSign));
        }

        [Fact]
        public async Task Merging_a_linked_Character_away_is_refused_but_merging_another_into_it_is_not()
        {
            await SeedAsync();

            var outcome = await CommitAsync(new MergeCharactersMutation(new ProjectFolderId(Study), HolmesInStudy, WatsonInStudy, false));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
            Assert.Equal(BookMutationRejection.Blocked, rejected.Reason);
            Assert.Contains("Dr. Watson", rejected.Message);
            Assert.IsType<BookMutationOutcome.Committed>(
                await CommitAsync(new MergeCharactersMutation(new ProjectFolderId(Study), WatsonInStudy, HolmesInStudy, false)));
        }

        // ---- deleting a linked Character -------------------------------------------------------------

        [Fact]
        public async Task Deleting_a_linked_Character_unlinks_it_and_the_other_books_keep_their_Voices()
        {
            await SeedAsync();

            var outcome = await CommitAsync(new DeleteCharacterMutation(new ProjectFolderId(Sign), WatsonInSign));

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            Assert.Equal(new[] { WatsonInStudy, WatsonInHound }.Order(), (await LinksAsync()).Order());
            Assert.Single(await VoicesOfAsync(Study, WatsonInStudy));
        }

        // ---- the series' own writes ------------------------------------------------------------------

        [Fact]
        public async Task A_copied_delete_stands_aside_so_it_is_neither_checked_nor_copied_on_nor_signalled()
        {
            await SeedAsync();

            // Left to the hook, this is the Sign's last Voice (refused) and would be copied on.
            var outcome = await CommitAsync(new DeleteVoiceMutation(new ProjectFolderId(Study), ArmyDoctor)
            {
                CopiedStep = ("Delete voice", StepAction.Delete),
            });

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            Assert.Empty(await VoicesOfAsync(Study, WatsonInStudy));
            Assert.Single(await VoicesOfAsync(Sign, WatsonInSign));
            Assert.Empty(_signals);
        }

        // ---- the series signal -----------------------------------------------------------------------

        [Fact]
        public async Task A_member_books_Character_commit_signals_its_series()
        {
            await SeedAsync();

            await CommitAsync(new RenameCharacterMutation(new ProjectFolderId(Sign), WatsonInSign, "John Watson"));

            Assert.Equal(SeriesId, Assert.Single(_signals).SeriesId);
        }

        // ---- arrange ---------------------------------------------------------------------------------

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
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Hound))
                .WithProject(title: "The Hound of the Baskervilles", author: "Arthur Conan Doyle")
                .WithCharacter("watson", new Character { Id = WatsonInHound, Name = "Dr. Watson" })
                .BuildAsync();
            await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor");
            await AddVoiceAsync(Sign, WatsonInSign, ArmyDoctor, "Army doctor");

            var folder = SeriesFolder;
            Directory.CreateDirectory(folder);
            await using var db = await SeriesDbContext.OpenAsync(folder, Ct);
            db.Series.Add(new SeriesRow { Id = SeriesId, Name = "Sherlock Holmes" });
            var (study, sign, hound) = (await ProjectIdAsync(Study), await ProjectIdAsync(Sign), await ProjectIdAsync(Hound));
            db.Books.AddRange(
                new SeriesBook { ProjectId = study, Position = 1m },
                new SeriesBook { ProjectId = sign, Position = 2m },
                new SeriesBook { ProjectId = hound, Position = 3m });
            db.Characters.Add(new SeriesCharacterRow { Id = Watson });
            db.Links.AddRange(
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = study, CharacterId = WatsonInStudy },
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = sign, CharacterId = WatsonInSign },
                new SeriesLink { SeriesCharacterId = Watson, ProjectId = hound, CharacterId = WatsonInHound });
            await db.SaveChangesAsync(Ct);
        }

        /// <summary>A Voice row straight in the book, with no reference audio.</summary>
        private async Task AddVoiceAsync(string folder, Guid characterId, Guid voiceId, string name)
        {
            await using var db = await OpenBookDbAsync(folder);
            db.Voices.Add(new VoiceEntity
            {
                Id = voiceId, CharacterId = characterId, Name = name, Source = VoiceSource.Uploaded,
                Transcript = "It is a capital mistake to theorize before one has data.",
                CreatedUtc = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync(Ct);
        }

        private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation, Ct);
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

        private async Task<List<VoiceEntity>> VoicesOfAsync(string folder, Guid characterId)
        {
            await using var db = await OpenBookDbAsync(folder);
            return await db.Voices.AsNoTracking().Where(v => v.CharacterId == characterId).ToListAsync(Ct);
        }

        /// <summary>The Characters Dr. Watson is linked to, in any book.</summary>
        private async Task<List<Guid>> LinksAsync()
        {
            await using var db = await SeriesDbContext.OpenAsync(SeriesFolder, Ct);
            return await db.Links.AsNoTracking().Where(l => l.SeriesCharacterId == Watson).Select(l => l.CharacterId).ToListAsync(Ct);
        }

        private string SeriesFolder => Path.Combine(TempDir, SeriesRegistry.FolderName, SeriesId.ToString("D"));

        private static CancellationToken Ct => TestContext.Current.CancellationToken;
    }
}
