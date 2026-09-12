using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Read2Me.App.Api;
using Read2Me.App.Configuration;
using Read2Me.App.Live;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;

namespace Read2Me.App
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddProjectServices(Configuration);
            services.AddLlmServices();
            services.AddAudioServices();
            services.AddAudioQueueServices();
            services.AddCharacterServices();
            services.AddAiWatchdogServices(Configuration);
            services.AddAppState();
            services.AddAppDatabase();

            services.AddOpenApi();
            services.AddLiveHub();

            services.AddHttpClient();
            services.AddRazorPages();
            services.AddServerSideBlazor();
            services.AddMudServices();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IOptions<WorkspaceOptions> workspaceOptions)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            // The Angular dev proxy, `npm run api:types` and the host-backed unit tests talk plain HTTP
            // to :5000 (Node fetch cannot follow a 307 to the self-signed dev cert), so only redirect
            // outside Development.
            if (!env.IsDevelopment())
            {
                app.UseHttpsRedirection();
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ApplyAngularBundleCaching
            });

            var workspacePath = workspaceOptions.Value.FolderPath;
            Directory.CreateDirectory(workspacePath);
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(workspacePath),
                RequestPath = "/workspace",
                // Voice/audio files are overwritten in place (same name across
                // regenerations). Tell clients never to reuse a cached copy so a
                // regenerated voice plays the new audio without a server restart.
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
                }
            });

            // Journals must attach to their broadcasters before any queue events flow, so a
            // stream view expanded mid-request can replay the in-progress turn.
            app.ApplicationServices.GetRequiredService<EventJournal<LlmStreamEvent>>();
            app.ApplicationServices.GetRequiredService<EventJournal<AudioGenEvent>>();

            // Likewise the throughput aggregator: it only sees events published after it
            // subscribes, and nothing resolves it until a surface paints — by which time the run
            // it should have been measuring has already started.
            app.ApplicationServices.GetRequiredService<ThroughputAggregator>();

            // And the live hub relay: it subscribes in its constructor, so resolving it here means no
            // event published before the hosted-service start is missed either.
            app.ApplicationServices.GetRequiredService<LiveRelay>();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapGet("/audio-preview/{token}", ServeAudioPreviewAsync);
                endpoints.MapGet("/preview-source/{folder}/{id}", ServePreviewSourceAsync);
                endpoints.MapAgentApi();
                endpoints.MapLiveHub();
                // The Angular app owns /app; its SPA fallback must run before Blazor's _Host catch-all.
                endpoints.MapFallback("/app", ctx => ServeAngularAppAsync(ctx, env));
                endpoints.MapFallback("/app/{**path}", ctx => ServeAngularAppAsync(ctx, env));
                endpoints.MapFallbackToPage("/_Host");
            });
        }

        private const string AngularBundleIndex = "app/index.html";
        private const string AngularMissingPage = "app-missing.html";

        // Angular emits content-hashed file names (main-UP4C2GUA.js, styles-CQKAZ5MM.css,
        // InterVariable-AM3KRH5U.woff2). Anything under /app/ carrying a hash is immutable;
        // everything else there (index.html, favicon, licences) must be revalidated.
        private static readonly Regex HashedAssetName = new(@"-[A-Z0-9]{8}\.[a-z0-9]+$", RegexOptions.Compiled);

        private static void ApplyAngularBundleCaching(StaticFileResponseContext ctx)
        {
            var path = ctx.Context.Request.Path.Value;
            if (path is null || !path.StartsWith("/app/", StringComparison.OrdinalIgnoreCase))
                return;

            ctx.Context.Response.Headers.CacheControl = HashedAssetName.IsMatch(path)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
        }

        /// SPA fallback for the Angular app: every unmatched /app/... URL gets the bundle's index.html
        /// so client-side routes deep-link. When the bundle has not been built the same URLs get a
        /// static "how to build it" page instead — never Blazor's _Host.
        ///
        /// The bundle is looked up on the physical web root (one stat per request) rather than the
        /// WebRootFileProvider: in Development that provider is a composite over the static-web-assets
        /// manifest, which would surface a developer's local ng build inside a test host that meant
        /// to run without one. The not-built page is a checked-in asset, so it goes through the provider.
        private static Task ServeAngularAppAsync(HttpContext context, IWebHostEnvironment env)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";

            var index = Path.Combine(env.WebRootPath, AngularBundleIndex);
            return File.Exists(index)
                ? context.Response.SendFileAsync(index, context.RequestAborted)
                : context.Response.SendFileAsync(env.WebRootFileProvider.GetFileInfo(AngularMissingPage), context.RequestAborted);
        }

        /// Serves an item's Preview Source — the unprocessed side of the A/B preview. It lives in a
        /// dot-prefixed dir the static-file provider will not serve, so it needs a route of its own.
        private static async Task ServePreviewSourceAsync(HttpContext context)
        {
            var folder = (string?)context.Request.RouteValues["folder"];
            var id = (string?)context.Request.RouteValues["id"];

            // Both route values are parsed before either can reach a file path, so a traversal
            // attempt is a 404 rather than a read.
            if (!ProjectFolderId.TryParse(folder, out var folderId) || !Guid.TryParse(id, out var itemId) ||
                !context.RequestServices.GetRequiredService<IPreviewSourceCache>()
                    .TryGetPath(folderId, itemId, out var path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await SendWavAsync(context, path!);
        }

        /// Serves an A/B preview WAV rendered by the circuit that owns <c>token</c>. The file is
        /// overwritten on every render, so it must never be cached.
        private static async Task ServeAudioPreviewAsync(HttpContext context)
        {
            var token = (string?)context.Request.RouteValues["token"];

            // Tokens are circuit-minted: a bare GUID from a paragraph card, or "{pageId}-{stepId}"
            // from the voice editor's per-step players. Both are alphanumerics and hyphens, and
            // rejecting anything else keeps a separator or a dot away from the file path.
            if (!IsPreviewToken(token) ||
                !context.RequestServices.GetRequiredService<AudioPreviewStore>().TryGetPath(token!, out var path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await SendWavAsync(context, path!);
        }

        private static bool IsPreviewToken(string? token) =>
            !string.IsNullOrEmpty(token) && token.Length <= 96 &&
            token.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

        /// Preview files are overwritten in place, so they must never be cached. The explicit length
        /// matters too: without it the response is chunked, and a WAV with no Content-Length gives the
        /// <c>&lt;audio&gt;</c> element an infinite duration — no total time, no scrub bar.
        private static async Task SendWavAsync(HttpContext context, string path)
        {
            context.Response.ContentType = "audio/wav";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentLength = new FileInfo(path).Length;
            await context.Response.SendFileAsync(path);
        }
    }
}
