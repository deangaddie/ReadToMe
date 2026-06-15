using System.IO;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Services
{
    public class ProjectDbContextProvider : IProjectDbContextFactory
    {
        private const string DbFileName = "project.db";

        public async Task<ProjectDbContext> CreateAsync(string folderPath)
        {
            Directory.CreateDirectory(folderPath);
            var dbPath = Path.Combine(folderPath, DbFileName);
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }
    }
}
