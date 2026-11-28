using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Read2Me.App.Api
{
    /// <summary>
    /// The web app's page: the built bundle's index with a nonce Content-Security-Policy that enforces
    /// Trusted Types (modern-web-guidance security/security §3.2, security/trusted-types §4), or the
    /// not-built page while there is no bundle.
    /// </summary>
    public static class AppPage
    {
        /// <summary>The web app's URL prefix and its bundle's index under the web root.</summary>
        public const string Prefix = "/app";
        private const string BundleIndex = "app/index.html";
        private const string MissingPage = "app-missing.html";

        /// <summary>
        /// What the web build stamps as the nonce of every script and modulepreload in the index
        /// (<c>src/Read2Me.Web/csp-nonce.ts</c>): an explicit token, never a tag found by pattern.
        /// </summary>
        public const string NoncePlaceholder = "__CSP_NONCE__";

        /// <summary>
        /// The policy for a nonce. <c>base-uri 'self'</c>, not <c>'none'</c>: the index declares
        /// <c>&lt;base href="/app/"&gt;</c> and the router resolves against <c>document.baseURI</c>.
        /// <c>style-src</c> stays open, for lit's <c>&lt;style&gt;</c> and <c>style=</c> bindings and the
        /// theme's injected sheet. No report endpoint: nothing collects reports.
        /// </summary>
        public static string Policy(string nonce) =>
            $"script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; base-uri 'self'; " +
            "frame-ancestors 'none'; connect-src 'self'; require-trusted-types-for 'script'; trusted-types lit-html";

        /// <summary>
        /// Sends <c>/app/index.html</c> asked for by name through <see cref="ServeAsync"/>, ahead of the
        /// static file mount, which would serve the placeholder raw and with no policy.
        /// </summary>
        public static IApplicationBuilder UseAppIndexByName(this IApplicationBuilder app, IWebHostEnvironment env) =>
            app.Use((context, next) =>
                string.Equals(context.Request.Path.Value, Prefix + "/index.html", StringComparison.OrdinalIgnoreCase)
                    ? ServeAsync(context, env)
                    : next(context));

        /// <summary>
        /// SPA fallback: every unmatched URL under the prefix gets the bundle's index, with a fresh
        /// nonce each response, so client-side routes deep-link. Without a built bundle the same URLs
        /// get a static "how to build it" page, with no policy (its one style is inline).
        /// <para>
        /// The bundle is looked up on the physical web root (one stat per request) rather than the
        /// WebRootFileProvider: in Development that provider is a composite over the static-web-assets
        /// manifest, which would surface a developer's local build inside a test host that meant to run
        /// without one. The not-built page is a checked-in asset, so it goes through the provider.
        /// </para>
        /// </summary>
        public static async Task ServeAsync(HttpContext context, IWebHostEnvironment env)
        {
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/html; charset=utf-8";
            response.Headers.CacheControl = "no-cache";

            var index = Path.Combine(env.WebRootPath, BundleIndex);
            if (!File.Exists(index))
            {
                await response.SendFileAsync(env.WebRootFileProvider.GetFileInfo(MissingPage), context.RequestAborted);
                return;
            }

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var html = await File.ReadAllTextAsync(index, context.RequestAborted);
            response.Headers.ContentSecurityPolicy = Policy(nonce);
            await response.WriteAsync(html.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal), context.RequestAborted);
        }
    }
}
