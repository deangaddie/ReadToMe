namespace Read2Me.Services.UseCases
{
    /// <summary>Why an import did not replace the Book's content. Every value is an expected condition.</summary>
    public enum BookImportFailure
    {
        /// <summary>The project names a source file that is not on disk.</summary>
        FileMissing,
        /// <summary>The project or its file cannot be imported — no project record, an unsupported type.</summary>
        Invalid,
        /// <summary>Another writer holds the project, or the mutation was refused as stale.</summary>
        Conflict,
        /// <summary>Cancellation was observed before the replacement committed.</summary>
        Cancelled,
        /// <summary>Something unforeseen went wrong. The Book is unchanged; the log carries why.</summary>
        Unexpected,
    }

    /// <summary>
    /// What an import or reread did. The cases are deliberately distinct because they need different
    /// answers: only <see cref="Replaced"/> changed the Book, and only it is worth telling other open
    /// Book Views about — while a missing file, a refusal and a cancellation are all things the
    /// reader can act on and none of them mean "try again, it may have half-worked" (ADR 0007).
    /// </summary>
    public abstract record BookImportOutcome
    {
        private BookImportOutcome() { }

        /// <summary>The Book's content was replaced, in one commit.</summary>
        public sealed record Replaced : BookImportOutcome;

        /// <summary>
        /// A valid import that changed nothing: an empty source file read into a Book that was
        /// already empty. Nothing committed, so nothing to reconcile.
        /// </summary>
        public sealed record Unchanged : BookImportOutcome;

        /// <summary>An expected failure. The Book is exactly as it was.</summary>
        public sealed record Failed(BookImportFailure Reason, string Message) : BookImportOutcome;

        /// <summary>The message to show a reader, or null when there is nothing wrong to say.</summary>
        public string? Error => this is Failed failed ? failed.Message : null;
    }
}
