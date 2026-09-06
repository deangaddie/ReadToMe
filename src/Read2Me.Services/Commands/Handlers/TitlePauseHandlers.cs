using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

/// <summary>
/// The structural title and pause additions, migrated to <see cref="BookMutations"/> (ADR 0007).
/// Each handler is a translation only; the sweeps themselves live in the mutation implementations,
/// where a sweep that finds nothing to add can finally say so rather than committing an empty
/// transaction and reporting success.
/// </summary>
public sealed class AddBookTitleHandler(BookMutations mutations) : ICommandHandler<AddBookTitleCommand>
{
    public Task<BookCommandResult> HandleAsync(AddBookTitleCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new AddBookTitleMutation(c.FolderId), ct);
}

public sealed class AddVolumeTitlesHandler(BookMutations mutations) : ICommandHandler<AddVolumeTitlesCommand>
{
    public Task<BookCommandResult> HandleAsync(AddVolumeTitlesCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new AddVolumeTitlesMutation(c.FolderId), ct);
}

public sealed class AddPartTitlesHandler(BookMutations mutations) : ICommandHandler<AddPartTitlesCommand>
{
    public Task<BookCommandResult> HandleAsync(AddPartTitlesCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new AddPartTitlesMutation(c.FolderId), ct);
}

public sealed class AddChapterTitlesHandler(BookMutations mutations) : ICommandHandler<AddChapterTitlesCommand>
{
    public Task<BookCommandResult> HandleAsync(AddChapterTitlesCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new AddChapterTitlesMutation(c.FolderId), ct);
}

public sealed class AddPausesHandler(BookMutations mutations) : ICommandHandler<AddPausesCommand>
{
    public Task<BookCommandResult> HandleAsync(AddPausesCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new AddPausesMutation(c.FolderId), ct);
}

public sealed class InsertPauseParagraphHandler(BookMutations mutations)
    : ICommandHandler<InsertPauseParagraphCommand>
{
    // The mutation reports the Paragraph it created, because a reader reconciling from the receipt
    // needs it. This command has never answered with one, and ADR 0007 keeps the commands endpoint's
    // JSON contract fixed through the migration, so the identity stops here.
    public async Task<BookCommandResult> HandleAsync(InsertPauseParagraphCommand c, CancellationToken ct) =>
        (await mutations.ExecuteCommandAsync(
            new InsertPauseParagraphMutation(c.FolderId, c.AnchorItemId, c.Position, c.PauseKind), ct))
            .WithoutIdentity();
}

/// <summary>
/// Clearing the Book's whole content, migrated with the rest of the destructive family
/// (ADR 0007). The transaction and commit point it used to own are now <see cref="BookMutations"/>'s.
/// </summary>
public sealed class ClearBookContentHandler(BookMutations mutations) : ICommandHandler<ClearBookContentCommand>
{
    public Task<BookCommandResult> HandleAsync(ClearBookContentCommand c, CancellationToken ct) =>
        mutations.ExecuteCommandAsync(new ClearBookContentMutation(c.FolderId), ct);
}
