using FractionalIndexing;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Tests.Infrastructure;

/// <summary>
/// Object Mother for the book hierarchy (Volume/Part/Chapter/Paragraph/ParagraphItem).
/// Usage:
///   var b = new BookHierarchyBuilder(OpenDbAsync);
///   b.WithProject(narratorOnlyMode: true);          // optional; defaults applied if omitted
///   b.WithCharacter("alice", aliceEntity);           // optional; for AddCharacterLine
///   await b
///     .AddVolume("V1", v => v
///       .AddChapter("ch1", c => c
///         .AddParagraph(p => p
///           .AddNarration("n1", "Hello"))))
///     .BuildAsync();
///   Guid itemId = b.ItemId("n1");
/// </summary>
public sealed class BookHierarchyBuilder
{
    // ── opener delegate ───────────────────────────────────────────────────────

    private readonly Func<Task<ProjectDbContext>> _openDb;

    // ── registered pre-made characters (name → entity) ────────────────────────

    private readonly Dictionary<string, Character> _characters = new();

    // ── named-lookup registry (name → id) ────────────────────────────────────

    private readonly Dictionary<string, Guid> _ids = new();

    // ── pending volumes ───────────────────────────────────────────────────────

    private readonly List<VolumeSpec> _volumes = new();
    private string? _lastVolumeOrder;

    // ── project override ──────────────────────────────────────────────────────

    private bool _narratorOnlyMode;
    private string _projectTitle = "Test Book";
    private string _projectAuthor = "Author";

    // ── legacy: context-based ctor for existing callers ───────────────────────

    private ProjectDbContext? _legacyDb;

    public BookHierarchyBuilder(Func<Task<ProjectDbContext>> openDb) => _openDb = openDb;

    // ── legacy ctor: keeps old callers compiling ──────────────────────────────

    public BookHierarchyBuilder(ProjectDbContext db)
    {
        _legacyDb = db;
        _openDb = () => throw new InvalidOperationException("Legacy mode: use SaveAsync()");
    }

    // ── project configuration ─────────────────────────────────────────────────

    public BookHierarchyBuilder WithProject(
        bool narratorOnlyMode = false,
        string title = "Test Book",
        string author = "Author")
    {
        _narratorOnlyMode = narratorOnlyMode;
        _projectTitle = title;
        _projectAuthor = author;
        return this;
    }

    // ── character registration ────────────────────────────────────────────────

    public BookHierarchyBuilder WithCharacter(string name, Character entity)
    {
        _characters[name] = entity;
        return this;
    }

    // ── named-id lookup ───────────────────────────────────────────────────────

    public Guid VolumeId(string name)    => _ids[name];
    public Guid PartId(string name)      => _ids[name];
    public Guid ChapterId(string name)   => _ids[name];
    public Guid ParagraphId(string name) => _ids[name];
    public Guid ItemId(string name)      => _ids[name];

    // ── fluent tree construction ──────────────────────────────────────────────

    public BookHierarchyBuilder AddVolume(
        string name,
        Action<VolumeScope>? configure = null)
    {
        var spec = new VolumeSpec(name, configure);
        _volumes.Add(spec);
        return this;
    }

    // ── build ─────────────────────────────────────────────────────────────────

