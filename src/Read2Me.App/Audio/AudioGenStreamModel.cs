using System;
using System.Collections.Generic;
using Read2Me.Services.Audio;

namespace Read2Me.App.Audio
{
    /// <summary>
    /// Accumulates one <see cref="AudioGenCard"/> per ParagraphItem id from the
    /// <see cref="AudioGenEvent"/> stream, updating the matching card in place as later
    /// events for that id arrive. Cards are kept in arrival order (newest last) with no cap.
    /// </summary>
    public sealed class AudioGenStreamModel
    {
        private readonly List<AudioGenCard> _cards = new();
        private readonly Dictionary<(Guid Id, int Attempt), AudioGenCard> _byId = new();

        public IReadOnlyList<AudioGenCard> Cards => _cards;

        public void Apply(AudioGenEvent e)
        {
            switch (e)
            {
                case ItemStarted s:
                    var card = new AudioGenCard(s.Id, s.Attempt) { Character = s.Character, Text = s.Text };
                    _cards.Add(card);
                    _byId[(s.Id, s.Attempt)] = card;
                    break;

                case AudioGenerated g when _byId.TryGetValue((g.Id, g.Attempt), out var gc):
                    gc.AudioGen = PhaseState.Ok;
                    break;

                case Normalized n when _byId.TryGetValue((n.Id, n.Attempt), out var nc):
                    nc.Normalize = n.Ok ? PhaseState.Ok : PhaseState.Fail;
                    nc.NormalizeReason = n.Reason;
                    break;

                case PostProcessed p when _byId.TryGetValue((p.Id, p.Attempt), out var pc):
                    // A skipped step is cosmetic-only: the item still ships unfiltered audio,
                    // so it shows as a phase ✗ (with the step id) rather than an item failure.
                    pc.PostProcess = p.Applied ? PhaseState.Ok : PhaseState.Fail;
                    pc.PostProcessReason = p.Applied ? null : $"{p.StepId}: {p.Reason}";
                    break;

                case Transcribed t when _byId.TryGetValue((t.Id, t.Attempt), out var tc):
                    tc.Transcript = t.Transcript;
                    tc.Transcribe = PhaseState.Ok;
                    break;

                case Verified v when _byId.TryGetValue((v.Id, v.Attempt), out var vc):
                    vc.Verify = v.Ok ? PhaseState.Ok : PhaseState.Fail;
                    vc.Wer = v.Wer;
                    vc.VerifyReason = v.Reason;
                    vc.Rescued = v.Rescued;
                    break;

                case Failed f when _byId.TryGetValue((f.Id, f.Attempt), out var fc):
                    fc.HasFailed = true;
                    fc.FailureReason = f.Reason;
                    break;
            }
        }
    }

    public enum PhaseState { Pending, Ok, Fail }

    public sealed class AudioGenCard
    {
        public AudioGenCard(Guid id, int attempt) { Id = id; Attempt = attempt; }

        public Guid Id { get; }
        public int Attempt { get; }
        public string? Character { get; set; }
        public string? Text { get; set; }

        public PhaseState AudioGen { get; set; } = PhaseState.Pending;
        public PhaseState Normalize { get; set; } = PhaseState.Pending;

        /// <summary>Stays <see cref="PhaseState.Pending"/> when no post-process step is enabled.</summary>
        public PhaseState PostProcess { get; set; } = PhaseState.Pending;
        public PhaseState Transcribe { get; set; } = PhaseState.Pending;
        public PhaseState Verify { get; set; } = PhaseState.Pending;

        public string? NormalizeReason { get; set; }
        public string? PostProcessReason { get; set; }
        public string? Transcript { get; set; }
        public string? VerifyReason { get; set; }
        public double? Wer { get; set; }
        public bool Rescued { get; set; }

        /// <summary>A terminal <see cref="Failed"/> event — rendered as a red error in place of
        /// further phase rows, distinct from a phase ✗.</summary>
        public bool HasFailed { get; set; }
        public string? FailureReason { get; set; }
    }
}
