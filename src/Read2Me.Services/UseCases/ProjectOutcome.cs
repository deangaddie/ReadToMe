namespace Read2Me.Services.UseCases
{
    /// <summary>
    /// What a project lifecycle write did (#330). A refusal is the caller's input, which nothing on
    /// disk was touched for; a failure is a write that did not happen, with the reason in the log.
    /// </summary>
    public abstract record ProjectOutcome
    {
        private ProjectOutcome() { }

        /// <summary>The write happened.</summary>
        public sealed record Done : ProjectOutcome;

        /// <summary>A project was created, in this folder.</summary>
        public sealed record Created(string FolderName) : ProjectOutcome;

        /// <summary>Nothing was written, and why, for the reader: a refusal or a failure.</summary>
        public abstract record NotDone(string Message) : ProjectOutcome;

        /// <summary>The input breaks a rule (a blank title); nothing was written.</summary>
        public sealed record Refused(string Message) : NotDone(Message);

        /// <summary>The <see cref="History.ProjectActivity"/> gate refused: the project is busy, restoring or being deleted; nothing was touched.</summary>
        public sealed record Blocked(History.ActivityRefusal Refusal) : NotDone(Refusal.Reason);

        /// <summary>The write did not happen.</summary>
        public sealed record Failed(string Message) : NotDone(Message);
    }
}
