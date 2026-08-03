using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Voice;
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

    /// <summary>
    /// One existing item of a context paragraph, in the wire shape the LLM answers in:
    /// <see cref="Type"/> is "narration" or "dialog"; <see cref="Speaker"/> is the attributed
    /// character name, "narrator" for narration, or "unknown" for an unattributed dialog item.
    /// </summary>
    public sealed record ContextSegment(string Text, string Type, string Speaker);

    /// <summary>
    /// A paragraph in an attribution context: its raw full text plus its current item split as
    /// segments. A query paragraph is fed to the LLM as raw text only (its current split may be
    /// wrong and must not bias re-segmentation); context paragraphs are fed as segments.
    /// </summary>
    public sealed record ContextParagraph(string Text, IReadOnlyList<ContextSegment> Segments);

    /// <summary>Text of a target paragraph plus its nearest neighbours within the same chapter.</summary>
    public sealed record ParagraphContext(
        ContextParagraph Query,
        IReadOnlyList<ContextParagraph> Preceding,
        IReadOnlyList<ContextParagraph> Following);

    /// <summary>
    /// One paragraph in a batch attribution context. <see cref="TargetIndex"/> is set only on
    /// the paragraphs to attribute (0-based, in order); context paragraphs carry segments instead.
    /// </summary>
    public sealed record BatchContextEntry(string Text, IReadOnlyList<ContextSegment> Segments, int? TargetIndex);

    /// <summary>
    /// Context for a multi-paragraph attribution request: a flat ordered span of paragraphs
    /// covering [before window … contiguous target run … after window].
    /// <see cref="IncludedIds"/> is the leading contiguous run of the requested paragraph ids
    /// (indexes match <see cref="BatchContextEntry.TargetIndex"/>); <see cref="DeferredIds"/> are
    /// requested ids trimmed off because an unassigned character paragraph not in the request
    /// sits between them and the run.
    /// </summary>
    public sealed record ParagraphBatchContext(
        IReadOnlyList<BatchContextEntry> Entries,
        IReadOnlyList<Guid> IncludedIds,
        IReadOnlyList<Guid> DeferredIds);

    /// <summary>
    /// Ordered children of a node at a given hierarchy level.
    /// Exactly one list is populated; the others are null.
    /// parentLevel=Volume → Parts; Part → Chapters; Chapter → Paragraphs (with Items included).
    /// </summary>
    public sealed record AssemblyManifestEntry(
        Guid ParagraphItemId,
        ParagraphItemType ItemType,
        string? AudioRelativePath,
        Guid VolumeId,
        string? VolumeTitle,
        Guid PartId,
        string? PartTitle,
        Guid ChapterId,
        string? ChapterTitle);

    public sealed record HierarchyChildren(
        List<Part>? Parts,
        List<Chapter>? Chapters,
        List<Paragraph>? Paragraphs);

    /// <summary>A resolved voice rule row for UI rendering.</summary>
    public sealed record VoiceRuleRow(
        Guid Id,
        bool IsDefault,
        string Rank,
        Guid VoiceId,
        string VoiceName,
        VoiceAnchorLevel? FromLevel,
        Guid? FromNodeId,
        string? FromDisplayName,
        bool FromDangling,
        VoiceAnchorLevel? ToLevel,
        Guid? ToNodeId,
        string? ToDisplayName,
        bool ToDangling);

    /// <summary>Workspace-level project catalog: which projects exist and their metadata.</summary>
    public interface IProjectCatalogReader
    {
        IReadOnlyList<string> GetProjects();
        Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync();
        Task<Project?> GetProjectAsync(ProjectFolderId folderId);

        /// <summary>
        /// Who narrates this project's book — the read-time projection of the narrator link
        /// (ADR-0004). Rides the context <see cref="GetProjectAsync"/> already opened.
        /// </summary>
        Task<NarratorIdentity> GetNarratorAsync(ProjectFolderId folderId, CancellationToken ct = default);
    }

    /// <summary>Book structure and text: volumes/parts/chapters/paragraphs and their content.</summary>
    public interface IBookContentReader
    {
        Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId);
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
        Task<int> GetTotalPartCountAsync(ProjectFolderId folderId);
        Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId);

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

        /// <summary>
        /// Returns a batch attribution context for <paramref name="paragraphIds"/> (chapter order
        /// assumed): the leading contiguous run of those paragraphs plus up to <paramref name="before"/>
        /// preceding and <paramref name="after"/> following context paragraphs. A character paragraph
        /// that still needs attribution and is not in the request ends the run; ids beyond that point
        /// are returned as deferred. Returns null if the first paragraph is not found.
        /// </summary>
        Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(
            ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after);
    }

    /// <summary>
    /// The single "is this paragraph fully stamped?" probe, split out of <see cref="ICharacterReader"/>
    /// so the attribution queue processor — its only consumer — depends on one method rather than
    /// twelve.
    /// </summary>
    public interface IUnattributedItemCounter
    {
        /// <summary>
        /// Number of Character items in <paramref name="paragraphId"/> with no character stamped.
        /// The paragraph is attributed when this is 0; a partly attributed paragraph stays
        /// queue-eligible.
        /// </summary>
        Task<int> CountUnattributedCharacterItemsAsync(ProjectFolderId folderId, Guid paragraphId);
    }

    /// <summary>Characters, their aliases/voices/voice rules, and character-paragraph attribution queries.</summary>
    public interface ICharacterReader : IUnattributedItemCounter
    {
        Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId);
        Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId);
        Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId);

        /// <summary>One voice by id, or null. The voice audio editor is routed by voice id alone.</summary>
        Task<VoiceEntity?> GetVoiceAsync(ProjectFolderId folderId, Guid voiceId);
        Task<Guid?> GetDefaultVoiceIdAsync(ProjectFolderId folderId, Guid characterId);
        Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId);
        Task<List<CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId);

        Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false);

        // All volume/part/chapter node ids that contain at least one character paragraph.
        Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId);

        /// <summary>
        /// What a bulk character assign over <paramref name="paragraphIds"/> would write. Deliberately
        /// character-agnostic: the confirm dialog states what will be written, never what changes, so
        /// items already pointing at the target still count. Answers for every listed paragraph, loaded
        /// or not — a selection can cover chapters that were never expanded.
        /// </summary>
        Task<BulkAssignPreview> GetBulkAssignPreviewAsync(
            ProjectFolderId folderId, IReadOnlyList<Guid> paragraphIds, CancellationToken ct = default);
    }

    /// <summary>
    /// The two figures behind the bulk-assign confirm. The third the dialog wants — selected
    /// paragraphs with nothing to stamp — is arithmetic at the call site:
    /// <c>paragraphIds.Count - ParagraphsWithCharacterItems</c>.
    /// </summary>
    public sealed record BulkAssignPreview(int ParagraphsWithCharacterItems, int CharacterItems);

    /// <summary>A generated paragraph item offered as an audio sample: its text and who speaks it.</summary>
    public sealed record AudioSampleInfo(
        Guid ParagraphItemId,
        string Text,
        string? CharacterName);

    /// <summary>Audio-generation state: item refs, review rows, status seeds, and the assembly manifest.</summary>
    public interface IAudioItemReader
    {
        // Text + speaker for the given items. Ids without stored audio are skipped.
        Task<IReadOnlyList<AudioSampleInfo>> GetAudioSampleInfosAsync(
            ProjectFolderId folderId, IReadOnlyCollection<Guid> itemIds);

        // Returns non-Pause ParagraphItems (Character + Narration) scoped to the given node, for audio selection.
        // When needsAudioOnly is true, filters to items missing a WAV and attribution-ready (Narration always; Character only when CharacterId != null, unless narratorOnlyMode is true in which case unattributed Character items are also included).
        Task<List<AudioItemRef>> GetAudioItemRefsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool needsAudioOnly = false, bool narratorOnlyMode = false);

        // Returns the given ParagraphItem IDs ordered by book position (Volume→Part→Chapter→Paragraph→Item order).
        Task<List<AudioItemRef>> GetOrderedAudioItemRefsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphItemIds);

        // Returns per-node (Chapter/Part/Volume) counts of non-Pause ParagraphItems for audio selection roll-up.
        Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId);

        // Returns all AudioReview rows for the folder in one query (rows are sparse). For service hydration.
        Task<List<(Guid ParagraphItemId, AudioReviewInfo Info)>> GetAudioReviewsAsync(ProjectFolderId folderId);

        // Returns one row per paragraph that has at least one non-Pause item, with per-paragraph stage counters and ancestry.
        Task<IReadOnlyList<ParagraphStatusSeedRow>> GetNodeStatusSeedAsync(ProjectFolderId folderId);

        /// <summary>
        /// Returns every ParagraphItem in the project in Position order (Volume→Part→Chapter→Paragraph→Item).
        /// Pause-kind entries have null AudioRelativePath regardless of any stored value.
        /// </summary>
        Task<IReadOnlyList<AssemblyManifestEntry>> GetAssemblyManifestAsync(ProjectFolderId folder, CancellationToken ct);
    }

    /// <summary>
    /// Composite of all read areas. Prefer the narrow interfaces
    /// (<see cref="IProjectCatalogReader"/>, <see cref="IBookContentReader"/>,
    /// <see cref="ICharacterReader"/>, <see cref="IAudioItemReader"/>) in new code;
    /// depend on this only when a consumer genuinely spans several areas.
    /// </summary>
    public interface IProjectReader : IProjectCatalogReader, IBookContentReader, ICharacterReader, IAudioItemReader
    {
    }
}
