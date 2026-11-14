namespace Read2Me.Tests.Fakes;

/// <summary>A Worker turn that holds nothing, for a fake <c>IQueueSource</c> with no busy to keep.</summary>
public sealed class NoTurn : IDisposable
{
    public static readonly NoTurn Instance = new();

    public void Dispose() { }
}