    public async Task BuildAsync()
    {
        await using var db = await _openDb();

        // Auto-project
        db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            Title = _projectTitle,
            BookTitle = _projectTitle,
            Author = _projectAuthor,
            Filename = "book.txt",
            Type = BookFileType.Text,
            NarratorOnlyMode = _narratorOnlyMode,
        });

        await FlushVolumesAsync(db);
    }

    // Like BuildAsync but skips creating a Project row — for tests where the project
    // was already created by a service (e.g. ProjectService.CreateProjectAsync).
    public async Task AddHierarchyAsync()
    {
        await using var db = await _openDb();
        await FlushVolumesAsync(db);
    }

    private async Task FlushVolumesAsync(ProjectDbContext db)
    {
        // Register pre-made characters
        foreach (var ch in _characters.Values)
            db.Characters.Add(ch);

        // Build each volume
        foreach (var vSpec in _volumes)
        {
            _lastVolumeOrder = OrderKeyGenerator.GenerateKeyBetween(_lastVolumeOrder, null);
            var vol = new Volume
            {
                Id = Guid.NewGuid(),
                Title = vSpec.Name,
                Order = _lastVolumeOrder,
            };
            Register(vSpec.Name, vol.Id);
            db.Volumes.Add(vol);

            var vScope = new VolumeScope(vol.Id, this, db);
            vSpec.Configure?.Invoke(vScope);
            vScope.Flush();
        }

        await db.SaveChangesAsync();
    }

    private void Register(string name, Guid id) => _ids[name] = id;

    // ── scopes ────────────────────────────────────────────────────────────────

    public sealed class VolumeScope
    {
        private readonly Guid _volumeId;
        private readonly BookHierarchyBuilder _root;
        private readonly ProjectDbContext _db;
        private readonly List<PartSpec> _parts = new();
        private string? _lastPartOrder;

        internal VolumeScope(Guid volumeId, BookHierarchyBuilder root, ProjectDbContext db)
        {
            _volumeId = volumeId;
            _root = root;
            _db = db;
        }

        public VolumeScope AddPart(string? name = null, Action<PartScope>? configure = null)
        {
            _parts.Add(new PartSpec(name, configure));
            return this;
        }

        // Shortcut: AddChapter directly on volume creates a single implicit part to hold all chapters
        public VolumeScope AddChapter(string? name = null, Action<ChapterScope>? configure = null)
        {
            // Ensure exactly one implicit Part exists (reuse if already there)
            if (_parts.Count == 0 || !_parts[^1].IsImplicit)
                _parts.Add(new PartSpec(null, null) { IsImplicit = true });
            _parts[^1].Chapters.Add(new ChapterSpec(name, configure));
            return this;
        }

        internal void Flush()
        {
            // If no parts were added at all, add a default implicit part + chapter
            if (_parts.Count == 0)
                _parts.Add(new PartSpec(null, null) { IsImplicit = true });

            foreach (var pSpec in _parts)
            {
                _lastPartOrder = OrderKeyGenerator.GenerateKeyBetween(_lastPartOrder, null);
                var part = new Part
                {
                    Id = Guid.NewGuid(),
                    VolumeId = _volumeId,
                    Order = _lastPartOrder,
                    Title = pSpec.Name,
                };
                if (pSpec.Name != null) _root.Register(pSpec.Name, part.Id);
                _db.Parts.Add(part);

                var pScope = new PartScope(part.Id, _root, _db);
                pSpec.Configure?.Invoke(pScope);
                // Chapters added directly via AddChapter shortcut
                foreach (var cSpec in pSpec.Chapters)
                    pScope.AddChapterSpec(cSpec);
                pScope.Flush();
            }
        }
    }

    public sealed class PartScope
    {
        private readonly Guid _partId;
        private readonly BookHierarchyBuilder _root;
        private readonly ProjectDbContext _db;
        private readonly List<ChapterSpec> _chapters = new();
        private string? _lastChapterOrder;

        internal PartScope(Guid partId, BookHierarchyBuilder root, ProjectDbContext db)
        {
            _partId = partId;
            _root = root;
            _db = db;
        }

        internal void AddChapterSpec(ChapterSpec spec) => _chapters.Add(spec);

        public PartScope AddChapter(string? name = null, Action<ChapterScope>? configure = null)
        {
            _chapters.Add(new ChapterSpec(name, configure));
            return this;
        }

        internal void Flush()
        {
            if (_chapters.Count == 0)
                _chapters.Add(new ChapterSpec(null, null));

            foreach (var cSpec in _chapters)
            {
                _lastChapterOrder = OrderKeyGenerator.GenerateKeyBetween(_lastChapterOrder, null);
                var chapter = new Chapter
                {
                    Id = Guid.NewGuid(),
                    PartId = _partId,
                    Order = _lastChapterOrder,
                    Title = cSpec.Name,
                };
                if (cSpec.Name != null) _root.Register(cSpec.Name, chapter.Id);
                _db.Chapters.Add(chapter);

                var cScope = new ChapterScope(chapter.Id, _root, _db);
                cSpec.Configure?.Invoke(cScope);
                cScope.Flush();
            }
        }
    }

    public sealed class ChapterScope
    {
        private readonly Guid _chapterId;
        private readonly BookHierarchyBuilder _root;
        private readonly ProjectDbContext _db;
        private readonly List<ParagraphSpec> _paragraphs = new();
        private string? _lastParaOrder;

        internal ChapterScope(Guid chapterId, BookHierarchyBuilder root, ProjectDbContext db)
        {
            _chapterId = chapterId;
            _root = root;
            _db = db;
        }

        public ChapterScope AddParagraph(string? name = null, Action<ParagraphScope>? configure = null)
        {
            _paragraphs.Add(new ParagraphSpec(name, configure));
            return this;
        }

        internal void Flush()
        {
            if (_paragraphs.Count == 0)
                _paragraphs.Add(new ParagraphSpec(null, null));

            foreach (var pSpec in _paragraphs)
            {
                _lastParaOrder = OrderKeyGenerator.GenerateKeyBetween(_lastParaOrder, null);
                var para = new Paragraph
                {
                    Id = Guid.NewGuid(),
                    ChapterId = _chapterId,
                    Order = _lastParaOrder,
                };
                if (pSpec.Name != null) _root.Register(pSpec.Name, para.Id);
                _db.Paragraphs.Add(para);

                var pScope = new ParagraphScope(para.Id, _root, _db);
                pSpec.Configure?.Invoke(pScope);
                pScope.Flush();
            }
        }
    }

    public sealed class ParagraphScope
    {
        private readonly Guid _paragraphId;
        private readonly BookHierarchyBuilder _root;
        private readonly ProjectDbContext _db;
        private readonly List<ItemSpec> _items = new();
        private string? _lastItemOrder;

        internal ParagraphScope(Guid paragraphId, BookHierarchyBuilder root, ProjectDbContext db)
        {
            _paragraphId = paragraphId;
            _root = root;
            _db = db;
        }

        public ParagraphScope AddNarration(string name, string text = "narration text")
        {
            _items.Add(new ItemSpec(name, ParagraphItemType.Narration, text, SpeakerName: null));
            return this;
        }

        public ParagraphScope AddCharacterLine(string name, string text, string speaker)
        {
            _items.Add(new ItemSpec(name, ParagraphItemType.Character, text, SpeakerName: speaker));
            return this;
        }

        public ParagraphScope AddPause(string name, ParagraphItemType kind = ParagraphItemType.Pause)
        {
            _items.Add(new ItemSpec(name, kind, Text: null, SpeakerName: null));
            return this;
        }

        // Adds a raw item with an explicit characterId (null = unassigned). Used for tests
        // that seed Character-type items before assignment (e.g. SetParagraphCharacterCommand tests).
        public ParagraphScope AddRawItem(string name, ParagraphItemType type, string? text, Guid? characterId = null)
        {
            _items.Add(new ItemSpec(name, type, text, SpeakerName: null, CharacterIdOverride: characterId, HasCharacterIdOverride: true));
            return this;
        }

        internal void Flush()
        {
            foreach (var iSpec in _items)
            {
                _lastItemOrder = OrderKeyGenerator.GenerateKeyBetween(_lastItemOrder, null);

                Guid? characterId = iSpec.HasCharacterIdOverride ? iSpec.CharacterIdOverride : iSpec.ItemType switch
                {
                    ParagraphItemType.Narration => ProjectDbContext.NarratorId,
                    ParagraphItemType.Character => iSpec.SpeakerName is { } sn
                        ? _root._characters[sn].Id
                        : throw new InvalidOperationException($"AddCharacterLine '{iSpec.Name}' has no speaker"),
                    _ => null,
                };

                var item = new ParagraphItem
                {
                    Id = Guid.NewGuid(),
                    ParagraphId = _paragraphId,
                    Order = _lastItemOrder,
                    ItemType = iSpec.ItemType,
                    Text = iSpec.Text,
                    CharacterId = characterId,
                };
                _root.Register(iSpec.Name, item.Id);
                _db.ParagraphItems.Add(item);
            }
        }
    }

    // ── spec records ──────────────────────────────────────────────────────────

    private sealed class VolumeSpec(string Name, Action<VolumeScope>? Configure)
    {
        public string Name { get; } = Name;
        public Action<VolumeScope>? Configure { get; } = Configure;
    }

    internal sealed class PartSpec(string? Name, Action<PartScope>? Configure)
    {
        public string? Name { get; } = Name;
        public Action<PartScope>? Configure { get; } = Configure;
        public bool IsImplicit { get; set; }
        public List<ChapterSpec> Chapters { get; } = new();
    }

    internal sealed class ChapterSpec(string? Name, Action<ChapterScope>? Configure)
    {
        public string? Name { get; } = Name;
        public Action<ChapterScope>? Configure { get; } = Configure;
    }

    private sealed class ParagraphSpec(string? Name, Action<ParagraphScope>? Configure)
    {
        public string? Name { get; } = Name;
        public Action<ParagraphScope>? Configure { get; } = Configure;
    }

    private sealed record ItemSpec(
        string Name,
        ParagraphItemType ItemType,
        string? Text,
        string? SpeakerName,
        Guid? CharacterIdOverride = null,
        bool HasCharacterIdOverride = false);

    // ── legacy API (keeps existing callers compiling) ─────────────────────────

    public Project AddProject(string title = "Test Book", string author = "Author",
        BookFileType type = BookFileType.Text, string filename = "book.txt")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Title = title,
            BookTitle = title,
            Author = author,
            Filename = filename,
            Type = type,
        };
        _legacyDb!.Projects.Add(project);
        return project;
    }

    public Volume AddSimpleVolume(string title, int paragraphs = 1)
    {
        var db = _legacyDb!;
        _lastVolumeOrder = OrderKeyGenerator.GenerateKeyBetween(_lastVolumeOrder, null);
        var vol = new Volume { Id = Guid.NewGuid(), Title = title, Order = _lastVolumeOrder };
        var part = new Part { Id = Guid.NewGuid(), VolumeId = vol.Id, Order = LegacyKey() };
        var chapter = new Chapter { Id = Guid.NewGuid(), PartId = part.Id, Order = LegacyKey() };
        db.Volumes.Add(vol);
        db.Parts.Add(part);
        db.Chapters.Add(chapter);

        string? prev = null;
        for (var i = 0; i < paragraphs; i++)
        {
            var paraOrder = OrderKeyGenerator.GenerateKeyBetween(prev, null);
            prev = paraOrder;
            var para = new Paragraph { Id = Guid.NewGuid(), ChapterId = chapter.Id, Order = paraOrder };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = para.Id,
                Order = LegacyKey(),
                ItemType = ParagraphItemType.Narration,
                Text = $"Paragraph {i + 1}",
                CharacterId = ProjectDbContext.NarratorId,
            };
            db.Paragraphs.Add(para);
            db.ParagraphItems.Add(item);
        }
        return vol;
    }

    public Task SaveAsync() => _legacyDb!.SaveChangesAsync();

    private static string LegacyKey() => OrderKeyGenerator.GenerateKeyBetween(null, null);
}
