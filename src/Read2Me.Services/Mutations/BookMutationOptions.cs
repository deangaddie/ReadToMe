namespace Read2Me.Services.Mutations;

/// <summary>
/// Tuning options for <see cref="BookMutations"/>, bound from the <c>BookMutations</c> config
/// section. Defaults apply when the section is absent.
/// </summary>
public sealed class BookMutationOptions
{
    public const string SectionName = "BookMutations";

    /// <summary>
    /// How long a mutation waits for its project's write lock before returning
    /// <see cref="BookMutationRejection.Conflict"/>. The lock is only ever held for one
    /// transaction, so exhausting this budget means a genuinely stuck writer, not a busy one.
    /// </summary>
    public TimeSpan LockWaitBudget { get; set; } = TimeSpan.FromSeconds(30);
}
