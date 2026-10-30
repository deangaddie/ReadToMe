using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Read2Me.App.Configuration;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData;
using Read2Me.Services;
using Serilog;

namespace Read2Me.App
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .AddJsonFile(LocalSettings.FileName, optional: true)
                .Build();

            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            try
            {
                var host = CreateHostBuilder(args).Build();
                ApplyMigrations(host);
                host.Run();
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        private static void ApplyMigrations(IHost host)
        {
            using var scope = host.Services.CreateScope();
            // Judging the Workspace creates a missing folder. With none usable the host still starts,
            // without app.db, and the Setup check says what to fix.
            var workspaceStart = scope.ServiceProvider.GetRequiredService<WorkspaceStart>();
            if (!workspaceStart.Usable)
            {
                Log.Warning("No usable Workspace ({Found}): the app starts without one; open the Setup check to fix it",
                    workspaceStart.AtStart.Found);
                return;
            }
            var db = scope.ServiceProvider.GetRequiredService<Read2MeDbContext>();
            if (!workspaceStart.MigrateDatabase(db))
                return;
            if (DefaultServiceConfigSeeder.SeedAsync(db).GetAwaiter().GetResult())
                Log.Information("New app database: seeded the default AI service configs for the Infra containers");
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureAppConfiguration((context, config) => LocalSettings.AddTo(config, context.HostingEnvironment))
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
