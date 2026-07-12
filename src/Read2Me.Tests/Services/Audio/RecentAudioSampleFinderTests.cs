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
        private readonly IProjectCatalogReader _catalog = Substitute.For<IProjectCatalogReader>();
        private readonly IAudioItemReader _items = Substitute.For<IAudioItemReader>();
        private readonly IVoiceResolver _voices = Substitute.For<IVoiceResolver>();

        private readonly Dictionary<string, List<AudioSampleInfo>> _rows = [];

        public RecentAudioSampleFinderTests()
        {
            _items.GetAudioSampleInfosAsync(Arg.Any<ProjectFolderId>(), Arg.Any<IReadOnlyCollection<Guid>>())
                .Returns(ci =>
                {
                    var folder = ((ProjectFolderId)ci[0]).Value;
                    var ids = (IReadOnlyCollection<Guid>)ci[1];
                    var rows = _rows.TryGetValue(folder, out var r) ? r : [];
                    return Task.FromResult<IReadOnlyList<AudioSampleInfo>>(
                        rows.Where(x => ids.Contains(x.ParagraphItemId)).ToList());
                });
        }

        private RecentAudioSampleFinder Finder() => new(_fs, _catalog, _items, _voices);

        [Fact]
        public async Task Orders_by_audio_write_time_across_projects_and_applies_limit()
        {
            var oldest = SeedItem("book-a", "Alice", "line one", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newest = SeedItem("book-b", "Bob", "line two", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
            var middle = SeedItem("book-a", "Alice", "line three", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
            SeedProjects(("book-a", "Book A"), ("book-b", "Book B"));

            var samples = await Finder().FindAsync(limit: 2);

            Assert.Equal([newest, middle], samples.Select(s => s.ParagraphItemId));
            Assert.Equal("Book B", samples[0].ProjectTitle);
            Assert.Equal("book-b", samples[0].FolderName);
            Assert.DoesNotContain(oldest, samples.Select(s => s.ParagraphItemId));
        }

        [Fact]
        public async Task Fills_row_details_from_reader_and_voice_resolver()
        {
            var id = SeedItem("book-a", "Alice", "hello there", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            SeedProjects(("book-a", "Book A"));
            _voices.ResolveNamesAsync(new ProjectFolderId("book-a"), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(new Dictionary<Guid, string?> { [id] = "Warm Alto" });

            var sample = Assert.Single(await Finder().FindAsync(limit: 20));

            Assert.Equal("hello there", sample.Text);
            Assert.Equal("Alice", sample.CharacterName);
            Assert.Equal("Warm Alto", sample.VoiceName);
            Assert.Equal($"audio/{id}.wav", sample.AudioRelativePath);
        }

        [Fact]
        public async Task Skips_wavs_with_no_matching_item_row()
        {
            // An orphan WAV (item deleted, file left behind) must not become a picker row.
            _fs.SeedFile(Path.Combine(Root, "book-a", "audio", $"{Guid.NewGuid()}.wav"), [1], DateTime.UnixEpoch);
            SeedProjects(("book-a", "Book A"));

            Assert.Empty(await Finder().FindAsync(limit: 20));
        }

        [Fact]
        public async Task Ignores_project_folders_with_no_audio_directory()
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

        private Guid SeedItem(string folder, string character, string text, DateTime writtenUtc)
        {
            var id = Guid.NewGuid();
            _fs.SeedFile(Path.Combine(Root, folder, "audio", $"{id}.wav"), [1, 2, 3], writtenUtc);

            if (!_rows.TryGetValue(folder, out var rows))
                _rows[folder] = rows = [];
            rows.Add(new AudioSampleInfo(id, text, character, $"audio/{id}.wav"));
            return id;
        }
    }
}
