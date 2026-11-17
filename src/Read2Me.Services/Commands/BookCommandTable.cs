using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

/// <summary>
/// One row per <see cref="BookCommand"/>, holding everything about it: how it runs, the History step
/// label its request records under, and which Refusal kinds it answers as
/// <c>200 { "newEntityId": null }</c> (glossary: <c>context/book-structure.md</c>, Book command).
/// <para>
/// A lookup, not a registry of mutations: a command is a wire name the API owns (ADR 0007), most
/// name exactly one Book mutation, and most mutations have no command. A
/// <see cref="Mutation{TCommand}"/> row maps its command onto that one mutation; a
/// <see cref="Handler{TCommand, THandler}"/> row hands it to a class with logic of its own. The rows
/// live in one file per command family.
/// </para>
/// <para>
/// The step label is in the words the web app uses for the gesture ("Edit with AI", "Split
/// chapter"), and the action picks its icon; the step's "what changed" adds the counts and the place,
/// so a label names the action and nothing more. Results-only commands are labelled too, though they
/// never record a step: every command has a label, so a new one has to choose its words.
/// </para>
/// </summary>
public static partial class BookCommandTable
{
    /// <summary>A command's row: its type, its step label and the step's action.</summary>
    public abstract class Row(Type commandType, string label, StepAction action)
    {
        public Type CommandType { get; } = commandType;
        public string Label { get; } = label;
        public StepAction Action { get; } = action;

        internal abstract Task<BookCommandResult> ExecuteAsync(
            BookCommand command, IServiceProvider services, CancellationToken ct);

        internal virtual void Register(IServiceCollection services) { }
    }

    /// <summary>A row whose command names one Book mutation, and the Refusal kinds it answers as null.</summary>
    public interface IMutationRow
    {
        IReadOnlyList<BookMutationRefusalKind> AnswersNullFor { get; }
    }

    private sealed class MutationRow<TCommand>(
        Func<TCommand, BookMutation> toMutation, string label, StepAction action,
        IReadOnlyList<BookMutationRefusalKind> answersNullFor)
        : Row(typeof(TCommand), label, action), IMutationRow
        where TCommand : BookCommand
    {
        public IReadOnlyList<BookMutationRefusalKind> AnswersNullFor { get; } = answersNullFor;

        internal override Task<BookCommandResult> ExecuteAsync(
            BookCommand command, IServiceProvider services, CancellationToken ct) =>
            services.GetRequiredService<BookMutations>()
                .ExecuteCommandAsync(toMutation((TCommand)command), ct, [.. AnswersNullFor]);
    }

    private sealed class HandlerRow<TCommand, THandler>(string label, StepAction action)
        : Row(typeof(TCommand), label, action)
        where TCommand : BookCommand
        where THandler : class, ICommandHandler<TCommand>
    {
        internal override Task<BookCommandResult> ExecuteAsync(
            BookCommand command, IServiceProvider services, CancellationToken ct) =>
            services.GetRequiredService<THandler>().HandleAsync((TCommand)command, ct);

        internal override void Register(IServiceCollection services) => services.AddScoped<THandler>();
    }

    /// <summary>
    /// The row for a command that names one mutation. <paramref name="answersNullFor"/> defaults to
    /// <c>missing</c>; a strict command passes <c>[]</c>.
    /// </summary>
    private static Row Mutation<TCommand>(
        Func<TCommand, BookMutation> mutation, string label, StepAction action,
        BookMutationRefusalKind[]? answersNullFor = null)
        where TCommand : BookCommand =>
        new MutationRow<TCommand>(mutation, label, action, answersNullFor ?? [BookMutationRefusalKind.Missing]);

    /// <summary>The row for a command with logic of its own, in an <see cref="ICommandHandler{TCommand}"/> class.</summary>
    private static Row Handler<TCommand, THandler>(string label, StepAction action)
        where TCommand : BookCommand
        where THandler : class, ICommandHandler<TCommand> =>
        new HandlerRow<TCommand, THandler>(label, action);

    /// <summary>Every row, by family.</summary>
    public static IReadOnlyList<Row> Rows { get; } = [.. Structure, .. Text, .. Speakers, .. Voices, .. Audio];

    // Throws on a command with two rows, so the table cannot disagree with itself.
    private static readonly Dictionary<Type, Row> ByCommand = Rows.ToDictionary(r => r.CommandType);

    /// <summary>The command's row. Every non-abstract <see cref="BookCommand"/> has one.</summary>
    public static Row Of(BookCommand command) =>
        ByCommand.TryGetValue(command.GetType(), out var row)
            ? row
            : throw new NotSupportedException($"Unhandled command type: {command.GetType().Name}");

    /// <summary>Registers the classes the <see cref="Handler{TCommand, THandler}"/> rows hand their commands to.</summary>
    internal static void RegisterHandlers(IServiceCollection services)
    {
        foreach (var row in Rows)
            row.Register(services);
    }
}
