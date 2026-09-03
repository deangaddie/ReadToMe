namespace Read2Me.Services.Mutations;

/// <summary>Tunables for <see cref="BookMutations"/>. Registered as a singleton.</summary>
public sealed class BookMutationOptions
{
    /// <summary>
    /// How long a mutation waits for its project's write lock before returning
    /// <see cref="BookMutationRejection.Conflict"/>. The lock is only ever held for one
    /// transaction, so exhausting this budget means a genuinely stuck writer, not a busy one.
    /// </summary>
    public TimeSpan LockWaitBudget { get; set; } = TimeSpan.FromSeconds(30);
}
