using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Read2Me.Services.UseCases;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.UseCases
{
    public class BookUseCasesTests : ProjectDbTestBase
    {
        private (BookUseCases sut, IBookCommandHandler commandHandler) Build()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            var epubReader = new EpubFileReader(NullLogger<EpubFileReader>.Instance);
            var textReader = new TextFileReader(NullLogger<TextFileReader>.Instance);
            var persister = Substitute.For<IBookContentPersister>();
            var readingService = new BookReadingService(session, epubReader, textReader, persister, NullLogger<BookReadingService>.Instance);
            var commandHandler = Substitute.For<IBookCommandHandler>();
            var sut = new BookUseCases(readingService, commandHandler, session);
            return (sut, commandHandler);
        }

        [Fact]
        public async Task ImportAsync_WithReread_IssuesClearCommandFirst()
        {
            var (sut, commandHandler) = Build();
            // No project record in db -> ReadBookAsync throws InvalidOperationException
            // which is still caught; what we care about is Clear was called first.
            await sut.ImportAsync(FolderName, reread: true);

            await commandHandler.Received(1).ExecuteAsync(
                Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ImportAsync_WithoutReread_DoesNotIssueClearCommand()
        {
            var (sut, commandHandler) = Build();
            await sut.ImportAsync(FolderName, reread: false);

            await commandHandler.DidNotReceive().ExecuteAsync(
                Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ImportAsync_WhenNoProjectRecord_ReturnsFailure()
        {
            // Seed only the DB schema (no Project row) so ReadBookAsync throws InvalidOperationException.
            await using var _ = await OpenDbAsync();

            var (sut, _) = Build();
            var result = await sut.ImportAsync(FolderName);

            Assert.False(result.IsSuccess);
            Assert.Contains("No project record found", result.Error);
        }

        [Fact]
        public async Task ImportAsync_WhenCommandHandlerThrows_ReturnsFailure()
        {
            var (sut, commandHandler) = Build();
            commandHandler.ExecuteAsync(Arg.Any<ClearBookContentCommand>(), Arg.Any<CancellationToken>())
                .ThrowsForAnyArgs(new Exception("db locked"));

            var result = await sut.ImportAsync(FolderName, reread: true);

            Assert.False(result.IsSuccess);
        }
    }
}
