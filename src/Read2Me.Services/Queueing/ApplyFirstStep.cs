using Read2Me.Services.Mutations;

namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// The <see cref="Plan.ApplyFirst"/> branch both processors share: apply the work, then let the
    /// queue's own <c>DecideApplied</c> read what it produced. A write the project's Restore refused
    /// becomes <see cref="Disposition.RetryAfterRestore"/>, so the item runs again against the restored
    /// project. Everything else the apply throws — cancellation included — is the processor's to handle.
    /// <para>
    /// Kept apart from <see cref="QueueDisposition"/>, which stays pure: this runs work.
    /// </para>
    /// </summary>
    public static class ApplyFirstStep
    {
        public static async Task<Disposition> RunAsync(Func<Task<Disposition>> applyThenDecide)
        {
            try
            {
                return await applyThenDecide();
            }
            catch (ProjectRestoringException)
            {
                return new Disposition.RetryAfterRestore();
            }
        }
    }
}
