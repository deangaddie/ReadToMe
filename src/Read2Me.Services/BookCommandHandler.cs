using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Books;

namespace Read2Me.Services
{
    public class BookCommandHandler : IBookCommandHandler
    {
        private readonly ProjectDbSession _session;

        public BookCommandHandler(ProjectDbSession session)
        {
            _session = session;
        }

        public async Task<Guid?> ExecuteAsync(BookCommand command, CancellationToken ct = default)
        {
            switch (command)
            {
                case DeleteVolumeCommand c: await DeleteVolumeAsync(c.FolderId, c.VolumeId); break;
                case DeletePartCommand c: await DeletePartAsync(c.FolderId, c.PartId); break;
                case DeleteChapterCommand c: await DeleteChapterAsync(c.FolderId, c.ChapterId); break;
                case DeleteParagraphCommand c: await DeleteParagraphAsync(c.FolderId, c.ParagraphId); break;
                case DeleteParagraphItemCommand c: await DeleteParagraphItemAsync(c.FolderId, c.ItemId); break;
                case UpdateVolumeTitleCommand c: await UpdateVolumeTitleAsync(c.FolderId, c.VolumeId, c.Title); break;
                case UpdatePartTitleCommand c: await UpdatePartTitleAsync(c.FolderId, c.PartId, c.Title); break;
                case UpdateChapterTitleCommand c: await UpdateChapterTitleAsync(c.FolderId, c.ChapterId, c.Title); break;
                case UpdateParagraphItemTextCommand c: await UpdateParagraphItemTextAsync(c.FolderId, c.ItemId, c.Text); break;
                case SplitAtPartCommand c: return await SplitVolumeAsync(c.FolderId, c.PartId, c.NewVolumeTitle);
                case SplitAtChapterCommand c: return await SplitPartAsync(c.FolderId, c.ChapterId, c.NewPartTitle);
                case SplitAtParagraphCommand c: return await SplitChapterAsync(c.FolderId, c.ParagraphId, c.NewChapterTitle);
                case SplitAtItemCommand c: return await SplitParagraphItemAsync(c.FolderId, c.ItemId);
                case MergeVolumeCommand c when c.Direction == MergeDirection.Previous: await MergeVolumeWithPreviousAsync(c.FolderId, c.VolumeId); break;
                case MergeVolumeCommand c: await MergeVolumeWithNextAsync(c.FolderId, c.VolumeId); break;
                case MergePartCommand c when c.Direction == MergeDirection.Previous: await MergePartWithPreviousAsync(c.FolderId, c.PartId); break;
                case MergePartCommand c: await MergePartWithNextAsync(c.FolderId, c.PartId); break;
                case MergeChapterCommand c when c.Direction == MergeDirection.Previous: await MergeChapterWithPreviousAsync(c.FolderId, c.ChapterId); break;
                case MergeChapterCommand c: await MergeChapterWithNextAsync(c.FolderId, c.ChapterId); break;
                case MergeParagraphCommand c when c.Direction == MergeDirection.Previous: await MergeParagraphWithPreviousAsync(c.FolderId, c.ParagraphId); break;
                case MergeParagraphCommand c: await MergeParagraphWithNextAsync(c.FolderId, c.ParagraphId); break;
                case MergeParagraphItemCommand c when c.Direction == MergeDirection.Previous: await MergeParagraphItemWithPreviousAsync(c.FolderId, c.ItemId); break;
                case MergeParagraphItemCommand c: await MergeParagraphItemWithNextAsync(c.FolderId, c.ItemId); break;
                case SetItemCharacterCommand c: await SetParagraphItemCharacterAsync(c.FolderId, c.ItemId, c.CharacterId); break;
                case AddBookTitleCommand c: await AddBookTitleAsync(c.FolderId); break;
                case AddVolumeTitlesCommand c: await AddVolumeTitlesAsync(c.FolderId); break;
                case AddPartTitlesCommand c: await AddPartTitlesAsync(c.FolderId); break;
                case AddChapterTitlesCommand c: await AddChapterTitlesAsync(c.FolderId); break;
                case ClearBookContentCommand c: await ClearBookContentAsync(c.FolderId); break;
                default: throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");
            }
            return null;
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

        private async Task DeleteVolumeAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            db.Volumes.Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task DeletePartAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            db.Parts.Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task DeleteChapterAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            db.Chapters.Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task DeleteParagraphAsync(ProjectFolderId folderId, Guid paragraphId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Paragraphs.FindAsync(paragraphId);
            if (entity == null) return;
            db.Paragraphs.Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task DeleteParagraphItemAsync(ProjectFolderId folderId, Guid itemId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            db.ParagraphItems.Remove(entity);
            await db.SaveChangesAsync();
        }

        private async Task UpdateVolumeTitleAsync(ProjectFolderId folderId, Guid volumeId, string title)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        private async Task UpdatePartTitleAsync(ProjectFolderId folderId, Guid partId, string title)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        private async Task UpdateChapterTitleAsync(ProjectFolderId folderId, Guid chapterId, string title)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        private async Task UpdateParagraphItemTextAsync(ProjectFolderId folderId, Guid itemId, string text)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            entity.Text = text;
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

        private async Task MergeVolumeWithPreviousAsync(ProjectFolderId folderId, Guid volumeId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeVolume(volumeId, MergeDirection.Previous));

        private async Task MergeVolumeWithNextAsync(ProjectFolderId folderId, Guid volumeId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeVolume(volumeId, MergeDirection.Next));

        private async Task MergePartWithPreviousAsync(ProjectFolderId folderId, Guid partId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergePart(partId, MergeDirection.Previous));

        private async Task MergePartWithNextAsync(ProjectFolderId folderId, Guid partId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergePart(partId, MergeDirection.Next));

        private async Task MergeChapterWithPreviousAsync(ProjectFolderId folderId, Guid chapterId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeChapter(chapterId, MergeDirection.Previous));

        private async Task MergeChapterWithNextAsync(ProjectFolderId folderId, Guid chapterId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeChapter(chapterId, MergeDirection.Next));

        private async Task MergeParagraphWithPreviousAsync(ProjectFolderId folderId, Guid paragraphId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeParagraph(paragraphId, MergeDirection.Previous));

        private async Task MergeParagraphWithNextAsync(ProjectFolderId folderId, Guid paragraphId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeParagraph(paragraphId, MergeDirection.Next));

        private async Task MergeParagraphItemWithPreviousAsync(ProjectFolderId folderId, Guid itemId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeParagraphItem(itemId, MergeDirection.Previous));

        private async Task MergeParagraphItemWithNextAsync(ProjectFolderId folderId, Guid itemId)
            => await PlanAndApplyAsync(folderId, h => h.PlanMergeParagraphItem(itemId, MergeDirection.Next));

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
