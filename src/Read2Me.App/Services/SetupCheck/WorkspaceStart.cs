namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// Whether the app created the Workspace folder: start-up creates a missing folder before the
    /// first Quick check runs (and the Workspace check creates one removed since), so the check
    /// reads the fact from here.
    /// </summary>
    public sealed class WorkspaceStart
    {
        /// <summary>The folder didn't exist and the app created it.</summary>
        public bool CreatedFolder { get; private set; }

        /// <summary>Creates the folder when it is missing, remembering that it did.</summary>
        public void EnsureFolder(string path)
        {
            if (Directory.Exists(path))
                return;
            Directory.CreateDirectory(path);
            CreatedFolder = true;
        }
    }
}
