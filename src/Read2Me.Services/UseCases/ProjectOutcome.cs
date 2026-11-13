namespace Read2Me.Services.UseCases
{
    /// <summary>
    /// What a project lifecycle write did (#330). A refusal is the caller's input, a conflict the
    /// project's state and a not-found a project the Book does not have; none of them touched
    /// anything on disk. A failure is a write that did not happen for a reason nobody foresaw, and
    /// the log has why (#347).
    /// </summary>
    public abstract record ProjectOutcome
    {
        private ProjectOutcome() { }

        /// <summary>The write happened.</summary>
        public sealed record Done : ProjectOutcome;

        /// <summary>A project was created, in this folder.</summary>
        public sealed record Created(string FolderName) : ProjectOutcome;

        /// <summary>Nothing was written, and why, for the reader: a refusal, a conflict, a not-found or a failure.</summary>
        public abstract record NotDone(string Message) : ProjectOutcome;

        /// <summary>The input breaks a rule (a blank title); nothing was written.</summary>
        public sealed record Refused(string Message) : NotDone(Message);

        /// <summary>The <see cref="History.ProjectActivity"/> gate refused: the project is busy, restoring or being deleted; nothing was touched.</summary>
        public sealed record ActivityRefused(History.ActivityRefusal Refusal) : NotDone(Refusal.Reason);

        /// <summary>
        /// The project's state refused the write: a project of that name already exists, another
        /// writer holds it, a Restore is replacing it, or a linked book blocks it. Nothing was written.
        /// </summary>
        public sealed record Conflict(string Message) : NotDone(Message);

        /// <summary>The Book has no such project to write to.</summary>
        public sealed record NotFound(string Message) : NotDone(Message);

        /// <summary>The write did not happen, for a reason nobody foresaw; the log has why.</summary>
        public sealed record Failed(string Message) : NotDone(Message);
    }
}
