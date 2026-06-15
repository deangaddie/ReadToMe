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
                case AddBookTitleCommand c: await AddBookTitleAsync(c.FolderId); break;
                case AddVolumeTitlesCommand c: await AddVolumeTitlesAsync(c.FolderId); break;
                case AddPartTitlesCommand c: await AddPartTitlesAsync(c.FolderId); break;
                case AddChapterTitlesCommand c: await AddChapterTitlesAsync(c.FolderId); break;
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
