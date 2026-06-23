using System.Text;
using Read2Me.Data.Enums;
using Read2Me.Services.Audio.Assembly;

namespace Read2Me.Services.Audio
{
    public static class AudiobookAssemblyPlanner
    {
        private static readonly ParagraphItemType[] PauseKinds =
        [
            ParagraphItemType.VolumePause,
            ParagraphItemType.PartPause,
            ParagraphItemType.ChapterPause,
            ParagraphItemType.ParagraphPause,
            ParagraphItemType.Pause,
        ];

        public static bool IsPause(ParagraphItemType kind) =>
            Array.IndexOf(PauseKinds, kind) >= 0;

        // ── 1. Pause kind → milliseconds ────────────────────────────────────

        public static int PauseMs(ParagraphItemType kind, AudioProcessingSettings settings) =>
            kind switch
            {
                ParagraphItemType.VolumePause    => settings.VolumePauseMs,
                ParagraphItemType.PartPause      => settings.PartPauseMs,
                ParagraphItemType.ChapterPause   => settings.ChapterPauseMs,
                ParagraphItemType.ParagraphPause => settings.ParagraphPauseMs,
                ParagraphItemType.Pause          => settings.PauseMs,
                _ => throw new ArgumentException($"Not a pause kind: {kind}", nameof(kind)),
            };

        // ── 2. Concat-entry list builder ─────────────────────────────────────

        public static IReadOnlyList<ConcatEntry> BuildConcatEntries(
            IReadOnlyList<AssemblyManifestEntry> manifest,
            AudioProcessingSettings settings)
        {
            var result = new List<ConcatEntry>(manifest.Count);
            foreach (var entry in manifest)
            {
                if (IsPause(entry.ItemType))
                {
                    result.Add(new ConcatEntry.Silence(PauseMs(entry.ItemType, settings)));
                }
                else
                {
                    if (entry.AudioRelativePath is null)
                        throw new InvalidOperationException(
                            $"Non-pause item {entry.ParagraphItemId} has null AudioRelativePath. " +
                            "Assembly precondition requires all non-pause items to have audio.");
                    result.Add(new ConcatEntry.Audio(entry.AudioRelativePath));
                }
            }
            return result;
        }

        // ── 3. Chapter-timestamp computation ─────────────────────────────────

        public static List<ChapterMarker> ComputeChapterTimestamps(
            IReadOnlyList<AssemblyManifestEntry> manifest,
            IReadOnlyDictionary<Guid, TimeSpan> audioDurations,
            AudioProcessingSettings settings)
        {
            var markers = new List<ChapterMarker>();
            var offset = TimeSpan.Zero;

            Guid? prevVolId = null;
            Guid? prevPartId = null;
            Guid? prevChapId = null;

            // Track per-level 1-based index for fallback titles
            int volIdx = 0, partIdx = 0, chapIdx = 0;
            // Last seen ids to detect resets when level above changes
            Guid? lastVolId = null;

            // We need to record pending markers then close them when next section starts.
            // Strategy: walk entries, accumulate offset; when section boundary detected,
            // record the start offset for the new marker(s).
            // Markers close when the next boundary (or end) is known.

            // Build a flat list of (startOffset, title) pairs, then assign End on a second pass.
            var pending = new List<(string Title, TimeSpan Start)>();

            foreach (var entry in manifest)
            {
                bool volChanged  = entry.VolumeId  != prevVolId;
                bool partChanged = entry.PartId     != prevPartId;
                bool chapChanged = entry.ChapterId  != prevChapId;

                bool isBoundary = prevVolId == null || volChanged || partChanged || chapChanged;

                if (isBoundary && !IsPause(entry.ItemType))
                {
                    // Advance level indexes
                    if (prevVolId == null || volChanged)
                    {
                        volIdx++;
                        partIdx = 0;
                        chapIdx = 0;
                        lastVolId = entry.VolumeId;
                    }
                    if (prevPartId == null || partChanged || volChanged)
                    {
                        partIdx++;
                        chapIdx = 0;
                    }
                    if (prevChapId == null || chapChanged || partChanged || volChanged)
                    {
                        chapIdx++;
                    }

                    if (volChanged || prevVolId == null)
                        pending.Add((entry.VolumeTitle ?? $"Chapter {volIdx}", offset));
                    if (partChanged || prevPartId == null)
                        pending.Add((entry.PartTitle ?? $"Chapter {partIdx}", offset));
                    if (chapChanged || prevChapId == null)
                        pending.Add((entry.ChapterTitle ?? $"Chapter {chapIdx}", offset));

                    prevVolId  = entry.VolumeId;
                    prevPartId = entry.PartId;
                    prevChapId = entry.ChapterId;
                }
                else if (isBoundary && IsPause(entry.ItemType))
                {
                    // Pause at a section boundary: advance prevIds so the next audio item
                    // triggers the boundary correctly, but don't emit a marker yet.
                    prevVolId  = entry.VolumeId;
                    prevPartId = entry.PartId;
                    prevChapId = entry.ChapterId;
                }

                // Advance offset
                if (IsPause(entry.ItemType))
                    offset += TimeSpan.FromMilliseconds(PauseMs(entry.ItemType, settings));
                else if (audioDurations.TryGetValue(entry.ParagraphItemId, out var dur))
                    offset += dur;
            }

            // Close all pending markers.
            // End = start of the *next* marker that begins at a later offset (or total).
            // Multiple markers sharing the same Start (e.g. Vol+Part+Chap at offset 0)
            // all share the same End — the start of the next distinct offset.
            for (int i = 0; i < pending.Count; i++)
            {
                TimeSpan end = offset;
                for (int j = i + 1; j < pending.Count; j++)
                {
                    if (pending[j].Start > pending[i].Start)
                    {
                        end = pending[j].Start;
                        break;
                    }
                }
                markers.Add(new ChapterMarker(pending[i].Title, pending[i].Start, end));
            }

            return markers;
        }

        // ── 4. ffmetadata text generator ──────────────────────────────────────

        public static string GenerateFfmetadata(
            string bookTitle,
            string author,
            IReadOnlyList<ChapterMarker> chapters)
        {
            var sb = new StringBuilder();
            sb.AppendLine(";FFMETADATA1");
            sb.AppendLine($"title={Escape(bookTitle)}");
            sb.AppendLine($"artist={Escape(author)}");
            sb.AppendLine($"album_artist={Escape(author)}");
            sb.AppendLine($"album={Escape(bookTitle)}");
            sb.AppendLine("genre=Audiobook");

            foreach (var ch in chapters)
            {
                sb.AppendLine();
                sb.AppendLine("[CHAPTER]");
                sb.AppendLine("TIMEBASE=1/1000");
                sb.AppendLine($"START={(long)ch.Start.TotalMilliseconds}");
                sb.AppendLine($"END={(long)ch.End.TotalMilliseconds}");
                sb.AppendLine($"title={Escape(ch.Title)}");
            }

            return sb.ToString();
        }

        private static string Escape(string value)
        {
            // ffmetadata escaping: \  =  ;  #  newline
            var sb = new StringBuilder(value.Length + 4);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '=':  sb.Append("\\=");  break;
                    case ';':  sb.Append("\\;");  break;
                    case '#':  sb.Append("\\#");  break;
                    case '\n': sb.Append("\\\n"); break;
                    default:   sb.Append(c);      break;
                }
            }
            return sb.ToString();
        }
    }
}
