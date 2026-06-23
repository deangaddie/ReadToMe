using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class CharacterResolverTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly BookCommandHandler _handler;
        private readonly ProjectReader _reader;
        private readonly CharacterResolver _resolver;

        public CharacterResolverTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectReader>();
            services.AddScoped(sp => NullLogger<ProjectReader>.Instance);
            services.AddScoped<CharacterResolver>();
            var sp = services.BuildServiceProvider();

            _handler = sp.GetRequiredService<BookCommandHandler>();
            _reader = sp.GetRequiredService<ProjectReader>();
            _resolver = sp.GetRequiredService<CharacterResolver>();
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task SeedProjectAsync()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project
            {
                Title = "T", BookTitle = "B", Author = "A",
                Filename = "t.epub", Type = BookFileType.Epub
            });
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task ResolveOrCreate_ExactName_ReturnsExisting()
        {
            await SeedProjectAsync();
            var existing = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "Bilbo", CancellationToken.None);

            Assert.Equal(existing!.Value, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count); // Narrator + Bilbo
        }

        [Fact]
        public async Task ResolveOrCreate_DifferentCase_ReturnsExisting()
        {
            await SeedProjectAsync();
            var existing = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "BILBO", CancellationToken.None);

            Assert.Equal(existing!.Value, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public async Task ResolveOrCreate_MatchesAlias_ReturnsCanonical()
        {
            await SeedProjectAsync();
            var bilboId = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));
            await _handler.ExecuteAsync(new AddCharacterAliasCommand(_folder, bilboId!.Value, "Mr. Baggins"));

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "mr. baggins", CancellationToken.None);

            Assert.Equal(bilboId.Value, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public async Task ResolveOrCreate_NoMatch_CreatesNew()
        {
            await SeedProjectAsync();

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "Gandalf", CancellationToken.None);

            Assert.NotEqual(Guid.Empty, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count); // Narrator + Gandalf
        }

        [Fact]
        public async Task ResolveOrCreate_NoMatch_DoesNotDuplicateOnSecondCall()
        {
            await SeedProjectAsync();

            var first = await _resolver.ResolveOrCreateAsync(_folder, "Gandalf", CancellationToken.None);
            var second = await _resolver.ResolveOrCreateAsync(_folder, "Gandalf", CancellationToken.None);

            Assert.Equal(first, second);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count);
        }
    }
}
