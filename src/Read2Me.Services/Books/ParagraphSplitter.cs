namespace Read2Me.Services.Books
{
    public enum SegmentType { Narration, Dialogue }

    public record ParagraphSegment(string Text, SegmentType Type);

    public static class ParagraphSplitter
    {
        private const char AsciiDoubleQuote  = '"';
        private const char CurlyOpenDouble   = '“'; // "
        private const char CurlyCloseDouble  = '”'; // "
        private const char AsciiSingleQuote  = '\'';
        private const char CurlyOpenSingle   = '‘'; // '
        private const char CurlyCloseSingle  = '’'; // '

        public static List<ParagraphSegment> Split(string text)
        {
            if (string.IsNullOrEmpty(text))
                return [new ParagraphSegment(text ?? string.Empty, SegmentType.Narration)];

            var segments = new List<ParagraphSegment>();
            var current  = new System.Text.StringBuilder();
            var currentType = SegmentType.Narration;
            int pos = 0;

            void Flush()
            {
                if (current.Length > 0)
                {
                    segments.Add(new ParagraphSegment(current.ToString(), currentType));
                    current.Clear();
                }
            }

            while (pos < text.Length)
            {
                char c = text[pos];

                if (c == AsciiDoubleQuote && currentType == SegmentType.Narration)
                {
                    Flush();
                    currentType = SegmentType.Dialogue;
                    current.Append(c); pos++;
                    while (pos < text.Length && text[pos] != AsciiDoubleQuote)
                        current.Append(text[pos++]);
                    if (pos < text.Length) current.Append(text[pos++]);
                    Flush(); currentType = SegmentType.Narration; continue;
                }

                if (c == CurlyOpenDouble && currentType == SegmentType.Narration)
                {
                    Flush();
                    currentType = SegmentType.Dialogue;
                    current.Append(c); pos++;
                    while (pos < text.Length && text[pos] != CurlyCloseDouble)
                        current.Append(text[pos++]);
                    if (pos < text.Length) current.Append(text[pos++]);
                    Flush(); currentType = SegmentType.Narration; continue;
                }

                if (c == AsciiSingleQuote && currentType == SegmentType.Narration && !PrecededByLetter(text, pos))
                {
                    Flush();
                    currentType = SegmentType.Dialogue;
                    current.Append(c); pos++;
                    while (pos < text.Length)
                    {
                        if (text[pos] == AsciiSingleQuote && !FollowedByLetter(text, pos)) break;
                        current.Append(text[pos++]);
                    }
                    if (pos < text.Length) current.Append(text[pos++]);
                    Flush(); currentType = SegmentType.Narration; continue;
                }

                if (c == CurlyOpenSingle && currentType == SegmentType.Narration)
                {
                    Flush();
                    currentType = SegmentType.Dialogue;
                    current.Append(c); pos++;
                    while (pos < text.Length && text[pos] != CurlyCloseSingle)
                        current.Append(text[pos++]);
                    if (pos < text.Length) current.Append(text[pos++]);
                    Flush(); currentType = SegmentType.Narration; continue;
                }

                current.Append(c); pos++;
            }

            Flush();

            if (segments.Count == 0)
                segments.Add(new ParagraphSegment(text, SegmentType.Narration));

            return segments;
        }

        private static bool PrecededByLetter(string text, int pos) =>
            pos > 0 && char.IsLetter(text[pos - 1]);

        private static bool FollowedByLetter(string text, int pos) =>
            pos + 1 < text.Length && char.IsLetter(text[pos + 1]);
    }
}
