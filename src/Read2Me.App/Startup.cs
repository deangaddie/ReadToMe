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
using Read2Me.AppData;

namespace Read2Me.App
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<WorkspaceOptions>(Configuration.GetSection(WorkspaceOptions.SectionName));
            services.AddSingleton<ThemeService>();
            services.AddSingleton<IFileSystem, FileSystemService>();
            services.AddScoped<ProjectService>();
            services.AddScoped<BookReadingService>();
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

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IOptions<WorkspaceOptions> workspaceOptions)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
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
