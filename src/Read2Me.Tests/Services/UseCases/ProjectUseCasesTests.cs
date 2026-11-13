using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Data.Series;
using Read2Me.Services;
using Read2Me.Services.Series;
using Read2Me.Services.UseCases;
using Read2Me.Tests.Fakes;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.UseCases
{
    /// <summary>
    /// The project lifecycle (#330) over the real <see cref="ProjectService"/> and
    /// <see cref="SeriesRegistry"/> in a temp Workspace: a delete forgets the book's Series
    /// membership, and one title rule holds for create and update.
    /// </summary>
    public class ProjectUseCasesTests : ProjectDbTestBase
    {
        private const string Study = "a-study-in-scarlet";

        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;
        private readonly CollectingLogger<ProjectUseCases> _log = new();

        public ProjectUseCasesTests()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectService>();
            services.AddScoped<IProjectWriter>(sp => sp.GetRequiredService<ProjectService>());
            services.AddSingleton<ILogger<ProjectUseCases>>(_log);
            services.AddScoped<ProjectUseCases>();
            _root = services.BuildServiceProvider();
            _scope = _root.CreateAsyncScope();
        }

        public override async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await base.DisposeAsync();
        }

        // ── delete ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task Deleting_a_project_forgets_its_Series_entry_by_the_Id_read_before_the_folder_goes()
        {
            var seriesId = await StudyInASeriesAsync();

            var outcome = await UseCases.DeleteAsync(new ProjectFolderId(Study), Ct);

            Assert.IsType<ProjectOutcome.Done>(outcome);
            Assert.False(Directory.Exists(Path.Combine(TempDir, Study)));
            Assert.Empty(await MembersAsync(seriesId));
        }

        [Fact]
        public async Task A_folder_that_will_not_delete_fails_and_keeps_the_Series_entry()
        {
            var seriesId = await StudyInASeriesAsync();

            var outcome = await With(new UndeletableWriter()).DeleteAsync(new ProjectFolderId(Study), Ct);

            Assert.Equal(new ProjectOutcome.Failed("Failed to delete project. Please try again."), outcome);
            Assert.Single(await MembersAsync(seriesId));
        }

        [Fact]
        public async Task A_forget_that_throws_still_reports_the_delete_and_warns()
        {
            var seriesId = await StudyInASeriesAsync();

            // The registry's lock wait is the first thing to see the token, after the folder is gone.
            var outcome = await UseCases.DeleteAsync(new ProjectFolderId(Study), new CancellationToken(canceled: true));

            Assert.IsType<ProjectOutcome.Done>(outcome);
            Assert.False(Directory.Exists(Path.Combine(TempDir, Study)));
            Assert.Single(await MembersAsync(seriesId));
            Assert.Single(_log.At(LogLevel.Warning));
        }

        // ── title rule ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Create_refuses_a_blank_title_and_writes_nothing(string title)
        {
            var outcome = await CreateAsync(title);

            Assert.Equal(new ProjectOutcome.Refused("Fields 'title' and 'file' are required."), outcome);
            Assert.Empty(Directory.GetDirectories(TempDir));
        }

        [Fact]
        public async Task Create_stores_the_title_book_title_and_author_trimmed()
        {
            var outcome = await CreateAsync("  A Study in Scarlet ", " A Study in Scarlet\t", " Arthur Conan Doyle ");

            Assert.Equal(new ProjectOutcome.Created(Study), outcome);
            var project = await ProjectAsync();
            Assert.Equal(("A Study in Scarlet", "A Study in Scarlet", "Arthur Conan Doyle"), (project.Title, project.BookTitle, project.Author));
        }

        [Theory]
        [InlineData("", null, "Title cannot be blank.")]
        [InlineData("  ", null, "Title cannot be blank.")]
        [InlineData(null, "", "Book title cannot be blank.")]
        [InlineData(null, " \t", "Book title cannot be blank.")]
        public async Task Update_refuses_a_blank_title_or_book_title_and_changes_nothing(string? title, string? bookTitle, string message)
        {
            await CreateAsync("A Study in Scarlet");

            var outcome = await UseCases.UpdateMetadataAsync(new ProjectFolderId(Study), title, bookTitle, "Doyle");

            Assert.Equal(new ProjectOutcome.Refused(message), outcome);
            Assert.Equal("Arthur Conan Doyle", (await ProjectAsync()).Author);
        }

        [Fact]
        public async Task Update_trims_and_lets_the_author_go_blank()
        {
            await CreateAsync("A Study in Scarlet");

            var outcome = await UseCases.UpdateMetadataAsync(new ProjectFolderId(Study), " Study in Scarlet ", null, "  ");

            Assert.IsType<ProjectOutcome.Done>(outcome);
            var project = await ProjectAsync();
            Assert.Equal(("Study in Scarlet", "A Study in Scarlet", ""), (project.Title, project.BookTitle, project.Author));
        }

        // ── arrange ─────────────────────────────────────────────────────────────

        /// <summary>The Study's project row as a fresh scope reads it.</summary>
        private async Task<Read2Me.Data.Entities.Project> ProjectAsync()
        {
            await using var scope = _root.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IProjectCatalogReader>().GetProjectAsync(new ProjectFolderId(Study)))!;
        }

        /// <summary>The module over the scope's real reader and registry, writing through <paramref name="writer"/>.</summary>
        private ProjectUseCases With(IProjectWriter writer)
        {
            var sp = _scope.ServiceProvider;
            return new ProjectUseCases(
                sp.GetRequiredService<IProjectCatalogReader>(), writer, sp.GetRequiredService<Read2Me.Services.Mutations.BookMutations>(),
                sp.GetRequiredService<SeriesRegistry>(), _log);
        }

        /// <summary>A writer whose folder will not go: a file held open, a permissions fault.</summary>
        private sealed class UndeletableWriter : IProjectWriter
        {
            public void DeleteProject(ProjectFolderId folderId) => throw new IOException("The folder is in use.");
            public Task<string> CreateProjectAsync(string title, string bookTitle, string author,
                string originalFileName, Stream fileStream, BookFileType fileType) => throw new NotSupportedException();
            public Task UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author) => throw new NotSupportedException();
            public Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream) => throw new NotSupportedException();
            public Task DeleteCoverImageAsync(ProjectFolderId folderId) => throw new NotSupportedException();
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private ProjectUseCases UseCases => _scope.ServiceProvider.GetRequiredService<ProjectUseCases>();

        private async Task<ProjectOutcome> CreateAsync(string title, string bookTitle = "A Study in Scarlet", string author = "Arthur Conan Doyle")
        {
            using var file = new MemoryStream(Encoding.UTF8.GetBytes("In the year 1878 I took my degree of Doctor of Medicine."));
            return await UseCases.CreateAsync(title, bookTitle, author, "study.txt", file, BookFileType.Text);
        }

        /// <summary>"A Study in Scarlet", created through the module and made Book 1 of "Sherlock Holmes".</summary>
        private async Task<Guid> StudyInASeriesAsync()
        {
            Assert.IsType<ProjectOutcome.Created>(await CreateAsync("A Study in Scarlet"));
            await using var scope = _root.CreateAsyncScope();
            var created = await scope.ServiceProvider.GetRequiredService<SeriesRegistry>().CreateAsync("Sherlock Holmes", Study, Ct);
            Assert.Null(created.Refusal);
            Assert.Single(await MembersAsync(created.Value));
            return created.Value;
        }

        /// <summary>The member rows stored in series.db, stale ones included: reads would filter those.</summary>
        private async Task<List<Guid>> MembersAsync(Guid seriesId)
        {
            await using var db = await SeriesDbContext.OpenAsync(
                Path.Combine(TempDir, SeriesRegistry.FolderName, seriesId.ToString("D")), Ct);
            return await db.Books.Select(b => b.ProjectId).ToListAsync(Ct);
        }
    }
}
