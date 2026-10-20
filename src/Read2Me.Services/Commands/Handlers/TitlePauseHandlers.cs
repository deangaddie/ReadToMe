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

/// <summary>
/// The Add titles dialog's levels as one request: one sweep per level, book first, each its own
/// commit. A refusal stops the rest and says which levels had already landed, since those stay.
/// </summary>
public sealed class AddTitlesHandler(BookMutations mutations) : ICommandHandler<AddTitlesCommand>
{
    public async Task<BookCommandResult> HandleAsync(AddTitlesCommand c, CancellationToken ct)
    {
        var levels = (c.Levels ?? []).Distinct().Order().ToList();
        if (levels.Count == 0)
            return Refused("Name at least one title level.");
        // A number the JSON binder let through names no level: refuse rather than guess one.
        if (levels.Any(l => !Enum.IsDefined(l)))
            return Refused($"Unknown title level '{levels.First(l => !Enum.IsDefined(l))}'.");

        BookMutationOutcome result = new BookMutationOutcome.NoChange();
        var landed = new List<TitleLevel>();
        foreach (var level in levels)
        {
            BookMutation sweep = level switch
            {
                TitleLevel.Book => new AddBookTitleMutation(c.FolderId),
                TitleLevel.Volume => new AddVolumeTitlesMutation(c.FolderId),
                TitleLevel.Part => new AddPartTitlesMutation(c.FolderId),
                TitleLevel.Chapter => new AddChapterTitlesMutation(c.FolderId),
                _ => throw new ArgumentOutOfRangeException(nameof(c), level, "Unknown title level."),
            };
            switch (await mutations.CommitAsync(sweep, ct))
            {
                case BookMutationOutcome.Rejected rejected:
                    var message = landed.Count == 0
                        ? rejected.Message
                        : $"{rejected.Message} {LandedMessage(landed)}";
                    return new(rejected with { Message = message }, null);
                case BookMutationOutcome.Committed committed:
                    landed.Add(level);
                    result = committed;
                    break;
            }
        }
        return new(result, null);
    }

    /// <summary>"Book and volume titles were added." — the web's own success wording, said by the host.</summary>
    internal static string LandedMessage(List<TitleLevel> landed)
    {
        var names = landed.Select(l => l.ToString().ToLowerInvariant()).ToList();
        var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
        var noun = landed is [TitleLevel.Book] ? "title was" : "titles were";
        return $"{char.ToUpperInvariant(list[0])}{list[1..]} {noun} added.";
    }

    private static BookCommandResult Refused(string message) =>
        new(new BookMutationOutcome.Rejected(BookMutationRejection.Validation, message), null);
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
