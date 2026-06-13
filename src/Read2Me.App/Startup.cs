using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Services;
using Read2Me.Services.Books;
using Read2Me.Services.IO;
using Read2Me.Services.UseCases;
using Read2Me.App.State;
using Read2Me.AppData;
using Read2Me.Data;

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
            services.Configure<WorkspaceOptions>(Configuration.GetSection(WorkspaceOptions.SectionName));
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IFileSystem, FileSystemService>();
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();

            services.AddScoped<ProjectService>();
            services.AddScoped<IProjectReader>(sp => sp.GetRequiredService<ProjectService>());
            services.AddScoped<IProjectWriter>(sp => sp.GetRequiredService<ProjectService>());

            services.AddScoped<IBookContentPersister, BookContentPersister>();
            services.AddScoped<BookReadingService>();
            services.AddScoped<ProjectUseCases>();
            services.AddScoped<BookUseCases>();
            services.AddScoped<BookHierarchyLoader>();
            services.AddScoped<BookTreeState>();

            services.AddSingleton<EpubFileReader>();
            services.AddSingleton<TextFileReader>();

            services.AddDbContextFactory<Read2MeDbContext>((sp, options) =>
            {
                var workspace = sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value;
                var dbPath = Path.Combine(workspace.FolderPath, "app.db");
                options.UseSqlite($"Data Source={dbPath}");
            });

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
                RequestPath = "/workspace"
            });

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
            });
        }
    }
}
