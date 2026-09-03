namespace Read2Me.Services.Mutations;

/// <summary>
/// Whether the affected identifiers on <see cref="BookMutationEffects"/> are exhaustive.
/// <see cref="Exact"/> means they name everything the mutation touched; <see cref="WholeProject"/>
/// means they do not, and is the safe answer whenever an implementation cannot enumerate what it
/// touched.
/// <para>
/// Scope says nothing about whether a targeted refresh is <em>sufficient</em> — that is a read-side
/// judgement made from the facets as well. An insertion is exact about which Paragraph moved and
/// still structural.
/// </para>
/// </summary>
public enum BookMutationScope { Exact, WholeProject }

/// <summary>
/// Which kinds of Book data a mutation changed. Facets are facts about the write, not
/// instructions: a reader decides for itself what to reread from them.
/// </summary>
[Flags]
public enum BookFacets
{
    None = 0,
    /// <summary>
    /// Nodes created, deleted, moved, split or merged. Structure moves counts and denominators that
    /// no single node's data carries, so a reader treats it as a rebuild trigger even when the
    /// identifiers are exact.
    /// </summary>
    Structure = 1 << 0,
    ItemText = 1 << 1,
    Attribution = 1 << 2,
    Audio = 1 << 3,
    Reviews = 1 << 4,
    Characters = 1 << 5,
    Narrator = 1 << 6,
    Voices = 1 << 7,
    VoiceRules = 1 << 8,
    ProjectPolicy = 1 << 9,
    All = Structure | ItemText | Attribution | Audio | Reviews
        | Characters | Narrator | Voices | VoiceRules | ProjectPolicy,
}

public enum BookStructuralRelationKind
{
    /// <summary><c>SourceId</c> was split; <c>ResultId</c> is the newly created sibling.</summary>
    Split,
    /// <summary><c>SourceId</c> was merged away; <c>ResultId</c> is the survivor.</summary>
    Merge,
}

/// <summary>
/// A split or merge stated as a relationship between two nodes, so a reader can carry expansion
/// from the node that disappeared to the one that took its place.
/// </summary>
public sealed record BookStructuralRelation(BookStructuralRelationKind Kind, Guid SourceId, Guid ResultId);

/// <summary>
/// What a mutation implementation actually applied inside the transaction — reported, never
/// inferred from the mutation's name. <see cref="Nothing"/> is a valid operation that changed
/// nothing; <see cref="Unknown"/> is the safe answer for an implementation that cannot say.
/// </summary>
public sealed record BookMutationEffects
{
    public required BookMutationScope Scope { get; init; }
    public required BookFacets Facets { get; init; }

    /// <summary>The identity created by this mutation, when it created exactly one thing.</summary>
    public Guid? CreatedId { get; init; }

    // One list per kind of identifier a migrated family actually reports. Each producer family adds
    // the list it needs as it lands; an empty list means "this mutation touched none", which is why
    // a family that cannot enumerate its effects says so through Scope instead of leaving them bare.
    public IReadOnlyList<Guid> ParagraphIds { get; init; } = [];
    public IReadOnlyList<Guid> ParagraphItemIds { get; init; } = [];
    public IReadOnlyList<BookStructuralRelation> Structural { get; init; } = [];

    /// <summary>A valid operation that applied no change: no revision, no receipt, no commit.</summary>
    public static BookMutationEffects Nothing { get; } =
        new() { Scope = BookMutationScope.Exact, Facets = BookFacets.None };

    /// <summary>Unknown effects are safe by default: whole-project scope, every facet.</summary>
    public static BookMutationEffects Unknown { get; } =
        new() { Scope = BookMutationScope.WholeProject, Facets = BookFacets.All };

    public bool ChangedNothing => Facets == BookFacets.None;
}
