using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.Services;

public class BookContentPersisterTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ProjectDbContext _db;
    private readonly BookContentPersister _sut = new();

    public BookContentPersisterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"Read2MePersisterTest_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=false")
            .Options;
        _db = new ProjectDbContext(options);
        _db.Database.Migrate();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    // ---------------------------------------------------------------
    // Import stamps the narrator on narration items — the invariant the
    // backfill migration extends to every already-imported book.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Persist_StampsTheNarrator_OnNarrationItemsAndLeavesDialogUnattributed()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [
                        new ParagraphContent("He walked on. “Hello,” she said."),
                    ])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        // Import records the splitter's decision on the speaker, not on a type: narration gets the
        // narrator, dialog is left for the attribution queue (ADR-0006). Both are Speech items.
        var items = await _db.ParagraphItems.AsNoTracking().ToListAsync();
        Assert.All(items, i => Assert.Equal(ParagraphItemType.Speech, i.ItemType));
        Assert.Contains(items, i => i.CharacterId == ProjectDbContext.NarratorId);
        Assert.Contains(items, i => i.CharacterId is null);
    }

    // ---------------------------------------------------------------
    // Global ordering — all Order keys form a strictly ascending sequence
    // when traversed: volume → part → chapter → paragraph → items
    // ---------------------------------------------------------------

    [Fact]
    public async Task SingleVolumePart_GlobalOrderIsStrictlyAscending()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [
                        new ParagraphContent("Hello world."),
                        new ParagraphContent("Second paragraph."),
                    ])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var allKeys = await CollectAllKeysInTraversalOrderAsync(_db);
        AssertStrictlyAscending(allKeys);
    }

    [Fact]
    public async Task MultipleVolumes_GlobalOrderIsStrictlyAscending()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part A", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Line 1.")])
                ]),
                new PartContent("Part B", [
                    new ChapterContent("Ch 2", [new ParagraphContent("Line 2.")])
                ]),
            ]),
            new VolumeContent("Vol 2", [
                new PartContent("Part C", [
                    new ChapterContent("Ch 3", [new ParagraphContent("Line 3.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var allKeys = await CollectAllKeysInTraversalOrderAsync(_db);
        AssertStrictlyAscending(allKeys);
    }

    // ---------------------------------------------------------------
    // Cross-level ordering: last entity of level N < first entity of N+1
    // ---------------------------------------------------------------

    [Fact]
    public async Task PartOrder_IsGreaterThan_PrecedingVolumeOrder()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Text.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var vol = await _db.Volumes.SingleAsync();
        var part = await _db.Parts.SingleAsync();

        Assert.True(string.Compare(vol.Order, part.Order, StringComparison.Ordinal) < 0,
            $"Volume order '{vol.Order}' should be < Part order '{part.Order}'");
    }

    [Fact]
    public async Task ChapterOrder_IsGreaterThan_PrecedingPartOrder()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Text.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var part = await _db.Parts.SingleAsync();
        var chapter = await _db.Chapters.SingleAsync();

        Assert.True(string.Compare(part.Order, chapter.Order, StringComparison.Ordinal) < 0,
            $"Part order '{part.Order}' should be < Chapter order '{chapter.Order}'");
    }

    [Fact]
    public async Task ParagraphOrder_IsGreaterThan_PrecedingChapterOrder()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Text.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var chapter = await _db.Chapters.SingleAsync();
        var para = await _db.Paragraphs.SingleAsync();

        Assert.True(string.Compare(chapter.Order, para.Order, StringComparison.Ordinal) < 0,
            $"Chapter order '{chapter.Order}' should be < Paragraph order '{para.Order}'");
    }

    [Fact]
    public async Task ItemOrder_IsGreaterThan_PrecedingParagraphOrder()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Text.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var para = await _db.Paragraphs.SingleAsync();
        var item = await _db.ParagraphItems.SingleAsync();

        Assert.True(string.Compare(para.Order, item.Order, StringComparison.Ordinal) < 0,
            $"Paragraph order '{para.Order}' should be < Item order '{item.Order}'");
    }

    // ---------------------------------------------------------------
    // Second part of Vol 1 must sort after last item of first part
    // ---------------------------------------------------------------

    [Fact]
    public async Task SecondPart_OrderGreaterThan_LastItemOfFirstPart()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [
                        new ParagraphContent("Para 1."),
                        new ParagraphContent("Para 2."),
                    ])
                ]),
                new PartContent("Part 2", [
                    new ChapterContent("Ch 2", [new ParagraphContent("Para 3.")])
                ]),
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var parts = await _db.Parts.OrderBy(p => p.Order).ToListAsync();
        var lastItemOfPart1 = await _db.ParagraphItems
            .Include(i => i.Paragraph).ThenInclude(p => p.Chapter).ThenInclude(c => c.Part)
            .Where(i => i.Paragraph.Chapter.Part.Order == parts[0].Order)
            .OrderByDescending(i => i.Order)
            .FirstAsync();

        Assert.True(string.Compare(lastItemOfPart1.Order, parts[1].Order, StringComparison.Ordinal) < 0,
            $"Last item of Part 1 '{lastItemOfPart1.Order}' should be < Part 2 order '{parts[1].Order}'");
    }

    [Fact]
    public async Task SecondVolume_OrderGreaterThan_LastItemOfFirstVolume()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [new ParagraphContent("Para 1.")])
                ])
            ]),
            new VolumeContent("Vol 2", [
                new PartContent("Part 2", [
                    new ChapterContent("Ch 2", [new ParagraphContent("Para 2.")])
                ])
            ]),
        ]);

        await _sut.PersistAsync(_db, content);

        var volumes = await _db.Volumes.OrderBy(v => v.Order).ToListAsync();
        var lastItemOfVol1 = await _db.ParagraphItems
            .Include(i => i.Paragraph).ThenInclude(p => p.Chapter).ThenInclude(c => c.Part).ThenInclude(p => p.Volume)
            .Where(i => i.Paragraph.Chapter.Part.Volume.Order == volumes[0].Order)
            .OrderByDescending(i => i.Order)
            .FirstAsync();

        Assert.True(string.Compare(lastItemOfVol1.Order, volumes[1].Order, StringComparison.Ordinal) < 0,
            $"Last item of Vol 1 '{lastItemOfVol1.Order}' should be < Vol 2 order '{volumes[1].Order}'");
    }

    // ---------------------------------------------------------------
    // All keys unique
    // ---------------------------------------------------------------

    [Fact]
    public async Task AllOrderKeys_AreUnique()
    {
        var content = new BookContent([
            new VolumeContent("Vol 1", [
                new PartContent("Part 1", [
                    new ChapterContent("Ch 1", [
                        new ParagraphContent("Para 1."),
                        new ParagraphContent("Para 2."),
                    ]),
                    new ChapterContent("Ch 2", [new ParagraphContent("Para 3.")])
                ]),
                new PartContent("Part 2", [
                    new ChapterContent("Ch 3", [new ParagraphContent("Para 4.")])
                ])
            ])
        ]);

        await _sut.PersistAsync(_db, content);

        var allKeys = await CollectAllKeysInTraversalOrderAsync(_db);
        Assert.Equal(allKeys.Count, allKeys.Distinct().Count());
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static async Task<List<string>> CollectAllKeysInTraversalOrderAsync(ProjectDbContext db)
    {
        var keys = new List<string>();

        var volumes = await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        foreach (var vol in volumes)
        {
            keys.Add(vol.Order);
            var parts = await db.Parts.Where(p => p.VolumeId == vol.Id).OrderBy(p => p.Order).ToListAsync();
            foreach (var part in parts)
            {
                keys.Add(part.Order);
                var chapters = await db.Chapters.Where(c => c.PartId == part.Id).OrderBy(c => c.Order).ToListAsync();
                foreach (var ch in chapters)
                {
                    keys.Add(ch.Order);
                    var paras = await db.Paragraphs.Where(p => p.ChapterId == ch.Id).OrderBy(p => p.Order).ToListAsync();
                    foreach (var para in paras)
                    {
                        keys.Add(para.Order);
                        var items = await db.ParagraphItems.Where(i => i.ParagraphId == para.Id).OrderBy(i => i.Order).ToListAsync();
                        keys.AddRange(items.Select(i => i.Order));
                    }
                }
            }
        }

        return keys;
    }

    private static void AssertStrictlyAscending(List<string> keys)
    {
        for (int i = 1; i < keys.Count; i++)
            Assert.True(string.Compare(keys[i - 1], keys[i], StringComparison.Ordinal) < 0,
                $"Key at [{i - 1}] '{keys[i - 1]}' is not < key at [{i}] '{keys[i]}'");
    }
}
