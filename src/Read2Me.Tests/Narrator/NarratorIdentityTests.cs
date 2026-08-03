using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// NarratorIdentity is the read-time projection of Projects.NarratorCharacterId
    /// (ADR-0004). A dangling link must self-heal to Unlinked, never throw.
    /// </summary>
    public class NarratorIdentityTests : ProjectDbTestBase
    {
        [Fact]
        public void Unlinked_IsSeedNarratorRow()
        {
            var identity = NarratorIdentity.Unlinked;

            Assert.Equal(ProjectDbContext.NarratorId, identity.CharacterId);
            Assert.Equal("Narrator", identity.DisplayName);
            Assert.False(identity.IsLinked);
        }

        [Fact]
        public async Task LoadAsync_NullColumn_ReturnsUnlinked()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("V1", v => v.AddChapter()).BuildAsync();

            await using var db = await OpenDbAsync();
            var identity = await NarratorIdentity.LoadAsync(db);

            Assert.Equal(NarratorIdentity.Unlinked, identity);
        }

        [Fact]
        public async Task LoadAsync_NoProjectRow_ReturnsUnlinked()
        {
            await using var db = await OpenDbAsync();

            var identity = await NarratorIdentity.LoadAsync(db);

            Assert.Equal(NarratorIdentity.Unlinked, identity);
        }

        [Fact]
        public async Task LoadAsync_ValidLink_ReturnsLinkedCharacter()
        {
            var watson = new Character { Id = Guid.NewGuid(), Name = "Dr. Watson" };
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("watson", watson)
                .WithNarratorLink(watson.Id);
            await b.AddVolume("V1", v => v.AddChapter()).BuildAsync();

            await using var db = await OpenDbAsync();
            var identity = await NarratorIdentity.LoadAsync(db);

            Assert.Equal(watson.Id, identity.CharacterId);
            Assert.Equal("Dr. Watson", identity.DisplayName);
            Assert.True(identity.IsLinked);
        }

        [Fact]
        public async Task LoadAsync_DanglingLink_ReturnsUnlinked()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithNarratorLink(Guid.NewGuid());
            await b.AddVolume("V1", v => v.AddChapter()).BuildAsync();

            await using var db = await OpenDbAsync();
            var identity = await NarratorIdentity.LoadAsync(db);

            Assert.Equal(NarratorIdentity.Unlinked, identity);
        }

        [Fact]
        public async Task LoadAsync_LinkToSeedNarratorRow_ReportsLinked()
        {
            // The seed row is a real Character, so the projection resolves it.
            // Rejecting a self-link is the write-side handler's job (slice 14).
            var b = new BookHierarchyBuilder(OpenDbAsync)
                .WithNarratorLink(ProjectDbContext.NarratorId);
            await b.AddVolume("V1", v => v.AddChapter()).BuildAsync();

            await using var db = await OpenDbAsync();
            var identity = await NarratorIdentity.LoadAsync(db);

            Assert.Equal(ProjectDbContext.NarratorId, identity.CharacterId);
            Assert.Equal("Narrator", identity.DisplayName);
        }

        [Fact]
        public async Task Migration_AddsNarratorCharacterIdColumnToProjects()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Projects)";
            await using var reader = await cmd.ExecuteReaderAsync();
            bool found = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "NarratorCharacterId") { found = true; break; }
            }
            Assert.True(found, "NarratorCharacterId column should exist on Projects");
        }

        [Fact]
        public async Task Migration_AppliesOverAnExistingSeededDatabase()
        {
            // Migrate to the migration before this slice's, seed a project, then migrate up.
            await using (var old = OpenUnmigratedDb())
            {
                await old.GetService<IMigrator>()
                    .MigrateAsync("20260713093729_DropParagraphCharacterId");
                await old.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO Projects (Id, Title, BookTitle, Author, Filename, Type, NarratorOnlyMode)
                    VALUES ('11111111-1111-1111-1111-111111111111', 'T', 'T', 'A', 'book.txt', 'Text', 0)
                    """);
            }

            await using var db = await OpenDbAsync();

            var project = await db.Projects.SingleAsync();
            Assert.Equal("T", project.Title);
            Assert.Null(project.NarratorCharacterId);
            Assert.Equal(NarratorIdentity.Unlinked, await NarratorIdentity.LoadAsync(db));
        }
    }
}
