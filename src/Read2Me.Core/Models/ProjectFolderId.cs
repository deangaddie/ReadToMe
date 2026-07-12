namespace Read2Me.Core.Models;

public readonly record struct ProjectFolderId(string Value)
{
    public override string ToString() => Value;

    public static implicit operator string(ProjectFolderId id) => id.Value;
    public static implicit operator ProjectFolderId(string value) => new(value);

    /// <summary>
    /// Parses a folder name that came from outside the app (a URL, say). A project folder is one
    /// path segment: anything with separators or a <c>..</c> walk in it cannot name a project, and
    /// refusing it here keeps it out of any path it would later be combined into. The implicit
    /// string conversion means the type itself is not a gate — this is the gate.
    /// </summary>
    public static bool TryParse(string? value, out ProjectFolderId id)
    {
        id = default;

        if (string.IsNullOrEmpty(value) || Path.GetFileName(value) != value)
            return false;

        id = new ProjectFolderId(value);
        return true;
    }
}
