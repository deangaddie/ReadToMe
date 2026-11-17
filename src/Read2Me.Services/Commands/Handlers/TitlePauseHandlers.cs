using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands.Handlers;

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

public sealed class InsertPauseParagraphHandler(BookMutations mutations)
    : ICommandHandler<InsertPauseParagraphCommand>
{
    // The mutation reports the Paragraph it created, because a reader reconciling from the receipt
    // needs it. This command has never answered with one, and ADR 0007 keeps the commands endpoint's
    // JSON contract fixed through the migration, so the identity stops here.
    public async Task<BookCommandResult> HandleAsync(InsertPauseParagraphCommand c, CancellationToken ct) =>
        (await mutations.ExecuteCommandAsync(
            new InsertPauseParagraphMutation(c.FolderId, c.AnchorItemId, c.Position, c.PauseKind), ct,
            BookMutationRefusalKind.Missing))
            .WithoutIdentity();
}
