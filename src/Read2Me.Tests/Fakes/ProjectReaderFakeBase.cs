using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Voice;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Tests.Fakes
{
    public abstract class ProjectReaderFakeBase : IProjectReader
    {
        public virtual IReadOnlyList<string> GetProjects() => [];
        public virtual Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync() => Task.FromResult<IReadOnlyList<ProjectSummary>>([]);
        public virtual Task<Project?> GetProjectAsync(ProjectFolderId folderId) => Task.FromResult<Project?>(null);
        public virtual Task<bool> HasBookContentAsync(ProjectFolderId folderId) => Task.FromResult(false);
        public virtual Task<BookOverview> GetBookOverviewAsync(ProjectFolderId folderId) =>
            Task.FromResult(new BookOverview(null, false, [], [], 0, 0, [], new Dictionary<Guid, int>()));
        public virtual Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId) => Task.FromResult(new List<Volume>());
        public virtual Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId) => Task.FromResult(new List<Part>());
        public virtual Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId) => Task.FromResult(new List<Chapter>());
        public virtual Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId) => Task.FromResult(new List<Paragraph>());
        public virtual Task<HierarchyChildren> GetChildrenAsync(ProjectFolderId folderId, BookNodeLevel parentLevel, Guid parentId) =>
            Task.FromResult(new HierarchyChildren(null, null, null));
        public virtual Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId) => Task.FromResult(new List<Character>());
        public virtual Task<List<Character>> GetCharactersWithAliasesAsync(ProjectFolderId folderId) => Task.FromResult(new List<Character>());
        public virtual Task<List<VoiceEntity>> GetCharacterVoicesAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult(new List<VoiceEntity>());
        public virtual Task<Guid?> GetDefaultVoiceIdAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult<Guid?>(null);
        public virtual Task<List<VoiceRuleRow>> GetCharacterVoiceRulesAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult(new List<VoiceRuleRow>());
        public virtual Task<List<CharacterLine>> GetCharacterLinesAsync(ProjectFolderId folderId, Guid characterId) => Task.FromResult(new List<CharacterLine>());
        public virtual Task<int> GetTotalPartCountAsync(ProjectFolderId folderId) => Task.FromResult(0);
        public virtual Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId) => Task.FromResult(0);
        public virtual Task<List<CharacterParagraphRef>> GetCharacterParagraphsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool unprocessedOnly = false) =>
            Task.FromResult(new List<CharacterParagraphRef>());
        public virtual Task<HashSet<Guid>> GetNodesWithCharacterParagraphsAsync(ProjectFolderId folderId) => Task.FromResult(new HashSet<Guid>());
        public virtual Task<List<(Guid ParagraphId, string Preview)>> GetOrderedParagraphsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphIds) =>
            Task.FromResult(new List<(Guid, string)>());
        public virtual Task<List<AudioItemRef>> GetAudioItemRefsAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool needsAudioOnly = false, bool narratorOnlyMode = false) =>
            Task.FromResult(new List<AudioItemRef>());
        public virtual Task<List<AudioItemRef>> GetOrderedAudioItemRefsAsync(ProjectFolderId folderId, IEnumerable<Guid> paragraphItemIds) =>
            Task.FromResult(new List<AudioItemRef>());
        public virtual Task<IReadOnlyDictionary<Guid, int>> GetNodeAudioItemCountsAsync(ProjectFolderId folderId) =>
            Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());
        public virtual Task<List<(Guid ParagraphItemId, AudioReviewInfo Info)>> GetAudioReviewsAsync(ProjectFolderId folderId) =>
            Task.FromResult(new List<(Guid, AudioReviewInfo)>());
        public virtual Task<IReadOnlyList<ParagraphStatusSeedRow>> GetNodeStatusSeedAsync(ProjectFolderId folderId) =>
            Task.FromResult<IReadOnlyList<ParagraphStatusSeedRow>>([]);
        public virtual Task<ParagraphContext?> GetParagraphContextAsync(ProjectFolderId folderId, Guid chapterId, Guid paragraphId, int before, int after) =>
            Task.FromResult<ParagraphContext?>(null);
        public virtual Task<ParagraphBatchContext?> GetParagraphBatchContextAsync(ProjectFolderId folderId, Guid chapterId, IReadOnlyList<Guid> paragraphIds, int before, int after) =>
            Task.FromResult<ParagraphBatchContext?>(null);
        public virtual Task<IReadOnlyList<AssemblyManifestEntry>> GetAssemblyManifestAsync(ProjectFolderId folder, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AssemblyManifestEntry>>([]);
    }
}
