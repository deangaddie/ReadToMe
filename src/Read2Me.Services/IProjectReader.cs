using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services
{
    public sealed record BookOverview(
        string? Filename,
        bool HasContent,
        IReadOnlyList<Volume> Volumes,
        IReadOnlyList<Character> Characters,
        int TotalParts,
        int TotalChapters,
        HashSet<Guid> SelectableNodeIds,
        IReadOnlyDictionary<Guid, int> NodeCharacterParagraphCounts);

    public sealed record ContextParagraph(string Text, string? Speaker);

    /// <summary>Text of a target paragraph plus its nearest neighbours within the same chapter.</summary>
    public sealed record ParagraphContext(
        ContextParagraph Query,
        IReadOnlyList<ContextParagraph> Preceding,
        IReadOnlyList<ContextParagraph> Following);

    public interface IProjectReader
    {
        Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId);
        IReadOnlyList<string> GetProjects();
        Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync();
        Task<Project?> GetProjectAsync(ProjectFolderId folderId);
        Task<bool> HasBookContentAsync(ProjectFolderId folderId);
        Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId);
        Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId);
        Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId);
        Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId);
        Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId);
        Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId);
        Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId);
        Task<List<CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId);
        Task<int> GetTotalPartCountAsync(ProjectFolderId folderId);
        Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId);

        Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false);

        // All volume/part/chapter node ids that contain at least one character paragraph.
        Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId);

        // Returns paragraphs from the given id set ordered by book position (Volume→Part→Chapter→Paragraph order).
        // Preview is the first character item's text, truncated.
        Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphIds);

        /// <summary>
        /// Returns the text of <paramref name="paragraphId"/> plus up to <paramref name="before"/> preceding
        /// and <paramref name="after"/> following paragraphs within the same chapter.
        /// Returns null if the paragraph is not found.
        /// </summary>
        Task<ParagraphContext?> GetParagraphContextAsync(
            ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after);
    }
}
