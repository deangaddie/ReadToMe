using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Books;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services
{
    public class BookCommandHandler : IBookCommandHandler
    {
        private readonly ProjectDbSession _session;
        private readonly IFileSystem _fs;

        public BookCommandHandler(ProjectDbSession session, IFileSystem fs)
        {
            _session = session;
            _fs = fs;
        }

        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            switch (command)
            {
                case DeleteVolumeCommand c: await DeleteEntityAsync<Volume>(c.FolderId, c.VolumeId); break;
                case DeletePartCommand c: await DeleteEntityAsync<Part>(c.FolderId, c.PartId); break;
                case DeleteChapterCommand c: await DeleteEntityAsync<Chapter>(c.FolderId, c.ChapterId); break;
                case DeleteParagraphCommand c: await DeleteEntityAsync<Paragraph>(c.FolderId, c.ParagraphId); break;
                case DeleteParagraphItemCommand c: await DeleteEntityAsync<ParagraphItem>(c.FolderId, c.ItemId); break;
                case UpdateVolumeTitleCommand c: await UpdateTitleAsync<Volume>(c.FolderId, c.VolumeId, v => v.Title = c.Title); break;
                case UpdatePartTitleCommand c: await UpdateTitleAsync<Part>(c.FolderId, c.PartId, p => p.Title = c.Title); break;
                case UpdateChapterTitleCommand c: await UpdateTitleAsync<Chapter>(c.FolderId, c.ChapterId, ch => ch.Title = c.Title); break;
                case UpdateParagraphItemTextCommand c: await UpdateTitleAsync<ParagraphItem>(c.FolderId, c.ItemId, i => i.Text = c.Text); break;
                case SplitAtPartCommand c: return await SplitVolumeAsync(c.FolderId, c.PartId, c.NewVolumeTitle);
                case SplitAtChapterCommand c: return await SplitPartAsync(c.FolderId, c.ChapterId, c.NewPartTitle);
                case SplitAtParagraphCommand c: return await SplitChapterAsync(c.FolderId, c.ParagraphId, c.NewChapterTitle);
                case SplitAtItemCommand c: return await SplitParagraphItemAsync(c.FolderId, c.ItemId);
                case MergeVolumeCommand c:
                    await PlanAndApplyAsync(c.FolderId, h => h.PlanMergeVolume(c.VolumeId, c.Direction)); break;
                case MergePartCommand c:
                    await PlanAndApplyAsync(c.FolderId, h => h.PlanMergePart(c.PartId, c.Direction)); break;
                case MergeChapterCommand c:
                    await PlanAndApplyAsync(c.FolderId, h => h.PlanMergeChapter(c.ChapterId, c.Direction)); break;
                case MergeParagraphCommand c:
                    await PlanAndApplyAsync(c.FolderId, h => h.PlanMergeParagraph(c.ParagraphId, c.Direction)); break;
                case MergeParagraphItemCommand c:
                    await PlanAndApplyAsync(c.FolderId, h => h.PlanMergeParagraphItem(c.ItemId, c.Direction)); break;
                case SetItemCharacterCommand c: await SetParagraphItemCharacterAsync(c.FolderId, c.ItemId, c.CharacterId); break;
                case CreateCharacterCommand c: return await CreateCharacterAsync(c.FolderId, c.Name);
                case SetParagraphCharacterCommand c: await SetParagraphCharacterAsync(c.FolderId, c.ParagraphId, c.CharacterId, c.VoiceInstructions); break;
                case AddCharacterAliasCommand c: await AddCharacterAliasAsync(c.FolderId, c.CharacterId, c.Name); break;
                case RemoveCharacterAliasCommand c: await RemoveCharacterAliasAsync(c.FolderId, c.AliasId); break;
                case MergeCharactersCommand c: await MergeCharactersAsync(c.FolderId, c.SurvivorId, c.MergedId, c.AddNameAsAlias); break;
                case DeleteCharacterCommand c: await DeleteCharacterAsync(c.FolderId, c.CharacterId); break;
                case CreateVoiceCommand c: return await CreateVoiceAsync(c.FolderId, c.CharacterId, c.Name, c.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded);
                case SetVoiceDefaultCommand c: await SetVoiceDefaultAsync(c.FolderId, c.VoiceId); break;
                case UpdateVoiceCommand c: await UpdateVoiceAsync(c.FolderId, c.VoiceId, c.Name, c.Description); break;
                case SetVoiceDesignPromptCommand c: await UpdateVoiceFieldAsync(c.FolderId, c.VoiceId, v => v.DesignPrompt = c.Prompt); break;
                case SetVoiceSettingsOverrideCommand c: await UpdateVoiceFieldAsync(c.FolderId, c.VoiceId, v => v.SettingsOverrideJson = c.Json); break;
                case SetVoiceTranscriptCommand c: await UpdateVoiceFieldAsync(c.FolderId, c.VoiceId, v => v.Transcript = c.Transcript); break;
                case SetVoiceAudioCommand c: await UpdateVoiceFieldAsync(c.FolderId, c.VoiceId, v => v.AudioFileName = c.AudioFileName); break;
                case SetVoiceSourceCommand c: await SetVoiceSourceAsync(c.FolderId, c.VoiceId, c.IsGenerated ? VoiceSource.Generated : VoiceSource.Uploaded); break;
                case DeleteVoiceCommand c: await DeleteVoiceAsync(c.FolderId, c.VoiceId); break;
                case AddBookTitleCommand c: await AddBookTitleAsync(c.FolderId); break;
                case AddVolumeTitlesCommand c: await AddVolumeTitlesAsync(c.FolderId); break;
                case AddPartTitlesCommand c: await AddPartTitlesAsync(c.FolderId); break;
                case AddChapterTitlesCommand c: await AddChapterTitlesAsync(c.FolderId); break;
                case AddPausesCommand c: await AddPausesAsync(c.FolderId); break;
                case InsertPauseParagraphCommand c: await InsertPauseParagraphAsync(c); break;
                case ClearBookContentCommand c: await ClearBookContentAsync(c.FolderId); break;
                default: throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");
            }
            return null;
        }

        private async Task DeleteEntityAsync<TEntity>(ProjectFolderId folderId, Guid id)
            where TEntity : class
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Set<TEntity>().FindAsync(id);
            if (entity == null) return;
            db.Set<TEntity>().Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task UpdateTitleAsync<TEntity>(
            ProjectFolderId folderId, Guid id, Action<TEntity> apply)
            where TEntity : class
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Set<TEntity>().FindAsync(id);
            if (entity == null) return;
            apply(entity);
            await db.SaveChangesAsync();
        }

        private async Task SetParagraphItemCharacterAsync(ProjectFolderId folderId, Guid itemId, Guid? characterId)
        {
            var db = await _session.OpenAsync(folderId);
            var item = await db.ParagraphItems.Include(i => i.Character).FirstOrDefaultAsync(i => i.Id == itemId);
            if (item == null) return;
            item.CharacterId = characterId;
            item.Character = characterId.HasValue
                ? await db.Characters.FindAsync(characterId.Value)
                : null;
            await db.SaveChangesAsync();
        }

        private async Task<Guid?> CreateCharacterAsync(ProjectFolderId folderId, string name)
        {
            var db = await _session.OpenAsync(folderId);
            var existing = await db.Characters.FirstOrDefaultAsync(c => c.Name == name);
            if (existing != null) return existing.Id;
            var character = new Character { Id = Guid.NewGuid(), Name = name };
            db.Characters.Add(character);
            await db.SaveChangesAsync();
            return character.Id;
        }

        private async Task SetParagraphCharacterAsync(
            ProjectFolderId folderId, Guid paragraphId, Guid? characterId, string? voiceInstructions)
        {
            var db = await _session.OpenAsync(folderId);
            var items = await db.ParagraphItems
                .Where(i => i.ParagraphId == paragraphId && i.ItemType == ParagraphItemType.Character)
                .ToListAsync();
            foreach (var item in items)
            {
                item.CharacterId = characterId;
                if (characterId.HasValue && voiceInstructions != null)
                    item.VoiceInstructions = voiceInstructions;
            }
            await db.SaveChangesAsync();
        }

        private async Task<Guid?> SplitVolumeAsync(ProjectFolderId folderId, Guid partId, string? newTitle)
        {
            var mutation = await PlanAndApplyAsync(folderId, h => h.PlanSplitVolume(partId, newTitle));
            return mutation != null ? ((Volume)mutation.ToAdd[0]).Id : null;
        }

        private async Task<Guid?> SplitPartAsync(ProjectFolderId folderId, Guid chapterId, string? newTitle)
        {
            var mutation = await PlanAndApplyAsync(folderId, h => h.PlanSplitPart(chapterId, newTitle));
            return mutation != null ? ((Part)mutation.ToAdd[0]).Id : null;
        }

        private async Task<Guid?> SplitChapterAsync(ProjectFolderId folderId, Guid paragraphId, string? newTitle)
        {
            var mutation = await PlanAndApplyAsync(folderId, h => h.PlanSplitChapter(paragraphId, newTitle));
            return mutation != null ? ((Chapter)mutation.ToAdd[0]).Id : null;
        }

        private async Task<Guid?> SplitParagraphAsync(ProjectFolderId folderId, Guid itemId, string? newTitle)
        {
            var mutation = await PlanAndApplyAsync(folderId, h => h.PlanSplitParagraph(itemId));
            return mutation != null ? ((Paragraph)mutation.ToAdd[0]).Id : null;
        }

        private async Task<Guid?> SplitParagraphItemAsync(ProjectFolderId folderId, Guid itemId)
        {
            return await SplitParagraphAsync(folderId, itemId, null);
        }

        private async Task AddBookTitleAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var project = await db.Projects.SingleOrDefaultAsync();
            if (project == null) return;
            var h = await LoadBookHierarchyAsync(db);
            var plan = h.PlanFrontMatterInsert();
            if (plan == null) return;
            var (mutation, chapterId, _) = plan.Value;
            await ApplyMutationAsync(db, mutation);
            var titlePara = TitleInserter.AddTitleParagraph(db, chapterId, project.BookTitle, null);
            TitleInserter.AddTitleParagraphAfter(db, chapterId, $"By {project.Author}", titlePara.Order);
            await db.SaveChangesAsync();
        }

        private async Task AddVolumeTitlesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            foreach (var (_, title, newChapter, _) in h.PlanVolumeTitleChapters())
            {
                db.Chapters.Add(newChapter);
                TitleInserter.AddTitleParagraph(db, newChapter.Id, title, null);
            }
            await db.SaveChangesAsync();
        }

        private async Task AddPartTitlesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            foreach (var (_, title, newChapter, _) in h.PlanPartTitleChapters())
            {
                db.Chapters.Add(newChapter);
                TitleInserter.AddTitleParagraph(db, newChapter.Id, title, null);
            }
            await db.SaveChangesAsync();
        }

        private async Task AddChapterTitlesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            foreach (var (chapterId, title, firstParagraphOrder) in h.PlanChapterTitleInsertions())
                TitleInserter.AddTitleParagraph(db, chapterId, title, firstParagraphOrder);
            await db.SaveChangesAsync();
        }

        private static ParagraphItemType MapPauseKind(PauseKind kind) => kind switch
        {
            PauseKind.Pause          => ParagraphItemType.Pause,
            PauseKind.ParagraphPause => ParagraphItemType.ParagraphPause,
            PauseKind.ChapterPause   => ParagraphItemType.ChapterPause,
            PauseKind.PartPause      => ParagraphItemType.PartPause,
            PauseKind.VolumePause    => ParagraphItemType.VolumePause,
            _                        => ParagraphItemType.Pause,
        };

        private async Task InsertPauseParagraphAsync(InsertPauseParagraphCommand c)
        {
            var db = await _session.OpenAsync(c.FolderId);
            var item = await db.ParagraphItems.FindAsync(c.AnchorItemId);
            if (item == null) return;
            var paragraph = await db.Paragraphs
                .Include(p => p.Items)
                .FirstOrDefaultAsync(p => p.Id == item.ParagraphId);
            if (paragraph == null) return;

            var siblings = await db.Paragraphs
                .Where(p => p.ChapterId == paragraph.ChapterId)
                .OrderBy(p => p.Order)
                .ToListAsync();

            var idx = siblings.FindIndex(p => p.Id == paragraph.Id);
            if (idx < 0) return;

            string? afterOrder, beforeOrder;
            if (c.Position == PauseInsertPosition.Before)
            {
                afterOrder  = idx > 0 ? siblings[idx - 1].Order : null;
                beforeOrder = paragraph.Order;
            }
            else
            {
                afterOrder  = paragraph.Order;
                beforeOrder = idx < siblings.Count - 1 ? siblings[idx + 1].Order : null;
            }

            PauseInserter.AddPauseParagraph(db, paragraph.ChapterId, MapPauseKind(c.PauseKind), afterOrder, beforeOrder);
            await db.SaveChangesAsync();
        }

        private async Task AddPausesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            foreach (var p in h.PlanPauseInsertions())
                PauseInserter.AddPauseParagraph(db, p.ChapterId, p.PauseType, p.AfterOrder, p.BeforeOrder);
            await db.SaveChangesAsync();
        }

        private async Task ClearBookContentAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.ParagraphItems.ExecuteDeleteAsync();
            await db.Paragraphs.ExecuteDeleteAsync();
            await db.Chapters.ExecuteDeleteAsync();
            await db.Parts.ExecuteDeleteAsync();
            await db.Volumes.ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        private async Task AddCharacterAliasAsync(ProjectFolderId folderId, Guid characterId, string name)
        {
            var db = await _session.OpenAsync(folderId);
            var character = await db.Characters
                .Include(c => c.Aliases)
                .FirstOrDefaultAsync(c => c.Id == characterId);
            if (character == null) return;

            var alreadyExists =
                string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase) ||
                character.Aliases.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (alreadyExists) return;

            db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = characterId, Name = name });
            await db.SaveChangesAsync();
        }

        private async Task RemoveCharacterAliasAsync(ProjectFolderId folderId, Guid aliasId)
        {
            var db = await _session.OpenAsync(folderId);
            var alias = await db.CharacterAliases.FindAsync(aliasId);
            if (alias == null) return;
            db.CharacterAliases.Remove(alias);
            await db.SaveChangesAsync();
        }

        private async Task MergeCharactersAsync(ProjectFolderId folderId, Guid survivorId, Guid mergedId, bool addNameAsAlias)
        {
            if (mergedId == ProjectDbContext.NarratorId || survivorId == ProjectDbContext.NarratorId) return;

            var db = await _session.OpenAsync(folderId);
            await using var tx = await db.Database.BeginTransactionAsync();

            var merged = await db.Characters.Include(c => c.Aliases).FirstOrDefaultAsync(c => c.Id == mergedId);
            if (merged == null) { await tx.RollbackAsync(); return; }

            await db.ParagraphItems
                .Where(i => i.CharacterId == mergedId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, survivorId));

            await db.Paragraphs
                .Where(p => p.CharacterId == mergedId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CharacterId, survivorId));

            // Move aliases to survivor
            await db.CharacterAliases
                .Where(a => a.CharacterId == mergedId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CharacterId, survivorId));

            if (addNameAsAlias)
            {
                // Re-query survivor aliases from DB (ExecuteUpdateAsync above already moved merged aliases there).
                var survivorAliasNames = await db.CharacterAliases
                    .Where(a => a.CharacterId == survivorId)
                    .Select(a => a.Name.ToLower())
                    .ToListAsync();
                var survivorNameLower = (await db.Characters.Where(c => c.Id == survivorId).Select(c => c.Name).FirstAsync()).ToLower();

                void AddIfAbsent(string name)
                {
                    if (!string.Equals(survivorNameLower, name, StringComparison.OrdinalIgnoreCase) &&
                        !survivorAliasNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        db.CharacterAliases.Add(new CharacterAlias { Id = Guid.NewGuid(), CharacterId = survivorId, Name = name });
                        survivorAliasNames.Add(name.ToLower());
                    }
                }

                AddIfAbsent(merged.Name);
                foreach (var alias in merged.Aliases)
                    AddIfAbsent(alias.Name);

                await db.SaveChangesAsync();
            }

            // Use bulk delete to avoid EF tracker conflicts after ExecuteUpdateAsync moved the aliases.
            await db.Characters
                .Where(c => c.Id == mergedId)
                .ExecuteDeleteAsync();

            await tx.CommitAsync();
        }

        private async Task DeleteCharacterAsync(ProjectFolderId folderId, Guid characterId)
        {
            if (characterId == ProjectDbContext.NarratorId) return;

            var db = await _session.OpenAsync(folderId);
            if (!await db.Characters.AnyAsync(c => c.Id == characterId)) return;

            await using var tx = await db.Database.BeginTransactionAsync();

            await db.ParagraphItems
                .Where(i => i.CharacterId == characterId)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.CharacterId, (Guid?)null));

            await db.Paragraphs
                .Where(p => p.CharacterId == characterId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CharacterId, (Guid?)null));

            await db.CharacterAliases
                .Where(a => a.CharacterId == characterId)
                .ExecuteDeleteAsync();

            await db.Characters
                .Where(c => c.Id == characterId)
                .ExecuteDeleteAsync();

            await tx.CommitAsync();
        }

        private async Task<Guid?> CreateVoiceAsync(ProjectFolderId folderId, Guid characterId, string name, Data.Enums.VoiceSource source = Data.Enums.VoiceSource.Uploaded)
        {
            var db = await _session.OpenAsync(folderId);
            var character = await db.Characters
                .Include(c => c.Voices)
                .FirstOrDefaultAsync(c => c.Id == characterId);
            if (character == null) return null;

            var isFirst = !character.Voices.Any();
            var effectiveName = string.IsNullOrWhiteSpace(name) ? character.Name : name.Trim();
            var voice = new VoiceEntity
            {
                Id = Guid.NewGuid(),
                CharacterId = characterId,
                Name = effectiveName,
                IsDefault = isFirst,
                Source = source,
                CreatedUtc = DateTime.UtcNow,
            };
            db.Voices.Add(voice);
            await db.SaveChangesAsync();
            return voice.Id;
        }

        private async Task SetVoiceSourceAsync(ProjectFolderId folderId, Guid voiceId, Data.Enums.VoiceSource source)
        {
            var db = await _session.OpenAsync(folderId);
            var voice = await db.Voices.FindAsync(voiceId);
            if (voice == null) return;
            voice.Source = source;
            if (source == Data.Enums.VoiceSource.Uploaded)
            {
                voice.DesignPrompt = null;
            }
            else
            {
                if (voice.AudioFileName != null)
                {
                    var projectFolder = _fs.GetProjectFolderPath(folderId.Value);
                    var audioPath = Path.Combine(projectFolder, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));
                    if (_fs.FileExists(audioPath))
                        _fs.DeleteFile(audioPath);
                    voice.AudioFileName = null;
                }
            }
            await db.SaveChangesAsync();
        }

        private async Task SetVoiceDefaultAsync(ProjectFolderId folderId, Guid voiceId)
        {
            var db = await _session.OpenAsync(folderId);
            var voice = await db.Voices.FindAsync(voiceId);
            if (voice == null) return;

            await db.Voices
                .Where(v => v.CharacterId == voice.CharacterId && v.IsDefault)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsDefault, false));

            voice.IsDefault = true;
            db.Voices.Update(voice);
            await db.SaveChangesAsync();
        }

        private async Task UpdateVoiceAsync(ProjectFolderId folderId, Guid voiceId, string name, string? description)
        {
            var db = await _session.OpenAsync(folderId);
            var voice = await db.Voices.FindAsync(voiceId);
            if (voice == null) return;
            voice.Name = name.Trim();
            voice.Description = description?.Trim();
            await db.SaveChangesAsync();
        }

        private async Task UpdateVoiceFieldAsync(ProjectFolderId folderId, Guid voiceId, Action<VoiceEntity> apply)
        {
            var db = await _session.OpenAsync(folderId);
            var voice = await db.Voices.FindAsync(voiceId);
            if (voice == null) return;
            apply(voice);
            await db.SaveChangesAsync();
        }

        private async Task DeleteVoiceAsync(ProjectFolderId folderId, Guid voiceId)
        {
            var db = await _session.OpenAsync(folderId);
            var voice = await db.Voices
                .Include(v => v.Character)
                .FirstOrDefaultAsync(v => v.Id == voiceId);
            if (voice == null) return;

            var wasDefault = voice.IsDefault;
            var characterId = voice.CharacterId;

            if (voice.AudioFileName != null)
            {
                var projectFolder = _fs.GetProjectFolderPath(folderId.Value);
                var audioPath = Path.Combine(projectFolder, voice.AudioFileName.Replace('/', Path.DirectorySeparatorChar));
                if (_fs.FileExists(audioPath))
                    _fs.DeleteFile(audioPath);
            }

            db.Voices.Remove(voice);
            await db.SaveChangesAsync();

            if (wasDefault)
            {
                var firstRemaining = await db.Voices
                    .Where(v => v.CharacterId == characterId)
                    .OrderBy(v => v.CreatedUtc)
                    .FirstOrDefaultAsync();
                if (firstRemaining != null)
                {
                    firstRemaining.IsDefault = true;
                    await db.SaveChangesAsync();
                }
            }
        }

        private static async Task<BookHierarchy> LoadBookHierarchyAsync(ProjectDbContext db)
        {
            var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            var parts = await db.Parts.OrderBy(p => p.Order).ToListAsync();
            var chapters = await db.Chapters.OrderBy(c => c.Order).ToListAsync();
            var paragraphs = await db.Paragraphs.OrderBy(p => p.Order).ToListAsync();
            var items = await db.ParagraphItems.OrderBy(i => i.Order).ToListAsync();
            return new BookHierarchy
            {
                Volumes = volumes,
                Parts = parts.GroupBy(p => p.VolumeId).ToDictionary(g => g.Key, g => g.ToList()),
                Chapters = chapters.GroupBy(c => c.PartId).ToDictionary(g => g.Key, g => g.ToList()),
                Paragraphs = paragraphs.GroupBy(p => p.ChapterId).ToDictionary(g => g.Key, g => g.ToList()),
                Items = items.GroupBy(i => i.ParagraphId).ToDictionary(g => g.Key, g => g.ToList()),
            };
        }

        internal static async Task ApplyMutationAsync(ProjectDbContext db, HierarchyMutation mutation)
        {
            foreach (var entity in mutation.ToAdd)
            {
                switch (entity)
                {
                    case Volume v: db.Volumes.Add(v); break;
                    case Part p: db.Parts.Add(p); break;
                    case Chapter c: db.Chapters.Add(c); break;
                    case Paragraph pg: db.Paragraphs.Add(pg); break;
                    case ParagraphItem i: db.ParagraphItems.Add(i); break;
                }
            }
            foreach (var entity in mutation.ToDelete)
            {
                switch (entity)
                {
                    case Volume v: db.Volumes.Remove(v); break;
                    case Part p: db.Parts.Remove(p); break;
                    case Chapter c: db.Chapters.Remove(c); break;
                    case Paragraph pg: db.Paragraphs.Remove(pg); break;
                    case ParagraphItem i: db.ParagraphItems.Remove(i); break;
                }
            }
            foreach (var entity in mutation.ToUpdate)
            {
                // Mark explicitly so the contract holds even for detached entities.
                db.Entry(entity).State = EntityState.Modified;
            }
            await db.SaveChangesAsync();
        }

        private async Task<HierarchyMutation?> PlanAndApplyAsync(
            ProjectFolderId folderId,
            Func<BookHierarchy, HierarchyMutation?> planner)
        {
            var db = await _session.OpenAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            var mutation = planner(h);
            if (mutation != null)
                await ApplyMutationAsync(db, mutation);
            return mutation;
        }
    }
}
