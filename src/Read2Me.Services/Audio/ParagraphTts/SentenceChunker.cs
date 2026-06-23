using System.Collections.Generic;

namespace Read2Me.Services.Audio.ParagraphTts;

public static class SentenceChunker
{
    public static IReadOnlyList<string> Chunk(IReadOnlyList<string> sentences, int maxChunkChars)
    {
        if (sentences.Count == 0)
            return [];

        var chunks = new List<string>();
        var current = string.Empty;

        foreach (var sentence in sentences)
        {
            if (current.Length == 0)
            {
                current = sentence;
            }
            else if (current.Length + 1 + sentence.Length <= maxChunkChars)
            {
                current = current + " " + sentence;
            }
            else
            {
                chunks.Add(current);
                current = sentence;
            }
        }

        if (current.Length > 0)
            chunks.Add(current);

        // Orphan merge-back: if last chunk < maxChunkChars/2, merge into previous
        if (chunks.Count > 1)
        {
            int orphanThreshold = maxChunkChars / 2;
            if (chunks[^1].Length < orphanThreshold)
            {
                chunks[^2] = chunks[^2] + " " + chunks[^1];
                chunks.RemoveAt(chunks.Count - 1);
            }
        }

        return chunks;
    }
}
