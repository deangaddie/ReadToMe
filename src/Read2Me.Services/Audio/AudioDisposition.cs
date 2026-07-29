using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The audio queue's phase-2 decision: given that a generated item was recorded, what is its
    /// fate? Pure — the caller does the recording and passes the product in.
    /// </summary>
    public static class AudioDisposition
    {
        /// <summary>
        /// One case: a recorded item is finished. This queue is not probe-less because it is simpler
        /// than the character side — completion needs the relative path the recorder returns, which
        /// exists only after the apply, so audio takes the <see cref="Plan.ApplyFirst"/> branch too.
        /// Its phase 2 is degenerate, not absent, and stays a named function so both queues read
        /// identically at the call site.
        /// <para>
        /// Elapsed is null: the store measures it from <c>MarkProcessing</c>, since one item is one
        /// unit of work here — unlike the character queue, whose stopwatch spans a drained batch.
        /// </para>
        /// </summary>
        /// <param name="relativePath">Where the recorder put the audio, relative to the project.</param>
        public static Disposition DecideApplied(string relativePath) =>
            new Disposition.Complete(null, relativePath);
    }
}
