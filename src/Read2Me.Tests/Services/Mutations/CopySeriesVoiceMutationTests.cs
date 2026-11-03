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
    /// One book's copy of a shared Voice edit (#221, ADR 0018): the whole Voice, added or
    /// overwritten under its Id, as a History step with the edit's label; and deleting a Voice moves
    /// the book's default to its oldest remaining Voice.
    /// </summary>
    public class CopySeriesVoiceMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        private static readonly Guid WatsonId = Guid.NewGuid();
        private static readonly Guid StamfordId = Guid.NewGuid();

        public CopySeriesVoiceMutationTests()
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

        private static SeriesVoiceCopy Copy(Guid id, string name, string? audio = null) => new(
            id, name, "warm", VoiceSource.Generated, "A warm army doctor.", "It is a capital mistake.", audio,
            "{\"cfg\":2}", "{\"temperature\":0.5}", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        private CopySeriesVoiceMutation CopyInto(Guid characterId, SeriesVoiceCopy copy) =>
            new(_folder, characterId, copy, "Edit voice", StepAction.Voice);

        private async Task<Guid> CreateVoiceAsync(Guid characterId, string name) =>
            ((BookMutationOutcome.Committed)await CommitAsync(new CreateVoiceMutation(_folder, characterId, name))).Receipt.Effects.CreatedId!.Value;

        [Fact]
        public async Task A_Voice_the_book_lacks_is_added_under_its_Id_and_is_the_default_of_a_Character_with_none()
        {
            await SeedAsync();
            var id = Guid.NewGuid();

            Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(CopyInto(WatsonId, Copy(id, "Army doctor"))));

            await using var db = await OpenDbAsync();
            var voice = await db.Voices.SingleAsync(v => v.Id == id);
            Assert.Equal((WatsonId, "Army doctor", "A warm army doctor."), (voice.CharacterId, voice.Name, voice.DesignPrompt));
            Assert.Equal(id, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);
        }

        [Fact]
        public async Task A_Voice_the_book_holds_has_every_shared_field_overwritten_and_keeps_the_books_default()
        {
            await SeedAsync();
            var own = await CreateVoiceAsync(WatsonId, "Watson");
            var shared = await CreateVoiceAsync(WatsonId, "Army doctor");

            await CommitAsync(CopyInto(WatsonId, Copy(shared, "Old campaigner", $"voices/{WatsonId}/{shared}-Army doctor.wav")));

            await using var db = await OpenDbAsync();
            var voice = await db.Voices.SingleAsync(v => v.Id == shared);
            Assert.Equal("Old campaigner", voice.Name);
            Assert.Equal("warm", voice.Description);
            Assert.Equal(VoiceSource.Generated, voice.Source);
            Assert.Equal("It is a capital mistake.", voice.Transcript);
            Assert.Equal($"voices/{WatsonId}/{shared}-Army doctor.wav", voice.AudioFileName);
            Assert.Equal("{\"cfg\":2}", voice.VoiceDesignSettingsOverrideJson);
            Assert.Equal("{\"temperature\":0.5}", voice.TtsSettingsOverrideJson);
            Assert.Equal(own, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);
        }

        [Fact]
        public async Task A_Voice_another_Character_of_the_book_holds_is_refused()
        {
            await SeedAsync();
            var stamfords = await CreateVoiceAsync(StamfordId, "Stamford");

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(await CommitAsync(CopyInto(WatsonId, Copy(stamfords, "Stamford"))));

            Assert.Equal(BookMutationRejection.Conflict, rejected.Reason);
            Assert.Contains("Stamford", rejected.Message);
        }

        [Fact]
        public async Task The_copy_is_a_History_step_with_the_edits_label()
        {
            await SeedAsync();
            await using var scope = _root.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(
                new CopySeriesVoiceMutation(_folder, WatsonId, Copy(Guid.NewGuid(), "Army doctor"), "Generate voice audio", StepAction.Voice));

            var step = (await scope.ServiceProvider.GetRequiredService<ProjectHistory>().ListAsync(_folder)).Steps[0];
            Assert.Equal(("Generate voice audio", (StepAction?)StepAction.Voice), (step.Label, step.Action));
        }

        [Fact]
        public async Task Deleting_the_default_Voice_moves_the_default_to_the_oldest_remaining_Voice_and_names_the_copied_step()
        {
            await SeedAsync();
            var first = await CreateVoiceAsync(WatsonId, "Army doctor");
            var second = await CreateVoiceAsync(WatsonId, "Old campaigner");
            var third = await CreateVoiceAsync(WatsonId, "Retired");
            await CommitAsync(new SetVoiceDefaultMutation(_folder, third));
            await CommitAsync(new DeleteVoiceMutation(_folder, first));

            await using (var scope = _root.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(
                    new DeleteVoiceMutation(_folder, third) { CopiedStep = ("Delete voice", StepAction.Delete) });
                var step = (await scope.ServiceProvider.GetRequiredService<ProjectHistory>().ListAsync(_folder)).Steps[0];
                Assert.Equal(("Delete voice", (StepAction?)StepAction.Delete), (step.Label, step.Action));
            }

            await using var db = await OpenDbAsync();
            Assert.Equal(second, (await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId && r.IsDefault)).VoiceId);
        }
    }
}
