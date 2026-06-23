using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;

namespace Read2Me.Tests.Infrastructure
{
    /// <summary>
    /// IDbContextFactory&lt;Read2MeDbContext&gt; backed by a shared in-memory SQLite connection.
    /// Connection kept open so schema survives between context instances.
    /// </summary>
    public abstract class AppDbTestBase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        protected IDbContextFactory<Read2MeDbContext> Factory { get; }

        protected AppDbTestBase()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite(_connection)
                .Options;

            using (var db = new Read2MeDbContext(options))
                db.Database.EnsureCreated();

            Factory = new TestFactory(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private sealed class TestFactory : IDbContextFactory<Read2MeDbContext>
        {
            private readonly DbContextOptions<Read2MeDbContext> _options;
            public TestFactory(DbContextOptions<Read2MeDbContext> options) => _options = options;
            public Read2MeDbContext CreateDbContext() => new(_options);
            public Task<Read2MeDbContext> CreateDbContextAsync(CancellationToken ct = default)
                => Task.FromResult(new Read2MeDbContext(_options));
        }
    }
}
