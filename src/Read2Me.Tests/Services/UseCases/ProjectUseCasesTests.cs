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
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Queueing;
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
        private BusySource? _busy;
        private readonly ProjectActivity _activity;

        public ProjectUseCasesTests()
        {
            _activity = BusyGate.With(new Busy(() => _busy));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(_activity);
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

        [Fact]
        public async Task A_delete_while_busy_is_refused_with_the_reason_and_deletes_nothing()
        {
            var seriesId = await StudyInASeriesAsync();
            _busy = new BusySource(BusySourceKind.Assembly, "128 paragraphs · assembling");

            var outcome = await UseCases.DeleteAsync(new ProjectFolderId(Study), Ct);

            Assert.Equal(new ProjectOutcome.ActivityRefused(new ActivityRefusal(ActivityRefusalKind.Busy, "128 paragraphs · assembling")), outcome);
            Assert.True(Directory.Exists(Path.Combine(TempDir, Study)));
            Assert.Single(await MembersAsync(seriesId));
        }

        [Fact]
        public async Task A_delete_while_restoring_is_refused_as_restoring()
        {
            await StudyInASeriesAsync();
            Assert.Null(_activity.TryBeginRestore(new ProjectFolderId(Study)));

            var outcome = await UseCases.DeleteAsync(new ProjectFolderId(Study), Ct);

            Assert.Equal(ActivityRefusalKind.Restoring, Assert.IsType<ProjectOutcome.ActivityRefused>(outcome).Refusal.Kind);
            Assert.True(Directory.Exists(Path.Combine(TempDir, Study)));
        }

        [Fact]
        public async Task The_project_is_deleting_while_its_folder_goes_and_idle_after_a_failed_delete()
        {
            await StudyInASeriesAsync();
            var folder = new ProjectFolderId(Study);
            ActivityRefusal? during = null;

            var outcome = await With(new UndeletableWriter(() => during = _activity.MayWrite(folder))).DeleteAsync(folder, Ct);

            Assert.IsType<ProjectOutcome.Failed>(outcome);
            Assert.Equal(ActivityRefusalKind.Deleting, during?.Kind);
            Assert.Null(_activity.MayWrite(folder));
            Assert.Null(_activity.TryBeginRestore(folder));
        }

        [Fact]
        public async Task The_claim_is_released_after_a_delete()
        {
            await StudyInASeriesAsync();
            var folder = new ProjectFolderId(Study);

            Assert.IsType<ProjectOutcome.Done>(await UseCases.DeleteAsync(folder, Ct));

            Assert.Null(_activity.TryStart([folder], () => { }));
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

        // ── rejections and faults (#347) ────────────────────────────────────────

        [Theory]
        [InlineData(BookMutationRejection.Conflict)]
        [InlineData(BookMutationRejection.Activity)]
        [InlineData(BookMutationRejection.Blocked)]
        public async Task A_write_the_project_state_refuses_is_a_conflict_with_the_mutations_reason(BookMutationRejection reason)
        {
            var writer = new ScriptedWriter(new BookMutationOutcome.Rejected(reason, "Project is restoring"));

            var outcome = await With(writer).UpdateMetadataAsync(new ProjectFolderId(Study), "A Study in Scarlet", null, null);

            Assert.Equal(new ProjectOutcome.Conflict("Project is restoring"), outcome);
            Assert.Empty(_log.Entries);
        }

        [Fact]
        public async Task A_write_to_a_project_the_Book_does_not_have_is_not_found()
        {
            var writer = new ScriptedWriter(new BookMutationOutcome.Rejected(BookMutationRejection.NotFound, "No project row."));

            var outcome = await With(writer).SaveCoverImageAsync(new ProjectFolderId(Study), "cover.jpg", new MemoryStream([1]));

            Assert.Equal(new ProjectOutcome.NotFound("No project row."), outcome);
        }

        [Fact]
        public async Task An_illegal_write_is_refused_with_the_mutations_reason()
        {
            var writer = new ScriptedWriter(new BookMutationOutcome.Rejected(BookMutationRejection.Validation, "Not a cover."));

            var outcome = await With(writer).DeleteCoverImageAsync(new ProjectFolderId(Study));

            Assert.Equal(new ProjectOutcome.Refused("Not a cover."), outcome);
        }

        [Fact]
        public async Task A_write_cancelled_before_it_committed_propagates_as_a_cancellation()
        {
            var writer = new ScriptedWriter(new BookMutationOutcome.Rejected(BookMutationRejection.Cancelled, "Cancelled."));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => With(writer).UpdateMetadataAsync(new ProjectFolderId(Study), "A Study in Scarlet", null, null));
            Assert.Empty(_log.Entries);
        }

        [Fact]
        public async Task A_cancellation_thrown_by_the_writer_propagates_and_is_not_logged()
        {
            var writer = new ScriptedWriter(fault: new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => With(writer).SaveCoverImageAsync(new ProjectFolderId(Study), "cover.jpg", new MemoryStream([1])));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAsync(With(writer), "A Study in Scarlet"));
            Assert.Empty(_log.Entries);
        }

        [Fact]
        public async Task An_unexpected_fault_fails_with_the_generic_message_and_is_logged_with_the_folder()
        {
            var writer = new ScriptedWriter(fault: new InvalidDataException("project.db is corrupt"));
            var folder = new ProjectFolderId(Study);

            Assert.Equal(new ProjectOutcome.Failed("Failed to save project details."),
                await With(writer).UpdateMetadataAsync(folder, "A Study in Scarlet", null, null));
            Assert.Equal(new ProjectOutcome.Failed("Failed to save cover image."),
                await With(writer).SaveCoverImageAsync(folder, "cover.jpg", new MemoryStream([1])));
            Assert.Equal(new ProjectOutcome.Failed("Failed to delete cover image."),
                await With(writer).DeleteCoverImageAsync(folder));

            var errors = _log.At(LogLevel.Error).ToList();
            Assert.Equal(3, errors.Count);
            Assert.All(errors, e => Assert.Contains(Study, e));
        }

        [Fact]
        public async Task Narrator_only_during_a_Restore_is_a_conflict_with_the_gates_reason()
        {
            await CreateAsync("A Study in Scarlet");
            var folder = new ProjectFolderId(Study);
            Assert.Null(_activity.TryBeginRestore(folder));
            var reason = _activity.MayWrite(folder)!.Reason;

            var outcome = await UseCases.SetNarratorOnlyModeAsync(folder, true);

            Assert.Equal(new ProjectOutcome.Conflict(reason), outcome);
        }

        [Fact]
        public async Task A_metadata_edit_during_a_Restore_is_a_conflict_not_a_failure()
        {
            await CreateAsync("A Study in Scarlet");
            var folder = new ProjectFolderId(Study);
            Assert.Null(_activity.TryBeginRestore(folder));

            var outcome = await UseCases.UpdateMetadataAsync(folder, "Study in Scarlet", null, null);

            Assert.IsType<ProjectOutcome.Conflict>(outcome);
            Assert.Equal("A Study in Scarlet", (await ProjectAsync()).Title);
        }

        [Fact]
        public async Task Creating_a_duplicate_title_is_a_conflict_naming_it()
        {
            await CreateAsync("A Study in Scarlet");

            var outcome = await CreateAsync("A Study in Scarlet");

            Assert.Equal(new ProjectOutcome.Conflict($"A project named \"{Study}\" already exists."), outcome);
            Assert.Empty(_log.Entries);
        }

        [Fact]
        public async Task Creating_a_title_with_no_folder_name_is_refused()
        {
            var outcome = await CreateAsync("!!!");

            Assert.IsType<ProjectOutcome.Refused>(outcome);
            Assert.Empty(Directory.GetDirectories(TempDir));
        }

        [Fact]
        public async Task A_create_whose_file_will_not_save_fails_and_is_logged()
        {
            var outcome = await CreateAsync(With(new ScriptedWriter(fault: new IOException("Disk full."))), "A Study in Scarlet");

            Assert.Equal(new ProjectOutcome.Failed("Failed to save book file. Please try again."), outcome);
            Assert.Single(_log.At(LogLevel.Error));
        }

        [Fact]
        public async Task A_create_that_faults_unexpectedly_fails_generically_and_is_logged()
        {
            var outcome = await CreateAsync(With(new ScriptedWriter(fault: new InvalidOperationException("EF said no."))), "A Study in Scarlet");

            Assert.Equal(new ProjectOutcome.Failed("Failed to create project. Please try again."), outcome);
            Assert.Single(_log.At(LogLevel.Error));
        }

        [Fact]
        public async Task A_summaries_read_that_faults_is_logged()
        {
            var reader = new FaultingReader();
            var sp = _scope.ServiceProvider;
            var useCases = new ProjectUseCases(
                reader, sp.GetRequiredService<IProjectWriter>(), sp.GetRequiredService<Read2Me.Services.Mutations.BookMutations>(),
                sp.GetRequiredService<SeriesRegistry>(), _activity, _log);

            var result = await useCases.GetSummariesAsync();

            Assert.Equal("Failed to load projects.", result.Error);
            Assert.Single(_log.At(LogLevel.Error));
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
                sp.GetRequiredService<SeriesRegistry>(), _activity, _log);
        }

        /// <summary>A writer whose folder will not go: a file held open, a permissions fault.</summary>
        private sealed class UndeletableWriter(Action? onDelete = null) : IProjectWriter
        {
            public void DeleteProject(ProjectFolderId folderId)
            {
                onDelete?.Invoke();
                throw new IOException("The folder is in use.");
            }
            public Task<string> CreateProjectAsync(string title, string bookTitle, string author,
                string originalFileName, Stream fileStream, BookFileType fileType) => throw new NotSupportedException();
            public Task<BookMutationOutcome> UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author) => throw new NotSupportedException();
            public Task<BookMutationOutcome> SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream) => throw new NotSupportedException();
            public Task<BookMutationOutcome> DeleteCoverImageAsync(ProjectFolderId folderId) => throw new NotSupportedException();
        }

        /// <summary>A writer whose every write answers <paramref name="outcome"/>, or throws <paramref name="fault"/>.</summary>
        private sealed class ScriptedWriter(BookMutationOutcome? outcome = null, Exception? fault = null) : IProjectWriter
        {
            private Task<BookMutationOutcome> Answer() =>
                fault is null ? Task.FromResult(outcome ?? new BookMutationOutcome.NoChange()) : Task.FromException<BookMutationOutcome>(fault);

            public Task<string> CreateProjectAsync(string title, string bookTitle, string author,
                string originalFileName, Stream fileStream, BookFileType fileType) =>
                fault is null ? Task.FromResult(Study) : Task.FromException<string>(fault);
            public Task<BookMutationOutcome> UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author) => Answer();
            public Task<BookMutationOutcome> SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream) => Answer();
            public Task<BookMutationOutcome> DeleteCoverImageAsync(ProjectFolderId folderId) => Answer();
            public void DeleteProject(ProjectFolderId folderId) => throw new NotSupportedException();
        }

        /// <summary>A catalog reader whose summaries will not load.</summary>
        private sealed class FaultingReader : IProjectCatalogReader
        {
            public Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync() =>
                throw new IOException("The Workspace is offline.");
            public IReadOnlyList<string> GetProjects() => throw new NotSupportedException();
            public Task<Read2Me.Data.Entities.Project?> GetProjectAsync(ProjectFolderId folderId) => throw new NotSupportedException();
            public Task<NarratorIdentity> GetNarratorAsync(ProjectFolderId folderId, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<Read2Me.Data.Entities.StepCheckpoint>> GetStepCheckpointsAsync(ProjectFolderId folderId, CancellationToken ct = default) => throw new NotSupportedException();
        }

        /// <summary>Every project busy with whatever the test last set, or idle.</summary>
        private sealed class Busy(Func<BusySource?> answer) : IBusySource
        {
            public BusySource? BusyFor(ProjectFolderId folder) => answer();
        }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private ProjectUseCases UseCases => _scope.ServiceProvider.GetRequiredService<ProjectUseCases>();

        private Task<ProjectOutcome> CreateAsync(string title, string bookTitle = "A Study in Scarlet", string author = "Arthur Conan Doyle") =>
            CreateAsync(UseCases, title, bookTitle, author);

        private static async Task<ProjectOutcome> CreateAsync(ProjectUseCases useCases, string title, string bookTitle = "A Study in Scarlet", string author = "Arthur Conan Doyle")
        {
            using var file = new MemoryStream(Encoding.UTF8.GetBytes("In the year 1878 I took my degree of Doctor of Medicine."));
            return await useCases.CreateAsync(title, bookTitle, author, "study.txt", file, BookFileType.Text);
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
