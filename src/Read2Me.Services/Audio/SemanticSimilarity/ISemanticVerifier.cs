namespace Read2Me.Services.Audio.SemanticSimilarity
{
    public interface ISemanticVerifier
    {
        /// <summary>
        /// Returns whether source and transcript are semantically close enough to rescue a WER fail,
        /// plus the raw score and threshold used (both null when no config or on error).
        /// Never throws: no active config or any HTTP/exception returns false after a logged warning.
        /// </summary>
        Task<(bool Passes, double? Score, double? Threshold)> PassesAsync(string source, string transcript, CancellationToken ct = default);
    }
}
