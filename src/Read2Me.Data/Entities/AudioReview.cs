using Read2Me.Data.Enums;

namespace Read2Me.Data.Entities
{
    public class AudioReview
    {
        public Guid Id { get; set; }
        public Guid ParagraphItemId { get; set; }       // FK, unique
        public AudioReviewState State { get; set; }

        public bool NormalizeOk { get; set; }
        public string? NormalizeReason { get; set; }     // max 500

        public bool VerifyOk { get; set; }
        public double? Wer { get; set; }
        public string? VerifyReason { get; set; }        // max 500
        public string? Transcript { get; set; }          // max 8000
        public string? OriginalTextSnapshot { get; set; } // max 8000

        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }

        public ParagraphItem ParagraphItem { get; set; } = null!;
    }
}
