namespace Read2Me.Data.Enums
{
    /// <summary>How the user settled a Pipeline step's confirmed checkpoint. No row means open.</summary>
    public enum CheckpointState
    {
        Confirmed,
        Skipped,
    }
}
