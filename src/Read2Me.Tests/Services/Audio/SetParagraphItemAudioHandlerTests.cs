using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class SetParagraphItemAudioHandlerTests : ProjectDbTestBase
    {
        private readonly BookCommandHandler _svc;
        private readonly ProjectFolderId _folder;

        public SetParagraphItemAudioHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            var sp = services.BuildServiceProvider();

            _svc = sp.GetRequiredService<BookCommandHandler>();
            _folder = new ProjectFolderId(FolderName);
        }

        private async Task<Guid> SeedItemAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v.AddChapter(configure: c => c
                .AddParagraph(configure: p => p.AddNarration("item", "Hello"))))
                .BuildAsync();
            return b.ItemId("item");
        }

        [Fact]
        public async Task SetParagraphItemAudio_SetsAudioFileName()
        {
            var itemId = await SeedItemAsync();

            await _svc.ExecuteAsync(new SetParagraphItemAudioCommand(_folder, itemId, "audio/abc.wav"));

            await using var verify = await OpenDbAsync();
            var updated = await verify.ParagraphItems
                .AsNoTracking()
                .FirstAsync(i => i.Id == itemId);
            Assert.Equal("audio/abc.wav", updated.AudioFileName);
        }

        [Fact]
        public async Task SetParagraphItemAudio_UnknownItem_ReturnsNull()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.BuildAsync();

            var result = await _svc.ExecuteAsync(new SetParagraphItemAudioCommand(_folder, Guid.NewGuid(), "audio/x.wav"));
            Assert.Null(result);
        }
    }
}
