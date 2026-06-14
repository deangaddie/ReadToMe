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
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectService : IProjectReader, IProjectWriter, IAsyncDisposable
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
            var folderName = SanitizeName(title);
            if (string.IsNullOrEmpty(folderName))
                throw new ArgumentException("Title produces an empty folder name.", nameof(title));

            if (_fs.ProjectFolderExists(folderName))
                throw new InvalidOperationException($"A project named \"{folderName}\" already exists.");

            _logger.LogInformation("Creating project '{Title}' in folder '{Folder}'", title, folderName);

            _fs.CreateProjectFolder(folderName);

            var destFile = Path.Combine(_fs.GetProjectFolderPath(folderName), originalFileName);
            await _fs.WriteFileAsync(destFile, fileStream);
            _logger.LogDebug("Saved book file: {File}", destFile);

            var db = await OpenProjectDbAsync(folderName);

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
            return folderName;
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

        public async Task<ProjectEntity?> GetProjectAsync(string folderName)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderName), "project.db");
            if (!_fs.FileExists(dbPath))
            {
                _logger.LogWarning("GetProjectAsync: no DB found for folder '{Folder}'", folderName);
                return null;
            }

            var db = await OpenProjectDbAsync(folderName);
            return await db.Projects.SingleOrDefaultAsync();
        }

        public async Task SaveCoverImageAsync(string folderName, string filename, Stream stream)
        {
            _logger.LogInformation("Saving cover image '{File}' for project '{Folder}'", filename, folderName);
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity == null)
            {
                _logger.LogWarning("SaveCoverImageAsync: project record not found for '{Folder}'", folderName);
                return;
            }

            var folderPath = _fs.GetProjectFolderPath(folderName);
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
            _logger.LogInformation("Cover image saved for project '{Folder}'", folderName);
        }

        public async Task DeleteCoverImageAsync(string folderName)
        {
            _logger.LogInformation("Deleting cover image for project '{Folder}'", folderName);
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity?.CoverImage == null)
            {
                _logger.LogDebug("DeleteCoverImageAsync: no cover image set for '{Folder}'", folderName);
                return;
            }

            var imagePath = Path.Combine(_fs.GetProjectFolderPath(folderName), entity.CoverImage);
            if (_fs.FileExists(imagePath))
            {
                _fs.DeleteFile(imagePath);
                _logger.LogDebug("Deleted cover image file: {File}", imagePath);
            }

            entity.CoverImage = null;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image removed for project '{Folder}'", folderName);
        }

        public async Task<bool> HasBookContentAsync(string folderName)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderName), "project.db");
            if (!_fs.FileExists(dbPath))
                return false;

            var db = await OpenProjectDbAsync(folderName);
            return await db.Volumes.AnyAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Volume>> GetVolumesAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Part>> GetPartsAsync(string folderName, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Chapter>> GetChaptersAsync(string folderName, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Paragraph>> GetChapterParagraphsAsync(string folderName, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                    .ThenInclude(i => i.Character)
                .ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Character>> GetCharactersAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
        }

        public async Task<int> GetTotalPartCountAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Parts.CountAsync();
        }

        public async Task<int> GetTotalChapterCountAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
            return await db.Chapters.CountAsync();
        }

        public async Task SetParagraphItemCharacterAsync(string folderName, Guid itemId, Guid? characterId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var item = await db.ParagraphItems.Include(i => i.Character).FirstOrDefaultAsync(i => i.Id == itemId);
            if (item == null) return;
            item.CharacterId = characterId;
            item.Character = characterId.HasValue
                ? await db.Characters.FindAsync(characterId.Value)
                : null;
            await db.SaveChangesAsync();
        }

        public async Task DeleteVolumeAsync(string folderName, Guid volumeId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            db.Volumes.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeletePartAsync(string folderName, Guid partId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            db.Parts.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteChapterAsync(string folderName, Guid chapterId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            db.Chapters.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteParagraphAsync(string folderName, Guid paragraphId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Paragraphs.FindAsync(paragraphId);
            if (entity == null) return;
            db.Paragraphs.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task DeleteParagraphItemAsync(string folderName, Guid itemId)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            db.ParagraphItems.Remove(entity);
            await db.SaveChangesAsync();
        }

        public async Task UpdateVolumeTitleAsync(string folderName, Guid volumeId, string title)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Volumes.FindAsync(volumeId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdatePartTitleAsync(string folderName, Guid partId, string title)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Parts.FindAsync(partId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdateChapterTitleAsync(string folderName, Guid chapterId, string title)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Chapters.FindAsync(chapterId);
            if (entity == null) return;
            entity.Title = title;
            await db.SaveChangesAsync();
        }

        public async Task UpdateParagraphItemTextAsync(string folderName, Guid itemId, string text)
        {
            var db = await OpenProjectDbAsync(folderName);
            var entity = await db.ParagraphItems.FindAsync(itemId);
            if (entity == null) return;
            entity.Text = text;
            await db.SaveChangesAsync();
        }

        public async Task SplitVolumeAsync(string folderName, Guid partId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderName);

            var part = await db.Parts.FindAsync(partId);
            if (part == null) return;

            var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
            var currentVolume = volumes.FirstOrDefault(v => v.Id == part.VolumeId);
            if (currentVolume == null) return;

            var currentIdx = volumes.IndexOf(currentVolume);
            var nextOrder = currentIdx < volumes.Count - 1 ? volumes[currentIdx + 1].Order : null;

            var newVolume = new Volume
            {
                Id = Guid.NewGuid(),
                Title = newTitle ?? currentVolume.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentVolume.Order, nextOrder),
            };
            db.Volumes.Add(newVolume);

            var siblings = await db.Parts.Where(p => p.VolumeId == part.VolumeId).OrderBy(p => p.Order).ToListAsync();
            var splitIdx = siblings.FindIndex(p => p.Id == partId);
            foreach (var p in siblings.Skip(splitIdx))
                p.VolumeId = newVolume.Id;

            await db.SaveChangesAsync();
        }

        public async Task SplitPartAsync(string folderName, Guid chapterId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderName);

            var chapter = await db.Chapters.Include(c => c.Part).FirstOrDefaultAsync(c => c.Id == chapterId);
            if (chapter == null) return;

            var parts = await db.Parts.Where(p => p.VolumeId == chapter.Part.VolumeId).OrderBy(p => p.Order).ToListAsync();
            var currentPart = chapter.Part;
            var currentIdx = parts.IndexOf(currentPart);
            var nextOrder = currentIdx < parts.Count - 1 ? parts[currentIdx + 1].Order : null;

            var newPart = new Part
            {
                Id = Guid.NewGuid(),
                VolumeId = currentPart.VolumeId,
                Title = newTitle ?? currentPart.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentPart.Order, nextOrder),
            };
            db.Parts.Add(newPart);

            var siblings = await db.Chapters.Where(c => c.PartId == chapter.PartId).OrderBy(c => c.Order).ToListAsync();
            var splitIdx = siblings.FindIndex(c => c.Id == chapterId);
            foreach (var c in siblings.Skip(splitIdx))
                c.PartId = newPart.Id;

            await db.SaveChangesAsync();
        }

        public async Task SplitChapterAsync(string folderName, Guid paragraphId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderName);

            var paragraph = await db.Paragraphs.Include(p => p.Chapter).ThenInclude(c => c.Part).FirstOrDefaultAsync(p => p.Id == paragraphId);
            if (paragraph == null) return;

            var currentChapter = paragraph.Chapter;
            var chapters = await db.Chapters.Where(c => c.PartId == currentChapter.PartId).OrderBy(c => c.Order).ToListAsync();
            var currentIdx = chapters.IndexOf(currentChapter);
            var nextOrder = currentIdx < chapters.Count - 1 ? chapters[currentIdx + 1].Order : null;

            var newChapter = new Chapter
            {
                Id = Guid.NewGuid(),
                PartId = currentChapter.PartId,
                Title = newTitle ?? currentChapter.Title,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentChapter.Order, nextOrder),
            };
            db.Chapters.Add(newChapter);

            var siblings = await db.Paragraphs.Where(p => p.ChapterId == paragraph.ChapterId).OrderBy(p => p.Order).ToListAsync();
            var splitIdx = siblings.FindIndex(p => p.Id == paragraphId);
            foreach (var p in siblings.Skip(splitIdx))
                p.ChapterId = newChapter.Id;

            await db.SaveChangesAsync();
        }

        public async Task SplitParagraphAsync(string folderName, Guid itemId, string? newTitle)
        {
            var db = await OpenProjectDbAsync(folderName);

            var item = await db.ParagraphItems.Include(i => i.Paragraph).ThenInclude(p => p.Chapter).FirstOrDefaultAsync(i => i.Id == itemId);
            if (item == null) return;

            var currentParagraph = item.Paragraph;
            var paragraphs = await db.Paragraphs.Where(p => p.ChapterId == currentParagraph.ChapterId).OrderBy(p => p.Order).ToListAsync();
            var currentIdx = paragraphs.IndexOf(currentParagraph);
            var nextOrder = currentIdx < paragraphs.Count - 1 ? paragraphs[currentIdx + 1].Order : null;

            var newParagraph = new Paragraph
            {
                Id = Guid.NewGuid(),
                ChapterId = currentParagraph.ChapterId,
                Order = OrderKeyGenerator.GenerateKeyBetween(currentParagraph.Order, nextOrder),
            };
            db.Paragraphs.Add(newParagraph);

            var siblings = await db.ParagraphItems.Where(i => i.ParagraphId == item.ParagraphId).OrderBy(i => i.Order).ToListAsync();
            var splitIdx = siblings.FindIndex(i => i.Id == itemId);
            foreach (var i in siblings.Skip(splitIdx))
                i.ParagraphId = newParagraph.Id;

            await db.SaveChangesAsync();
        }

        public async Task SplitParagraphItemAsync(string folderName, Guid itemId)
        {
            // Split Line: same as SplitParagraph — splits the paragraph at this item boundary
            await SplitParagraphAsync(folderName, itemId, null);
        }

        public async Task AddBookTitleAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);

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

        public async Task AddVolumeTitlesAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
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

        public async Task AddPartTitlesAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
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

        public async Task AddChapterTitlesAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
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

        public async Task ClearBookContentAsync(string folderName)
        {
            var db = await OpenProjectDbAsync(folderName);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.ParagraphItems.ExecuteDeleteAsync();
            await db.Paragraphs.ExecuteDeleteAsync();
            await db.Chapters.ExecuteDeleteAsync();
            await db.Parts.ExecuteDeleteAsync();
            await db.Volumes.ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        public void DeleteProject(string folderName)
        {
            if (_contextCache.Remove(folderName, out var ctx))
                ctx.Dispose();

            if (_fs.ProjectFolderExists(folderName))
            {
                _logger.LogInformation("Deleting project '{Folder}'", folderName);
                _fs.DeleteProjectFolder(folderName);
                _logger.LogInformation("Project '{Folder}' deleted", folderName);
            }
            else
            {
                _logger.LogWarning("DeleteProject: folder not found '{Folder}'", folderName);
            }
        }

        private async Task<ProjectDbContext> OpenProjectDbAsync(string folderName)
        {
            var folderPath = _fs.GetProjectFolderPath(folderName);
            if (_contextCache.TryGetValue(folderName, out var cached))
                return cached;
            _logger.LogDebug("Opening project DB: {FolderPath}", folderPath);
            var ctx = await _dbFactory.CreateAsync(folderPath);
            _contextCache[folderName] = ctx;
            return ctx;
        }
    }
}
