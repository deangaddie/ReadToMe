using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services;

namespace Read2Me.App.Api
{
    public sealed record ThemeSelectionDto(int? SelectedThemeId, bool FollowSystemPreference);

    /// Both fields optional so a client can flip one without knowing the other.
    public sealed record ThemeSelectionUpdateRequest(int? SelectedThemeId = null, bool? FollowSystemPreference = null);

    /// <summary>
    /// Themes over HTTP (spec D9): the same <see cref="AppTheme"/> rows and selection the Blazor UI
    /// uses, so applying a theme in either UI restyles the other. Built-in rows are read-only.
    /// Mapped ahead of the <c>/api/settings/{**rest}</c> 404 fallback.
    /// </summary>
    public static class ThemeEndpoints
    {
        private static readonly Regex HexColour = new("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$", RegexOptions.Compiled);

        public static void MapThemeEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/settings/themes");

            group.MapGet("", ListAsync)
                .WithSummary("List all themes, built-in first.");
            group.MapPost("", CreateAsync)
                .WithMetadata(new AcceptsMetadata(["application/json"], typeof(AppTheme), isOptional: false))
                .WithSummary("Create a custom theme. Colours are #rgb or #rrggbb; optional colours may be null.");
            group.MapPut("/{id:int}", UpdateAsync)
                .WithMetadata(new AcceptsMetadata(["application/json"], typeof(AppTheme), isOptional: false))
                .WithSummary("Update a custom theme. 400 for built-in themes.");
            group.MapDelete("/{id:int}", DeleteAsync)
                .WithSummary("Delete a custom theme; clears the selection if it was active. 400 for built-in themes.");
            group.MapGet("/selection", GetSelectionAsync)
                .WithSummary("The selected theme id and the follow-system-preference flag.");
            group.MapPut("/selection", SetSelectionAsync)
                .WithMetadata(new AcceptsMetadata(["application/json"], typeof(ThemeSelectionUpdateRequest), isOptional: false))
                .WithSummary("Change the selected theme and/or the follow-system flag; both fields optional.");
        }

        // TODO(06): relay ThemeService.OnThemeChanged as `settingsChanged { area: 'themes' }` on the live hub.


        private static async Task<IResult> ListAsync(ThemeService svc) =>
            Results.Ok(await svc.GetAllThemesAsync());

        private static async Task<IResult> CreateAsync(HttpContext ctx, ThemeService svc)
        {
            if (await ctx.Request.ReadFromJsonAsync<AppTheme>() is not { } theme)
                return Results.Problem("Missing theme body.", statusCode: StatusCodes.Status400BadRequest);
            if (Validate(theme) is { } problem)
                return problem;

            theme.Id = 0;
            var created = await svc.CreateThemeAsync(theme);
            return Results.Created($"/api/settings/themes/{created.Id}", created);
        }

        private static async Task<IResult> UpdateAsync(HttpContext ctx, int id, ThemeService svc)
        {
            if (await ctx.Request.ReadFromJsonAsync<AppTheme>() is not { } theme)
                return Results.Problem("Missing theme body.", statusCode: StatusCodes.Status400BadRequest);
            if (Validate(theme) is { } problem)
                return problem;

            var existing = (await svc.GetAllThemesAsync()).FirstOrDefault(t => t.Id == id);
            if (existing is null)
                return Results.NotFound();
            if (existing.IsBuiltIn)
                return Results.Problem($"Theme '{existing.Name}' is built-in and cannot be edited.",
                    statusCode: StatusCodes.Status400BadRequest);

            theme.Id = id;
            await svc.UpdateThemeAsync(theme);
            return Results.Ok(theme);
        }

        private static async Task<IResult> DeleteAsync(int id, ThemeService svc)
        {
            var existing = (await svc.GetAllThemesAsync()).FirstOrDefault(t => t.Id == id);
            if (existing is null)
                return Results.NotFound();
            if (existing.IsBuiltIn)
                return Results.Problem($"Theme '{existing.Name}' is built-in and cannot be deleted.",
                    statusCode: StatusCodes.Status400BadRequest);

            await svc.DeleteThemeAsync(id);
            return Results.NoContent();
        }

        private static async Task<IResult> GetSelectionAsync(ThemeService svc)
        {
            return Results.Ok(new ThemeSelectionDto(
                await svc.GetSelectedThemeIdAsync(),
                await svc.GetFollowSystemPreferenceAsync()));
        }

        private static async Task<IResult> SetSelectionAsync(HttpContext ctx, ThemeService svc)
        {
            if (await ctx.Request.ReadFromJsonAsync<ThemeSelectionUpdateRequest>() is not { } request)
                return Results.Problem("Missing body.", statusCode: StatusCodes.Status400BadRequest);

            if (request.SelectedThemeId is { } themeId)
            {
                if ((await svc.GetAllThemesAsync()).All(t => t.Id != themeId))
                    return Results.NotFound();
                await svc.SetSelectedThemeAsync(themeId);
            }
            if (request.FollowSystemPreference is { } follow)
                await svc.SetFollowSystemPreferenceAsync(follow);

            return Results.Ok(new ThemeSelectionDto(
                await svc.GetSelectedThemeIdAsync(),
                await svc.GetFollowSystemPreferenceAsync()));
        }

        private static IResult? Validate(AppTheme theme)
        {
            if (string.IsNullOrWhiteSpace(theme.Name))
                return Results.Problem("Theme name is required.", statusCode: StatusCodes.Status400BadRequest);

            var colours = new (string Field, string? Value, bool Required)[]
            {
                ("primary", theme.Primary, true),
                ("secondary", theme.Secondary, true),
                ("background", theme.Background, false),
                ("surface", theme.Surface, false),
                ("appbarBackground", theme.AppbarBackground, false),
                ("drawerBackground", theme.DrawerBackground, false),
                ("textPrimary", theme.TextPrimary, false),
                ("textSecondary", theme.TextSecondary, false),
            };

            foreach (var (field, value, required) in colours)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (required)
                        return Results.Problem($"Colour '{field}' is required.", statusCode: StatusCodes.Status400BadRequest);
                    continue;
                }
                if (!HexColour.IsMatch(value))
                    return Results.Problem($"Colour '{field}' must be #rgb or #rrggbb, got '{value}'.",
                        statusCode: StatusCodes.Status400BadRequest);
            }

            // Optional colours: blank means "use the palette default", persist as null.
            theme.Background = Blank(theme.Background);
            theme.Surface = Blank(theme.Surface);
            theme.AppbarBackground = Blank(theme.AppbarBackground);
            theme.DrawerBackground = Blank(theme.DrawerBackground);
            theme.TextPrimary = Blank(theme.TextPrimary);
            theme.TextSecondary = Blank(theme.TextSecondary);
            theme.Name = theme.Name.Trim();
            return null;
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
