using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;
using VoiceEntity = Read2Me.Data.Entities.Voice;
using CoreAnchorLevel = Read2Me.Core.Models.VoiceAnchorLevel;

namespace Read2Me.Tests.Services.Characters
{
    public class VoiceRuleCommandTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public VoiceRuleCommandTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();
            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        /// Seeds character + 2 voices + default VoiceRule (floor Rank).
        private async Task<(Guid CharId, Guid VoiceAId, Guid VoiceBId, Guid DefaultRuleId)> SeedCharacterWithTwoVoicesAsync()
        {
            await using var db = await OpenDbAsync();
            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "f.txt", Type = BookFileType.Text });

            var charId   = Guid.NewGuid();
            var voiceAId = Guid.NewGuid();
            var voiceBId = Guid.NewGuid();
            var defaultRuleId = Guid.NewGuid();

            db.Characters.Add(new Character { Id = charId, Name = "Alice" });
            db.Voices.Add(new VoiceEntity { Id = voiceAId, CharacterId = charId, Name = "Voice A", Source = VoiceSource.Uploaded, AudioFileName = "a.wav" });
            db.Voices.Add(new VoiceEntity { Id = voiceBId, CharacterId = charId, Name = "Voice B", Source = VoiceSource.Uploaded, AudioFileName = "b.wav" });
            db.VoiceRules.Add(new VoiceRule { Id = defaultRuleId, CharacterId = charId, VoiceId = voiceAId, IsDefault = true, Rank = "a0" });
            await db.SaveChangesAsync();
            return (charId, voiceAId, voiceBId, defaultRuleId);
        }

        // ── CreateVoiceRuleCommand ────────────────────────────────────────────

        [Fact]
        public async Task CreateVoiceRule_AppendsBelow_AllExistingRules()
        {
            var (charId, voiceAId, voiceBId, _) = await SeedCharacterWithTwoVoicesAsync();

            var ruleId1 = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            var ruleId2 = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceBId, null, null, null, null));

            Assert.NotNull(ruleId1);
            Assert.NotNull(ruleId2);

            await using var db = await OpenDbAsync();
            var rules = await db.VoiceRules
                .Where(r => r.CharacterId == charId)
                .OrderBy(r => r.Rank)
                .ToListAsync();

            Assert.Equal(3, rules.Count); // default + 2 new
            Assert.True(rules[0].IsDefault);
            Assert.Equal(ruleId1, rules[1].Id);
            Assert.Equal(ruleId2, rules[2].Id);
            // Each successive rank must be strictly greater.
            Assert.True(string.CompareOrdinal(rules[1].Rank, rules[0].Rank) > 0);
            Assert.True(string.CompareOrdinal(rules[2].Rank, rules[1].Rank) > 0);
        }

        [Fact]
        public async Task CreateVoiceRule_RejectsVoiceFromDifferentCharacter_ReturnsNull()
        {
            var (charId, _, _, _) = await SeedCharacterWithTwoVoicesAsync();

            await using var db = await OpenDbAsync();
            var otherCharId = Guid.NewGuid();
            var otherVoiceId = Guid.NewGuid();
            db.Characters.Add(new Character { Id = otherCharId, Name = "Bob" });
            db.Voices.Add(new VoiceEntity { Id = otherVoiceId, CharacterId = otherCharId, Name = "Voice C", Source = VoiceSource.Uploaded, AudioFileName = "c.wav" });
            await db.SaveChangesAsync();

            var result = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, otherVoiceId, null, null, null, null));

            Assert.Null(result);

            await using var db2 = await OpenDbAsync();
            var count = await db2.VoiceRules.CountAsync(r => r.CharacterId == charId);
            Assert.Equal(1, count); // only default
        }

        // ── DeleteVoiceRuleCommand ────────────────────────────────────────────

        [Fact]
        public async Task DeleteVoiceRule_RemovesNonDefaultRule()
        {
            var (charId, voiceAId, _, _) = await SeedCharacterWithTwoVoicesAsync();
            var ruleId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            Assert.NotNull(ruleId);

            await _svc.ExecuteAsync(new DeleteVoiceRuleCommand(_folder, ruleId!.Value));

            await using var db = await OpenDbAsync();
            var exists = await db.VoiceRules.AnyAsync(r => r.Id == ruleId.Value);
            Assert.False(exists);
        }

        [Fact]
        public async Task DeleteVoiceRule_RefusesToDeleteDefaultRule_NoOp()
        {
            var (charId, _, _, defaultRuleId) = await SeedCharacterWithTwoVoicesAsync();

            await _svc.ExecuteAsync(new DeleteVoiceRuleCommand(_folder, defaultRuleId));

            await using var db = await OpenDbAsync();
            var defaultStillExists = await db.VoiceRules
                .AnyAsync(r => r.Id == defaultRuleId && r.IsDefault);
            Assert.True(defaultStillExists);
        }

        // ── MoveVoiceRuleCommand ──────────────────────────────────────────────

        [Fact]
        public async Task MoveVoiceRule_Up_ReordersWithPredecessor()
        {
            var (charId, voiceAId, voiceBId, _) = await SeedCharacterWithTwoVoicesAsync();
            var ruleAId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            var ruleBId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceBId, null, null, null, null));
            Assert.NotNull(ruleAId); Assert.NotNull(ruleBId);

            // Before: default < A < B. Move B up → order becomes default < B < A.
            await _svc.ExecuteAsync(new MoveVoiceRuleCommand(_folder, ruleBId!.Value, RuleMoveDirection.Up));

            await using var db = await OpenDbAsync();
            var ranks = await db.VoiceRules
                .Where(r => r.CharacterId == charId && !r.IsDefault)
                .OrderBy(r => r.Rank)
                .Select(r => r.Id)
                .ToListAsync();

            Assert.Equal(2, ranks.Count);
            Assert.Equal(ruleBId!.Value, ranks[0]);
            Assert.Equal(ruleAId!.Value, ranks[1]);
        }

        [Fact]
        public async Task MoveVoiceRule_Up_TopMostNonDefault_IsNoOp()
        {
            var (charId, voiceAId, voiceBId, _) = await SeedCharacterWithTwoVoicesAsync();
            var ruleAId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            var ruleBId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceBId, null, null, null, null));
            Assert.NotNull(ruleAId); Assert.NotNull(ruleBId);

            // ruleA is already top-most non-default; move up should be no-op.
            await using var db0 = await OpenDbAsync();
            var rankBefore = (await db0.VoiceRules.FindAsync(ruleAId!.Value))!.Rank;

            await _svc.ExecuteAsync(new MoveVoiceRuleCommand(_folder, ruleAId!.Value, RuleMoveDirection.Up));

            await using var db = await OpenDbAsync();
            var rankAfter = (await db.VoiceRules.FindAsync(ruleAId!.Value))!.Rank;
            Assert.Equal(rankBefore, rankAfter);
        }

        [Fact]
        public async Task MoveVoiceRule_Down_BottomMostNonDefault_IsNoOp()
        {
            var (charId, voiceAId, voiceBId, _) = await SeedCharacterWithTwoVoicesAsync();
            var ruleAId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            var ruleBId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceBId, null, null, null, null));
            Assert.NotNull(ruleAId); Assert.NotNull(ruleBId);

            // ruleB is already bottom-most; move down should be no-op.
            await using var db0 = await OpenDbAsync();
            var rankBefore = (await db0.VoiceRules.FindAsync(ruleBId!.Value))!.Rank;

            await _svc.ExecuteAsync(new MoveVoiceRuleCommand(_folder, ruleBId!.Value, RuleMoveDirection.Down));

            await using var db = await OpenDbAsync();
            var rankAfter = (await db.VoiceRules.FindAsync(ruleBId!.Value))!.Rank;
            Assert.Equal(rankBefore, rankAfter);
        }

        // ── DeleteVoice cascade ───────────────────────────────────────────────

        [Fact]
        public async Task DeleteVoice_CascadesNonDefaultRules_RepointsDefault()
        {
            var (charId, voiceAId, voiceBId, defaultRuleId) = await SeedCharacterWithTwoVoicesAsync();

            // Create non-default rule targeting voiceA.
            var nonDefaultRuleId = await _svc.ExecuteAsync(
                new CreateVoiceRuleCommand(_folder, charId, voiceAId, null, null, null, null));
            Assert.NotNull(nonDefaultRuleId);

            // Default rule also targets voiceA. Delete voiceA → default repoints to voiceB, non-default cascade-deleted.
            await _svc.ExecuteAsync(new DeleteVoiceCommand(_folder, voiceAId));

            await using var db = await OpenDbAsync();

            // Non-default rule for voiceA must be gone.
            Assert.False(await db.VoiceRules.AnyAsync(r => r.Id == nonDefaultRuleId!.Value));

            // Default rule must still exist, repointed to voiceB.
            var defaultRule = await db.VoiceRules.FindAsync(defaultRuleId);
            Assert.NotNull(defaultRule);
            Assert.True(defaultRule.IsDefault);
            Assert.Equal(voiceBId, defaultRule.VoiceId);
        }
    }
}
