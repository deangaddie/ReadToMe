using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Services.Voice;
using Read2Me.Tests.Infrastructure;
using VoiceEntity = Read2Me.Data.Entities.Voice;
using DataAnchorLevel = Read2Me.Data.Enums.VoiceAnchorLevel;
using Xunit;

namespace Read2Me.Tests.App.Audio
{
    /// <summary>
    /// Acceptance tests for Issue 001c: SplitAudio resolved-voice preview via IVoiceResolver.
    /// </summary>
    public class SplitAudioVoicePreviewTests : ProjectDbTestBase
    {
        private readonly VoiceResolver _resolver;
        private readonly ProjectFolderId _folder;

        public SplitAudioVoicePreviewTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _resolver = new VoiceResolver(session);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string FloorRank => OrderKeyGenerator.GenerateKeyBetween(null, null);

        // ── seed helpers ──────────────────────────────────────────────────────

        private async Task<(
            Guid VolId, Guid PartId, Guid ChId, Guid ParaId,
            Guid CharId, Guid DefaultVoiceId)> SeedMinimalAsync()
        {
            var charId = Guid.NewGuid();
            var voiceId = Guid.NewGuid();
            var character = new Character { Id = charId, Name = "Alice" };
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", character);
            await b.AddVolume("vol", v => v.AddPart("part", p => p.AddChapter("ch", c => c.AddParagraph("para"))))
                .BuildAsync();

            await using var db = await OpenDbAsync();
            db.Voices.Add(new VoiceEntity { Id = voiceId, CharacterId = charId, Name = "Default Voice", Source = VoiceSource.Uploaded, AudioFileName = "d.wav" });
            await db.SaveChangesAsync();

            return (b.VolumeId("vol"), b.PartId("part"), b.ChapterId("ch"), b.ParagraphId("para"), charId, voiceId);
        }

