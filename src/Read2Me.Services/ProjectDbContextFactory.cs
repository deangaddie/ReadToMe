using System.Collections.Concurrent;
using System.IO;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services
{
    public class ProjectDbContextProvider : IProjectDbContextFactory
    {
        private static readonly ConcurrentDictionary<string, bool> _migratedPaths =
            new(StringComparer.OrdinalIgnoreCase);

        private const string DbFileName = "project.db";

        public async Task<ProjectDbContext> CreateAsync(string folderPath)
        {
            var dbPath = Path.Combine(folderPath, DbFileName);
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            if (_migratedPaths.TryAdd(dbPath, true))
                await db.Database.MigrateAsync();
            return db;
        }
    }
}
