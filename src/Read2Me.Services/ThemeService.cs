using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MudBlazor;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    public class ThemeService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<ThemeService> _logger;
        private readonly SemaphoreSlim _cacheLock = new(1, 1);
        private MudTheme? _cachedMudTheme;
        private AppTheme? _cachedAppTheme;

        public event Action? OnThemeChanged;

        public ThemeService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<ThemeService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<(MudTheme Theme, bool IsDark)> GetCurrentThemeAsync()
        {
            await _cacheLock.WaitAsync();
            try
            {
                if (_cachedMudTheme != null && _cachedAppTheme != null)
                {
                    _logger.LogDebug("Returning cached theme '{Name}'", _cachedAppTheme.Name);
                    return (_cachedMudTheme, _cachedAppTheme.IsDark);
                }

                await using var db = await _dbFactory.CreateDbContextAsync();
                await EnsureSeededAsync(db);

                var settings = await db.Settings.SingleOrDefaultAsync();
                AppTheme? theme = null;

                if (settings?.SelectedThemeId != null)
                    theme = await db.Themes.FindAsync(settings.SelectedThemeId);

                theme ??= await db.Themes.OrderBy(t => t.Id).FirstAsync();

                _logger.LogDebug("Loaded theme '{Name}' (dark={IsDark})", theme.Name, theme.IsDark);
                _cachedAppTheme = theme;
                _cachedMudTheme = BuildMudTheme(theme);
                return (_cachedMudTheme, theme.IsDark);
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        public async Task<List<AppTheme>> GetAllThemesAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await EnsureSeededAsync(db);
            return await db.Themes
                .OrderBy(t => t.IsBuiltIn ? 0 : 1)
                .ThenBy(t => t.Name)
                .ToListAsync();
        }

        public async Task<int?> GetSelectedThemeIdAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.SelectedThemeId;
        }

        public async Task SetSelectedThemeAsync(int themeId)
        {
            _logger.LogInformation("Setting selected theme to ID {ThemeId}", themeId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null)
            {
                db.Settings.Add(new AppSettings { SelectedThemeId = themeId });
            }
            else
            {
                settings.SelectedThemeId = themeId;
            }
            await db.SaveChangesAsync();

            InvalidateCache();
            OnThemeChanged?.Invoke();
        }

        public async Task<AppTheme> CreateThemeAsync(AppTheme theme)
        {
            _logger.LogInformation("Creating custom theme '{Name}'", theme.Name);
            theme.IsBuiltIn = false;
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Themes.Add(theme);
            await db.SaveChangesAsync();
            _logger.LogInformation("Custom theme '{Name}' created with ID {Id}", theme.Name, theme.Id);
            return theme;
        }

        public async Task UpdateThemeAsync(AppTheme theme)
        {
            _logger.LogInformation("Updating theme '{Name}' (ID {Id})", theme.Name, theme.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.Themes.Update(theme);
            await db.SaveChangesAsync();

            await _cacheLock.WaitAsync();
            try
            {
                if (_cachedAppTheme?.Id == theme.Id)
                {
                    _logger.LogDebug("Active theme updated — invalidating cache");
                    InvalidateCacheUnsafe();
                }
            }
            finally
            {
                _cacheLock.Release();
            }

            OnThemeChanged?.Invoke();
        }

        public async Task DeleteThemeAsync(int themeId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var theme = await db.Themes.FindAsync(themeId);
            if (theme == null || theme.IsBuiltIn)
            {
                _logger.LogWarning("DeleteThemeAsync: theme ID {ThemeId} not found or is built-in", themeId);
                return;
            }

            _logger.LogInformation("Deleting theme '{Name}' (ID {Id})", theme.Name, themeId);
            db.Themes.Remove(theme);

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.SelectedThemeId == themeId)
            {
                _logger.LogDebug("Clearing selected theme setting — deleted theme was active");
                settings.SelectedThemeId = null;
            }

            await db.SaveChangesAsync();
            InvalidateCache();
            OnThemeChanged?.Invoke();
        }

        private void InvalidateCache()
        {
            _cacheLock.Wait();
            try { InvalidateCacheUnsafe(); }
            finally { _cacheLock.Release(); }
        }

        private void InvalidateCacheUnsafe()
        {
            _logger.LogDebug("Theme cache invalidated");
            _cachedMudTheme = null;
            _cachedAppTheme = null;
        }

        private static async Task EnsureSeededAsync(Read2MeDbContext db)
        {
            if (await db.Themes.AnyAsync()) return;

            db.Themes.AddRange(GetBuiltInThemes());
            db.Settings.Add(new AppSettings());
            await db.SaveChangesAsync();
        }

        private static List<AppTheme> GetBuiltInThemes() =>
        [
            new() { Name = "Light",  IsBuiltIn = true, IsDark = false, Primary = "#594AE2", Secondary = "#FF4081" },
            new() { Name = "Dark",   IsBuiltIn = true, IsDark = true,  Primary = "#7C5CBF", Secondary = "#FF4081" },
            new() { Name = "Ocean",  IsBuiltIn = true, IsDark = true,  Primary = "#006064", Secondary = "#00BCD4" },
            new() { Name = "Forest", IsBuiltIn = true, IsDark = false, Primary = "#2E7D32", Secondary = "#8BC34A" },
        ];

        private static MudTheme BuildMudTheme(AppTheme theme) => new()
        {
            PaletteLight = new PaletteLight
            {
                Primary = theme.Primary,
                Secondary = theme.Secondary,
            },
            PaletteDark = new PaletteDark
            {
                Primary = theme.Primary,
                Secondary = theme.Secondary,
            },
        };
    }
}
