namespace Read2Me.Data
{
    public interface IProjectDbContextFactory
    {
        Task<ProjectDbContext> CreateAsync(string folderPath);
    }
}
