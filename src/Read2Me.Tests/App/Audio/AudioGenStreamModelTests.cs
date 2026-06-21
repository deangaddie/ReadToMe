using System;
using System.Linq;
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

            _model.Apply(new ItemStarted(id, "Bilbo", "In a hole"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(id, card.Id);
            Assert.Equal("Bilbo", card.Character);
            Assert.Equal("In a hole", card.Text);
        }

        [Fact]
        public void LaterEventForSameId_UpdatesExistingCard_NoNewCard()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "In a hole"));

            _model.Apply(new AudioGenerated(id));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.AudioGen);
        }

        [Fact]
        public void Normalized_Ok_SetsPhaseOk_WithReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Normalized(id, Ok: true, Reason: null));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.Normalize);
            Assert.Null(card.NormalizeReason);
        }

        [Fact]
        public void Normalized_NotOk_SetsPhaseFail_WithReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Normalized(id, Ok: false, Reason: "ffmpeg boom"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Fail, card.Normalize);
            Assert.Equal("ffmpeg boom", card.NormalizeReason);
        }

        [Fact]
        public void Transcribed_SetsTranscript_AndTranscribePhaseOk()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Transcribed(id, "In a hole transcript"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal("In a hole transcript", card.Transcript);
            Assert.Equal(PhaseState.Ok, card.Transcribe);
        }

        [Fact]
        public void Verified_Ok_SetsPhaseOk_WithWer()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Verified(id, Ok: true, Wer: 0.05, Reason: null));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Ok, card.Verify);
            Assert.Equal(0.05, card.Wer);
        }

        [Fact]
        public void Verified_NotOk_SetsPhaseFail_WithWerAndReason()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Verified(id, Ok: false, Wer: 0.42, Reason: "WER 0.42 > 0.15"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(PhaseState.Fail, card.Verify);
            Assert.Equal(0.42, card.Wer);
            Assert.Equal("WER 0.42 > 0.15", card.VerifyReason);
        }

        [Fact]
        public void Failed_SetsTerminalError_DistinctFromPhaseFail()
        {
            var id = Guid.NewGuid();
            _model.Apply(new ItemStarted(id, "Bilbo", "x"));

            _model.Apply(new Failed(id, "No active TTS configuration"));

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
            _model.Apply(new ItemStarted(id, null, null));
            _model.Apply(new Failed(id, "ParagraphItem not found"));

            var card = Assert.Single(_model.Cards);
            Assert.Equal(id, card.Id);
            Assert.Null(card.Character);
            Assert.True(card.HasFailed);
            Assert.Equal("ParagraphItem not found", card.FailureReason);
        }

        [Fact]
        public void MultipleItems_KeptInArrivalOrder_NewestLast_NoCap()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var id in ids)
                _model.Apply(new ItemStarted(id, "C", "t"));

            // A later event for the first item must not reorder it to the bottom.
            _model.Apply(new AudioGenerated(ids[0]));

            Assert.Equal(50, _model.Cards.Count);
            Assert.Equal(ids, _model.Cards.Select(c => c.Id).ToArray());
        }
    }
}
