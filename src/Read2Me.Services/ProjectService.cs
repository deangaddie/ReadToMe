using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.Books;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectService : IProjectReader, IProjectWriter, IBookCommandHandler, IAsyncDisposable
    {
        private readonly IFileSystem _fs;
        private readonly IProjectDbContextFactory _dbFactory;
        private readonly ILogger<ProjectService> _logger;
        private readonly Dictionary<string, ProjectDbContext> _contextCache = new(StringComparer.OrdinalIgnoreCase);

        public ProjectService(IFileSystem fs, IProjectDbContextFactory dbFactory, ILogger<ProjectService> logger)
        {
            _fs = fs;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var ctx in _contextCache.Values)
                await ctx.DisposeAsync();
            _contextCache.Clear();
        }

        public IReadOnlyList<string> GetProjects()
        {
            var folders = _fs.ListProjectFolders();
            _logger.LogDebug("Found {Count} project(s) in workspace", folders.Count);
            return folders;
        }

        public string SanitizeName(string name)
        {
            var s = name.ToLowerInvariant().Replace(' ', '-');
            s = Regex.Replace(s, @"[^\w\-]", "");
            s = Regex.Replace(s, @"-{2,}", "-").Trim('-');
            return s;
        }

        public bool CreateProject(string name)
        {
            var sanitized = SanitizeName(name);
            if (string.IsNullOrEmpty(sanitized))
            {
                _logger.LogWarning("CreateProject: name '{Name}' produced an empty sanitized folder name", name);
                return false;
            }

            if (_fs.ProjectFolderExists(sanitized))
            {
                _logger.LogWarning("CreateProject: folder already exists for '{Name}'", sanitized);
                return false;
            }

            _fs.CreateProjectFolder(sanitized);
            _logger.LogInformation("Created project folder: {Name}", sanitized);
            return true;
        }

        public async Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType)
        {
            var folderId = SanitizeName(title);
            if (string.IsNullOrEmpty(folderId))
                throw new ArgumentException("Title produces an empty folder name.", nameof(title));

            if (_fs.ProjectFolderExists(folderId))
                throw new InvalidOperationException($"A project named \"{folderId}\" already exists.");

            _logger.LogInformation("Creating project '{Title}' in folder '{Folder}'", title, folderId);

            _fs.CreateProjectFolder(folderId);

            var destFile = Path.Combine(_fs.GetProjectFolderPath(folderId), originalFileName);
            await _fs.WriteFileAsync(destFile, fileStream);
            _logger.LogDebug("Saved book file: {File}", destFile);

            var db = await OpenProjectDbAsync(folderId);

            db.Projects.Add(new ProjectEntity
            {
                Title = title,
                BookTitle = bookTitle,
                Author = author,
                Filename = originalFileName,
                Type = fileType,
            });
            await db.SaveChangesAsync();

            _logger.LogInformation("Project '{Title}' created successfully", title);
            return folderId;
        }

        public async Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync()
        {
            var folders = GetProjects();
            var summaries = new List<ProjectSummary>(folders.Count);
            foreach (var folder in folders)
            {
                var project = await GetProjectAsync(folder);
                summaries.Add(new ProjectSummary(folder, project?.Title ?? folder));
            }
            return summaries;
        }

        public async Task<ProjectEntity?> GetProjectAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderId), "project.db");
            if (!_fs.FileExists(dbPath))
            {
                _logger.LogWarning("GetProjectAsync: no DB found for folder '{Folder}'", folderId);
                return null;
            }

            var db = await OpenProjectDbAsync(folderId);
            return await db.Projects.SingleOrDefaultAsync();
        }

        public async Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream)
        {
            _logger.LogInformation("Saving cover image '{File}' for project '{Folder}'", filename, folderId);
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity == null)
            {
                _logger.LogWarning("SaveCoverImageAsync: project record not found for '{Folder}'", folderId);
                return;
            }

            var folderPath = _fs.GetProjectFolderPath(folderId);
            if (entity.CoverImage != null)
            {
                var existing = Path.Combine(folderPath, entity.CoverImage);
                if (_fs.FileExists(existing))
                {
                    _fs.DeleteFile(existing);
                    _logger.LogDebug("Deleted previous cover image: {File}", existing);
                }
            }

            await _fs.WriteFileAsync(Path.Combine(folderPath, filename), stream);
            entity.CoverImage = filename;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image saved for project '{Folder}'", folderId);
        }

        public async Task DeleteCoverImageAsync(ProjectFolderId folderId)
        {
            _logger.LogInformation("Deleting cover image for project '{Folder}'", folderId);
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity?.CoverImage == null)
            {
                _logger.LogDebug("DeleteCoverImageAsync: no cover image set for '{Folder}'", folderId);
                return;
            }

            var imagePath = Path.Combine(_fs.GetProjectFolderPath(folderId), entity.CoverImage);
            if (_fs.FileExists(imagePath))
            {
                _fs.DeleteFile(imagePath);
                _logger.LogDebug("Deleted cover image file: {File}", imagePath);
            }

            entity.CoverImage = null;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image removed for project '{Folder}'", folderId);
        }

        public async Task<bool> HasBookContentAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderId), "project.db");
            if (!_fs.FileExists(dbPath))
                return false;

            var db = await OpenProjectDbAsync(folderId);
            return await db.Volumes.AnyAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Volume>> GetVolumesAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                    .ThenInclude(i => i.Character)
                .ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Character>> GetCharactersAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
        }

        public async Task<int> GetTotalPartCountAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Parts.CountAsync();
        }

        public async Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            return await db.Chapters.CountAsync();
        }

        public async Task SetParagraphItemCharacterAsync(ProjectFolderId folderId, Guid itemId, Guid? characterId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var item = await db.ParagraphItems.Include(i => i.Character).FirstOrDefaultAsync(i => i.Id == itemId);
            if (item == null) return;
            item.CharacterId = characterId;
            item.Character = characterId.HasValue
                ? await db.Characters.FindAsync(characterId.Value)
                : null;
            await db.SaveChangesAsync();
        }

        public async Task DeleteVolumeAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            db.Volumes.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeletePartAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            db.Parts.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteChapterAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            db.Chapters.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteParagraphAsync(ProjectFolderId folderId, Guid paragraphId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Paragraphs.FindAsync(paragraphId);
            if (entity == null) return;
            db.Paragraphs.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteParagraphItemAsync(ProjectFolderId folderId, Guid itemId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            db.ParagraphItems.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task UpdateVolumeTitleAsync(ProjectFolderId folderId, Guid volumeId, string title)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdatePartTitleAsync(ProjectFolderId folderId, Guid partId, string title)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdateChapterTitleAsync(ProjectFolderId folderId, Guid chapterId, string title)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdateParagraphItemTextAsync(ProjectFolderId folderId, Guid itemId, string text)
        {
            var db = await OpenProjectDbAsync(folderId);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            entity.Text = text;
            await db.SaveChangesAsync();
        }

        public async Task<Guid?> SplitVolumeAsync(ProjectFolderId folderId, Guid partId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            var mutation = h.PlanSplitVolume(partId, newTitle);
            if (mutation == null) return null;
            await ApplyMutationAsync(db, mutation);
            return ((Volume)mutation.ToAdd[0]).Id;
        }

        public async Task<Guid?> SplitPartAsync(ProjectFolderId folderId, Guid chapterId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            var mutation = h.PlanSplitPart(chapterId, newTitle);
            if (mutation == null) return null;
            await ApplyMutationAsync(db, mutation);
            return ((Part)mutation.ToAdd[0]).Id;
        }

        public async Task<Guid?> SplitChapterAsync(ProjectFolderId folderId, Guid paragraphId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            var mutation = h.PlanSplitChapter(paragraphId, newTitle);
            if (mutation == null) return null;
            await ApplyMutationAsync(db, mutation);
            return ((Chapter)mutation.ToAdd[0]).Id;
        }

        public async Task<Guid?> SplitParagraphAsync(ProjectFolderId folderId, Guid itemId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderId);
            var h = await LoadBookHierarchyAsync(db);
            var mutation = h.PlanSplitParagraph(itemId);
            if (mutation == null) return null;
            await ApplyMutationAsync(db, mutation);
            return ((Paragraph)mutation.ToAdd[0]).Id;
        }

        public async Task<Guid?> SplitParagraphItemAsync(ProjectFolderId folderId, Guid itemId)
        {
            // Split Line: same as SplitParagraph — splits the paragraph at this item boundary
            return await SplitParagraphAsync(folderId, itemId, null);
        }

        public async Task AddBookTitleAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);

            var project = await db.Projects.SingleOrDefaultAsync();
            if (project == null) return;

            var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            if (volumes.Count == 0) return;

            Guid chapterId;

            if (volumes.Count > 1)
            {
                var firstVolume = volumes[0];
                var newVolume = new Volume
                {
                    Id = Guid.NewGuid(),
                    Title = string.Empty,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstVolume.Order),
                };
                db.Volumes.Add(newVolume);

                var newPart = new Part
                {
                    Id = Guid.NewGuid(),
                    VolumeId = newVolume.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                db.Parts.Add(newPart);

                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = newPart.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                db.Chapters.Add(newChapter);
                chapterId = newChapter.Id;
            }
            else
            {
                var volume = volumes[0];
                var parts = await db.Parts.Where(p => p.VolumeId == volume.Id).OrderBy(p => p.Order).ToListAsync();

                if (parts.Count > 1)
                {
                    var firstPart = parts[0];
                    var newPart = new Part
                    {
                        Id = Guid.NewGuid(),
                        VolumeId = volume.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, firstPart.Order),
                    };
                    db.Parts.Add(newPart);

                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = newPart.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                    };
                    db.Chapters.Add(newChapter);
                    chapterId = newChapter.Id;
                }
                else
                {
                    var part = parts[0];
                    var chapters = await db.Chapters.Where(c => c.PartId == part.Id).OrderBy(c => c.Order).ToListAsync();
                    var firstChapter = chapters.FirstOrDefault();

                    var newChapter = new Chapter
                    {
                        Id = Guid.NewGuid(),
                        PartId = part.Id,
                        Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                    };
                    db.Chapters.Add(newChapter);
                    chapterId = newChapter.Id;
                }
            }

            var titlePara = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
            };
            db.Paragraphs.Add(titlePara);
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = titlePara.Id,
                ItemType = ParagraphItemType.Narration,
                Text = project.BookTitle,
                Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
            });

            var byPara = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = chapterId,
                Order = OrderKeyGenerator.GenerateKeyBetween(titlePara.Order, null),
            };
            db.Paragraphs.Add(byPara);
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = byPara.Id,
                ItemType = ParagraphItemType.Narration,
                Text = $"By {project.Author}",
                Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
            });

            await db.SaveChangesAsync();
        }

        public async Task AddVolumeTitlesAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();

            foreach (var volume in volumes)
            {
                if (string.IsNullOrWhiteSpace(volume.Title)) continue;

                var firstPart = await db.Parts.Where(p => p.VolumeId == volume.Id).OrderBy(p => p.Order).FirstOrDefaultAsync();
                if (firstPart == null) continue;

                var firstChapter = await db.Chapters.Where(c => c.PartId == firstPart.Id).OrderBy(c => c.Order).FirstOrDefaultAsync();

                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = firstPart.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                };
                db.Chapters.Add(newChapter);

                var para = new Paragraph
                {
                    Id = Guid.NewGuid(),
                    ChapterId = newChapter.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Narration,
                    Text = volume.Title,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task AddPartTitlesAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var parts = await db.Parts.OrderBy(p => p.Order).ToListAsync();

            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part.Title)) continue;

                var firstChapter = await db.Chapters.Where(c => c.PartId == part.Id).OrderBy(c => c.Order).FirstOrDefaultAsync();

                var newChapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = part.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstChapter?.Order),
                };
                db.Chapters.Add(newChapter);

                var para = new Paragraph
                {
                    Id = Guid.NewGuid(),
                    ChapterId = newChapter.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Narration,
                    Text = part.Title,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task AddChapterTitlesAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var chapters = await db.Chapters.OrderBy(c => c.Order).ToListAsync();

            foreach (var chapter in chapters)
            {
                if (string.IsNullOrWhiteSpace(chapter.Title)) continue;

                var firstParagraph = await db.Paragraphs.Where(p => p.ChapterId == chapter.Id).OrderBy(p => p.Order).FirstOrDefaultAsync();

                var para = new Paragraph
                {
                    Id = Guid.NewGuid(),
                    ChapterId = chapter.Id,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, firstParagraph?.Order),
                };
                db.Paragraphs.Add(para);
                db.ParagraphItems.Add(new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = para.Id,
                    ItemType = ParagraphItemType.Narration,
                    Text = chapter.Title,
                    Order = OrderKeyGenerator.GenerateKeyBetween(null, null),
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task MergeVolumeWithPreviousAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var siblings = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            await MergeWithPreviousAsync(db, siblings, v => v.Id, volumeId,
                self => db.Parts.Where(p => p.VolumeId == self.Id).ToListAsync(),
                (child, winnerId) => child.VolumeId = winnerId,
                db.Volumes);
        }

        public async Task MergeVolumeWithNextAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var siblings = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            await MergeWithNextAsync(db, siblings, v => v.Id, volumeId,
                loser => db.Parts.Where(p => p.VolumeId == loser.Id).ToListAsync(),
                (child, winnerId) => child.VolumeId = winnerId,
                db.Volumes);
        }

        public async Task MergePartWithPreviousAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var part = await db.Parts.FindAsync(partId);
            if (part == null) return;
            var siblings = await db.Parts.Where(p => p.VolumeId == part.VolumeId).OrderBy(p => p.Order).ToListAsync();
            await MergeWithPreviousAsync(db, siblings, p => p.Id, partId,
                self => db.Chapters.Where(c => c.PartId == self.Id).ToListAsync(),
                (child, winnerId) => child.PartId = winnerId,
                db.Parts);
        }

        public async Task MergePartWithNextAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var part = await db.Parts.FindAsync(partId);
            if (part == null) return;
            var siblings = await db.Parts.Where(p => p.VolumeId == part.VolumeId).OrderBy(p => p.Order).ToListAsync();
            await MergeWithNextAsync(db, siblings, p => p.Id, partId,
                loser => db.Chapters.Where(c => c.PartId == loser.Id).ToListAsync(),
                (child, winnerId) => child.PartId = winnerId,
                db.Parts);
        }

        public async Task MergeChapterWithPreviousAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var chapter = await db.Chapters.FindAsync(chapterId);
            if (chapter == null) return;
            var siblings = await db.Chapters.Where(c => c.PartId == chapter.PartId).OrderBy(c => c.Order).ToListAsync();
            await MergeWithPreviousAsync(db, siblings, c => c.Id, chapterId,
                self => db.Paragraphs.Where(p => p.ChapterId == self.Id).ToListAsync(),
                (child, winnerId) => child.ChapterId = winnerId,
                db.Chapters);
        }

        public async Task MergeChapterWithNextAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var chapter = await db.Chapters.FindAsync(chapterId);
            if (chapter == null) return;
            var siblings = await db.Chapters.Where(c => c.PartId == chapter.PartId).OrderBy(c => c.Order).ToListAsync();
            await MergeWithNextAsync(db, siblings, c => c.Id, chapterId,
                loser => db.Paragraphs.Where(p => p.ChapterId == loser.Id).ToListAsync(),
                (child, winnerId) => child.ChapterId = winnerId,
                db.Chapters);
        }

        public async Task MergeParagraphWithPreviousAsync(ProjectFolderId folderId, Guid paragraphId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var paragraph = await db.Paragraphs.FindAsync(paragraphId);
            if (paragraph == null) return;
            var siblings = await db.Paragraphs.Where(p => p.ChapterId == paragraph.ChapterId).OrderBy(p => p.Order).ToListAsync();
            await MergeWithPreviousAsync(db, siblings, p => p.Id, paragraphId,
                self => db.ParagraphItems.Where(i => i.ParagraphId == self.Id).ToListAsync(),
                (child, winnerId) => child.ParagraphId = winnerId,
                db.Paragraphs);
        }

        public async Task MergeParagraphWithNextAsync(ProjectFolderId folderId, Guid paragraphId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var paragraph = await db.Paragraphs.FindAsync(paragraphId);
            if (paragraph == null) return;
            var siblings = await db.Paragraphs.Where(p => p.ChapterId == paragraph.ChapterId).OrderBy(p => p.Order).ToListAsync();
            await MergeWithNextAsync(db, siblings, p => p.Id, paragraphId,
                loser => db.ParagraphItems.Where(i => i.ParagraphId == loser.Id).ToListAsync(),
                (child, winnerId) => child.ParagraphId = winnerId,
                db.Paragraphs);
        }

        public async Task MergeParagraphItemWithPreviousAsync(ProjectFolderId folderId, Guid itemId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var item = await db.ParagraphItems.FindAsync(itemId);
            if (item == null) return;
            var siblings = await db.ParagraphItems.Where(i => i.ParagraphId == item.ParagraphId).OrderBy(i => i.Order).ToListAsync();
            var idx = siblings.FindIndex(i => i.Id == itemId);
            if (idx <= 0) return;
            var prev = siblings[idx - 1];
            prev.Text = string.IsNullOrWhiteSpace(prev.Text)
                ? item.Text
                : string.IsNullOrWhiteSpace(item.Text) ? prev.Text : prev.Text + " " + item.Text;
            db.ParagraphItems.Remove(item);
            await db.SaveChangesAsync();
        }

        public async Task MergeParagraphItemWithNextAsync(ProjectFolderId folderId, Guid itemId)
        {
            var db = await OpenProjectDbAsync(folderId);
            var item = await db.ParagraphItems.FindAsync(itemId);
            if (item == null) return;
            var siblings = await db.ParagraphItems.Where(i => i.ParagraphId == item.ParagraphId).OrderBy(i => i.Order).ToListAsync();
            var idx = siblings.FindIndex(i => i.Id == itemId);
            if (idx < 0 || idx >= siblings.Count - 1) return;
            var next = siblings[idx + 1];
            item.Text = string.IsNullOrWhiteSpace(item.Text)
                ? next.Text
                : string.IsNullOrWhiteSpace(next.Text) ? item.Text : item.Text + " " + next.Text;
            db.ParagraphItems.Remove(next);
            await db.SaveChangesAsync();
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

        private static async Task ApplyMutationAsync(ProjectDbContext db, HierarchyMutation mutation)
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
            // ToUpdate entities are already tracked by the context (loaded via LoadBookHierarchyAsync).
            // EF change tracking picks up the mutations Plan* methods made.
            await db.SaveChangesAsync();
        }

        // Merge "previous wins": self is deleted, its children reassigned to prev.
        private static async Task MergeWithPreviousAsync<TEntity, TChild>(
            ProjectDbContext db,
            List<TEntity> siblings,
            Func<TEntity, Guid> getId,
            Guid entityId,
            Func<TEntity, Task<List<TChild>>> getChildren,
            Action<TChild, Guid> reassign,
            DbSet<TEntity> dbSet)
            where TEntity : class
        {
            var idx = siblings.FindIndex(e => getId(e) == entityId);
            if (idx <= 0) return;
            var winner = siblings[idx - 1];
            var loser = siblings[idx];
            var children = await getChildren(loser);
            foreach (var child in children) reassign(child, getId(winner));
            dbSet.Remove(loser);
            await db.SaveChangesAsync();
        }

        // Merge "self wins": loser (next) is deleted, its children reassigned to self.
        private static async Task MergeWithNextAsync<TEntity, TChild>(
            ProjectDbContext db,
            List<TEntity> siblings,
            Func<TEntity, Guid> getId,
            Guid entityId,
            Func<TEntity, Task<List<TChild>>> getChildren,
            Action<TChild, Guid> reassign,
            DbSet<TEntity> dbSet)
            where TEntity : class
        {
            var idx = siblings.FindIndex(e => getId(e) == entityId);
            if (idx < 0 || idx >= siblings.Count - 1) return;
            var winner = siblings[idx];
            var loser = siblings[idx + 1];
            var children = await getChildren(loser);
            foreach (var child in children) reassign(child, getId(winner));
            dbSet.Remove(loser);
            await db.SaveChangesAsync();
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

        public async Task ClearBookContentAsync(ProjectFolderId folderId)
        {
            var db = await OpenProjectDbAsync(folderId);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.ParagraphItems.ExecuteDeleteAsync();
            await db.Paragraphs.ExecuteDeleteAsync();
            await db.Chapters.ExecuteDeleteAsync();
            await db.Parts.ExecuteDeleteAsync();
            await db.Volumes.ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        public void DeleteProject(ProjectFolderId folderId)
        {
            if (_contextCache.Remove(folderId, out var ctx))
                ctx.Dispose();

            if (_fs.ProjectFolderExists(folderId))
            {
                _logger.LogInformation("Deleting project '{Folder}'", folderId);
                _fs.DeleteProjectFolder(folderId);
                _logger.LogInformation("Project '{Folder}' deleted", folderId);
            }
            else
            {
                _logger.LogWarning("DeleteProject: folder not found '{Folder}'", folderId);
            }
        }

        private async Task<ProjectDbContext> OpenProjectDbAsync(ProjectFolderId folderId)
        {
            var folderPath = _fs.GetProjectFolderPath(folderId);
            if (_contextCache.TryGetValue(folderId, out var cached))
                return cached;
            _logger.LogDebug("Opening project DB: {FolderPath}", folderPath);
            var ctx = await _dbFactory.CreateAsync(folderPath);
            _contextCache[folderId] = ctx;
            return ctx;
        }
    }
}
