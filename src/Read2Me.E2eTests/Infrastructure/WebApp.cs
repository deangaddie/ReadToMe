namespace Read2Me.E2eTests.Infrastructure;

/// <summary>
/// The front end a browser test class drives. Each app is a bundle the host serves under its own
/// prefix; a test class names one and never runs against both (ADR 0014).
/// </summary>
public enum WebApp
{
    /// <summary>The Angular app (<c>src/Read2Me.Web</c>), served at <c>/app</c>. Chromium only.</summary>
    Angular,

    /// <summary>The native app (<c>src/Read2Me.Native</c>), served at <c>/app2</c>. Chromium and Firefox.</summary>
    Native,
}

public static class WebAppInfo
{
    /// <summary>The path prefix the host serves the app under, without a trailing slash.</summary>
    public static string Prefix(this WebApp app) => app switch
    {
        WebApp.Angular => "/app",
        WebApp.Native => "/app2",
        _ => throw new ArgumentOutOfRangeException(nameof(app), app, null),
    };

    /// <summary>The bundle folder under <c>src/Read2Me.App/wwwroot</c> (and under the test web root).</summary>
    public static string BundleFolder(this WebApp app) => app.Prefix().TrimStart('/');

    /// <summary>The command that produces the bundle, for the skip message.</summary>
    public static string BuildCommand(this WebApp app) => app switch
    {
        WebApp.Angular => "`npm run build` in src/Read2Me.Web (or `pwsh scripts/build-web.ps1`)",
        WebApp.Native => "`bun run build` in src/Read2Me.Native (or `pwsh scripts/build-native.ps1`)",
        _ => throw new ArgumentOutOfRangeException(nameof(app), app, null),
    };
}
