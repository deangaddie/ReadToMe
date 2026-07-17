using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Read2Me.App.Api;
using Read2Me.App.Configuration;
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

            app.UseHttpsRedirection();
            app.UseStaticFiles();

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

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapGet("/audio-preview/{token}", ServeAudioPreviewAsync);
                endpoints.MapGet("/preview-source/{folder}/{id}", ServePreviewSourceAsync);
                endpoints.MapAgentApi();
                endpoints.MapFallbackToPage("/_Host");
            });
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
