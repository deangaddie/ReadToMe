namespace Read2Me.App.Services.SetupCheck
{
    /// <summary>
    /// What happened to the Workspace folder as the host started: start-up creates a missing
    /// folder before the first Quick check runs, so the Workspace check reads the fact from here.
    /// </summary>
    public sealed class WorkspaceStart
    {
        /// <summary>The folder didn't exist and this start created it.</summary>
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
