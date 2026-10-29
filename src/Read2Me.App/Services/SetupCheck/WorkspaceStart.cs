using Microsoft.EntityFrameworkCore;
using Read2Me.AppData;

namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What this start did to the Workspace: start-up creates a missing folder and migrates app.db
    /// before the first Quick check runs (and the Workspace check creates a folder removed since),
    /// so the check reads the facts from here.
    /// </summary>
    public sealed class WorkspaceStart
    {
        /// <summary>The folder didn't exist and the app created it.</summary>
        public bool CreatedFolder { get; private set; }

        /// <summary>This start created app.db: the first run ever.</summary>
        public bool CreatedDatabase { get; private set; }

        /// <summary>Creates the folder when it is missing, remembering that it did.</summary>
        public void EnsureFolder(string path)
        {
            if (Directory.Exists(path))
                return;
            Directory.CreateDirectory(path);
            CreatedFolder = true;
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