        private async Task<Guid> AddCharItemAsync(Guid paraId, Guid? charId, string text = "Hello")
        {
            await using var db = await OpenDbAsync();
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = paraId,
                Order = FloorRank,
                ItemType = ParagraphItemType.Character,
                Text = text,
                CharacterId = charId
            };
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        private async Task<Guid> AddNarratorItemAsync(Guid paraId, string text = "Narration")
        {
            await using var db = await OpenDbAsync();
            var narratorId = ProjectDbContext.NarratorId;
            if (!await db.VoiceRules.AnyAsync(r => r.CharacterId == narratorId))
            {
                var nVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = nVoiceId, CharacterId = narratorId, Name = "Narrator Voice", Source = VoiceSource.Uploaded, AudioFileName = "n.wav" });
                db.VoiceRules.Add(new VoiceRule { Id = Guid.NewGuid(), CharacterId = narratorId, VoiceId = nVoiceId, IsDefault = true, Rank = FloorRank });
                await db.SaveChangesAsync();
            }
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = paraId,
                Order = FloorRank,
                ItemType = ParagraphItemType.Narration,
                Text = text,
                CharacterId = null
            };
            db.ParagraphItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        private async Task AddDefaultRuleAsync(Guid charId, Guid voiceId)
        {
            await using var db = await OpenDbAsync();
            db.VoiceRules.Add(new VoiceRule { Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId, IsDefault = true, Rank = FloorRank });
            await db.SaveChangesAsync();
        }

        private async Task<Guid> AddPositionalRuleAsync(Guid charId, Guid voiceId,
            DataAnchorLevel fromLevel, Guid fromNodeId,
            DataAnchorLevel? toLevel = null, Guid? toNodeId = null, string? rank = null)
        {
            await using var db = await OpenDbAsync();
            var ruleId = Guid.NewGuid();
            rank ??= OrderKeyGenerator.GenerateKeyBetween(FloorRank, null);
            db.VoiceRules.Add(new VoiceRule
            {
                Id = ruleId,
                CharacterId = charId,
                VoiceId = voiceId,
                IsDefault = false,
                Rank = rank,
                FromLevel = fromLevel,
                FromNodeId = fromNodeId,
                ToLevel = toLevel,
                ToNodeId = toNodeId
            });
            await db.SaveChangesAsync();
            return ruleId;
        }

        // ── AC1+AC2: attributed character + narration items show resolved voice ──

        [Fact]
        public async Task ResolvedVoiceNames_DefaultRule_ReturnsDefaultVoiceForCharacterItem()
        {
            var (_, _, _, paraId, charId, voiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, voiceId);
            var itemId = await AddCharItemAsync(paraId, charId);

            var result = await _resolver.ResolveNamesAsync(_folder, [itemId]);

            Assert.True(result.ContainsKey(itemId));
            Assert.Equal("Default Voice", result[itemId]);
        }

        [Fact]
        public async Task ResolvedVoiceNames_NarrationItem_ReturnsNarratorVoice()
        {
            var (_, _, _, paraId, _, _) = await SeedMinimalAsync();
            var itemId = await AddNarratorItemAsync(paraId);

            var result = await _resolver.ResolveNamesAsync(_folder, [itemId]);

            Assert.True(result.ContainsKey(itemId));
            Assert.Equal("Narrator Voice", result[itemId]);
        }

        // ── AC3: positional rule wins over default for covered item ──────────

        [Fact]
        public async Task ResolvedVoiceNames_PositionalRule_WinsOverDefault()
        {
            var (_, _, chId, paraId, charId, defaultVoiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, defaultVoiceId);

            Guid posVoiceId;
            await using (var db = await OpenDbAsync())
            {
                posVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = posVoiceId, CharacterId = charId, Name = "Positional Voice", Source = VoiceSource.Uploaded, AudioFileName = "p.wav" });
                await db.SaveChangesAsync();
            }
            await AddPositionalRuleAsync(charId, posVoiceId, DataAnchorLevel.Chapter, chId);

            var itemId = await AddCharItemAsync(paraId, charId);

            var result = await _resolver.ResolveNamesAsync(_folder, [itemId]);

            Assert.Equal("Positional Voice", result[itemId]);
        }

        // ── AC4: unattributed character item → null (no voice, no error) ─────

        [Fact]
        public async Task ResolvedVoiceNames_UnattributedItem_ReturnsNull()
        {
            var (_, _, _, paraId, charId, voiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, voiceId);
            var itemId = await AddCharItemAsync(paraId, charId: null);

            var result = await _resolver.ResolveNamesAsync(_folder, [itemId]);

            var hasKey = result.TryGetValue(itemId, out var name);
            Assert.True(!hasKey || name == null);
        }

        // ── AC5: dangling rule doesn't surface as error; fallback voice shown ──

        [Fact]
        public async Task ResolvedVoiceNames_DanglingRule_FallsBackToDefault()
        {
            var (_, _, _, paraId, charId, defaultVoiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, defaultVoiceId);

            Guid posVoiceId;
            await using (var db = await OpenDbAsync())
            {
                posVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = posVoiceId, CharacterId = charId, Name = "Positional Voice", Source = VoiceSource.Uploaded, AudioFileName = "p.wav" });
                await db.SaveChangesAsync();
            }
            await AddPositionalRuleAsync(charId, posVoiceId, DataAnchorLevel.Chapter, Guid.NewGuid());

            var itemId = await AddCharItemAsync(paraId, charId);

            var result = await _resolver.ResolveNamesAsync(_folder, [itemId]);

            Assert.Equal("Default Voice", result[itemId]);
        }

        // ── 003a: staleness-after-mutation — resolver has no stale layer ─────

        [Fact]
        public async Task ResolveNamesAsync_RepointDefault_SecondCallReturnsNewVoiceName()
        {
            var (_, _, _, paraId, charId, defaultVoiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, defaultVoiceId);
            var itemId = await AddCharItemAsync(paraId, charId);

            var first = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("Default Voice", first[itemId]);

            // Repoint the default rule to a new voice
            Guid newVoiceId;
            await using (var db = await OpenDbAsync())
            {
                newVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = newVoiceId, CharacterId = charId, Name = "New Default Voice", Source = VoiceSource.Uploaded, AudioFileName = "nd.wav" });
                var rule = await db.VoiceRules.SingleAsync(r => r.CharacterId == charId && r.IsDefault);
                rule.VoiceId = newVoiceId;
                await db.SaveChangesAsync();
            }

            var second = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("New Default Voice", second[itemId]);
        }

        [Fact]
        public async Task ResolveNamesAsync_AddPositionalRule_SecondCallReturnsPositionalVoiceName()
        {
            var (_, _, chId, paraId, charId, defaultVoiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, defaultVoiceId);
            var itemId = await AddCharItemAsync(paraId, charId);

            var first = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("Default Voice", first[itemId]);

            Guid posVoiceId;
            await using (var db = await OpenDbAsync())
            {
                posVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = posVoiceId, CharacterId = charId, Name = "Positional Voice", Source = VoiceSource.Uploaded, AudioFileName = "pos.wav" });
                await db.SaveChangesAsync();
            }
            await AddPositionalRuleAsync(charId, posVoiceId, DataAnchorLevel.Chapter, chId);

            var second = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("Positional Voice", second[itemId]);
        }

        [Fact]
        public async Task ResolveNamesAsync_DeletePositionalRule_SecondCallFallsBackToDefault()
        {
            var (_, _, chId, paraId, charId, defaultVoiceId) = await SeedMinimalAsync();
            await AddDefaultRuleAsync(charId, defaultVoiceId);

            Guid posVoiceId;
            await using (var db = await OpenDbAsync())
            {
                posVoiceId = Guid.NewGuid();
                db.Voices.Add(new VoiceEntity { Id = posVoiceId, CharacterId = charId, Name = "Positional Voice", Source = VoiceSource.Uploaded, AudioFileName = "pos.wav" });
                await db.SaveChangesAsync();
            }
            var posRuleId = await AddPositionalRuleAsync(charId, posVoiceId, DataAnchorLevel.Chapter, chId);
            var itemId = await AddCharItemAsync(paraId, charId);

            var first = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("Positional Voice", first[itemId]);

            // Delete the positional rule
            await using (var db = await OpenDbAsync())
            {
                var rule = await db.VoiceRules.SingleAsync(r => r.Id == posRuleId);
                db.VoiceRules.Remove(rule);
                await db.SaveChangesAsync();
            }

            var second = await _resolver.ResolveNamesAsync(_folder, [itemId]);
            Assert.Equal("Default Voice", second[itemId]);
        }
    }
}
