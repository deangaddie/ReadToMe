using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Mutations;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeAudioResultRecorder : IAudioResultRecorder
    {
        public ProjectFolderId? LastFolder { get; private set; }
        public Guid? LastParagraphItemId { get; private set; }
        public PipelineResult? LastResult { get; private set; }
        public string? LastSourceText { get; private set; }
        public TakeInputs? LastResolvedFrom { get; private set; }

        public string CannedRelativePath { get; set; } = string.Empty;

        public Exception? Throws { get; set; }

        /// <summary>When set, the next this many takes answer stale instead of recording.</summary>
        public int StaleTakes { get; set; }

        public int Calls { get; private set; }

        public Task<TakeRecording> RecordAsync(
            ProjectFolderId folder,
            Guid paragraphItemId,
            PipelineResult result,
            string sourceText,
            TakeInputs resolvedFrom,
            CancellationToken ct)
        {
            Calls++;
            LastFolder = folder;
            LastParagraphItemId = paragraphItemId;
            LastResult = result;
            LastSourceText = sourceText;
            LastResolvedFrom = resolvedFrom;
            if (Throws is not null) throw Throws;
            if (StaleTakes > 0)
            {
                StaleTakes--;
                return Task.FromResult<TakeRecording>(new TakeRecording.Stale("its Spoken text changed"));
            }
            var path = string.IsNullOrEmpty(CannedRelativePath)
                ? $"audio/{paragraphItemId}.wav"
                : CannedRelativePath;
            return Task.FromResult<TakeRecording>(new TakeRecording.Recorded(path));
        }
    }
}
