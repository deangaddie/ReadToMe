using FractionalIndexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Services.Voice;
using VoiceEntity = Read2Me.Data.Entities.Voice;
using DataAnchorLevel = Read2Me.Data.Enums.VoiceAnchorLevel;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Voice
{
    public class VoiceRuleReaderTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public VoiceRuleReaderTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null, string? next = null) =>
            OrderKeyGenerator.GenerateKeyBetween(prev, next);

        private static string FloorRank => OrderKeyGenerator.GenerateKeyBetween(null, null); // "a0"

        /// Seeds: 1 Volume → 1 Part → 2 Chapters, each with 1 Paragraph → 1 Item.
        private async Task<(
            Guid VolId, Guid PartId,
            Guid Ch1Id, Guid Para1Id, Guid Item1Id,
            Guid Ch2Id, Guid Para2Id, Guid Item2Id,
            Guid CharId, Guid VoiceAId, Guid VoiceBId)> SeedTwoChapterHierarchyAsync()
        {
            await using var db = await OpenDbAsync();

            db.Projects.Add(new Project { Title = "T", BookTitle = "B", Author = "A", Filename = "f.txt", Type = BookFileType.Text });

            var charId  = Guid.NewGuid();
            var voiceAId = Guid.NewGuid();
            var voiceBId = Guid.NewGuid();
            db.Characters.Add(new Character { Id = charId, Name = "Alice" });
            db.Voices.Add(new VoiceEntity { Id = voiceAId, CharacterId = charId, Name = "Voice A", Source = VoiceSource.Uploaded, AudioFileName = "a.wav" });
            db.Voices.Add(new VoiceEntity { Id = voiceBId, CharacterId = charId, Name = "Voice B", Source = VoiceSource.Uploaded, AudioFileName = "b.wav" });

            var vol   = new Volume  { Id = Guid.NewGuid(), Title = "V", Order = Key() };
            var part  = new Part    { Id = Guid.NewGuid(), VolumeId = vol.Id,  Order = Key() };
            var ch1   = new Chapter { Id = Guid.NewGuid(), PartId   = part.Id, Order = Key() };
            var ch2   = new Chapter { Id = Guid.NewGuid(), PartId   = part.Id, Order = Key(ch1.Order) };
            var para1 = new Paragraph    { Id = Guid.NewGuid(), ChapterId = ch1.Id, Order = Key() };
            var item1 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para1.Id, Order = Key(), ItemType = ParagraphItemType.Character, Text = "Line 1", CharacterId = charId };
            var para2 = new Paragraph    { Id = Guid.NewGuid(), ChapterId = ch2.Id, Order = Key() };
            var item2 = new ParagraphItem { Id = Guid.NewGuid(), ParagraphId = para2.Id, Order = Key(), ItemType = ParagraphItemType.Character, Text = "Line 2", CharacterId = charId };

            db.Volumes.Add(vol); db.Parts.Add(part);
            db.Chapters.Add(ch1); db.Chapters.Add(ch2);
            db.Paragraphs.Add(para1); db.ParagraphItems.Add(item1);
            db.Paragraphs.Add(para2); db.ParagraphItems.Add(item2);

            await db.SaveChangesAsync();
            return (vol.Id, part.Id, ch1.Id, para1.Id, item1.Id, ch2.Id, para2.Id, item2.Id, charId, voiceAId, voiceBId);
        }

        private async Task SeedDefaultRule(Guid charId, Guid voiceId)
        {
            await using var db = await OpenDbAsync();
            db.VoiceRules.Add(new VoiceRule { Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceId, IsDefault = true, Rank = FloorRank });
            await db.SaveChangesAsync();
        }

        private async Task SeedChapterRule(Guid charId, Guid voiceId, string rank, Guid chapterId, bool fromHereOn = false)
        {
            await using var db = await OpenDbAsync();
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(),
                CharacterId = charId, VoiceId = voiceId,
                IsDefault = false, Rank = rank,
                FromLevel = DataAnchorLevel.Chapter, FromNodeId = chapterId,
                ToLevel = fromHereOn ? null : DataAnchorLevel.Chapter,
                ToNodeId = fromHereOn ? null : chapterId,
            });
            await db.SaveChangesAsync();
        }

        // ── StoryPosition of item ─────────────────────────────────────────────

        [Fact]
        public async Task GetVoiceRuleInputs_ReturnsCorrectItemPosition()
        {
            var (_, _, ch1Id, _, item1Id, _, _, _, charId, voiceAId, _) = await SeedTwoChapterHierarchyAsync();
            await SeedDefaultRule(charId, voiceAId);

            var (pos, _) = await _reader.GetVoiceRuleInputsAsync(_folder, item1Id, charId);

            Assert.NotEqual(default, pos);
        }

        // ── Chapter-level anchor span ─────────────────────────────────────────

        [Fact]
        public async Task ChapterAnchor_SpanCoversItemsInThatChapter_NotSiblings()
        {
            var (_, _, ch1Id, _, item1Id, ch2Id, _, item2Id, charId, voiceAId, voiceBId) = await SeedTwoChapterHierarchyAsync();
            await SeedDefaultRule(charId, voiceAId);
            await SeedChapterRule(charId, voiceBId, Key(FloorRank), ch1Id); // "just ch1"

            // item1 is in ch1 → rule covers it → VoiceB wins
            var (pos1, rules1) = await _reader.GetVoiceRuleInputsAsync(_folder, item1Id, charId);
            Assert.Equal(voiceBId, VoiceRuleEvaluator.Evaluate(rules1, pos1));

            // item2 is in ch2 → rule does NOT cover it → VoiceA (default) wins
            var (pos2, rules2) = await _reader.GetVoiceRuleInputsAsync(_folder, item2Id, charId);
            Assert.Equal(voiceAId, VoiceRuleEvaluator.Evaluate(rules2, pos2));
        }

        // ── Dangling anchor ───────────────────────────────────────────────────

        [Fact]
        public async Task DanglingAnchor_RuleFlaggedDangling_NotThrowing()
        {
            var (_, _, _, _, item1Id, _, _, _, charId, voiceAId, voiceBId) = await SeedTwoChapterHierarchyAsync();
            await SeedDefaultRule(charId, voiceAId);

            // Rule with a non-existent chapter id → dangling
            await using var db = await OpenDbAsync();
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceBId,
                IsDefault = false, Rank = Key(FloorRank),
                FromLevel = DataAnchorLevel.Chapter, FromNodeId = Guid.NewGuid(), // non-existent
                ToLevel = DataAnchorLevel.Chapter, ToNodeId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();

            var (pos, rules) = await _reader.GetVoiceRuleInputsAsync(_folder, item1Id, charId);
            var dangling = rules.First(r => !r.IsDefault);
            Assert.True(dangling.IsDangling);
            // Evaluation falls through to default
            Assert.Equal(voiceAId, VoiceRuleEvaluator.Evaluate(rules, pos));
        }

        // ── ParagraphItem-level anchor ────────────────────────────────────────

        [Fact]
        public async Task ParagraphItemAnchor_SinglePositionSpan()
        {
            var (_, _, _, _, item1Id, _, _, item2Id, charId, voiceAId, voiceBId) = await SeedTwoChapterHierarchyAsync();
            await SeedDefaultRule(charId, voiceAId);

            await using var db = await OpenDbAsync();
            db.VoiceRules.Add(new VoiceRule
            {
                Id = Guid.NewGuid(), CharacterId = charId, VoiceId = voiceBId,
                IsDefault = false, Rank = Key(FloorRank),
                FromLevel = DataAnchorLevel.ParagraphItem, FromNodeId = item1Id,
                ToLevel = DataAnchorLevel.ParagraphItem, ToNodeId = item1Id,
            });
            await db.SaveChangesAsync();

            var (pos1, rules1) = await _reader.GetVoiceRuleInputsAsync(_folder, item1Id, charId);
            var (pos2, rules2) = await _reader.GetVoiceRuleInputsAsync(_folder, item2Id, charId);

            Assert.Equal(voiceBId, VoiceRuleEvaluator.Evaluate(rules1, pos1)); // item1: covered
            Assert.Equal(voiceAId, VoiceRuleEvaluator.Evaluate(rules2, pos2)); // item2: not covered
        }

        // ── From-here-on rule ─────────────────────────────────────────────────

        [Fact]
        public async Task FromHereOnChapterRule_MatchesCurrentAndAfter_NotBefore()
        {
            var (_, _, _, _, item1Id, ch2Id, _, item2Id, charId, voiceAId, voiceBId) = await SeedTwoChapterHierarchyAsync();
            await SeedDefaultRule(charId, voiceAId);
            await SeedChapterRule(charId, voiceBId, Key(FloorRank), ch2Id, fromHereOn: true); // from ch2 onward

            // item1 is before ch2 → default
            var (pos1, rules1) = await _reader.GetVoiceRuleInputsAsync(_folder, item1Id, charId);
            Assert.Equal(voiceAId, VoiceRuleEvaluator.Evaluate(rules1, pos1));

            // item2 is in ch2 → from-here-on rule covers it
            var (pos2, rules2) = await _reader.GetVoiceRuleInputsAsync(_folder, item2Id, charId);
            Assert.Equal(voiceBId, VoiceRuleEvaluator.Evaluate(rules2, pos2));
        }
    }
}
