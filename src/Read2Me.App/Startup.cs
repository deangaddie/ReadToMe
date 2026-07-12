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
using Read2Me.App.Configuration;
using Read2Me.Core.Configuration;
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

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapGet("/audio-preview/{token}", ServeAudioPreviewAsync);
                endpoints.MapFallbackToPage("/_Host");
            });
        }

        /// Serves the consonant-soften A/B preview WAV rendered by the circuit that owns
        /// <c>token</c>. The file is overwritten on every render, so it must never be cached.
        private static async Task ServeAudioPreviewAsync(HttpContext context)
        {
            var token = (string?)context.Request.RouteValues["token"];

            // Tokens are circuit-minted GUIDs; anything else cannot name a stored preview, and
            // refusing it up front keeps the value away from the file path.
            if (!Guid.TryParseExact(token, "N", out _) ||
                !context.RequestServices.GetRequiredService<AudioPreviewStore>().TryGetPath(token!, out var path))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "audio/wav";
            context.Response.Headers.CacheControl = "no-store";
            // Without an explicit length the response is chunked, and a WAV with no Content-Length
            // gives the <audio> element an infinite duration — no total time, no scrub bar.
            context.Response.ContentLength = new FileInfo(path!).Length;
            await context.Response.SendFileAsync(path!);
        }
    }
}
