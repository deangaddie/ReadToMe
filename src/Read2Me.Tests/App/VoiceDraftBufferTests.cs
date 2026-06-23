using Read2Me.App.State;
using Xunit;

namespace Read2Me.Tests.App
{
    public class VoiceDraftBufferTests
    {
        private static Guid Id() => Guid.NewGuid();

        [Fact]
        public void Current_NoEdit_ReturnsSavedValue()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();

            Assert.Equal("saved", buf.Current(id, VoiceDraftField.Prompt, "saved"));
        }

        [Fact]
        public void Current_NoEdit_NullSaved_ReturnsEmpty()
        {
            var buf = new VoiceDraftBuffer();
            Assert.Equal("", buf.Current(Id(), VoiceDraftField.Prompt, null));
        }

        [Fact]
        public void Current_AfterSet_ReturnsDraft()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();
            buf.Set(id, VoiceDraftField.Prompt, "draft");

            Assert.Equal("draft", buf.Current(id, VoiceDraftField.Prompt, "saved"));
        }

        [Fact]
        public void IsDirty_FalseWhenDraftEqualsSaved()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();
            buf.Set(id, VoiceDraftField.Prompt, "same");

            Assert.False(buf.IsDirty(id, VoiceDraftField.Prompt, "same"));
        }

        [Fact]
        public void IsDirty_TrueWhenDraftDiffers()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();
            buf.Set(id, VoiceDraftField.Prompt, "new value");

            Assert.True(buf.IsDirty(id, VoiceDraftField.Prompt, "old value"));
        }

        [Fact]
        public void Clear_DropsDraft_RevertsToSaved()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();
            buf.Set(id, VoiceDraftField.Prompt, "draft");
            buf.Clear(id, VoiceDraftField.Prompt);

            Assert.Equal("saved", buf.Current(id, VoiceDraftField.Prompt, "saved"));
            Assert.False(buf.IsDirty(id, VoiceDraftField.Prompt, "saved"));
        }

        [Fact]
        public void Fields_AreIndependent()
        {
            var buf = new VoiceDraftBuffer();
            var id = Id();
            buf.Set(id, VoiceDraftField.Prompt, "prompt draft");

            Assert.Equal("saved", buf.Current(id, VoiceDraftField.Transcript, "saved"));
            Assert.False(buf.IsDirty(id, VoiceDraftField.Transcript, "saved"));
        }

        [Fact]
        public void Voices_AreIndependent()
        {
            var buf = new VoiceDraftBuffer();
            var a = Id();
            var b = Id();
            buf.Set(a, VoiceDraftField.Prompt, "draft for A");

            Assert.Equal("saved", buf.Current(b, VoiceDraftField.Prompt, "saved"));
            Assert.False(buf.IsDirty(b, VoiceDraftField.Prompt, "saved"));
        }
    }
}
