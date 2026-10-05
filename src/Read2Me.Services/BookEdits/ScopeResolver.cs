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

    /// <summary>How many volume, part and chapter titles a pattern matches, book-wide.</summary>
    public sealed record TitleMatchCounts(int Volume, int Part, int Chapter)
    {
        public int Total => Volume + Part + Chapter;
    }

    /// <summary>
    /// Resolves an edit program's scope selector to concrete entities by walking the
    /// book hierarchy in reading order. Pure traversal — no LLM. Ordinal filters are
    /// 1-based and counted book-wide at the target level (chapter level for paragraph
    /// text). Entities with null titles are skipped for regex_replace and treated as
    /// empty strings otherwise. A regex_replace, or a change_case with a pattern, keeps
    /// only the targets its pattern matches, so the count is the real job; a value the
    /// pattern times out on is kept, so its row fails visibly rather than vanishing.
    /// </summary>
    public class ScopeResolver(IBookContentReader reader)
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        public virtual async Task<IReadOnlyList<EditTarget>> ResolveAsync(
            ProjectFolderId folderId, EditProgram program, CancellationToken ct = default)
        {
            var titleRegex = CreateRegex(program.NodeFilter.TitleRegex);
            var transformRegex = PrunesByPattern(program.Transform) ? CreateRegex(program.Transform.Pattern) : null;
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
                        AddTitleTarget(targets, program, transformRegex, BookEditTargetKind.VolumeTitle, volume.Id, volume.Title, volumeLabel);
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
                            AddTitleTarget(targets, program, transformRegex, BookEditTargetKind.PartTitle, part.Id, part.Title,
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
                                AddTitleTarget(targets, program, transformRegex, BookEditTargetKind.ChapterTitle, chapter.Id, chapter.Title, chapterPath);
                            continue;
                        }

                        // paragraph_text: node filter selects chapters
                        if (!NodeMatches(program.NodeFilter, titleRegex, chapterN, chapter.Title))
                            continue;

                        var paragraphs = (await reader.GetChildrenAsync(folderId, BookNodeLevel.Chapter, chapter.Id)).Paragraphs ?? [];
                        AddParagraphTargets(targets, predicates, transformRegex, chapter.Id, chapterPath, paragraphs);
                    }
                }
            }

            return targets;
        }

        /// <summary>
        /// For a paragraph-text change_case with a pattern, counts the volume, part and chapter titles
        /// the pattern also matches — what the run leaves behind, since one program targets one kind
        /// of value. Book-wide on purpose: the advice is a separate run over those titles, which the
        /// program's chapter and paragraph filters do not describe. All zero for any other program;
        /// a title the pattern times out on is not counted.
        /// </summary>
        public virtual async Task<TitleMatchCounts> CountTitlesAlsoMatchingAsync(
            ProjectFolderId folderId, EditProgram program, CancellationToken ct = default)
        {
            if (program is not { Target: EditTargetSelector.ParagraphText, Transform.Kind: TransformKind.ChangeCase }
                || CreateRegex(program.Transform.Pattern) is not { } regex)
                return new TitleMatchCounts(0, 0, 0);
            bool Matches(string? title) => title != null && SafeIsMatch(regex, title);
            int volumes = 0, parts = 0, chapters = 0;

            foreach (var volume in await reader.GetVolumesAsync(folderId))
            {
                ct.ThrowIfCancellationRequested();
                if (Matches(volume.Title)) volumes++;
                foreach (var part in (await reader.GetChildrenAsync(folderId, BookNodeLevel.Volume, volume.Id)).Parts ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (Matches(part.Title)) parts++;
                    chapters += ((await reader.GetChildrenAsync(folderId, BookNodeLevel.Part, part.Id)).Chapters ?? [])
                        .Count(c => Matches(c.Title));
                }
            }

            return new TitleMatchCounts(volumes, parts, chapters);
        }

        private static bool PrunesByPattern(EditTransform transform) =>
            transform.Kind is TransformKind.RegexReplace or TransformKind.ChangeCase;

        private static void AddTitleTarget(
            List<EditTarget> targets, EditProgram program, Regex? transformRegex,
            BookEditTargetKind kind, Guid id, string? title, string path)
        {
            if (title == null && program.Transform.Kind == TransformKind.RegexReplace)
                return;
            if (!MatchesOrTimesOut(transformRegex, title ?? string.Empty))
                return;
            targets.Add(new EditTarget(kind, id, title ?? string.Empty, path, targets.Count + 1, null, null));
        }

        private void AddParagraphTargets(
            List<EditTarget> targets, List<(EditPredicate Predicate, Regex? Regex)> predicates, Regex? transformRegex,
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
                    if (!MatchesOrTimesOut(transformRegex, text))
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

        /// <summary>Null regex = no pruning. A timeout keeps the target: the transform then fails
        /// that row with its timeout reason instead of the item silently leaving the plan.</summary>
        private static bool MatchesOrTimesOut(Regex? regex, string input)
        {
            if (regex == null) return true;
            try { return regex.IsMatch(input); }
            catch (RegexMatchTimeoutException) { return true; }
        }

        private static Regex? CreateRegex(string? pattern) =>
            string.IsNullOrEmpty(pattern) ? null : new Regex(pattern, RegexOptions.None, RegexTimeout);
    }
}
