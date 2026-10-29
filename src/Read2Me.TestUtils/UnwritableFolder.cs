using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Read2Me.TestUtils;

/// <summary>
/// A folder this process can list but can't create files in: on Windows an ACL entry denying the
/// current user file creation, elsewhere read-and-list permissions only. Disposing lifts the
/// restriction and deletes the folder.
/// </summary>
public sealed class UnwritableFolder : IDisposable
{
    public string Path { get; }

    public UnwritableFolder(string path)
    {
        Path = path;
        var dir = Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            var security = dir.GetAccessControl();
            security.AddAccessRule(Rule());
            dir.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;
        var dir = new DirectoryInfo(Path);
        if (OperatingSystem.IsWindows())
        {
            var security = dir.GetAccessControl();
            security.RemoveAccessRule(Rule());
            dir.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        dir.Delete(recursive: true);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule Rule() =>
        new(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.WriteData,
            AccessControlType.Deny);
}
