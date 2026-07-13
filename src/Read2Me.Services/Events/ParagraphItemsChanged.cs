using Read2Me.Core.Models;

namespace Read2Me.Services.Events;

/// <summary>
/// A paragraph's items were rewritten (re-segmented, restamped, or both). Published via
/// <c>EventBroadcaster&lt;ParagraphItemsChanged&gt;</c>; subscribers reload that paragraph's items
/// rather than patching a single stamp, because segmentation can add and remove items too.
/// </summary>
public sealed record ParagraphItemsChanged(ProjectFolderId FolderId, Guid ParagraphId);
