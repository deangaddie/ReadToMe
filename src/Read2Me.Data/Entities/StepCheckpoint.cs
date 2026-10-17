using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    /// <summary>
    /// A Pipeline step the user confirmed or skipped. One project per database, so the step alone is
    /// the key. <see cref="At"/> is when it was confirmed or skipped, and is kept when the row goes
    /// <see cref="Stale"/>.
    /// </summary>
    public class StepCheckpoint
    {
        public PipelineStep Step { get; set; }
        public CheckpointState State { get; set; }
        public bool Stale { get; set; }
        public DateTimeOffset At { get; set; }
    }
}
