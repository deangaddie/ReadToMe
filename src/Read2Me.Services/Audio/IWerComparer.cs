namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Computes a numeral- and spelling-tolerant Word Error Rate between a reference
    /// and a hypothesis transcript. Pure (no DI). Result is in <c>[0..1]</c>.
    /// </summary>
    public interface IWerComparer
    {
        /// <summary>
        /// Returns the token-level Word Error Rate of <paramref name="hypothesis"/> against
        /// <paramref name="reference"/> after normalization. Zero reference tokens ⇒ <c>0</c>
        /// when the hypothesis is also empty, else <c>1</c>.
        /// </summary>
        double Compute(string reference, string hypothesis);
    }
}
