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

    /// <summary>
    /// Ordered children of a node at a given hierarchy level.
    /// Exactly one list is populated; the others are null.
    /// parentLevel=Volume → Parts; Part → Chapters; Chapter → Paragraphs (with Items included).
    /// </summary>
    public sealed record HierarchyChildren(
        List<Part>? Parts,
        List<Chapter>? Chapters,
        List<Paragraph>? Paragraphs);

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

        /// <summary>
        /// Returns the ordered structural children of <paramref name="parentId"/> at <paramref name="parentLevel"/>.
        /// Volume → Parts; Part → Chapters; Chapter → Paragraphs (Items included).
        /// Returns an empty result if the parent is not found.
        /// </summary>
        Task<HierarchyChildren> GetChildrenAsync(ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId);
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

        // Returns non-Pause ParagraphItems (Character + Narration) scoped to the given node, for audio selection.
        Task<List<AudioItemRef>> GetAudioItemRefsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId);

        // Returns per-node (Chapter/Part/Volume) counts of non-Pause ParagraphItems for audio selection roll-up.
        Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId);

        /// <summary>
        /// Returns the text of <paramref name="paragraphId"/> plus up to <paramref name="before"/> preceding
        /// and <paramref name="after"/> following paragraphs within the same chapter.
        /// Returns null if the paragraph is not found.
        /// </summary>
        Task<ParagraphContext?> GetParagraphContextAsync(
            ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after);
    }
}
