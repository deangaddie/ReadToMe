using NSubstitute;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Voice;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class RecentAudioSampleFinderTests
    {
        private const string Root = "C:\\fake-workspace";

        private readonly FakeFileSystem _fs = new(Root);
        private readonly IPreviewSourceCache _previewSources;
        private readonly IProjectCatalogReader _catalog = Substitute.For<IProjectCatalogReader>();
        private readonly IAudioItemReader _items = Substitute.For<IAudioItemReader>();
        private readonly IVoiceResolver _voices = Substitute.For<IVoiceResolver>();

        private readonly Dictionary<string, List<AudioSampleInfo>> _rows = [];

        public RecentAudioSampleFinderTests()
        {
            _previewSources = new PreviewSourceCache(_fs);
            _items.GetAudioSampleInfosAsync(Arg.Any<ProjectFolderId>(), Arg.Any<IReadOnlyCollection<Guid>>())
                .Returns(ci =>
                {
                    var folder = ci.ArgAt<ProjectFolderId>(0).Value;
                    var ids = ci.ArgAt<IReadOnlyCollection<Guid>>(1);
                    var rows = _rows.TryGetValue(folder, out var r) ? r : [];
                    return Task.FromResult<IReadOnlyList<AudioSampleInfo>>(
                        rows.Where(x => ids.Contains(x.ParagraphItemId)).ToList());
                });
        }

        private RecentAudioSampleFinder Finder() => new(_previewSources, _catalog, _items, _voices);

        [Fact]
        public async Task Orders_by_preview_source_recency_across_projects_and_applies_limit()
        {
            // The cache writes in call order, so the last item seeded is the newest.
            var oldest = await SeedItemAsync("book-a", "Alice", "line one");
            var middle = await SeedItemAsync("book-a", "Alice", "line three");
            var newest = await SeedItemAsync("book-b", "Bob", "line two");
            SeedProjects(("book-a", "Book A"), ("book-b", "Book B"));

            var samples = await Finder().FindAsync(limit: 2);

            Assert.Equal([newest, middle], samples.Select(s => s.ParagraphItemId));
            Assert.Equal("Book B", samples[0].ProjectTitle);
            Assert.Equal("book-b", samples[0].Folder.Value);
            Assert.DoesNotContain(oldest, samples.Select(s => s.ParagraphItemId));
        }

        [Fact]
        public async Task Fills_row_details_from_reader_and_voice_resolver()
        {
            var id = await SeedItemAsync("book-a", "Alice", "hello there");
            SeedProjects(("book-a", "Book A"));
            _voices.ResolveNamesAsync(new ProjectFolderId("book-a"), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, string?> { [id] = "Warm Alto" });

            var sample = Assert.Single(await Finder().FindAsync(limit: 20));

            Assert.Equal("hello there", sample.Text);
            Assert.Equal("Alice", sample.CharacterName);
            Assert.Equal("Warm Alto", sample.VoiceName);
        }

        [Fact]
        public async Task Skips_preview_sources_with_no_matching_item_row()
        {
            // An orphan source (item deleted, file left behind) must not become a picker row.
            await _previewSources.SaveAsync(new ProjectFolderId("book-a"), Guid.NewGuid(), [1]);
            SeedProjects(("book-a", "Book A"));

            Assert.Empty(await Finder().FindAsync(limit: 20));
        }

        [Fact]
        public async Task Skips_preview_sources_whose_project_is_gone()
        {
            // The cache outlives the project — opening its DB would throw, so it must not be tried.
            await SeedItemAsync("deleted-book", "Alice", "line one");
            SeedProjects(("book-a", "Book A"));

            Assert.Empty(await Finder().FindAsync(limit: 20));
            await _items.DidNotReceiveWithAnyArgs().GetAudioSampleInfosAsync(default, default!);
        }

        [Fact]
        public async Task Empty_cache_touches_no_project_db()
        {
            SeedProjects(("book-a", "Book A"));

            Assert.Empty(await Finder().FindAsync(limit: 20));
            await _items.DidNotReceiveWithAnyArgs().GetAudioSampleInfosAsync(default, default!);
        }

        private void SeedProjects(params (string Folder, string Title)[] projects)
        {
            _catalog.GetProjects().Returns(projects.Select(p => p.Folder).ToList());
            _catalog.GetProjectSummariesAsync().Returns(
                projects.Select(p => new ProjectSummary(p.Folder, p.Title)).ToList());
        }

        private async Task<Guid> SeedItemAsync(string folder, string character, string text)
        {
            var id = Guid.NewGuid();
            await _previewSources.SaveAsync(new ProjectFolderId(folder), id, [1, 2, 3]);

            if (!_rows.TryGetValue(folder, out var rows))
                _rows[folder] = rows = [];
            rows.Add(new AudioSampleInfo(id, text, character));
            return id;
        }
    }
}
