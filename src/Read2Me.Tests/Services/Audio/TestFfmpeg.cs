namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// The two things every post-process step test needs: a path that is certainly not ffmpeg (which
    /// exercises the never-throw fallback without touching the disk), and the PATH probe the
    /// real-ffmpeg tests gate on.
    /// </summary>
    internal static class TestFfmpeg
    {
        public static string BogusPath() =>
            Path.Combine(Path.GetTempPath(), $"definitely-not-ffmpeg-{Guid.NewGuid():N}.exe");

        public static bool Available()
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
