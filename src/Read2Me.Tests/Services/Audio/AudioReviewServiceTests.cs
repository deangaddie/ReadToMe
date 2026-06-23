using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioReviewServiceTests
    {
        private readonly ProjectFolderId _folder = new("book");

        private static AudioReviewInfo Info(AudioReviewState state = AudioReviewState.NeedsReview) =>
            new(state, NormalizeOk: true, NormalizeReason: null,
                VerifyOk: false, Wer: 0.2, VerifyReason: "over",
                Transcript: "t", OriginalTextSnapshot: "o");

        [Fact]
        public void Set_ThenReviewOf_ReturnsInfo_AndRaisesChanged()
        {
            var svc = new AudioReviewService();
            var itemId = Guid.NewGuid();
            int changed = 0;
            svc.Changed += () => changed++;

            svc.Set(_folder, itemId, Info());

            Assert.Equal(AudioReviewState.NeedsReview, svc.ReviewOf(_folder, itemId)!.State);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void ReviewOf_UnknownItem_ReturnsNull()
        {
            var svc = new AudioReviewService();
            Assert.Null(svc.ReviewOf(_folder, Guid.NewGuid()));
        }

        [Fact]
        public void Clear_RemovesItem_AndRaisesChanged()
        {
            var svc = new AudioReviewService();
            var itemId = Guid.NewGuid();
            svc.Set(_folder, itemId, Info());
            int changed = 0;
            svc.Changed += () => changed++;

            svc.Clear(_folder, itemId);

            Assert.Null(svc.ReviewOf(_folder, itemId));
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Hydrate_ReplacesFolderState()
        {
            var svc = new AudioReviewService();
            var stale = Guid.NewGuid();
            svc.Set(_folder, stale, Info());

            var fresh = Guid.NewGuid();
            svc.Hydrate(_folder, [(fresh, Info(AudioReviewState.Dismissed))]);

            Assert.Null(svc.ReviewOf(_folder, stale));
            Assert.Equal(AudioReviewState.Dismissed, svc.ReviewOf(_folder, fresh)!.State);
        }
    }
}
