using Read2Me.Services.Books;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// Finding a node in a loaded <see cref="BookHierarchy"/>, which is how a structural mutation tells
/// "the Book does not contain this" apart from "there was nothing to do here".
/// </summary>
internal static class HierarchyLookup
{
    /// <summary>
    /// The node a child currently hangs off, or null when the Book does not contain the child.
    /// <para>
    /// Read before planning, never after: a planner reassigns children, so afterwards a child no
    /// longer knows where it came from. <see cref="BookHierarchy"/> groups children by parent id, so
    /// the parent is simply the key the child is found under.
    /// </para>
    /// </summary>
    public static Guid? OwnerOf<T>(Dictionary<Guid, List<T>> childrenByParent, Guid childId, Func<T, Guid> idOf)
    {
        foreach (var (parentId, children) in childrenByParent)
            if (children.Any(child => idOf(child) == childId)) return parentId;
        return null;
    }
}
