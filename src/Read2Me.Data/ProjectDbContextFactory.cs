using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Read2Me.Data
{
    public class ProjectDbContextFactory : IDesignTimeDbContextFactory<ProjectDbContext>
    {
        public ProjectDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite("Data Source=project.db;Pooling=false")
                .Options;
            return new ProjectDbContext(options);
        }
    }
}
