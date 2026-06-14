namespace Read2Me.Core.Models;

public readonly record struct ProjectFolderId(string Value)
{
    public override string ToString() => Value;

    public static implicit operator string(ProjectFolderId id) => id.Value;
    public static implicit operator ProjectFolderId(string value) => new(value);
}
