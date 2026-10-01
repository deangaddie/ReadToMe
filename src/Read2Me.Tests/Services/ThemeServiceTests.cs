using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ThemeServiceTests : AppDbTestBase
    {
        private ThemeService CreateService() =>
            new(Factory, NullLogger<ThemeService>.Instance);

        // ---------------------------------------------------------------
        // Seeding / GetAllThemesAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task GetAllThemesAsync_EmptyDb_SeedsBuiltInsAndASettingsRow()
        {
            var svc = CreateService();

            await svc.GetAllThemesAsync();

            await using var db = await Factory.CreateDbContextAsync();
            Assert.True(await db.Themes.CountAsync(t => t.IsBuiltIn) >= 2);
            Assert.Equal(1, await db.Settings.CountAsync());
            Assert.Null(await svc.GetSelectedThemeIdAsync());
            Assert.False(await svc.GetFollowSystemPreferenceAsync());
        }

        [Fact]
        public async Task GetAllThemesAsync_SeedsAndOrdersBuiltInsFirst()
        {
            var svc = CreateService();
            await svc.CreateThemeAsync(new AppTheme { Name = "AAA Custom" });

            var themes = await svc.GetAllThemesAsync();

            Assert.True(themes.Count >= 2);
            Assert.True(themes.First().IsBuiltIn);
            Assert.False(themes.Last().IsBuiltIn);
        }

        [Fact]
        public async Task GetAllThemesAsync_CalledTwice_DoesNotDuplicateBuiltIns()
        {
            var svc = CreateService();

            var first = await svc.GetAllThemesAsync();
            var second = await svc.GetAllThemesAsync();

            Assert.Equal(first.Count, second.Count);
        }

        // ---------------------------------------------------------------
        // SetSelectedThemeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetSelectedThemeAsync_Persists()
        {
            var svc = CreateService();
            var themes = await svc.GetAllThemesAsync();
            var dark = themes.Single(t => t.Name == "Dark");

            await svc.SetSelectedThemeAsync(dark.Id);

            Assert.Equal(dark.Id, await CreateService().GetSelectedThemeIdAsync());
        }

        [Fact]
        public async Task SetSelectedThemeAsync_PublishesAThemesSettingsChange()
        {
            var changes = new EventBroadcaster<SettingsChanged>();
            var svc = new ThemeService(Factory, NullLogger<ThemeService>.Instance, changes);
            var themes = await svc.GetAllThemesAsync();
            var published = new List<SettingsChanged>();
            changes.Event += published.Add;

            await svc.SetSelectedThemeAsync(themes.First().Id);

            Assert.Equal([new SettingsChanged(SettingsArea.Themes)], published);
        }

        // ---------------------------------------------------------------
        // CreateThemeAsync / UpdateThemeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task CreateThemeAsync_ForcesIsBuiltInFalse()
        {
            var svc = CreateService();

            var created = await svc.CreateThemeAsync(new AppTheme { Name = "Mine", IsBuiltIn = true });

            Assert.False(created.IsBuiltIn);
            Assert.True(created.Id > 0);
        }

        [Fact]
        public async Task UpdateThemeAsync_CustomTheme_PersistsTheChange()
        {
            var svc = CreateService();
            var custom = await svc.CreateThemeAsync(new AppTheme { Name = "Mine", IsDark = false });

            custom.IsDark = true;
            await svc.UpdateThemeAsync(custom);

            var themes = await svc.GetAllThemesAsync();
            Assert.True(themes.Single(t => t.Id == custom.Id).IsDark);
        }

        [Fact]
        public async Task UpdateThemeAsync_BuiltIn_IsIgnored()
        {
            var svc = CreateService();
            var themes = await svc.GetAllThemesAsync();
            var builtIn = themes.First(t => t.IsBuiltIn);
            var originalName = builtIn.Name;

            builtIn.Name = "Hacked";
            await svc.UpdateThemeAsync(builtIn);

            await using var db = await Factory.CreateDbContextAsync();
            var stored = await db.Themes.SingleAsync(t => t.Id == builtIn.Id);
            Assert.Equal(originalName, stored.Name);
        }

        [Fact]
        public async Task UpdateThemeAsync_CannotPromoteCustomToBuiltIn()
        {
            var svc = CreateService();
            var custom = await svc.CreateThemeAsync(new AppTheme { Name = "Mine" });

            custom.IsBuiltIn = true;
            await svc.UpdateThemeAsync(custom);

            await using var db = await Factory.CreateDbContextAsync();
            var stored = await db.Themes.SingleAsync(t => t.Id == custom.Id);
            Assert.False(stored.IsBuiltIn);
        }

        // ---------------------------------------------------------------
        // DeleteThemeAsync
        // ---------------------------------------------------------------

        [Fact]
        public async Task DeleteThemeAsync_BuiltIn_IsIgnored()
        {
            var svc = CreateService();
            var themes = await svc.GetAllThemesAsync();
            var builtIn = themes.First(t => t.IsBuiltIn);

            await svc.DeleteThemeAsync(builtIn.Id);

            await using var db = await Factory.CreateDbContextAsync();
            Assert.True(await db.Themes.AnyAsync(t => t.Id == builtIn.Id));
        }

        [Fact]
        public async Task DeleteThemeAsync_ActiveCustomTheme_DeletesAndClearsSelection()
        {
            var svc = CreateService();
            await svc.GetAllThemesAsync(); // seed settings row
            var custom = await svc.CreateThemeAsync(new AppTheme { Name = "Mine" });
            await svc.SetSelectedThemeAsync(custom.Id);

            await svc.DeleteThemeAsync(custom.Id);

            Assert.Null(await svc.GetSelectedThemeIdAsync());
            await using var db = await Factory.CreateDbContextAsync();
            Assert.False(await db.Themes.AnyAsync(t => t.Id == custom.Id));
        }

        // ---------------------------------------------------------------
        // Follow-system preference
        // ---------------------------------------------------------------

        [Fact]
        public async Task SetFollowSystemPreferenceAsync_RoundTrips()
        {
            var svc = CreateService();
            await svc.GetAllThemesAsync(); // seed

            await svc.SetFollowSystemPreferenceAsync(true);

            Assert.True(await svc.GetFollowSystemPreferenceAsync());
        }
    }
}
