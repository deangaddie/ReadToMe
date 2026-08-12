using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class AliasInjectionTests : ProjectDbTestBase
    {
        private readonly ProjectFolderId _folder;
        private readonly BookCommandHandler _handler;
        private readonly ProjectReader _reader;

        public AliasInjectionTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            services.AddScoped<ProjectReader>();
            services.AddScoped(sp => NullLogger<ProjectReader>.Instance);
            var sp = services.BuildServiceProvider();

            _handler = sp.GetRequiredService<BookCommandHandler>();
            _reader = sp.GetRequiredService<ProjectReader>();
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
        public async Task GetCharactersWithAliasesAsync_IncludesAliases()
        {
            await SeedProjectAsync();
            var charId = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));
            await _handler.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Mr. Baggins"));
            await _handler.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Old Bilbo"));

            var characters = await _reader.GetCharactersWithAliasesAsync(_folder);
            var bilbo = characters.Single(c => c.Name == "Bilbo");

            Assert.Equal(2, bilbo.Aliases.Count);
            Assert.Contains(bilbo.Aliases, a => a.Name == "Mr. Baggins");
            Assert.Contains(bilbo.Aliases, a => a.Name == "Old Bilbo");
        }

        [Fact]
        public async Task KnownCharactersJson_ContainsAliasesForEachCharacter()
        {
            await SeedProjectAsync();
            var charId = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));
            await _handler.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Mr. Baggins"));

            var characters = await _reader.GetCharactersWithAliasesAsync(_folder);
            var serialized = PromptTemplates.BuildKnownCharactersJson(
                characters.Select(c => new PromptTemplates.RosterCharacter(
                    c.Name, [.. c.Aliases.Select(a => a.Name)])));

            Assert.Contains("\"name\":\"Bilbo\"", serialized.Replace(" ", ""));
            Assert.Contains("\"Mr. Baggins\"", serialized);
        }

        [Fact]
        public async Task AliasMatch_ResolvesToCanonicalCharacter_NotDuplicate()
        {
            // Simulate the queue worker alias-matching logic.
            await SeedProjectAsync();
            var charId = await _handler.ExecuteAsync(new CreateCharacterCommand(_folder, "Bilbo"));
            await _handler.ExecuteAsync(new AddCharacterAliasCommand(_folder, charId!.Value, "Mr. Baggins"));

            var characters = await _reader.GetCharactersWithAliasesAsync(_folder);

            // LLM returned the alias name
            var llmName = "Mr. Baggins";
            var existing = characters.FirstOrDefault(c =>
                string.Equals(c.Name, llmName, StringComparison.OrdinalIgnoreCase) ||
                c.Aliases.Any(a => string.Equals(a.Name, llmName, StringComparison.OrdinalIgnoreCase)));

            Assert.NotNull(existing);
            Assert.Equal("Bilbo", existing!.Name);
            Assert.Equal(charId!.Value, existing.Id);

            // No duplicate character should be created when alias resolves
            var allCharacters = await _reader.GetCharactersAsync(_folder);
            Assert.Equal(2, allCharacters.Count); // Narrator + Bilbo only
        }
    }
}
