using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.TestUtils;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class PreviewStoreTests
    {
        private static readonly ProjectFolderId Folder = new("book-a");
        private readonly ManualTimeProvider _clock = new();
        private readonly Guid _voiceId = Guid.NewGuid();

        private static ChainStepOutcome Stage(string stepId, byte marker, bool applied = true, string? reason = null) =>
            new(stepId, applied, reason, [1, 2, marker]);

        [Fact]
        public void Save_then_TryGet_answers_the_entry_with_its_stages_and_final_audio()
        {
            var store = new PreviewStore(_clock);

            var saved = store.Save(Folder, _voiceId, [Stage("denoise", 10), Stage("silence-trim", 20, applied: false, reason: "too short")]);
            var found = store.TryGet(saved.PreviewId);

            Assert.NotNull(found);
            Assert.Equal(Folder, found.Folder);
            Assert.Equal(_voiceId, found.VoiceId);
            Assert.Equal(["denoise", "silence-trim"], found.Stages.Select(s => s.StepId));
            Assert.False(found.Stages[1].Applied);
            Assert.Equal("too short", found.Stages[1].Reason);
            Assert.Equal(new byte[] { 1, 2, 20 }, found.Final);
        }

        [Fact]
        public void Preview_ids_are_url_safe_and_unique()
        {
            var store = new PreviewStore(_clock);

            var a = store.Save(Folder, _voiceId, [Stage("denoise", 1)]);
            var b = store.Save(Folder, _voiceId, [Stage("denoise", 1)]);

            Assert.NotEqual(a.PreviewId, b.PreviewId);
            Assert.All(a.PreviewId, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        }

        [Fact]
        public void Entries_expire_after_the_ttl()
        {
            var store = new PreviewStore(_clock, ttl: TimeSpan.FromMinutes(30));
            var saved = store.Save(Folder, _voiceId, [Stage("denoise", 1)]);

            _clock.Advance(TimeSpan.FromMinutes(29));
            Assert.NotNull(store.TryGet(saved.PreviewId));

            _clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(store.TryGet(saved.PreviewId));
        }

        [Fact]
        public void Unknown_id_answers_null()
        {
            var store = new PreviewStore(_clock);
            Assert.Null(store.TryGet("nope"));
        }

        [Fact]
        public void Save_refuses_an_empty_chain()
        {
            var store = new PreviewStore(_clock);
            Assert.Throws<ArgumentException>(() => store.Save(Folder, _voiceId, []));
        }
    }
}
