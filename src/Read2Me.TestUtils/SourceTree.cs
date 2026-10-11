using System.Reflection;

namespace Read2Me.TestUtils
{
    /// <summary>
    /// The solution's <c>src</c> folder, as recorded when this assembly was built (see the
    /// <c>Read2Me.SourceRoot</c> metadata in the csproj). Not found by walking up from the binaries,
    /// so it does not matter where the build output lives.
    /// </summary>
    public static class SourceTree
    {
        public static string Root { get; } = typeof(SourceTree).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "Read2Me.SourceRoot").Value!;
    }
}
