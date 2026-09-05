using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The title and text edits, migrated to <see cref="BookMutations"/> (ADR 0007). Nothing but
/// <c>POST /api/projects/{folder}/commands</c> comes through here any more — the item menu commits
/// its mutation directly — and its response shape is unchanged: a node the Book does not contain,
/// and a value the node already carries, both answer null.
/// </summary>
public sealed class UpdateVolumeTitleHandler(BookMutations mutations) : ICommandHandler<UpdateVolumeTitleCommand>
{
    public Task<Guid?> HandleAsync(UpdateVolumeTitleCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new UpdateVolumeTitleMutation(c.FolderId, c.VolumeId, c.Title), ct);
}

public sealed class UpdatePartTitleHandler(BookMutations mutations) : ICommandHandler<UpdatePartTitleCommand>
{
    public Task<Guid?> HandleAsync(UpdatePartTitleCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new UpdatePartTitleMutation(c.FolderId, c.PartId, c.Title), ct);
}

public sealed class UpdateChapterTitleHandler(BookMutations mutations) : ICommandHandler<UpdateChapterTitleCommand>
{
    public Task<Guid?> HandleAsync(UpdateChapterTitleCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new UpdateChapterTitleMutation(c.FolderId, c.ChapterId, c.Title), ct);
}

/// <summary>
/// Rewrites one item's text. The rewrite discards the item's stale audio and any verdict on it —
/// see <see cref="Mutations.Implementations.BookEditEffects"/> for why.
/// </summary>
public sealed class UpdateParagraphItemTextHandler(BookMutations mutations) : ICommandHandler<UpdateParagraphItemTextCommand>
{
    public Task<Guid?> HandleAsync(UpdateParagraphItemTextCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(new UpdateParagraphItemTextMutation(c.FolderId, c.ItemId, c.Text), ct);
}

/// <summary>
/// Points an item at an audio file, migrated to <see cref="BookMutations"/> (ADR 0007). The Audio
/// Queue does not come through here — it records the take and its verdict together — so this serves
/// only <c>POST /api/projects/{folder}/commands</c>, whose response shape is unchanged: an unknown
/// item and a path the item already carries both answer null.
/// </summary>
public sealed class SetParagraphItemAudioHandler(BookMutations mutations) : ICommandHandler<SetParagraphItemAudioCommand>
{
    public Task<Guid?> HandleAsync(SetParagraphItemAudioCommand c, CancellationToken ct) =>
        mutations.ExecuteLegacyAsync(
            new SetParagraphItemAudioMutation(c.FolderId, c.ItemId, c.AudioFileName), ct);
}
