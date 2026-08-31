using System.Text.RegularExpressions;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

namespace Read2Me.Services.BookEdits
{
    /// <summary>One concrete entity matched by an edit program's scope.</summary>
    public sealed record EditTarget(
        BookEditTargetKind Kind,
        Guid Id,
        string CurrentValue,
        string DisplayPath,
        int OrdinalInScope,
        Guid? ChapterId,
        Guid? ParagraphId);

    /// <summary>
    /// Resolves an edit program's scope selector to concrete entities by walking the
    /// book hierarchy in reading order. Pure traversal — no LLM. Ordinal filters are
    /// 1-based and counted book-wide at the target level (chapter level for paragraph
    /// text). Entities with null titles are skipped for regex_replace and treated as
    /// empty strings otherwise.
    /// </summary>
    public class ScopeResolver(IBookContentReader reader)
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        public virtual async Task<IReadOnlyList<EditTarget>> ResolveAsync(
            ProjectFolderId folderId, EditProgram program, CancellationToken ct = default)
        {
            var titleRegex = CreateRegex(program.NodeFilter.TitleRegex);
            var predicates = program.ParagraphFilter.Where
                .Select(p => (Predicate: p, Regex: CreateRegex(p.Regex)))
                .ToList();
            var targets = new List<EditTarget>();

            var volumes = await reader.GetVolumesAsync(folderId);
            int volumeN = 0, partN = 0, chapterN = 0;

            foreach (var volume in volumes)
            {
                ct.ThrowIfCancellationRequested();
                volumeN++;
                var volumeLabel = string.IsNullOrWhiteSpace(volume.Title) ? $"Volume {volumeN}" : volume.Title;

                if (program.Target == EditTargetSelector.VolumeTitle)
                {
                    if (NodeMatches(program.NodeFilter, titleRegex, volumeN, volume.Title))
                        AddTitleTarget(targets, program, BookEditTargetKind.VolumeTitle, volume.Id, volume.Title, volumeLabel);
                    continue;
                }

                var parts = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Volume, volume.Id)).Parts ?? [];
                foreach (var part in parts)
                {
                    ct.ThrowIfCancellationRequested();
                    partN++;
                    var partLabel = string.IsNullOrWhiteSpace(part.Title) ? null : part.Title;

                    if (program.Target == EditTargetSelector.PartTitle)
                    {
                        if (NodeMatches(program.NodeFilter, titleRegex, partN, part.Title))
                            AddTitleTarget(targets, program, BookEditTargetKind.PartTitle, part.Id, part.Title,
                                $"{volumeLabel} › {partLabel ?? $"Part {partN}"}");
                        continue;
                    }

                    var chapters = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Part, part.Id)).Chapters ?? [];
                    foreach (var chapter in chapters)
                    {
                        ct.ThrowIfCancellationRequested();
                        chapterN++;
                        var chapterLabel = string.IsNullOrWhiteSpace(chapter.Title)
                            ? $"Chapter {chapterN}"
                            : $"Chapter {chapterN} '{chapter.Title}'";
                        var chapterPath = partLabel == null
                            ? $"{volumeLabel} › {chapterLabel}"
                            : $"{volumeLabel} › {partLabel} › {chapterLabel}";

                        if (program.Target == EditTargetSelector.ChapterTitle)
                        {
                            if (NodeMatches(program.NodeFilter, titleRegex, chapterN, chapter.Title))
                                AddTitleTarget(targets, program, BookEditTargetKind.ChapterTitle, chapter.Id, chapter.Title, chapterPath);
                            continue;
                        }

                        // paragraph_text: node filter selects chapters
                        if (!NodeMatches(program.NodeFilter, titleRegex, chapterN, chapter.Title))
                            continue;

                        var paragraphs = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Chapter, chapter.Id)).Paragraphs ?? [];
                        AddParagraphTargets(targets, predicates, chapter.Id, chapterPath, paragraphs);
                    }
                }
            }

            return targets;
        }

        private static void AddTitleTarget(
            List<EditTarget> targets, EditProgram program, BookEditTargetKind kind, Guid id, string? title, string path)
        {
            if (title == null && program.Transform.Kind == TransformKind.RegexReplace)
                return;
            targets.Add(new EditTarget(kind, id, title ?? string.Empty, path, targets.Count + 1, null, null));
        }

        private void AddParagraphTargets(
            List<EditTarget> targets, List<(EditPredicate Predicate, Regex? Regex)> predicates,
            Guid chapterId, string chapterPath, List<Paragraph> paragraphs)
        {
            var contentParagraphs = paragraphs
                .Select((p, i) => (Paragraph: p, Items: ContentItems(p)))
                .Where(x => x.Items.Count > 0)
                .Select((x, i) => (x.Paragraph, x.Items, Number: i + 1))
                .ToList();

            foreach (var (paragraph, items, number) in contentParagraphs)
            {
                var fromEnd = contentParagraphs.Count - number + 1;
                for (var j = 0; j < items.Count; j++)
                {
                    var text = items[j].Text!;
                    var itemOrdinal = j + 1;
                    if (!predicates.All(p => PredicateMatches(p.Predicate, p.Regex, number, fromEnd, itemOrdinal, text)))
                        continue;
                    targets.Add(new EditTarget(
                        BookEditTargetKind.ParagraphItemText, items[j].Id, text,
                        $"{chapterPath} › ¶{number}", targets.Count + 1, chapterId, paragraph.Id));
                }
            }
        }

        private static bool PredicateMatches(
            EditPredicate predicate, Regex? regex, int paragraphOrdinal, int fromEnd, int itemOrdinal, string text)
        {
            if (predicate.Field == PredicateField.Text)
                return regex != null && SafeIsMatch(regex, text);

            var actual = predicate.Field switch
            {
                PredicateField.ParagraphOrdinal => paragraphOrdinal,
                PredicateField.ParagraphOrdinalFromEnd => fromEnd,
                _ => itemOrdinal,
            };
            return predicate.Op switch
            {
                PredicateOp.Eq => actual == predicate.Value,
                PredicateOp.Ne => actual != predicate.Value,
                PredicateOp.Lt => actual < predicate.Value,
                PredicateOp.Le => actual <= predicate.Value,
                PredicateOp.Gt => actual > predicate.Value,
                PredicateOp.Ge => actual >= predicate.Value,
                PredicateOp.Between => actual >= predicate.Value && actual <= predicate.ValueTo,
                _ => false,
            };
        }

        private static List<ParagraphItem> ContentItems(Paragraph paragraph) =>
            paragraph.Items
                .Where(i => !ParagraphItemKinds.IsPause(i.ItemType)
                            && !string.IsNullOrWhiteSpace(i.Text))
                .OrderBy(i => i.Order, StringComparer.Ordinal)
                .ToList();

        private bool NodeMatches(NodeFilter filter, Regex? titleRegex, int ordinal, string? title)
        {
            if (filter.OrdinalFrom is { } from && ordinal < from) return false;
            if (filter.OrdinalTo is { } to && ordinal > to) return false;
            if (titleRegex != null && (title == null || !SafeIsMatch(titleRegex, title))) return false;
            return true;
        }

        private static bool SafeIsMatch(Regex regex, string input)
        {
            try { return regex.IsMatch(input); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static Regex? CreateRegex(string? pattern) =>
            string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.None, RegexTimeout);
    }
}
