using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.Characters;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class CharacterResolverTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly BookMutations _mutations;
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

            _mutations = sp.GetRequiredService<BookMutations>();
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

        /// <summary>Arrange-side writes, committed the way the app commits them (ADR 0007).</summary>
        private async Task<Guid> CreateCharacterAsync(string name) =>
            Assert.IsType<BookMutationOutcome.Committed>(
                await _mutations.CommitAsync(new CreateCharacterMutation(_folder, name)))
                .Receipt.Effects.CreatedId!.Value;

        private Task AddAliasAsync(Guid characterId, string alias) =>
            _mutations.CommitAsync(new AddCharacterAliasMutation(_folder, characterId, alias));

        [Fact]
        public async Task ResolveOrCreate_ExactName_ReturnsExisting()
        {
            await SeedProjectAsync();
            var existing = await CreateCharacterAsync("Bilbo");

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "Bilbo", CancellationToken.None);

            Assert.Equal(existing, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count); // Narrator + Bilbo
        }

        [Fact]
        public async Task ResolveOrCreate_DifferentCase_ReturnsExisting()
        {
            await SeedProjectAsync();
            var existing = await CreateCharacterAsync("Bilbo");

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "BILBO", CancellationToken.None);

            Assert.Equal(existing, resolved);
            var all = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, all.Count);
        }

        [Fact]
        public async Task ResolveOrCreate_MatchesAlias_ReturnsCanonical()
        {
            await SeedProjectAsync();
            var bilboId = await CreateCharacterAsync("Bilbo");
            await AddAliasAsync(bilboId, "Mr. Baggins");

            var resolved = await _resolver.ResolveOrCreateAsync(_folder, "mr. baggins", CancellationToken.None);

            Assert.Equal(bilboId, resolved);
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

        /// <summary>
        /// The same answer, with the write it did or did not make named. The commands endpoint
        /// reports the id either way; a caller that reads the outcome must not be told a Character
        /// was created when one was only found, or that nothing changed when one was created.
        /// </summary>
        [Fact]
        public async Task ResolveOrCreateWithOutcome_SaysWhetherItWrote()
        {
            await SeedProjectAsync();

            var created = await _resolver.ResolveOrCreateWithOutcomeAsync(
                _folder, "Gandalf", CancellationToken.None);
            var found = await _resolver.ResolveOrCreateWithOutcomeAsync(
                _folder, "gandalf", CancellationToken.None);

            var receipt = Assert.IsType<BookMutationOutcome.Committed>(created.Outcome).Receipt;
            Assert.Equal(created.Id, receipt.Effects.CreatedId);
            Assert.IsType<BookMutationOutcome.NoChange>(found.Outcome);
            Assert.Equal(created.Id, found.Id);
        }

        /// <summary>
        /// One discovery row, applied. Both producers of these rows — the review dialog and
        /// <c>POST /characters/discover/apply</c> — go through here, so a row is a resolve plus its
        /// aliases in exactly one place.
        /// </summary>
        [Fact]
        public async Task ApplyDiscovered_CreatesTheCharacterAndItsAliases()
        {
            await SeedProjectAsync();

            var outcome = await _resolver.ApplyDiscoveredAsync(
                _folder, "Gandalf", ["Mithrandir", "Greyhame"], CancellationToken.None);

            Assert.IsType<BookMutationOutcome.Committed>(outcome);

            var gandalf = (await _reader.GetCharactersWithAliasesAsync(_folder))
                .Single(c => c.Name == "Gandalf");
            Assert.Equal(["Greyhame", "Mithrandir"], gandalf.Aliases.Select(a => a.Name).Order());
        }

        /// <summary>
        /// The row the dialog already knew about: nobody new is created, the aliases still land, and
        /// the caller is told the roster did not gain a Character.
        /// </summary>
        [Fact]
        public async Task ApplyDiscovered_OnAKnownName_AddsTheAliasesWithoutCreatingASecondCharacter()
        {
            await SeedProjectAsync();
            var frodoId = await CreateCharacterAsync("Frodo");

            var outcome = await _resolver.ApplyDiscoveredAsync(
                _folder, "frodo", ["Ringbearer"], CancellationToken.None);

            Assert.IsType<BookMutationOutcome.NoChange>(outcome);

            var roster = await _reader.GetCharactersWithAliasesAsync(_folder);
            var frodo = Assert.Single(roster, c => c.Id == frodoId);
            Assert.Equal("Ringbearer", Assert.Single(frodo.Aliases).Name);
        }
    }
}
