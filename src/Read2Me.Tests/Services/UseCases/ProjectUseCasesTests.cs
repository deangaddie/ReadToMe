using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.UseCases;
using Xunit;

namespace Read2Me.Tests.Services.UseCases
{
    public class ProjectUseCasesTests
    {
        private readonly IProjectReader _reader = Substitute.For<IProjectReader>();
        private readonly IProjectWriter _writer = Substitute.For<IProjectWriter>();
        private ProjectUseCases Sut => new(_reader, _writer);

        [Fact]
        public async Task CreateAsync_OnSuccess_ReturnsFolderName()
        {
            _writer.CreateProjectAsync(default!, default!, default!, default!, default!, default)
                .ReturnsForAnyArgs("my-folder");

            var result = await Sut.CreateAsync("t", "b", "a", "f.txt", Stream.Null, BookFileType.Text);

            Assert.True(result.IsSuccess);
            Assert.Equal("my-folder", result.Value);
        }

        [Fact]
        public async Task CreateAsync_WhenWriterThrowsInvalidOperation_ReturnsThatMessage()
        {
            _writer.CreateProjectAsync(default!, default!, default!, default!, default!, default)
                .ThrowsForAnyArgs(new InvalidOperationException("folder exists"));

            var result = await Sut.CreateAsync("t", "b", "a", "f.txt", Stream.Null, BookFileType.Text);

            Assert.False(result.IsSuccess);
            Assert.Equal("folder exists", result.Error);
        }

        [Fact]
        public async Task CreateAsync_WhenWriterThrowsIO_ReturnsFriendlyMessage()
        {
            _writer.CreateProjectAsync(default!, default!, default!, default!, default!, default)
                .ThrowsForAnyArgs(new IOException("disk full"));

            var result = await Sut.CreateAsync("t", "b", "a", "f.txt", Stream.Null, BookFileType.Text);

            Assert.False(result.IsSuccess);
            Assert.Equal("Failed to save book file. Please try again.", result.Error);
        }

        [Fact]
        public async Task GetSummariesAsync_OnSuccess_ReturnsSummaries()
        {
            var summaries = new List<ProjectSummary>();
            _reader.GetProjectSummariesAsync().Returns(summaries);

            var result = await Sut.GetSummariesAsync();

            Assert.True(result.IsSuccess);
            Assert.Same(summaries, result.Value);
        }

        [Fact]
        public async Task GetSummariesAsync_WhenReaderThrows_ReturnsFailure()
        {
            _reader.GetProjectSummariesAsync().ThrowsForAnyArgs(new Exception("boom"));

            var result = await Sut.GetSummariesAsync();

            Assert.False(result.IsSuccess);
            Assert.Equal("Failed to load projects.", result.Error);
        }

        [Fact]
        public void DeleteProject_OnSuccess_ReturnsOk()
        {
            var result = Sut.DeleteProject("my-folder");
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void DeleteProject_WhenWriterThrows_ReturnsFailure()
        {
            _writer.When(w => w.DeleteProject(Arg.Any<ProjectFolderId>()))
                   .Do(_ => throw new Exception("access denied"));

            var result = Sut.DeleteProject("my-folder");

            Assert.False(result.IsSuccess);
        }
    }
}
