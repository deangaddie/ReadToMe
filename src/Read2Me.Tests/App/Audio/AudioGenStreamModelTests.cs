using Read2Me.App.Audio;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.App.Audio
{
    public class AudioGenStreamModelTests
    {
        private readonly AudioGenStreamModel _model = new();

        [Fact]
        public void ItemStarted_CreatesCard_WithCharacterAndText()
        {
            var id = Guid.NewGuid();

            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "In a hole"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(id, card.Id);
            Assert.Equal("Bilbo", card.Character);
            Assert.Equal("In a hole", card.Text);
        }

        [Fact]
        public void LaterEventForSameId_UpdatesExistingCard_NoNewCard()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "In a hole"));

            _model.Apply(new AudioGenerated(id, Attempt: 1));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.AudioGen);
        }

        [Fact]
        public void Normalized_Ok_SetsPhaseOk_WithReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Normalized(id, Attempt: 1, Ok: true, Reason: null));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.Normalize);
            Assert.Null(card.NormalizeReason);
        }

        [Fact]
        public void PostProcessed_Applied_SetsPhaseOk()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new PostProcessed(id, Attempt: 1, "consonant-soften", Applied: true, Reason: null));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.PostProcess);
            Assert.Null(card.PostProcessReason);
        }

        [Fact]
        public void PostProcessed_Skipped_SetsPhaseFail_WithReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new PostProcessed(id, Attempt: 1, "consonant-soften", Applied: false, Reason: "ffmpeg missing"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Fail, card.PostProcess);
            Assert.Equal("consonant-soften: ffmpeg missing", card.PostProcessReason);
        }

        [Fact]
        public void PostProcess_NoEvent_StaysPending()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Pending, card.PostProcess);
        }

        [Fact]
        public void Normalized_NotOk_SetsPhaseFail_WithReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Normalized(id, Attempt: 1, Ok: false, Reason: "ffmpeg boom"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Fail, card.Normalize);
            Assert.Equal("ffmpeg boom", card.NormalizeReason);
        }

        [Fact]
        public void Transcribed_SetsTranscript_AndTranscribePhaseOk()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Transcribed(id, Attempt: 1, "In a hole transcript"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal("In a hole transcript", card.Transcript);
            Assert.Equal(PhaseState.Ok, card.Transcribe);
        }

        [Fact]
        public void Verified_Ok_SetsPhaseOk_WithWer()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Verified(id, Attempt: 1, Ok: true, Wer: 0.05, Reason: null));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.Verify);
            Assert.Equal(0.05, card.Wer);
        }

        [Fact]
        public void Verified_NotOk_SetsPhaseFail_WithWerAndReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Verified(id, Attempt: 1, Ok: false, Wer: 0.42, Reason: "WER 0.42 > 0.15"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Fail, card.Verify);
            Assert.Equal(0.42, card.Wer);
            Assert.Equal("WER 0.42 > 0.15", card.VerifyReason);
        }

        [Fact]
        public void Failed_SetsTerminalError_DistinctFromPhaseFail()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Failed(id, Attempt: 1, "No active TTS configuration"));

            var card = Assert.Single(_model.Cards);
            Assert.True(card.HasFailed);
            Assert.Equal("No active TTS configuration", card.FailureReason);
            // A terminal failure is not a phase ✗ — phase stays pending.
            Assert.Equal(PhaseState.Pending, card.AudioGen);
        }

        [Fact]
        public void FailedBeforeCharacterResolved_StillProducesVisibleCard()
        {
            var id = Guid.NewGuid();
            // Row not found ⇒ ItemStarted with null character/text, then Failed.
            _model.Apply(new ItemStarted(id, Attempt: 1, null, null));
            _model.Apply(new Failed(id, Attempt: 1, "ParagraphItem not found"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(id, card.Id);
            Assert.Null(card.Character);
            Assert.True(card.HasFailed);
            Assert.Equal("ParagraphItem not found", card.FailureReason);
        }

        [Fact]
        public void Verified_Rescued_SetsRescuedTrue_OnCard()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            _model.Apply(new Verified(id, Attempt: 1, Ok: true, Wer: 0.42, Reason: "rescued by semantic 0.91", Rescued: true));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.Verify);
            Assert.True(card.Rescued);
        }

        [Fact]
        public void MultipleItems_KeptInArrivalOrder_NewestLast_NoCap()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in ids)
                _model.Apply(new ItemStarted(id, Attempt: 1, "C", "t"));

            // A later event for the first item must not reorder it to the bottom.
            _model.Apply(new AudioGenerated(ids[0], Attempt: 1));

            Assert.Equal(50, _model.Cards.Count);
            Assert.Equal(ids, _model.Cards.Select(c => c.Id).ToArray());
        }

        [Fact]
        public void SecondAttempt_ItemStarted_AppendsNewCard_ForSameId()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));
            _model.Apply(new ItemStarted(id, Attempt: 2, "Bilbo", "x"));

            Assert.Equal(2, _model.Cards.Count);
            Assert.Equal(1, _model.Cards[0].Attempt);
            Assert.Equal(2, _model.Cards[1].Attempt);
        }

        [Fact]
        public void SecondAttempt_LaterEvent_RoutesToSecondCard_NotFirst()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));
            _model.Apply(new ItemStarted(id, Attempt: 2, "Bilbo", "x"));

            _model.Apply(new Verified(id, Attempt: 2, Ok: true, Wer: 0.05, Reason: null));

            Assert.Equal(PhaseState.Pending, _model.Cards[0].Verify);
            Assert.Equal(PhaseState.Ok, _model.Cards[1].Verify);
        }

        [Fact]
        public void Attempt1_Card_HasAttempt1_NoRetryChipNeeded()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));

            Assert.Equal(1, _model.Cards[0].Attempt);
        }

        [Fact]
        public void Attempt2_Card_HasAttempt2_RetryChipShouldShowRetry1()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, Attempt: 1, "Bilbo", "x"));
            _model.Apply(new ItemStarted(id, Attempt: 2, "Bilbo", "x"));

            var retryCard = _model.Cards[1];
            Assert.Equal(2, retryCard.Attempt);
            // View renders "retry N" where N = Attempt - 1 = 1
        }
    }
}
