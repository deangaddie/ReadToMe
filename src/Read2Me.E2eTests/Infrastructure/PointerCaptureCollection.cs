namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// Browser tests that hold a pointer capture (a drag on an element that calls
/// <c>setPointerCapture</c>, such as <c>r2m-splitter</c>). xunit runs this collection alone, after
/// the parallel classes have finished.
/// <para>
/// Firefox keeps pointer capture per content process, and every test page (all on 127.0.0.1)
/// shares one. While a page holds the mouse, another class's click goes to the captured element
/// instead of its own page: its menu never opens, and the drag picks up the stray position (#427).
/// Chromium keeps pages apart. Each class still gets its own host, since
/// <see cref="E2eAppFixture"/> is a class fixture.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PointerCaptureCollection
{
    public const string Name = "Pointer capture";
}
