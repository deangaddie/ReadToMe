using Read2Me.Core.Models;

namespace Read2Me.Services.Events;

/// <summary>
/// A paragraph's items changed — restamped by attribution, or rewritten by an import or a user
/// edit. Published via <c>EventBroadcaster&lt;ParagraphItemsChanged&gt;</c>; subscribers reload that
/// paragraph's items rather than patching a single stamp, because a writer other than attribution
/// may add or remove items (attribution itself never does — ADR 0005).
/// </summary>
public sealed record ParagraphItemsChanged(ProjectFolderId FolderId, Guid ParagraphId);
