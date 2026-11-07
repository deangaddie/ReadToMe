using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
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
    /// The Series Voice replicator (ADR 0018) over real migrated books in a temp Workspace: a plan
    /// in; each book's Voices, files and History step out. Dr. Watson is the Series character, linked
    /// in "A Study in Scarlet" (the source), "The Sign of the Four" and "The Hound of the Baskervilles".
    /// </summary>
    public class SeriesVoiceReplicatorTests : ProjectDbTestBase
    {
        private const string Study = "a-study-in-scarlet";
        private const string Sign = "the-sign-of-the-four";
        private const string Hound = "the-hound-of-the-baskervilles";

        private static readonly Guid WatsonInStudy = Guid.NewGuid();
        private static readonly Guid WatsonInSign = Guid.NewGuid();
        private static readonly Guid WatsonInHound = Guid.NewGuid();
        private static readonly Guid StamfordInSign = Guid.NewGuid();
        private static readonly Guid ArmyDoctor = Guid.NewGuid();

        private static readonly SeriesVoiceStep Step = new("Rename voice", StepAction.Voice);
        private static readonly SeriesVoiceStep Take = new(SeriesRegistry.TakeLabel, StepAction.Voice);

        private readonly ServiceProvider _root;

        public SeriesVoiceReplicatorTests()
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
            await base.DisposeAsync();
        }

        // ── a link's gains ──────────────────────────────────────────────────────

        [Fact]
        public async Task A_joining_book_with_a_different_file_at_the_target_path_holds_the_series_audio_after_Link_gain()
        {
            await SeedAsync();
            var source = await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor", "series audio");
            // The joining book's own file under the same name, named by no Voice of its Watson.
            Write(Sign, $"voices/{WatsonInSign}/army-doctor.wav", "the book's own audio");

            var results = await WriteAsync(new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Gain([source])));

            Assert.Null(Assert.Single(results).Failure);
            var voice = await VoiceAsync(Sign, ArmyDoctor);
            Assert.Equal(WatsonInSign, voice.CharacterId);
            Assert.Equal($"voices/{WatsonInSign}/army-doctor.wav", voice.AudioFileName);
            // Doesn't differ from series: the same reference audio as the series copy.
            Assert.Equal("series audio", Read(Sign, voice.AudioFileName!));
            Assert.Empty(Backups(Sign));
        }

        // ── a failed commit ─────────────────────────────────────────────────────

        [Fact]
        public async Task A_failed_commit_puts_the_set_aside_files_back_and_removes_the_new_ones()
        {
            await SeedAsync();
            var source = await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor", "series audio", withOriginal: true);
            // Stamford holds the Voice in this book, so the commit is refused.
            await AddVoiceAsync(Sign, StamfordInSign, ArmyDoctor, "Army doctor", "unused");
            Write(Sign, $"voices/{WatsonInSign}/army-doctor.wav", "the book's own audio");

            var results = await WriteAsync(new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Copy(source)));

            Assert.NotNull(Assert.Single(results).Failure);
            Assert.Equal("the book's own audio", Read(Sign, $"voices/{WatsonInSign}/army-doctor.wav"));
            Assert.False(File.Exists(PathIn(Sign, VoiceOriginalStore.PathOf(WatsonInSign, ArmyDoctor))));
            Assert.Empty(Backups(Sign));
        }

        [Fact]
        public async Task A_failure_in_one_book_leaves_it_as_it_was_and_still_writes_the_rest()
        {
            await SeedAsync();
            var source = await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor", "series audio");
            await AddVoiceAsync(Sign, StamfordInSign, ArmyDoctor, "Army doctor", "Stamford's audio");

            var results = await WriteAsync(
                new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Copy(source)),
                new SeriesVoiceWrite(Peer(Hound, WatsonInHound), new SeriesVoiceChange.Copy(source)));

            Assert.Equal([Sign, Hound], results.Select(r => r.Folder));
            Assert.NotNull(results[0].Failure);
            Assert.Null(results[1].Failure);
            // The failed book differs from series: its Watson still lacks the Voice.
            Assert.Empty(await VoicesOfAsync(Sign, WatsonInSign));
            Assert.Equal("series audio", Read(Hound, (await VoiceAsync(Hound, ArmyDoctor)).AudioFileName!));
        }

        // ── after a successful commit ───────────────────────────────────────────

        [Fact]
        public async Task Files_no_longer_named_are_removed_after_the_commit()
        {
            await SeedAsync();
            var source = await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor", "series audio");
            var restored = Guid.NewGuid();
            await AddVoiceAsync(Sign, WatsonInSign, restored, "Restored", "the book's own audio", withOriginal: true);

            var results = await WriteAsync(new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Replace([source])));

            Assert.Null(Assert.Single(results).Failure);
            Assert.Equal([ArmyDoctor], (await VoicesOfAsync(Sign, WatsonInSign)).Select(v => v.Id));
            Assert.False(File.Exists(PathIn(Sign, $"voices/{WatsonInSign}/restored.wav")));
            Assert.False(File.Exists(PathIn(Sign, VoiceOriginalStore.PathOf(WatsonInSign, restored))));
            Assert.Equal("series audio", Read(Sign, $"voices/{WatsonInSign}/army-doctor.wav"));
        }

        [Fact]
        public async Task A_copied_delete_removes_the_Voice_and_its_files()
        {
            await SeedAsync();
            await AddVoiceAsync(Sign, WatsonInSign, ArmyDoctor, "Army doctor", "the book's copy", withOriginal: true);
            var kept = Guid.NewGuid();
            await AddVoiceAsync(Sign, WatsonInSign, kept, "Old campaigner", "kept audio");

            var results = await WriteAsync(new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Delete(ArmyDoctor)));

            Assert.Null(Assert.Single(results).Failure);
            Assert.Equal([kept], (await VoicesOfAsync(Sign, WatsonInSign)).Select(v => v.Id));
            Assert.False(File.Exists(PathIn(Sign, $"voices/{WatsonInSign}/army-doctor.wav")));
            Assert.False(File.Exists(PathIn(Sign, VoiceOriginalStore.PathOf(WatsonInSign, ArmyDoctor))));
            Assert.Equal("kept audio", Read(Sign, $"voices/{WatsonInSign}/old-campaigner.wav"));
        }

        [Fact]
        public async Task Each_book_gets_its_own_History_step_with_the_given_label_and_action()
        {
            await SeedAsync();
            var campaigner = await AddVoiceAsync(Study, WatsonInStudy, Guid.NewGuid(), "Old campaigner", "more series audio");
            var source = await AddVoiceAsync(Study, WatsonInStudy, ArmyDoctor, "Army doctor", "series audio");

            await WriteAsync(
                new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Copy(source)),
                new SeriesVoiceWrite(Peer(Hound, WatsonInHound), new SeriesVoiceChange.Gain([source])));
            await AssertTipStepsAsync(Step, Sign, Hound);

            await WriteAsync(Take,
                new SeriesVoiceWrite(Peer(Sign, WatsonInSign), new SeriesVoiceChange.Replace([source, campaigner])),
                new SeriesVoiceWrite(Peer(Hound, WatsonInHound), new SeriesVoiceChange.Delete(ArmyDoctor)));
            await AssertTipStepsAsync(Take, Sign, Hound);
            Assert.Empty(await VoicesOfAsync(Hound, WatsonInHound));
        }

        private async Task AssertTipStepsAsync(SeriesVoiceStep step, params string[] folders)
        {
            foreach (var folder in folders)
            {
                await using var scope = _root.CreateAsyncScope();
                var history = await scope.ServiceProvider.GetRequiredService<ProjectHistory>().ListAsync(new ProjectFolderId(folder));
                Assert.Equal((step.Label, (StepAction?)step.Action), (history.Steps[0].Label, history.Steps[0].Action));
            }
        }

        // ── arrange ─────────────────────────────────────────────────────────────

        private async Task SeedAsync()
        {
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Study))
                .WithCharacter("watson", new Character { Id = WatsonInStudy, Name = "Dr. Watson" })
                .BuildAsync();
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Sign))
                .WithCharacter("watson", new Character { Id = WatsonInSign, Name = "Dr. Watson" })
                .WithCharacter("stamford", new Character { Id = StamfordInSign, Name = "Stamford" })
                .BuildAsync();
            await new BookHierarchyBuilder(() => OpenBookDbAsync(Hound))
                .WithCharacter("watson", new Character { Id = WatsonInHound, Name = "Dr. Watson" })
                .BuildAsync();
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

        /// <summary>A Voice row straight in the book, with its reference audio (and kept original) on disk.</summary>
        private async Task<SeriesVoiceSource> AddVoiceAsync(
            string folder, Guid characterId, Guid voiceId, string name, string audio, bool withOriginal = false)
        {
            var file = $"voices/{characterId}/{name.ToLowerInvariant().Replace(' ', '-')}.wav";
            Write(folder, file, audio);
            if (withOriginal) Write(folder, VoiceOriginalStore.PathOf(characterId, voiceId), audio + " (original)");
            var created = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
            await using (var db = await OpenBookDbAsync(folder))
            {
                db.Voices.Add(new VoiceEntity
                {
                    Id = voiceId, CharacterId = characterId, Name = name, Source = VoiceSource.Uploaded,
                    Transcript = "It is a capital mistake to theorize before one has data.", AudioFileName = file, CreatedUtc = created,
                });
                await db.SaveChangesAsync();
            }
            return new SeriesVoiceSource(new ProjectFolderId(folder), characterId, new SeriesVoiceCopy(
                voiceId, name, null, VoiceSource.Uploaded, null, "It is a capital mistake to theorize before one has data.",
                file, null, null, created));
        }

        private static SeriesPeer Peer(string folder, Guid characterId) => new(folder, folder, 1m, characterId);

        private Task<IReadOnlyList<SeriesCopyResult>> WriteAsync(params SeriesVoiceWrite[] plan) => WriteAsync(Step, plan);

        private async Task<IReadOnlyList<SeriesCopyResult>> WriteAsync(SeriesVoiceStep step, params SeriesVoiceWrite[] plan)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<SeriesVoiceReplicator>().WriteAsync(plan, step, TestContext.Current.CancellationToken);
        }

        private async Task<VoiceEntity> VoiceAsync(string folder, Guid voiceId)
        {
            await using var db = await OpenBookDbAsync(folder);
            return await db.Voices.AsNoTracking().SingleAsync(v => v.Id == voiceId, TestContext.Current.CancellationToken);
        }

        private async Task<List<VoiceEntity>> VoicesOfAsync(string folder, Guid characterId)
        {
            await using var db = await OpenBookDbAsync(folder);
            return await db.Voices.AsNoTracking().Where(v => v.CharacterId == characterId).ToListAsync(TestContext.Current.CancellationToken);
        }

        private string PathIn(string folder, string relative) => Path.Combine(TempDir, folder, relative);

        private void Write(string folder, string relative, string content)
        {
            var path = PathIn(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private string Read(string folder, string relative) => File.ReadAllText(PathIn(folder, relative));

        private IEnumerable<string> Backups(string folder) =>
            Directory.EnumerateFiles(Path.Combine(TempDir, folder), "*.series-backup", SearchOption.AllDirectories);
    }
}
