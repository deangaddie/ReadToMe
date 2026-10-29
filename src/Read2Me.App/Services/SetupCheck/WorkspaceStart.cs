using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Read2Me.AppData;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// The Workspace as judged: <see cref="Path"/> is the usable folder, or null when it isn't
    /// usable; <see cref="Found"/> is what the Setup check shows (the path, or why it failed).
    /// </summary>
    public sealed record WorkspaceVerdict(string? Path, string Found)
    {
        public bool Usable => Path is not null;
    }

    /// <summary>
    /// What this start did to the Workspace. The first ask judges <c>Workspace:FolderPath</c> and
    /// creates a missing folder; start-up asks before anything opens app.db, and with no usable
    /// Workspace the host starts without one (no migrations, no hosted service that touches
    /// app.db, every API call but the Setup check's refused). The Setup check reads the facts from here.
    /// </summary>
    public sealed class WorkspaceStart
    {
        public const string Key = "Workspace:FolderPath";

        private readonly Lazy<WorkspaceVerdict> _atStart;

        public WorkspaceStart(IConfiguration configuration)
        {
            // Untrimmed: the host uses the value as written, so the judgement is of that same path.
            _atStart = new Lazy<WorkspaceVerdict>(() => Judge(configuration[Key]));
        }

        /// <summary>The Workspace this start runs on, judged at the first ask.</summary>
        public WorkspaceVerdict AtStart => _atStart.Value;

        /// <summary>This start has a usable Workspace; false is no-Workspace mode until the app restarts.</summary>
        public bool Usable => AtStart.Usable;

        /// <summary>The usable folder; throws in no-Workspace mode, so nothing opens a relative app.db.</summary>
        public string FolderPath => AtStart.Path
            ?? throw new InvalidOperationException($"The app started without a usable Workspace: {AtStart.Found}.");

        /// <summary>The folder didn't exist and the app created it.</summary>
        public bool CreatedFolder { get; private set; }

        /// <summary>This start created app.db: the first run ever.</summary>
        public bool CreatedDatabase { get; private set; }

        /// <summary>
        /// Judges a Workspace path: it must be set, absolute, not a file, and a writable folder. A
        /// missing folder is created (remembered in <see cref="CreatedFolder"/>) and passes.
        /// </summary>
        public WorkspaceVerdict Judge(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new(null, $"{Key} isn't set");
            if (!System.IO.Path.IsPathFullyQualified(path))
                return new(null, $"{path} is a relative path");
            if (File.Exists(path))
                return new(null, $"{path} is a file");

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    CreatedFolder = true;
                }
            }
            catch (Exception ex)
            {
                return new(null, $"{path} can't be created: {ex.Message}");
            }

            try
            {
                var probe = System.IO.Path.Combine(path, $".setup-check-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                return new(null, $"{path} isn't writable: {ex.Message}");
            }

            return new(path, path);
        }

        /// <summary>
        /// Migrates app.db, remembering whether this start created it (no migration had been
        /// applied), and answers that: a new database is then seeded.
        /// </summary>
        public bool MigrateDatabase(Read2MeDbContext db)
        {
            var isNew = !db.Database.GetAppliedMigrations().Any();
            db.Database.Migrate();
            CreatedDatabase = isNew;
            return isNew;
        }
    }
}
