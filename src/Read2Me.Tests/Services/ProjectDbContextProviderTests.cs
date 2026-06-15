using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectDbContextProviderTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly ProjectDbContextProvider _provider;

        public ProjectDbContextProviderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "ProviderTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _provider = new ProjectDbContextProvider();
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Fact]
        public async Task CreateAsync_RecreatedFolder_HasSchema()
        {
            var folderPath = Path.Combine(_tempDir, "myproject");

            // First creation: migrate
            await using (var db1 = await _provider.CreateAsync(folderPath))
                Assert.False(await db1.Volumes.AnyAsync());

            // Delete folder
            Directory.Delete(folderPath, recursive: true);

            // Recreate at same path — must migrate again without static cache blocking it
            await using var db2 = await _provider.CreateAsync(folderPath);
            Assert.False(await db2.Volumes.AnyAsync());
        }
    }
}
