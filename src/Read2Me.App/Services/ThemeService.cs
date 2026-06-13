using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.App.Services
{
    public class ThemeService
    {
        private readonly Read2MeDbContext _db;
        private MudTheme? _cachedMudTheme;
        private AppTheme? _cachedAppTheme;

        public event Action? OnThemeChanged;

        public ThemeService(Read2MeDbContext db)
        {
            _db = db;
        }

        public async Task<(MudTheme Theme, bool IsDark)> GetCurrentThemeAsync()
        {
            if (_cachedMudTheme != null && _cachedAppTheme != null)
                return (_cachedMudTheme, _cachedAppTheme.IsDark);

            await EnsureSeededAsync();

            var settings = await _db.Settings.FirstOrDefaultAsync();
            AppTheme? theme = null;

            if (settings?.SelectedThemeId != null)
                theme = await _db.Themes.FindAsync(settings.SelectedThemeId);

            theme ??= await _db.Themes.FirstAsync();

            _cachedAppTheme = theme;
            _cachedMudTheme = BuildMudTheme(theme);
            return (_cachedMudTheme, theme.IsDark);
        }

        public async Task<List<AppTheme>> GetAllThemesAsync()
        {
            await EnsureSeededAsync();
            return await _db.Themes
                .OrderBy(t => t.IsBuiltIn ? 0 : 1)
                .ThenBy(t => t.Name)
                .ToListAsync();
        }

        public async Task<int?> GetSelectedThemeIdAsync()
        {
            var settings = await _db.Settings.FirstOrDefaultAsync();
            return settings?.SelectedThemeId;
        }

        public async Task SetSelectedThemeAsync(int themeId)
        {
            var settings = await _db.Settings.FirstOrDefaultAsync();
            if (settings == null)
            {
                _db.Settings.Add(new AppSettings { SelectedThemeId = themeId });
            }
            else
            {
                settings.SelectedThemeId = themeId;
            }
            await _db.SaveChangesAsync();

            InvalidateCache();
            OnThemeChanged?.Invoke();
        }

        public async Task<AppTheme> CreateThemeAsync(AppTheme theme)
        {
            theme.IsBuiltIn = false;
            _db.Themes.Add(theme);
            await _db.SaveChangesAsync();
            return theme;
        }

        public async Task UpdateThemeAsync(AppTheme theme)
        {
            _db.Themes.Update(theme);
            await _db.SaveChangesAsync();

            if (_cachedAppTheme?.Id == theme.Id)
            {
                InvalidateCache();
                OnThemeChanged?.Invoke();
            }
        }

        public async Task DeleteThemeAsync(int themeId)
        {
            var theme = await _db.Themes.FindAsync(themeId);
            if (theme == null || theme.IsBuiltIn) return;

            _db.Themes.Remove(theme);

            var settings = await _db.Settings.FirstOrDefaultAsync();
            if (settings?.SelectedThemeId == themeId)
                settings.SelectedThemeId = null;

            await _db.SaveChangesAsync();
            InvalidateCache();
            OnThemeChanged?.Invoke();
        }

        private void InvalidateCache()
        {
            _cachedMudTheme = null;
            _cachedAppTheme = null;
        }

        private async Task EnsureSeededAsync()
        {
            if (await _db.Themes.AnyAsync()) return;

            _db.Themes.AddRange(GetBuiltInThemes());
            _db.Settings.Add(new AppSettings());
            await _db.SaveChangesAsync();
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
