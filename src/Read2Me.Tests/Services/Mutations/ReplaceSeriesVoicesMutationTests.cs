using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations
{
    /// <summary>
    /// One book's side of a push or take (#222): its linked Character's Voices become exactly the
    /// given set, under the same Ids, as one History step. The book's Voice Rules stay its own,
    /// except those on a Voice that goes.
    /// </summary>
    public class ReplaceSeriesVoicesMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        private static readonly Guid WatsonId = Guid.NewGuid();
        private static readonly Guid StamfordId = Guid.NewGuid();

        public ReplaceSeriesVoicesMutationTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
            _folder = new ProjectFolderId(FolderName);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
        {
            await using var scope = _root.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
        }

        private Task SeedAsync() =>
            new BookHierarchyBuilder(OpenDbAsync)
                .WithCharacter("watson", new Character { Id = WatsonId, Name = "Dr. Watson" })
                .WithCharacter("stamford", new Character { Id = StamfordId, Name = "Stamford" })
                .BuildAsync();

        private static SeriesVoiceCopy Copy(Guid id, string name, int day = 1) => new(
            id, name, "warm", VoiceSource.Generated, "A warm army doctor.", null, null,
            null, null, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc));

        private ReplaceSeriesVoicesMutation Sync(Guid characterId, params SeriesVoiceCopy[] voices) =>
            new(_folder, characterId, voices, "Take series voices", StepAction.Voice);

        private async Task<Guid> CreateVoiceAsync(Guid characterId, string name) =>
            ((BookMutationOutcome.Committed)await CommitAsync(new CreateVoiceMutation(_folder, characterId, name))).Receipt.Effects.CreatedId!.Value;

        [Fact]
        public async Task The_Character_holds_exactly_the_given_Voices_afterwards()
        {
            await SeedAsync();
            var kept = await CreateVoiceAsync(WatsonId, "Army doctor");
            var gone = await CreateVoiceAsync(WatsonId, "Restored");
            var gained = Guid.NewGuid();

            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(Sync(WatsonId, Copy(kept, "Old campaigner"), Copy(gained, "Retired"))));

            await using var db = await OpenDbAsync();
            var voices = await db.Voices.Where(v => v.CharacterId == WatsonId).OrderBy(v => v.Name).ToListAsync();
            Assert.Equal([(kept, "Old campaigner"), (gained, "Retired")], voices.Select(v => (v.Id, v.Name)));
            Assert.False(await db.Voices.AnyAsync(v => v.Id == gone));
        }

        [Fact]
        public async Task The_books_default_stays_its_own_and_moves_only_off_a_Voice_that_goes()
        {
            await SeedAsync();
            var first = await CreateVoiceAsync(WatsonId, "Army doctor");
            var second = await CreateVoiceAsync(WatsonId, "Old campaigner");
            await CommitAsync(new SetVoiceDefaultMutation(_folder, second));

            await CommitAsync(Sync(WatsonId, Copy(first, "Army doctor", day: 2), Copy(second, "Old campaigner", day: 1)));
            await using (var db = await OpenDbAsync())
                Assert.Equal(second, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);

            var later = Guid.NewGuid();
            var earlier = Guid.NewGuid();
            await CommitAsync(Sync(WatsonId, Copy(first, "Army doctor", day: 3), Copy(later, "Later", day: 2), Copy(earlier, "Earlier", day: 1)));
            await using (var db = await OpenDbAsync())
                Assert.Equal(earlier, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);
        }

        [Fact]
        public async Task A_positional_rule_on_a_Voice_that_goes_goes_with_it_and_the_rest_stay()
        {
            await SeedAsync();
            var kept = await CreateVoiceAsync(WatsonId, "Army doctor");
            var gone = await CreateVoiceAsync(WatsonId, "Restored");
            await CommitAsync(new CreateVoiceRuleMutation(_folder, WatsonId, kept, null, null, null, null));
            await CommitAsync(new CreateVoiceRuleMutation(_folder, WatsonId, gone, null, null, null, null));

            await CommitAsync(Sync(WatsonId, Copy(kept, "Army doctor")));

            await using var db = await OpenDbAsync();
            var positional = await db.VoiceRules.Where(r => r.CharacterId == WatsonId && !r.IsDefault).ToListAsync();
            Assert.Equal([kept], positional.Select(r => r.VoiceId));
        }

        [Fact]
        public async Task A_Character_with_no_Voices_gets_its_default_on_the_oldest_gained()
        {
            await SeedAsync();
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

            await CommitAsync(Sync(WatsonId, Copy(a, "Later", day: 2), Copy(b, "Earlier", day: 1)));

            await using var db = await OpenDbAsync();
            Assert.Equal(b, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);
        }

        [Fact]
        public async Task A_Voice_another_Character_of_the_book_holds_is_refused_and_nothing_changes()
        {
            await SeedAsync();
            var watsons = await CreateVoiceAsync(WatsonId, "Army doctor");
            var stamfords = await CreateVoiceAsync(StamfordId, "Stamford");

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(Sync(WatsonId, Copy(stamfords, "Stamford"))));

            Assert.Equal(BookMutationRejection.Conflict, rejected.Reason);
            await using var db = await OpenDbAsync();
            Assert.True(await db.Voices.AnyAsync(v => v.Id == watsons));
        }

        [Fact]
        public async Task It_is_one_History_step_with_its_label()
        {
            await SeedAsync();
            await using var scope = _root.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(Sync(WatsonId, Copy(Guid.NewGuid(), "Army doctor")) with { Label = "Push series voices" });

            var step = (await scope.ServiceProvider.GetRequiredService<ProjectHistory>().ListAsync(_folder)).Steps[0];
            Assert.Equal(("Push series voices", (StepAction?)StepAction.Voice), (step.Label, step.Action));
        }
    }
}
