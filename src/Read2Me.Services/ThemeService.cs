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
        private bool _cachedFollowSystem;

        public event Action? OnThemeChanged;

        public ThemeService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<ThemeService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<(MudTheme Theme, bool IsDark, bool FollowSystem)> GetCurrentThemeAsync()
        {
            await _cacheLock.WaitAsync();
            try
            {
                if (_cachedMudTheme != null && _cachedAppTheme != null)
                {
                    _logger.LogDebug("Returning cached theme '{Name}'", _cachedAppTheme.Name);
                    return (_cachedMudTheme, _cachedAppTheme.IsDark, _cachedFollowSystem);
                }

                await using var db = await _dbFactory.CreateDbContextAsync();
                await EnsureSeededAsync(db);

                var settings = await db.Settings.SingleOrDefaultAsync();
                AppTheme? theme = null;

                if (settings?.SelectedThemeId != null)
                    theme = await db.Themes.FindAsync(settings.SelectedThemeId);

                theme ??= await db.Themes.OrderBy(t => t.Id).FirstAsync();

                _logger.LogDebug("Loaded theme '{Name}' (dark={IsDark}, follow={Follow})", theme.Name, theme.IsDark, settings?.FollowSystemPreference ?? false);
                _cachedAppTheme = theme;
                _cachedMudTheme = BuildMudTheme(theme);
                _cachedFollowSystem = settings?.FollowSystemPreference ?? false;
                return (_cachedMudTheme, theme.IsDark, _cachedFollowSystem);
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
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Check the stored row, not the caller's copy — built-in presets are immutable.
            var isBuiltIn = await db.Themes
                .Where(t => t.Id == theme.Id)
                .Select(t => (bool?)t.IsBuiltIn)
                .SingleOrDefaultAsync();
            if (isBuiltIn != false)
            {
                _logger.LogWarning("UpdateThemeAsync: theme ID {ThemeId} not found or is built-in", theme.Id);
                return;
            }

            _logger.LogInformation("Updating theme '{Name}' (ID {Id})", theme.Name, theme.Id);
            theme.IsBuiltIn = false;
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

        public async Task SetFollowSystemPreferenceAsync(bool follow)
        {
            _logger.LogInformation("Setting follow system preference to {Follow}", follow);
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null)
            {
                db.Settings.Add(new AppSettings { FollowSystemPreference = follow });
            }
            else
            {
                settings.FollowSystemPreference = follow;
            }
            await db.SaveChangesAsync();

            InvalidateCache();
            OnThemeChanged?.Invoke();
        }

        public async Task<bool> GetFollowSystemPreferenceAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.FollowSystemPreference ?? false;
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
            var builtInThemes = GetBuiltInThemes();
            var existingBuiltInNames = await db.Themes
                .Where(t => t.IsBuiltIn)
                .Select(t => t.Name)
                .ToListAsync();

            var missingThemes = builtInThemes
                .Where(t => !existingBuiltInNames.Contains(t.Name))
                .ToList();

            if (missingThemes.Any())
            {
                db.Themes.AddRange(missingThemes);
            }

            if (!await db.Settings.AnyAsync())
            {
                db.Settings.Add(new AppSettings());
            }

            if (missingThemes.Any() || !await db.Settings.AnyAsync())
            {
                await db.SaveChangesAsync();
            }
        }

        private static List<AppTheme> GetBuiltInThemes() =>
        [
            new() { Name = "Light",  IsBuiltIn = true, IsDark = false, Primary = "#594AE2", Secondary = "#FF4081" },
            new() { Name = "Dark",   IsBuiltIn = true, IsDark = true,  Primary = "#7C5CBF", Secondary = "#FF4081", Background = "#27272f", Surface = "#373740", AppbarBackground = "#27272f", DrawerBackground = "#27272f" },
            new() { Name = "Ocean",  IsBuiltIn = true, IsDark = true,  Primary = "#006064", Secondary = "#00BCD4", Background = "#002f35", Surface = "#004d40", AppbarBackground = "#006064", DrawerBackground = "#002f35" },
            new() { Name = "Forest", IsBuiltIn = true, IsDark = false, Primary = "#2E7D32", Secondary = "#8BC34A" },
            new() { Name = "Sunset", IsBuiltIn = true, IsDark = false, Primary = "#FF6F61", Secondary = "#FFB347", Background = "#FFF5F2", Surface = "#FFFFFF", AppbarBackground = "#FF6F61", TextPrimary = "#4A4A4A" },
            new() { Name = "Midnight", IsBuiltIn = true, IsDark = true, Primary = "#BB86FC", Secondary = "#03DAC6", Background = "#121212", Surface = "#1E1E1E", AppbarBackground = "#1F1B24", DrawerBackground = "#121212" },
            new() { Name = "Nord", IsBuiltIn = true, IsDark = true, Primary = "#88C0D0", Secondary = "#81A1C1", Background = "#2E3440", Surface = "#3B4252", AppbarBackground = "#2E3440", DrawerBackground = "#2E3440", TextPrimary = "#ECEFF4" },
            new() { Name = "Coffee", IsBuiltIn = true, IsDark = false, Primary = "#6F4E37", Secondary = "#A67B5B", Background = "#F5F5DC", Surface = "#FFFFFF", AppbarBackground = "#6F4E37" },
            new() { Name = "Cyberpunk", IsBuiltIn = true, IsDark = true, Primary = "#F0ED0D", Secondary = "#00F0FF", Background = "#010101", Surface = "#1A1A1A", AppbarBackground = "#010101", DrawerBackground = "#010101", TextPrimary = "#F0ED0D" },
        ];

        private static MudTheme BuildMudTheme(AppTheme theme)
        {
            var mudTheme = new MudTheme();

            if (theme.IsDark)
            {
                ApplyPalette(mudTheme.PaletteDark, theme);
            }
            else
            {
                ApplyPalette(mudTheme.PaletteLight, theme);
            }

            return mudTheme;
        }

        private static void ApplyPalette(Palette palette, AppTheme theme)
        {
            palette.Primary = theme.Primary;
            palette.Secondary = theme.Secondary;

            if (!string.IsNullOrEmpty(theme.Background)) palette.Background = theme.Background;
            if (!string.IsNullOrEmpty(theme.Surface)) palette.Surface = theme.Surface;
            if (!string.IsNullOrEmpty(theme.AppbarBackground)) palette.AppbarBackground = theme.AppbarBackground;
            if (!string.IsNullOrEmpty(theme.DrawerBackground)) palette.DrawerBackground = theme.DrawerBackground;
            if (!string.IsNullOrEmpty(theme.TextPrimary)) palette.TextPrimary = theme.TextPrimary;
            if (!string.IsNullOrEmpty(theme.TextSecondary)) palette.TextSecondary = theme.TextSecondary;
        }
    }
}
