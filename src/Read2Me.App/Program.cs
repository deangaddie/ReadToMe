using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
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
            var workspace = scope.ServiceProvider.GetRequiredService<IOptions<WorkspaceOptions>>().Value;
            Directory.CreateDirectory(workspace.FolderPath);
            var db = scope.ServiceProvider.GetRequiredService<Read2MeDbContext>();
            var isNewDatabase = !db.Database.GetAppliedMigrations().Any();
            db.Database.Migrate();
            if (isNewDatabase && DefaultServiceConfigSeeder.SeedAsync(db).GetAwaiter().GetResult())
                Log.Information("New app database: seeded the default AI service configs for the Infra containers");
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
