using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Read2Me.App.Api
{
    /// <summary>The request headers a browser sends that say where a request came from.</summary>
    public sealed record FetchMetadata(string Method, string? Site, string? Mode, string? Dest, string? Origin);

    /// <summary>
    /// Whether a request may reach the host's non-navigational routes.
    /// </summary>
    public static class RequestIsolation
    {
        /// <summary>
        /// Every route that isn't a page: the API, the live hub (negotiate and the WebSocket upgrade) and
        /// files. NoWorkspaceGuard lists the same routes but /hubs: a new one belongs in both.
        /// </summary>
        private static readonly PathString[] Covered = ["/api", "/hubs", "/workspace", "/audio-preview", "/preview-source"];

        private const string Vary = "Sec-Fetch-Dest, Sec-Fetch-Mode, Sec-Fetch-Site";

        /// <summary>
        /// Refuses another page's request to a covered route with 403, ahead of every route and file
        /// mount (modern-web-guidance security/security §3.7). The host has no CORS policy, so a page
        /// that gets through still can't read a response; this stops the requests that need no read to
        /// do harm.
        /// </summary>
        public static IApplicationBuilder UseRequestIsolation(this IApplicationBuilder app, bool development)
        {
            // Bound addresses are known only once the server has started, so read them at the first request.
            var ownOrigins = new Lazy<IReadOnlySet<string>>(() => OwnOrigins(
                app.ServerFeatures.Get<IServerAddressesFeature>()?.Addresses ?? [], development));

            return app.Use(async (context, next) =>
            {
                var request = context.Request;
                if (!Covered.Any(p => request.Path.StartsWithSegments(p)))
                {
                    await next(context);
                    return;
                }

                context.Response.Headers.Append(HeaderNames.Vary, Vary);
                if (Allows(Read(request), ownOrigins.Value))
                {
                    await next(context);
                    return;
                }

                await Results.Problem(
                    "Refused: a request from another site or origin. The host answers only its own pages and callers that send no browser headers.",
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            });
        }

        private static FetchMetadata Read(HttpRequest request) => new(
            request.Method,
            Header(request, "Sec-Fetch-Site"),
            Header(request, "Sec-Fetch-Mode"),
            Header(request, "Sec-Fetch-Dest"),
            Header(request, HeaderNames.Origin));

        private static string? Header(HttpRequest request, string name) =>
            request.Headers.TryGetValue(name, out var value) ? value.ToString() : null;

        public static bool Allows(FetchMetadata request, IReadOnlySet<string> ownOrigins)
        {
            if (request.Site is { } site)
                return site is "same-origin" or "none" || IsTopLevelGetNavigation(request);
            // A browser without Fetch Metadata still sends Origin on a cross-origin write or socket.
            // Whole-string comparison: a prefix test would let http://localhost:5000.evil.test in.
            if (request.Origin is { } origin)
                return ownOrigins.Contains(origin);
            // Neither header: curl, an agent, the .NET client, a test. Not a browser page.
            return true;
        }

        // The web dev server (src/Read2Me.Web/dev.ts), whose proxy forwards the browser's headers.
        private static readonly string[] DevServerOrigins = ["http://localhost:4300", "http://127.0.0.1:4300"];

        private static readonly string[] LoopbackNames = ["localhost", "127.0.0.1", "[::1]"];
        private static readonly string[] AnyAddress = ["+", "*", "0.0.0.0", "[::]"];

        /// <summary>
        /// The origins a browser page served by this host has: one per bound address (Kestrel's
        /// <c>IServerAddressesFeature</c>, which holds the configured <c>Urls</c>), in the lowercase,
        /// default-port-free form a browser writes <c>Origin</c> in. A loopback or any-address binding
        /// is reached by every loopback name; Development adds the web dev server.
        /// </summary>
        public static IReadOnlySet<string> OwnOrigins(IEnumerable<string> addresses, bool development)
        {
            var origins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var address in addresses)
            {
                if (!TrySplit(address, out var scheme, out var host, out var port))
                    continue;
                var names = LoopbackNames.Contains(host) || AnyAddress.Contains(host) ? LoopbackNames : [host];
                var portPart = port == (scheme == "https" ? 443 : 80) ? "" : $":{port}";
                foreach (var name in names)
                    origins.Add($"{scheme}://{name}{portPart}");
            }
            if (development)
                origins.UnionWith(DevServerOrigins);
            return origins;
        }

        // Kestrel addresses aren't all URIs ("http://+:5000"), so split scheme://host:port by hand.
        private static bool TrySplit(string address, out string scheme, out string host, out int port)
        {
            (scheme, host, port) = ("", "", 0);
            var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0)
                return false;
            scheme = address[..schemeEnd].ToLowerInvariant();
            var authority = address[(schemeEnd + 3)..].TrimEnd('/').ToLowerInvariant();
            var colon = authority.LastIndexOf(':');
            if (colon > 0 && !authority.EndsWith(']') && int.TryParse(authority[(colon + 1)..], out port))
            {
                host = authority[..colon];
                return true;
            }
            host = authority;
            port = scheme == "https" ? 443 : 80;
            return scheme is "http" or "https";
        }

        // A link or a typed URL from another page opens a file or the app; an <object> or <embed>
        // also loads as a navigation, but embeds the response in the other page, so it stays refused.
        private static bool IsTopLevelGetNavigation(FetchMetadata request) =>
            HttpMethods.IsGet(request.Method) && request.Mode == "navigate" &&
            request.Dest is not ("object" or "embed");
    }
}
