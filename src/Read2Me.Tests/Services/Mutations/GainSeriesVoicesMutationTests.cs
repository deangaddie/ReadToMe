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
    /// One book's half of the Voice merge on link (ADR 0018): the linked Character gains the series'
    /// Voices under the same Voice Ids, as one History step, and its own default rule is kept.
    /// </summary>
    public class GainSeriesVoicesMutationTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        private static readonly Guid WatsonId = Guid.NewGuid();
        private static readonly Guid NarratorId = Guid.NewGuid();
        private static readonly Guid StamfordId = Guid.NewGuid();

        public GainSeriesVoicesMutationTests()
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
                .WithCharacter("narrator", new Character { Id = NarratorId, Name = "Narrator", IsNarrator = true })
                .WithCharacter("watson", new Character { Id = WatsonId, Name = "Dr. Watson" })
                .WithCharacter("stamford", new Character { Id = StamfordId, Name = "Stamford" })
                .BuildAsync();

        private static SeriesVoiceCopy Copy(Guid id, string name, string? audio = null) => new(
            id, name, "warm", VoiceSource.Uploaded, null, "It is a capital mistake.", audio, null, "{\"temperature\":0.5}",
            new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));

        private async Task<Guid> CreateVoiceAsync(Guid characterId, string name) =>
            ((BookMutationOutcome.Committed)await CommitAsync(new CreateVoiceMutation(_folder, characterId, name))).Receipt.Effects.CreatedId!.Value;

        [Fact]
        public async Task The_Character_gains_each_Voice_under_its_own_Id_with_every_field_and_keeps_its_default()
        {
            await SeedAsync();
            var own = await CreateVoiceAsync(WatsonId, "Watson");
            var gained = Guid.NewGuid();

            var outcome = await CommitAsync(new GainSeriesVoicesMutation(
                _folder, WatsonId, [Copy(gained, "Army doctor", $"voices/{WatsonId}/{gained}-Army doctor.wav")]));

            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            await using var db = await OpenDbAsync();
            var voice = await db.Voices.SingleAsync(v => v.Id == gained);
            Assert.Equal(WatsonId, voice.CharacterId);
            Assert.Equal("Army doctor", voice.Name);
            Assert.Equal("warm", voice.Description);
            Assert.Equal("It is a capital mistake.", voice.Transcript);
            Assert.Equal($"voices/{WatsonId}/{gained}-Army doctor.wav", voice.AudioFileName);
            Assert.Equal("{\"temperature\":0.5}", voice.TtsSettingsOverrideJson);
            var rule = await db.VoiceRules.SingleAsync(r => r.CharacterId == WatsonId);
            Assert.True(rule.IsDefault);
            Assert.Equal(own, rule.VoiceId);
        }

        [Fact]
        public async Task A_Character_with_no_Voices_gets_a_default_rule_on_the_first_one_it_gains()
        {
            await SeedAsync();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await CommitAsync(new GainSeriesVoicesMutation(_folder, NarratorId, [Copy(first, "Narration"), Copy(second, "Narration (old)")]));

            await using var db = await OpenDbAsync();
            Assert.Equal(2, await db.Voices.CountAsync(v => v.CharacterId == NarratorId));
            var rule = await db.VoiceRules.SingleAsync(r => r.CharacterId == NarratorId);
            Assert.Equal(first, rule.VoiceId);
        }

        [Fact]
        public async Task A_Voice_the_Character_already_holds_is_not_copied_again()
        {
            await SeedAsync();
            var own = await CreateVoiceAsync(WatsonId, "Watson");

            var outcome = await CommitAsync(new GainSeriesVoicesMutation(_folder, WatsonId, [Copy(own, "Renamed")]));

            Assert.IsType<BookMutationOutcome.NoChange>(outcome);
        }

        [Fact]
        public async Task A_Voice_another_Character_of_the_book_holds_is_refused_naming_it()
        {
            await SeedAsync();
            var stamfords = await CreateVoiceAsync(StamfordId, "Stamford");

            var outcome = await CommitAsync(new GainSeriesVoicesMutation(_folder, WatsonId, [Copy(stamfords, "Stamford")]));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
            Assert.Equal(BookMutationRejection.Conflict, rejected.Reason);
            Assert.Contains("Stamford", rejected.Message);
        }

        [Fact]
        public async Task The_gain_is_one_History_step_named_for_the_link()
        {
            await SeedAsync();
            await using var scope = _root.CreateAsyncScope();

            await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(
                new GainSeriesVoicesMutation(_folder, WatsonId, [Copy(Guid.NewGuid(), "Army doctor")]));

            var history = await scope.ServiceProvider.GetRequiredService<ProjectHistory>().ListAsync(_folder);
            var step = history.Steps[0];
            Assert.Equal("Link series character", step.Label);
            Assert.Equal(StepAction.Voice, step.Action);
        }
    }
}
