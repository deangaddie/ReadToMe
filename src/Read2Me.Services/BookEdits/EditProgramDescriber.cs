namespace Read2Me.Services.BookEdits
{
    /// <summary>Human-readable one-line summary of a parsed edit program for the review UI.</summary>
    public static class EditProgramDescriber
    {
        public static string Describe(EditProgram program)
        {
            var target = program.Target switch
            {
                EditTargetSelector.VolumeTitle => "volume titles",
                EditTargetSelector.PartTitle => "part titles",
                EditTargetSelector.ChapterTitle => "chapter titles",
                _ => "paragraph text",
            };

            var scope = ScopePhrase(program);
            var transform = program.Transform.Kind switch
            {
                TransformKind.RegexReplace =>
                    $"replace pattern \"{program.Transform.Pattern}\" with \"{program.Transform.Replacement}\"",
                TransformKind.SetTemplate =>
                    $"set to template \"{program.Transform.Template}\"",
                _ => $"AI edit: \"{program.Transform.Instruction}\"",
            };

            return $"Edit {target}{scope} — {transform}";
        }

        private static string ScopePhrase(EditProgram program)
        {
            var parts = new List<string>();
            var nf = program.NodeFilter;
            if (nf.OrdinalFrom != null || nf.OrdinalTo != null)
                parts.Add(nf.OrdinalFrom == nf.OrdinalTo && nf.OrdinalFrom != null
                    ? $"#{nf.OrdinalFrom}"
                    : $"#{nf.OrdinalFrom?.ToString() ?? "1"}–{nf.OrdinalTo?.ToString() ?? "end"}");
            if (nf.TitleRegex != null)
                parts.Add($"titles matching \"{nf.TitleRegex}\"");

            if (program.Target == EditTargetSelector.ParagraphText)
            {
                if (program.ParagraphFilter.Where.Count == 0)
                    parts.Add("all paragraphs");
                else
                    parts.AddRange(program.ParagraphFilter.Where.Select(PredicatePhrase));
            }

            return parts.Count == 0 ? " (whole book)" : $" ({string.Join(", ", parts)})";
        }

        private static string PredicatePhrase(EditPredicate p) => p switch
        {
            { Field: PredicateField.Text } => $"text matching \"{p.Regex}\"",
            { Field: PredicateField.ParagraphOrdinal, Op: PredicateOp.Eq, Value: 1 } => "first paragraph of each chapter",
            { Field: PredicateField.ParagraphOrdinal, Op: PredicateOp.Eq } => $"paragraph #{p.Value} of each chapter",
            { Field: PredicateField.ParagraphOrdinalFromEnd, Op: PredicateOp.Eq, Value: 1 } => "last paragraph of each chapter",
            { Field: PredicateField.ParagraphOrdinalFromEnd, Op: PredicateOp.Eq } => $"paragraph #{p.Value} from the end of each chapter",
            { Field: PredicateField.ItemOrdinal, Op: PredicateOp.Eq, Value: 1 } => "opening text item",
            _ => $"{FieldName(p.Field)} {OpPhrase(p)}",
        };

        private static string FieldName(PredicateField field) => field switch
        {
            PredicateField.ParagraphOrdinal => "paragraph #",
            PredicateField.ParagraphOrdinalFromEnd => "paragraph-from-end #",
            _ => "item #",
        };

        private static string OpPhrase(EditPredicate p) => p.Op switch
        {
            PredicateOp.Eq => $"= {p.Value}",
            PredicateOp.Ne => $"≠ {p.Value}",
            PredicateOp.Lt => $"< {p.Value}",
            PredicateOp.Le => $"≤ {p.Value}",
            PredicateOp.Gt => $"> {p.Value}",
            PredicateOp.Ge => $"≥ {p.Value}",
            _ => $"{p.Value}–{p.ValueTo}",
        };
    }
}
