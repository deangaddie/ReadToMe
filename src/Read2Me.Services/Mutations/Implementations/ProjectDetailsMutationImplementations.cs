using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services.Mutations.Implementations;

/// <summary>
/// What the project details family shares: one project row, and effects that name nothing on it.
/// <see cref="BookFacets.ProjectPolicy"/> is what sends the Overview and the app bar back to the
/// project detail.
/// </summary>
internal static class ProjectDetailsEffects
{
    public static readonly BookMutationEffects Changed = new()
    {
        Scope = BookMutationScope.Exact,
        Facets = BookFacets.ProjectPolicy,
    };

    public static async Task<ProjectEntity> ProjectAsync(
        ProjectDbContext db, ProjectFolderId folder, CancellationToken ct) =>
        await db.Projects.SingleOrDefaultAsync(ct)
            ?? throw new BookMutationRejectedException(
                BookMutationRejection.NotFound, $"No project record found in '{folder.Value}'.");
}

/// <summary>Rewrites the fields it is given; values that come back unchanged are no change.</summary>
public sealed class UpdateProjectMetadataMutationImplementation
    : IBookMutationImplementation<UpdateProjectMetadataMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        UpdateProjectMetadataMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var project = await ProjectDetailsEffects.ProjectAsync(db, mutation.FolderId, ct);

        var changed = false;
        if (mutation.Title is { } title && title != project.Title)
        {
            project.Title = title;
            changed = true;
        }
        if (mutation.BookTitle is { } bookTitle && bookTitle != project.BookTitle)
        {
            project.BookTitle = bookTitle;
            changed = true;
        }
        if (mutation.Author is { } author && author != project.Author)
        {
            project.Author = author;
            changed = true;
        }

        return changed ? ProjectDetailsEffects.Changed : BookMutationEffects.Nothing;
    }
}

/// <summary>
/// Names or clears the cover. Naming one is always a change, for the reason
/// <see cref="SetCoverImageMutation"/> gives; clearing a cover that is not there is not.
/// </summary>
public sealed class SetCoverImageMutationImplementation : IBookMutationImplementation<SetCoverImageMutation>
{
    public async Task<BookMutationEffects> ApplyAsync(
        SetCoverImageMutation mutation, ProjectDbContext db, CancellationToken ct)
    {
        var project = await ProjectDetailsEffects.ProjectAsync(db, mutation.FolderId, ct);

        if (mutation.CoverImageFileName is null && project.CoverImage is null)
            return BookMutationEffects.Nothing;

        project.CoverImage = mutation.CoverImageFileName;
        return ProjectDetailsEffects.Changed;
    }
}
