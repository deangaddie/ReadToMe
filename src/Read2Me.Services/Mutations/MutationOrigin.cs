namespace Read2Me.Services.Mutations;

/// <summary>
/// The origin every mutation committed in this DI scope is stamped with unless it names one itself
/// (<see cref="BookMutation.OriginId"/>). One API request is one scope, so an endpoint that reads
/// the caller's <c>X-Origin-Id</c> sets it here once and every command handler and use case the
/// request runs commits with it — the receipts the hub echoes then carry it back to the tab that
/// wrote, which is how the web client tells its own writes from everyone else's (spec D6).
/// <para>
/// A scoped holder rather than a parameter because the write travels through handlers and use
/// cases that construct the mutation themselves; threading an origin through fifty signatures would
/// buy nothing. A producer that stamps its own origin (a Book View projection) is left alone.
/// </para>
/// </summary>
public sealed class MutationOrigin
{
    /// <summary><see cref="Guid.Empty"/> means unattributed, which every scope starts as.</summary>
    public Guid Id { get; set; }
}
