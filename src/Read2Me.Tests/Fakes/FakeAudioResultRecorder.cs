using Read2Me.Core.Models;
using Read2Me.Services.Audio;

namespace Read2Me.Tests.Fakes
{
    public sealed class FakeAudioResultRecorder : IAudioResultRecorder
    {
        public ProjectFolderId? LastFolder { get; private set; }
        public Guid? LastParagraphItemId { get; private set; }
        public PipelineResult? LastResult { get; private set; }
        public string? LastSourceText { get; private set; }

        public string CannedRelativePath { get; set; } = string.Empty;

        public Exception? Throws { get; set; }

        public Task<string> RecordAsync(
            ProjectFolderId folder,
            Guid paragraphItemId,
            PipelineResult result,
            string sourceText,
            CancellationToken ct)
        {
            LastFolder = folder;
            LastParagraphItemId = paragraphItemId;
            LastResult = result;
            LastSourceText = sourceText;
            if (Throws is not null) throw Throws;
            var path = string.IsNullOrEmpty(CannedRelativePath)
                ? $"audio/{paragraphItemId}.wav"
                : CannedRelativePath;
            return Task.FromResult(path);
        }
    }
}
