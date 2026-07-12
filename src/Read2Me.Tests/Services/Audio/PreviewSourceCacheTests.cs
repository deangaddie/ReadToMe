using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class PreviewSourceCacheTests
    {
        private const string Root = "C:\\fake-workspace";

        private readonly FakeFileSystem _fs = new(Root);
        private readonly IPreviewSourceCache _sut;

        public PreviewSourceCacheTests() => _sut = new PreviewSourceCache(_fs);

        [Fact]
        public async Task Round_trips_a_preview_source()
        {
            var id = Guid.NewGuid();

            await _sut.SaveAsync(new ProjectFolderId("book-a"), id, [1, 2, 3]);

            Assert.True(_sut.TryGetPath("book-a", id, out var path));
            Assert.Equal([1, 2, 3], _fs.GetFileContent(path!));
        }

        [Fact]
        public async Task Regenerating_an_item_overwrites_its_entry()
        {
            var id = Guid.NewGuid();
            var folder = new ProjectFolderId("book-a");

            await _sut.SaveAsync(folder, id, [1]);
            await _sut.SaveAsync(folder, id, [2]);

            var entry = Assert.Single(_sut.List());
            Assert.Equal([2], _fs.GetFileContent(entry.Path));
        }

        [Fact]
        public async Task Lists_newest_first_and_keeps_folder_and_item_apart()
        {
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            await _sut.SaveAsync(new ProjectFolderId("book-a"), first, [1]);
            await _sut.SaveAsync(new ProjectFolderId("book-b"), second, [2]);

            var entries = _sut.List();

            Assert.Equal([second, first], entries.Select(e => e.ParagraphItemId));
            Assert.Equal(["book-b", "book-a"], entries.Select(e => e.Folder.Value));
        }

        [Fact]
        public async Task A_folder_name_containing_the_separator_still_parses()
        {
            // The id is fixed-length and parsed from the right, so the folder name cannot shift the split.
            var id = Guid.NewGuid();

            await _sut.SaveAsync(new ProjectFolderId("my__book"), id, [1]);

            var entry = Assert.Single(_sut.List());
            Assert.Equal("my__book", entry.Folder.Value);
            Assert.Equal(id, entry.ParagraphItemId);
        }

        [Fact]
        public async Task Evicts_the_oldest_beyond_capacity()
        {
            var folder = new ProjectFolderId("book-a");
            var ids = new List<Guid>();
            for (var i = 0; i < PreviewSourceCache.Capacity + 5; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                await _sut.SaveAsync(folder, id, [1]);
            }

            var entries = _sut.List();

            Assert.Equal(PreviewSourceCache.Capacity, entries.Count);
            // The five oldest are gone; the newest survive.
            Assert.All(ids.Take(5), id => Assert.False(_sut.TryGetPath("book-a", id, out _)));
            Assert.All(ids.TakeLast(PreviewSourceCache.Capacity), id => Assert.True(_sut.TryGetPath("book-a", id, out _)));
        }

        [Theory]
        [InlineData("../../etc")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("")]
        public async Task Refuses_a_folder_name_that_is_not_a_bare_path_segment(string folder)
        {
            // The name arrives from a URL, so it must never be combined into a path unchecked.
            var id = Guid.NewGuid();
            await _sut.SaveAsync(new ProjectFolderId("book-a"), id, [1]);

            Assert.False(_sut.TryGetPath(folder, id, out var path));
            Assert.Null(path);
        }

        [Fact]
        public void Missing_entry_yields_no_path()
        {
            Assert.False(_sut.TryGetPath("book-a", Guid.NewGuid(), out var path));
            Assert.Null(path);
        }
    }
}
