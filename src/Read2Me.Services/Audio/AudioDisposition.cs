using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The audio queue's phase-2 decision: given what recording a generated item did, what is its
    /// fate? Pure — the caller does the recording and passes the product in.
    /// </summary>
    public static class AudioDisposition
    {
        /// <summary>
        /// A recorded item is finished; a <b>Stale take</b> goes straight back on the queue to be
        /// generated from what its item says now. This queue is not probe-less because it is simpler
        /// than the character side — completion needs the relative path the recorder returns, which
        /// exists only after the apply, so audio takes the <see cref="Plan.ApplyFirst"/> branch too.
        /// <para>
        /// Elapsed is null: the store measures it from <c>MarkProcessing</c>, since one item is one
        /// unit of work here — unlike the character queue, whose stopwatch spans a drained batch.
        /// </para>
        /// </summary>
        /// <param name="recording">What the recorder did with the take.</param>
        public static Disposition DecideApplied(TakeRecording recording) => recording switch
        {
            TakeRecording.Recorded recorded => new Disposition.Complete(null, recorded.RelativePath),
            TakeRecording.Stale => new Disposition.Regenerate(),
            _ => throw new ArgumentOutOfRangeException(nameof(recording), recording, "Unhandled take recording."),
        };
    }
}
